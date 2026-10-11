using System.Diagnostics;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Navigation;

/// <summary>
/// The navigation grid and the flow fields over it. On a host or offline game with an <see cref="INavGridSource"/>,
/// <see cref="Initialize"/> places the grid's window (the whole map where it is at most
/// <c>Engine:Navigation:WindowCells</c> across, else a square of that many cells round the match's start) and starts a
/// thread of its own that fills it from the world as generated, stamps the edits made so far (a save's, whose voxels are
/// not loaded) onto it from their shapes, and builds every field the source asks for; <see cref="AfterFixedStep"/>, once
/// a fixed step on the main thread, takes finished fields in, and from then on <see cref="Ready"/> is true.
/// <para>
/// After that, each fixed step reads the world's new edits. Cells under an edit whose level-0 voxels are loaded (most
/// live edits on a host: tools reach 16 m, and remote players and loose bodies stream discs around them; rounds and
/// rockets dig at any distance) are read
/// again from the voxels (<see cref="NavGridScanner.Rescan"/>, at most <see cref="RescansPerStep"/> a step); cells under
/// one whose voxels are not are stamped from its shape and read again once a disc covers them (the stamped edits are
/// checked in turn, <see cref="StampChecksPerStep"/> a step, and a queued cell whose voxels unloaded before its read
/// waits among them). An edit that covers no cell's centre column, or lies outside the window, is passed over
/// (<see cref="NavGridScanner.CoversAnyCentre"/>). Changed rows are copied
/// to the thread's own grid, and the thread builds every field again from it (all of it: the
/// whole build is measured before a partial one; the cells each size can stand in are worked out again only in the changed rows and
/// as far round them as its circle reaches), at most every <see cref="JobIntervalSeconds"/>, while walkers keep the
/// fields they have until the new ones are swapped in whole. The main thread's grid follows edits at once.
/// </para>
/// <para>
/// A window smaller than the map follows a focus: what a game gives <see cref="Follow"/>, else the first streaming
/// centre (the local player's view). When the window's corner for the focus (the focus in the middle, snapped to
/// <see cref="TileCells"/> and kept inside the map) lies more than a quarter of the window from where it is, or differs
/// from it while the focus is within an eighth of the window of its edge, the window moves: the main thread stops reading edits, copies the cells both windows hold to the thread's grid at their new
/// places, and finds the logged edits since the last reset that reach the strips coming in; the thread fills those
/// strips from the source, stamps those edits on them, and builds every field; the main thread then takes that grid
/// whole with the fields, and those edits wait to be read from the voxels as any stamped edit does. Walkers keep the old
/// fields, each in its own window (<see cref="FlowField.TryCell(int, int, out int)"/>), until then.
/// </para>
/// <para>
/// A reset of the world (<see cref="VoxelWorld.Reset"/>) drops every waiting rescan and stamped edit, and the thread
/// fills its grid from the layout again and builds every field from it; the main thread takes that grid whole with the
/// fields, and only then reads the edits after the reset (<see cref="Refilling"/> until then).
/// </para>
/// A session client builds nothing: the host moves every walker.
/// </summary>
public sealed partial class NavigationService : IDisposable
{
    /// <summary>
    /// The most cells read again from the voxels in one fixed step: about 1 ms (a cell took 7.5 us in Release on real
    /// ground, 2026-09-27). A 50 cm dig reaches 9 to 25 cells.
    /// </summary>
    public const int RescansPerStep = 128;

    /// <summary>The window's corner moves by whole tiles of this many cells (32 m).</summary>
    public const int TileCells = 64;

    /// <summary>
    /// The most stamped edits checked for loaded voxels in one fixed step, in turn from where the last step stopped
    /// (a check of only the last few would let that many far edits hold every older
    /// one's stamp for the rest of the match).
    /// </summary>
    internal const int StampChecksPerStep = 32;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly INavGridSource? _source;
    private readonly Func<bool> _isAuthority;
    private readonly VoxelWorld? _voxels;
    private readonly IVoxelPassability? _passability;
    private readonly ILogger<NavigationService> _logger;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Action<int> _changed;
    private readonly List<StampedEdit> _stamped = [];
    private readonly NavArea _area;
    private readonly int _windowCells;
    private readonly List<VoxelEdit> _stripEdits = [];
    private readonly Strip[] _strips = new Strip[2];
    private int _stripCount;
    private int _stampCursor;
    private NavGrid? _grid;
    private NavGrid? _work;
    private NavGridScanner? _scanner;
    private NavFieldSpec[] _specs = [];
    private FlowField[] _front = [];
    private FlowField[] _back = [];
    private VoxelEdit[] _startEdits = [];
    private byte[] _queued = [];
    private int[] _queue = new int[1024];
    private int _queueHead;
    private int _queueCount;
    private long _editsSeen;
    private int _dirtyFirstRow = int.MaxValue;
    private int _dirtyLastRow = -1;
    private bool _busy;
    private long _lastJobStart;
    private int _jobEdits;
    private int _jobRows;
    private WorldPoint _focus;
    private bool _followed;

    // The rows the thread's next job changes, set before it is woken; -1 for every row (the first build, a refill, a
    // move of the window).
    private int _jobFirstRow = -1;
    private int _jobLastRow = -1;
    private int _editsSinceJob;
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _published;
    private volatile bool _failed;
    private volatile bool _refilling;
    private volatile bool _moving;
    private bool _refillWanted;
    private double _fillMs;
    private double _gatherMs;
    private NavigationTimings _timings;
    private NavigationTimings _takenTimings;
    private bool _disposed;

    public NavigationService(
        SessionService session,
        VoxelWorld voxels,
        IVoxelPassability passability,
        IEnumerable<INavGridSource> sources,
        IOptions<EngineOptions> options,
        ILogger<NavigationService> logger
    )
        : this(
            sources.FirstOrDefault(),
            () => session.IsAuthority,
            logger,
            voxels,
            passability,
            NavArea.Of(voxels.Grid),
            options.Value.Navigation.WindowCells
        ) { }

    /// <param name="source">The game's cells and fields, or null for no navigation.</param>
    /// <param name="isAuthority">Whether this game moves walkers (a host or offline); asked in <see cref="Initialize"/>.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="voxels">The world whose edits change the grid; null to follow no edits.</param>
    /// <param name="passability">The materials walkers pass through; with <paramref name="voxels"/>.</param>
    /// <param name="area">The square the window may cover (the map); none for <paramref name="voxels"/>'s map.</param>
    /// <param name="windowCells">The most cells the window is across.</param>
    internal NavigationService(
        INavGridSource? source,
        Func<bool> isAuthority,
        ILogger<NavigationService> logger,
        VoxelWorld? voxels = null,
        IVoxelPassability? passability = null,
        NavArea? area = null,
        int windowCells = NavigationSettings.DefaultWindowCells
    )
    {
        _source = source;
        _isAuthority = isAuthority;
        _logger = logger;
        _voxels = voxels;
        _passability = passability;
        _area =
            area
            ?? (
                voxels is null
                    ? throw new ArgumentException("Give the area without a world.", nameof(area))
                    : NavArea.Of(voxels.Grid)
            );
        _windowCells = Math.Min(windowCells, _area.Cells);
        _changed = MarkDirty;
    }

    /// <summary>True once the grid and every field are built and handed to the main thread.</summary>
    public bool Ready { get; private set; }

    /// <summary>True when this game keeps a grid: a host or offline game with a source.</summary>
    public bool Enabled => _grid is not null;

    /// <summary>True when the navigation thread failed (logged at Error); <see cref="Ready"/> then never becomes true.</summary>
    public bool Failed => _failed;

    /// <summary>The grid as the world stands (edits read at each fixed step); valid once <see cref="Ready"/>.</summary>
    public NavGrid Grid => Ready ? _grid! : throw new InvalidOperationException("The navigation grid is not ready.");

    /// <summary>The fields kept, in the source's order.</summary>
    public IReadOnlyList<NavFieldSpec> Fields => _specs;

    /// <summary>
    /// The timings of the last job whose fields the main thread took. A copy made at the hand-off, so a read never mixes
    /// two jobs' numbers while the thread writes the next.
    /// </summary>
    public NavigationTimings Timings => _takenTimings;

    /// <summary>How many cells wait to be read again from the voxels.</summary>
    public int PendingRescans => _queueCount;

    /// <summary>How many edits were stamped from their shapes and wait for their voxels to be loaded.</summary>
    public int UncheckedEdits => _stamped.Count;

    /// <summary>True from a reset of the world until the grid filled again from the layout and its fields are taken in.</summary>
    public bool Refilling => _refillWanted || _refilling;

    /// <summary>True from a move of the window until its grid and fields are taken in.</summary>
    public bool Moving => _moving;

    /// <summary>How many times the window has moved this match.</summary>
    public int Moves { get; private set; }

    /// <summary>True while cells changed that no job has taken yet, or a job runs.</summary>
    public bool Recomputing => _busy || _dirtyFirstRow <= _dirtyLastRow;

    /// <summary>The shortest time between the starts of two jobs, so a burst of edits makes one job.</summary>
    internal double JobIntervalSeconds { get; set; } = 0.5;

    /// <summary>The field <paramref name="index"/> of <see cref="Fields"/>; valid once <see cref="Ready"/>.</summary>
    public FlowField Field(int index) =>
        Ready ? _front[index] : throw new InvalidOperationException("The navigation fields are not ready.");

    /// <summary>
    /// Makes the window follow <paramref name="focus"/> (an absolute point: where a game's walkers and players are)
    /// from now on, in place of the first streaming centre; a game calls it as often as its focus moves.
    /// </summary>
    public void Follow(in WorldPoint focus)
    {
        _focus = focus;
        _followed = true;
    }

    /// <summary>
    /// The start-up step: on a host or offline game with a source, places the window, takes the fields' specs (on this
    /// thread, so the game's world layout is built here) and the edits made so far, and starts the navigation thread,
    /// which fills the grid, stamps those edits, and builds the fields.
    /// </summary>
    public void Initialize()
    {
        if (_source is null)
        {
            LogOff("the game has no navigation grid");
            return;
        }

        if (!_isAuthority())
        {
            LogOff("a session client moves no walkers");
            return;
        }

        // Round the focus a game gave already, else the match's start (the origin starts where the game's match does),
        // else the area's middle.
        var start = _followed
            ? _focus
            : _voxels?.Origin.Metres
                ?? WorldPoint.FromCentimetres(
                    new VoxelCoord(
                        _area.MinX + _area.Cells * NavGrid.CellCentimetres / 2,
                        0,
                        _area.MinZ + _area.Cells * NavGrid.CellCentimetres / 2
                    )
                );
        var (x, z) = CornerFor(in start);
        _grid = new NavGrid(x, z, _windowCells, _windowCells);
        _specs = [.. _source.CreateFields()];
        if (_voxels is not null && _passability is not null)
        {
            _scanner = new NavGridScanner(_voxels, _passability, _source.LowCentimetres, _source);
            _editsSeen = _voxels.EditCount;
            // Edits before the last reset changed nothing that is left; nor does one that changes no cell.
            var reset = _voxels.LastReset;
            var kept = new List<VoxelEdit>();
            for (var sequence = reset + 1; sequence <= _editsSeen; sequence++)
            {
                ref readonly var edit = ref _voxels.GetEdit(sequence);
                if (Changes(in edit))
                {
                    kept.Add(edit);
                    _stamped.Add(StampedEdit.Of(in edit));
                }
            }

            _startEdits = [.. kept];
        }

        _thread = new Thread(Run)
        {
            Name = "Itera navigation",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>
    /// Once a fixed step, on the main thread: takes in the fields the navigation thread finished, reads the world's new
    /// edits into the grid, moves the window when its focus has gone far enough, and starts a job when cells changed and
    /// the thread is free.
    /// </summary>
    public void AfterFixedStep()
    {
        if (_grid is null)
        {
            return;
        }

        if (_published)
        {
            TakeFields();
        }

        if (!Ready)
        {
            return;
        }

        if (_scanner is not null && !_moving)
        {
            ReadEdits();
            if (_refillWanted && !_busy)
            {
                StartRefill();
            }

            Rescan();
            CheckStamped();
        }

        if (!_busy && !Refilling)
        {
            FollowFocus();
        }

        if (
            !_busy
            && _dirtyFirstRow <= _dirtyLastRow
            && Stopwatch.GetElapsedTime(_lastJobStart).TotalSeconds >= JobIntervalSeconds
        )
        {
            StartJob();
        }
    }

    /// <summary>Waits for the thread to finish a job, for tests: true when it did within <paramref name="timeout"/>.</summary>
    internal bool WaitUntilPublished(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!_published && !_failed && watch.Elapsed < timeout)
        {
            Thread.Sleep(1);
        }

        return _published;
    }

    /// <summary>
    /// Runs fixed steps until nothing waits to be read or built, for tests: true when that happened within
    /// <paramref name="timeout"/>.
    /// </summary>
    internal bool Settle(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout && !_failed)
        {
            AfterFixedStep();
            if (
                Ready
                && !_busy
                && !_published
                && !Refilling
                && !_moving
                && _queueCount == 0
                && _dirtyFirstRow > _dirtyLastRow
            )
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _wake.Set();
        if (_thread is not null && !_thread.Join(StopTimeout))
        {
            LogStuck(StopTimeout.TotalSeconds);
        }

        _wake.Dispose();
    }

    private void TakeFields()
    {
        (_front, _back) = (_back, _front);
        // Read before the flag is cleared: the thread writes the next job's timings only after it sees the flag clear.
        var timings = _timings;
        _takenTimings = timings;
        _published = false;
        _busy = false;
        if (_refilling)
        {
            // The thread is waiting: its grid, filled again from the layout, becomes the main thread's.
            _grid!.CopyRows(_work!, 0, _grid.Depth - 1);
            _refilling = false;
            LogRefilled(timings.FillMilliseconds, _specs.Length, timings.FieldsMilliseconds);
            return;
        }

        if (_moving)
        {
            TakeMovedWindow(in timings);
            return;
        }

        if (!Ready)
        {
            Ready = true;
            _queued = new byte[_grid!.Count];
            LogReady(
                _grid.Width,
                _grid.Depth,
                NavGrid.CellCentimetres,
                timings.FillMilliseconds,
                _startEdits.Length,
                _specs.Length,
                timings.FieldsMilliseconds,
                timings.LongestFieldMilliseconds
            );
            _startEdits = [];
        }
        else
        {
            LogRecomputed(timings.FieldsMilliseconds, timings.LongestFieldMilliseconds, _jobEdits, _jobRows);
        }
    }

    private void ReadEdits()
    {
        var voxels = _voxels!;
        var grid = _grid!;
        // Only edits already in the voxels: a large edit applied over steps is rescanned once it is in them all. Edits
        // after a reset wait until the grid is filled again.
        var applied = voxels.AppliedEditCount;
        while (_editsSeen < applied && !Refilling)
        {
            ref readonly var edit = ref voxels.GetEdit(++_editsSeen);
            if (edit.Operation == VoxelEditOperation.Paint)
            {
                continue;
            }

            if (edit.Operation == VoxelEditOperation.Reset)
            {
                ForgetEdits();
                continue;
            }

            if (!Changes(in edit))
            {
                continue;
            }

            _editsSinceJob++;
            var cells = StampedEdit.Of(in edit);
            if (Resident(in cells))
            {
                Queue(in cells);
            }
            else
            {
                _scanner!.Stamp(grid, in edit, _changed);
                _stamped.Add(cells);
            }
        }
    }

    /// <summary>
    /// False for an edit that cannot change a cell: painting, a clear of only what walkers pass through, or one that
    /// covers no cell's centre column (outside the window, among others).
    /// </summary>
    private bool Changes(in VoxelEdit edit) =>
        edit.Operation is not VoxelEditOperation.Paint and not VoxelEditOperation.Reset
        && !_scanner!.ClearsOnlyPassable(in edit)
        && NavGridScanner.CoversAnyCentre(_grid!, in edit);

    /// <summary>At a reset: drops what waits to be read again, and asks the thread for a grid from the layout.</summary>
    private void ForgetEdits()
    {
        _stamped.Clear();
        _stampCursor = 0;
        _queueHead = 0;
        _queueCount = 0;
        Array.Clear(_queued);
        _dirtyFirstRow = int.MaxValue;
        _dirtyLastRow = -1;
        _editsSinceJob = 0;
        _refillWanted = true;
    }

    /// <summary>Has the thread fill its grid from the layout and build every field from it.</summary>
    private void StartRefill()
    {
        _refillWanted = false;
        _refilling = true;
        _busy = true;
        _jobFirstRow = -1;
        _jobLastRow = -1;
        _jobEdits = 0;
        _jobRows = _grid!.Depth;
        _lastJobStart = Stopwatch.GetTimestamp();
        _wake.Set();
    }

    /// <summary>
    /// The window's corner for <paramref name="focus"/>: the focus in its middle, snapped down to whole tiles from the
    /// area's corner and kept inside the area; the area's corner when the window covers it.
    /// </summary>
    private (int X, int Z) CornerFor(in WorldPoint focus)
    {
        var at = focus.ToCentimetres();
        return (Corner(at.X, _area.MinX), Corner(at.Z, _area.MinZ));

        int Corner(int centimetres, int min)
        {
            var cell = NavGrid.FloorDivide(centimetres - min, NavGrid.CellCentimetres) - _windowCells / 2;
            var snapped = NavGrid.FloorDivide(cell, TileCells) * TileCells;
            return min + Math.Clamp(snapped, 0, _area.Cells - _windowCells) * NavGrid.CellCentimetres;
        }
    }

    /// <summary>
    /// Moves the window when its corner for the focus (what a game follows, else the first streaming centre) differs from
    /// where it is, and lies more than a quarter of it from there or the focus is within an eighth of it of its edge.
    /// </summary>
    private void FollowFocus()
    {
        if (_windowCells >= _area.Cells)
        {
            return;
        }

        if (!_followed)
        {
            if (_voxels is null || _voxels.Centers.Length == 0)
            {
                return;
            }

            _focus = _voxels.Origin.ToWorld(_voxels.Centers[0]);
        }

        var grid = _grid!;
        var (x, z) = CornerFor(in _focus);
        if (x == grid.OriginX && z == grid.OriginZ)
        {
            return;
        }

        // Far enough for a move, or near the window's edge (where the map's edge, keeping the window in, may leave the
        // corner for the focus within a quarter of it).
        var quarter = _windowCells / 4 * NavGrid.CellCentimetres;
        var margin = _windowCells / 8 * NavGrid.CellCentimetres;
        var focus = _focus.ToCentimetres();
        var span = _windowCells * NavGrid.CellCentimetres;
        if (
            Math.Abs(x - grid.OriginX) > quarter
            || Math.Abs(z - grid.OriginZ) > quarter
            || focus.X - grid.OriginX < margin
            || grid.OriginX + span - focus.X < margin
            || focus.Z - grid.OriginZ < margin
            || grid.OriginZ + span - focus.Z < margin
        )
        {
            StartMove(x, z);
        }
    }

    /// <summary>
    /// Starts a move of the window to the corner (<paramref name="x"/>, <paramref name="z"/>): the cells both windows
    /// hold go to the thread's grid at their new places, the strips coming in and the logged edits that reach them go
    /// to the thread, which fills and stamps them and builds every field.
    /// </summary>
    private void StartMove(int x, int z)
    {
        var start = Stopwatch.GetTimestamp();
        var grid = _grid!;
        var work = _work!;
        work.MoveTo(x, z);
        work.CopyOverlap(grid);
        // The strips coming in, in the new window: the columns past the old window's side, then the rows past its end
        // over the columns both hold (one of each at most).
        var size = _windowCells;
        var dx = (grid.OriginX - x) / NavGrid.CellCentimetres;
        var dz = (grid.OriginZ - z) / NavGrid.CellCentimetres;
        var keptFirst = Math.Clamp(dx, 0, size);
        var keptLast = Math.Clamp(size - 1 + dx, -1, size - 1);
        _stripCount = 0;
        if (keptFirst > 0)
        {
            _strips[_stripCount++] = new Strip(0, 0, keptFirst - 1, size - 1);
        }
        else if (keptLast < size - 1)
        {
            _strips[_stripCount++] = new Strip(Math.Max(keptLast + 1, 0), 0, size - 1, size - 1);
        }

        if (keptFirst <= keptLast)
        {
            var rowsFirst = Math.Clamp(dz, 0, size);
            var rowsLast = Math.Clamp(size - 1 + dz, -1, size - 1);
            if (rowsFirst > 0)
            {
                _strips[_stripCount++] = new Strip(keptFirst, 0, keptLast, rowsFirst - 1);
            }
            else if (rowsLast < size - 1)
            {
                _strips[_stripCount++] = new Strip(keptFirst, Math.Max(rowsLast + 1, 0), keptLast, size - 1);
            }
        }

        GatherStripEdits();
        _gatherMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _moving = true;
        _busy = true;
        _jobFirstRow = -1;
        _jobLastRow = -1;
        _jobEdits = _stripEdits.Count;
        _jobRows = size;
        _dirtyFirstRow = int.MaxValue;
        _dirtyLastRow = -1;
        _lastJobStart = Stopwatch.GetTimestamp();
        _wake.Set();
    }

    /// <summary>
    /// The logged edits since the last reset that reach the strips coming in and can change a cell, for the thread to
    /// stamp on them.
    /// </summary>
    private void GatherStripEdits()
    {
        _stripEdits.Clear();
        if (_scanner is null || _voxels is null)
        {
            return;
        }

        var work = _work!;
        for (var sequence = _voxels.LastReset + 1; sequence <= _editsSeen; sequence++)
        {
            ref readonly var edit = ref _voxels.GetEdit(sequence);
            if (
                edit.Operation is VoxelEditOperation.Paint or VoxelEditOperation.Reset
                || _scanner.ClearsOnlyPassable(in edit)
            )
            {
                continue;
            }

            var cells = StampedEdit.Of(in edit);
            for (var i = 0; i < _stripCount; i++)
            {
                if (cells.Meets(work, in _strips[i]))
                {
                    _stripEdits.Add(edit);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The thread's grid of the moved window becomes the main thread's; the cells waiting to be read move with it (those
    /// now outside are dropped: they are filled and stamped again when the window comes back), and the edits stamped on
    /// the new strips wait to be read from the voxels.
    /// </summary>
    private void TakeMovedWindow(in NavigationTimings timings)
    {
        var grid = _grid!;
        var work = _work!;
        var dx = (grid.OriginX - work.OriginX) / NavGrid.CellCentimetres;
        var dz = (grid.OriginZ - work.OriginZ) / NavGrid.CellCentimetres;
        grid.MoveTo(work.OriginX, work.OriginZ);
        grid.CopyRows(work, 0, grid.Depth - 1);
        var count = _queueCount;
        var kept = 0;
        Array.Clear(_queued);
        for (var i = 0; i < count; i++)
        {
            var cell = _queue[(_queueHead + i) % _queue.Length];
            var column = cell % grid.Width + dx;
            var row = cell / grid.Width + dz;
            if (column < 0 || column >= grid.Width || row < 0 || row >= grid.Depth)
            {
                continue;
            }

            var moved = grid.Index(column, row);
            if (_queued[moved] == 0)
            {
                _queue[(_queueHead + kept) % _queue.Length] = moved;
                _queued[moved] = 1;
                kept++;
            }
        }

        _queueCount = kept;
        foreach (ref readonly var edit in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_stripEdits))
        {
            _stamped.Add(StampedEdit.Of(in edit));
        }

        _moving = false;
        Moves++;
        LogMoved(
            grid.OriginX / (double)VoxelConstants.CentimetresPerMetre,
            grid.OriginZ / (double)VoxelConstants.CentimetresPerMetre,
            _stripEdits.Count,
            _gatherMs,
            timings.FillMilliseconds,
            _specs.Length,
            timings.FieldsMilliseconds
        );
        _stripEdits.Clear();
    }

    private void Rescan()
    {
        var grid = _grid!;
        for (var i = 0; i < RescansPerStep && _queueCount > 0; i++)
        {
            var cell = _queue[_queueHead];
            _queueHead = (_queueHead + 1) % _queue.Length;
            _queueCount--;
            _queued[cell] = 0;
            if (!_scanner!.IsResident(grid, cell))
            {
                // Its voxels unloaded before its turn: it waits with the stamped edits to be read once they load
                // (dropped, its edit would be lost).
                var column = (grid.OriginX / NavGrid.CellCentimetres) + grid.Column(cell);
                var row = (grid.OriginZ / NavGrid.CellCentimetres) + grid.Row(cell);
                _stamped.Add(new StampedEdit(column, row, column, row));
                continue;
            }

            if (_scanner.Rescan(grid, cell))
            {
                MarkDirty(cell);
            }
        }
    }

    /// <summary>
    /// Reads again, from the voxels, the cells of stamped edits whose voxels have since been loaded: at most
    /// <see cref="StampChecksPerStep"/> a step, in turn round the list. An entry the window has left is dropped: a move
    /// back over it stamps its edit again.
    /// </summary>
    private void CheckStamped()
    {
        var checks = Math.Min(StampChecksPerStep, _stamped.Count);
        for (var i = 0; i < checks && _stamped.Count > 0; i++)
        {
            if (_stampCursor >= _stamped.Count)
            {
                _stampCursor = 0;
            }

            var cells = _stamped[_stampCursor];
            var outside = !cells.TryClip(_grid!, out _, out _, out _, out _);
            if (outside || Resident(in cells))
            {
                // The last entry takes its place, and is checked next.
                if (!outside)
                {
                    Queue(in cells);
                }

                _stamped[_stampCursor] = _stamped[^1];
                _stamped.RemoveAt(_stamped.Count - 1);
            }
            else
            {
                _stampCursor++;
            }
        }
    }

    /// <summary>True when the voxel columns at the centres of the cells' corners in the window are loaded.</summary>
    private bool Resident(in StampedEdit cells)
    {
        var grid = _grid!;
        if (!cells.TryClip(grid, out var firstColumn, out var firstRow, out var lastColumn, out var lastRow))
        {
            return false;
        }

        var voxels = _voxels!.Grid;
        return _voxels.ColumnsResident(
            voxels.ToVoxel(grid.CentreX(firstColumn)),
            voxels.ToVoxel(grid.CentreZ(firstRow)),
            voxels.ToVoxel(grid.CentreX(lastColumn)),
            voxels.ToVoxel(grid.CentreZ(lastRow))
        );
    }

    private void Queue(in StampedEdit cells)
    {
        var grid = _grid!;
        if (!cells.TryClip(grid, out var firstColumn, out var firstRow, out var lastColumn, out var lastRow))
        {
            return;
        }

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var cell = grid.Index(column, row);
                if (_queued[cell] != 0)
                {
                    continue;
                }

                if (_queueCount == _queue.Length)
                {
                    var grown = new int[_queue.Length * 2];
                    for (var i = 0; i < _queueCount; i++)
                    {
                        grown[i] = _queue[(_queueHead + i) % _queue.Length];
                    }

                    _queue = grown;
                    _queueHead = 0;
                }

                _queue[(_queueHead + _queueCount) % _queue.Length] = cell;
                _queueCount++;
                _queued[cell] = 1;
            }
        }
    }

    private void MarkDirty(int cell)
    {
        var row = _grid!.Row(cell);
        _dirtyFirstRow = Math.Min(_dirtyFirstRow, row);
        _dirtyLastRow = Math.Max(_dirtyLastRow, row);
    }

    private void StartJob()
    {
        // The erosion reads a row either side of a changed one, so the rows beside it are copied too.
        var first = Math.Max(0, _dirtyFirstRow - 1);
        var last = Math.Min(_grid!.Depth - 1, _dirtyLastRow + 1);
        _work!.CopyRows(_grid, first, last);
        _jobFirstRow = _dirtyFirstRow;
        _jobLastRow = _dirtyLastRow;
        _jobRows = last - first + 1;
        _jobEdits = _editsSinceJob;
        _editsSinceJob = 0;
        _dirtyFirstRow = int.MaxValue;
        _dirtyLastRow = -1;
        _busy = true;
        _lastJobStart = Stopwatch.GetTimestamp();
        _wake.Set();
    }

    private void Run()
    {
        try
        {
            var grid = _grid!;
            _work = new NavGrid(grid.OriginX, grid.OriginZ, grid.Width, grid.Depth);
            var builder = new FlowFieldBuilder();
            var walkable = new Dictionary<NavSize, byte[]>(ReferenceEqualityComparer.Instance);
            foreach (var spec in _specs)
            {
                walkable.TryAdd(spec.Size, new byte[grid.Count]);
            }

            _front = new FlowField[_specs.Length];
            _back = new FlowField[_specs.Length];
            for (var i = 0; i < _specs.Length; i++)
            {
                _front[i] = new FlowField(grid);
                _back[i] = new FlowField(grid);
            }

            var start = Stopwatch.GetTimestamp();
            _source!.Fill(_work, 0, 0, _work.Width - 1, _work.Depth - 1);
            // Only edits that change cells were kept (Initialize), and with them there is a scanner.
            foreach (ref readonly var edit in _startEdits.AsSpan())
            {
                _scanner!.Stamp(_work, in edit);
            }

            // The main thread reads nothing until the first fields are published.
            grid.CopyRows(_work, 0, grid.Depth - 1);
            _fillMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            while (!_stopping)
            {
                BuildFields(_work, builder, walkable);
                _wake.WaitOne();
                if (_stopping)
                {
                    break;
                }

                if (_refilling)
                {
                    var refill = Stopwatch.GetTimestamp();
                    _source.Fill(_work, 0, 0, _work.Width - 1, _work.Depth - 1);
                    _fillMs = Stopwatch.GetElapsedTime(refill).TotalMilliseconds;
                }
                else if (_moving)
                {
                    FillStrips();
                }
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            LogFailed(ex);
        }
    }

    /// <summary>On the thread: fills the strips a move of the window brought in, and stamps the edits that reach them.</summary>
    private void FillStrips()
    {
        var start = Stopwatch.GetTimestamp();
        var work = _work!;
        for (var i = 0; i < _stripCount; i++)
        {
            var strip = _strips[i];
            _source!.Fill(work, strip.FirstColumn, strip.FirstRow, strip.LastColumn, strip.LastRow);
            foreach (ref readonly var edit in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_stripEdits))
            {
                _scanner?.Stamp(
                    work,
                    in edit,
                    null,
                    strip.FirstColumn,
                    strip.FirstRow,
                    strip.LastColumn,
                    strip.LastRow
                );
            }
        }

        _fillMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void BuildFields(NavGrid grid, FlowFieldBuilder builder, Dictionary<NavSize, byte[]> walkable)
    {
        var start = Stopwatch.GetTimestamp();
        // Only the changed rows and as far round them as each size's circle reaches (working the whole grid out
        // again for every size took about 175 ms of a 1.3 s build in Release); every row after a
        // fill.
        var (changedFirst, changedLast) = (_jobFirstRow, _jobLastRow);
        foreach (var (size, cells) in walkable)
        {
            var whole = changedFirst < 0;
            size.Derive(
                grid,
                cells,
                whole ? 0 : Math.Max(0, changedFirst - size.ReachCells),
                whole ? grid.Depth - 1 : Math.Min(grid.Depth - 1, changedLast + size.ReachCells)
            );
        }

        var longest = 0d;
        for (var i = 0; i < _specs.Length; i++)
        {
            if (_stopping)
            {
                return;
            }

            var fieldStart = Stopwatch.GetTimestamp();
            builder.Build(grid, _specs[i], walkable[_specs[i].Size], _back[i]);
            longest = Math.Max(longest, Stopwatch.GetElapsedTime(fieldStart).TotalMilliseconds);
        }

        _timings = new NavigationTimings(_fillMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds, longest);
        _published = true;
    }

    /// <summary>A rectangle of cells in the window, inclusive: one strip a move of the window brings in.</summary>
    private readonly record struct Strip(int FirstColumn, int FirstRow, int LastColumn, int LastRow);

    /// <summary>
    /// The cells an edit's bounds cover, with one more on each side for the rounding of cell centres, as absolute cell
    /// coordinates (centimetres over <see cref="NavGrid.CellCentimetres"/>), so they hold across moves of the window.
    /// </summary>
    private readonly record struct StampedEdit(int FirstColumn, int FirstRow, int LastColumn, int LastRow)
    {
        public static StampedEdit Of(in VoxelEdit edit)
        {
            edit.GetBounds(out var min, out var end);
            return new StampedEdit(
                NavGrid.FloorDivide(min.X, NavGrid.CellCentimetres) - 1,
                NavGrid.FloorDivide(min.Z, NavGrid.CellCentimetres) - 1,
                NavGrid.FloorDivide(end.X - 1, NavGrid.CellCentimetres) + 1,
                NavGrid.FloorDivide(end.Z - 1, NavGrid.CellCentimetres) + 1
            );
        }

        /// <summary>The cells in <paramref name="grid"/>'s window; false when none is.</summary>
        public bool TryClip(NavGrid grid, out int firstColumn, out int firstRow, out int lastColumn, out int lastRow)
        {
            var x = grid.OriginX / NavGrid.CellCentimetres;
            var z = grid.OriginZ / NavGrid.CellCentimetres;
            firstColumn = Math.Max(0, FirstColumn - x);
            firstRow = Math.Max(0, FirstRow - z);
            lastColumn = Math.Min(grid.Width - 1, LastColumn - x);
            lastRow = Math.Min(grid.Depth - 1, LastRow - z);
            return firstColumn <= lastColumn && firstRow <= lastRow;
        }

        /// <summary>True when the cells meet <paramref name="strip"/> of <paramref name="grid"/>'s window.</summary>
        public bool Meets(NavGrid grid, in Strip strip) =>
            TryClip(grid, out var firstColumn, out var firstRow, out var lastColumn, out var lastRow)
            && firstColumn <= strip.LastColumn
            && lastColumn >= strip.FirstColumn
            && firstRow <= strip.LastRow
            && lastRow >= strip.FirstRow;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Navigation: off; {Reason}")]
    private partial void LogOff(string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Navigation: grid {Width}x{Depth} ({Cell} cm) from the layout in {FillMs:F0} ms with {Stamped} edit(s) stamped; {Fields} field(s) in {FieldsMs:F0} ms (longest {LongestMs:F0} ms)"
    )]
    private partial void LogReady(
        int width,
        int depth,
        int cell,
        double fillMs,
        int stamped,
        int fields,
        double fieldsMs,
        double longestMs
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Navigation: fields built again in {FieldsMs:F0} ms (longest {LongestMs:F0} ms) after {Edits} edit(s), {Rows} row(s) copied"
    )]
    private partial void LogRecomputed(double fieldsMs, double longestMs, int edits, int rows);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Navigation: window moved to its corner at {X:F1},{Z:F1} m; {Edits} edit(s) found for the new strips in {GatherMs:F1} ms, the strips filled and stamped in {FillMs:F0} ms; {Fields} field(s) in {FieldsMs:F0} ms"
    )]
    private partial void LogMoved(
        double x,
        double z,
        int edits,
        double gatherMs,
        double fillMs,
        int fields,
        double fieldsMs
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Navigation: grid filled again from the layout after a reset in {FillMs:F0} ms; {Fields} field(s) in {FieldsMs:F0} ms"
    )]
    private partial void LogRefilled(double fillMs, int fields, double fieldsMs);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Navigation: the navigation thread failed; walkers have no routes"
    )]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Navigation: the navigation thread did not stop within {Seconds} s"
    )]
    private partial void LogStuck(double seconds);
}

/// <summary>
/// The square navigation may cover: its corner (absolute centimetres, a whole number of cells from the absolute 0) and
/// how many cells it is across.
/// </summary>
public readonly record struct NavArea(int MinX, int MinZ, int Cells)
{
    /// <summary>The square of whole cells that holds <paramref name="grid"/>'s map.</summary>
    public static NavArea Of(VoxelGrid grid)
    {
        long min = grid.ToCentimetres(grid.MinVoxelXZ);
        long end = grid.ToCentimetres(grid.EndVoxelXZ);
        var first = (int)Math.Floor(min / (double)NavGrid.CellCentimetres);
        var last = (int)Math.Ceiling(end / (double)NavGrid.CellCentimetres);
        return new NavArea(first * NavGrid.CellCentimetres, first * NavGrid.CellCentimetres, last - first);
    }
}

/// <summary>
/// How long the navigation's work took, in milliseconds: filling the grid at start (with the edits stamped then), or the
/// strips a move of the window brought in, and, for the thread's last job, building every field (with the walkable
/// cells of each size) and the longest single field.
/// </summary>
public readonly record struct NavigationTimings(
    double FillMilliseconds,
    double FieldsMilliseconds,
    double LongestFieldMilliseconds
);
