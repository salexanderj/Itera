using System.Diagnostics;
using Itera.Engine.Networking;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Navigation;

/// <summary>
/// The navigation grid and the flow fields over it On a host or offline game with an
/// <see cref="INavGridSource"/>, <see cref="Initialize"/> starts a thread of its own that fills a grid from the world
/// as generated, stamps the edits made so far (a save's, whose voxels are not loaded) onto it from their shapes, and
/// builds every field the source asks for; <see cref="AfterFixedStep"/>, once a fixed step on the main thread, takes
/// finished fields in, and from then on <see cref="Ready"/> is true.
/// <para>
/// After that, each fixed step reads the world's new edits. Cells under an edit whose level-0 voxels are loaded (most
/// live edits on a host: tools reach 16 m, and remote players and loose bodies stream discs around them; rounds and
/// rockets dig at any distance) are read
/// again from the voxels (<see cref="NavGridScanner.Rescan"/>, at most <see cref="RescansPerStep"/> a step); cells under
/// one whose voxels are not are stamped from its shape and read again once a disc covers them (the stamped edits are
/// checked in turn, <see cref="StampChecksPerStep"/> a step, and a queued cell whose voxels unloaded before its read
/// waits among them). An edit that covers no cell's centre column changes nothing and is passed over
/// (<see cref="NavGridScanner.CoversAnyCentre"/>). Changed rows are copied
/// to the thread's own grid, and the thread builds every field again from it (all of it: the
/// whole build is measured before a partial one; the cells each size can stand in are worked out again only in the changed rows and
/// as far round them as its circle reaches), at most every <see cref="JobIntervalSeconds"/>, while walkers keep the
/// fields they have until the new ones are swapped in whole. The main thread's grid follows edits at once.
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

    // The rows the thread's next job changes, set before it is woken; -1 for every row (the first build, a refill).
    private int _jobFirstRow = -1;
    private int _jobLastRow = -1;
    private int _editsSinceJob;
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _published;
    private volatile bool _failed;
    private volatile bool _refilling;
    private bool _refillWanted;
    private double _fillMs;
    private NavigationTimings _timings;
    private NavigationTimings _takenTimings;
    private bool _disposed;

    public NavigationService(
        SessionService session,
        VoxelWorld voxels,
        IVoxelPassability passability,
        IEnumerable<INavGridSource> sources,
        ILogger<NavigationService> logger
    )
        : this(sources.FirstOrDefault(), () => session.IsAuthority, logger, voxels, passability) { }

    /// <param name="source">The game's grid and fields, or null for no navigation.</param>
    /// <param name="isAuthority">Whether this game moves walkers (a host or offline); asked in <see cref="Initialize"/>.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="voxels">The world whose edits change the grid; null to follow no edits.</param>
    /// <param name="passability">The materials walkers pass through; with <paramref name="voxels"/>.</param>
    internal NavigationService(
        INavGridSource? source,
        Func<bool> isAuthority,
        ILogger<NavigationService> logger,
        VoxelWorld? voxels = null,
        IVoxelPassability? passability = null
    )
    {
        _source = source;
        _isAuthority = isAuthority;
        _logger = logger;
        _voxels = voxels;
        _passability = passability;
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

    /// <summary>True while cells changed that no job has taken yet, or a job runs.</summary>
    public bool Recomputing => _busy || _dirtyFirstRow <= _dirtyLastRow;

    /// <summary>The shortest time between the starts of two jobs, so a burst of edits makes one job.</summary>
    internal double JobIntervalSeconds { get; set; } = 0.5;

    /// <summary>The field <paramref name="index"/> of <see cref="Fields"/>; valid once <see cref="Ready"/>.</summary>
    public FlowField Field(int index) =>
        Ready ? _front[index] : throw new InvalidOperationException("The navigation fields are not ready.");

    /// <summary>
    /// The start-up step: on a host or offline game with a source, makes the grid and the fields' specs (on this
    /// thread, so the game's world layout is built here), takes the edits made so far, and starts the navigation
    /// thread, which fills the grid, stamps those edits, and builds the fields.
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

        _grid = _source.CreateGrid();
        _specs = [.. _source.CreateFields(_grid)];
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
                    _stamped.Add(StampedEdit.Of(_grid, in edit));
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
    /// edits into the grid, and starts a job when cells changed and the thread is free.
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

        if (!Ready || _scanner is null)
        {
            return;
        }

        ReadEdits();
        if (_refillWanted && !_busy)
        {
            StartRefill();
        }

        Rescan();
        CheckStamped();
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
            if (Ready && !_busy && !_published && !Refilling && _queueCount == 0 && _dirtyFirstRow > _dirtyLastRow)
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
            var cells = StampedEdit.Of(grid, in edit);
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
    /// covers no cell's centre column.
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
                var (column, row) = (grid.Column(cell), grid.Row(cell));
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
    /// <see cref="StampChecksPerStep"/> a step, in turn round the list.
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
            if (Resident(in cells))
            {
                // The last entry takes its place, and is checked next.
                Queue(in cells);
                _stamped[_stampCursor] = _stamped[^1];
                _stamped.RemoveAt(_stamped.Count - 1);
            }
            else
            {
                _stampCursor++;
            }
        }
    }

    private bool Resident(in StampedEdit cells)
    {
        var grid = _grid!;
        var voxels = _voxels!.Grid;
        return _voxels.ColumnsResident(
            voxels.ToVoxel(grid.CentreX(cells.FirstColumn)),
            voxels.ToVoxel(grid.CentreZ(cells.FirstRow)),
            voxels.ToVoxel(grid.CentreX(cells.LastColumn)),
            voxels.ToVoxel(grid.CentreZ(cells.LastRow))
        );
    }

    private void Queue(in StampedEdit cells)
    {
        var grid = _grid!;
        for (var row = cells.FirstRow; row <= cells.LastRow; row++)
        {
            for (var column = cells.FirstColumn; column <= cells.LastColumn; column++)
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
            _source!.Fill(_work);
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
                if (_refilling && !_stopping)
                {
                    var refill = Stopwatch.GetTimestamp();
                    _source.Fill(_work);
                    _fillMs = Stopwatch.GetElapsedTime(refill).TotalMilliseconds;
                }
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            LogFailed(ex);
        }
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

    /// <summary>The cells an edit's bounds cover, with one more on each side for the rounding of cell centres.</summary>
    private readonly record struct StampedEdit(int FirstColumn, int FirstRow, int LastColumn, int LastRow)
    {
        public static StampedEdit Of(NavGrid grid, in VoxelEdit edit)
        {
            edit.GetBounds(out var min, out var end);
            return new StampedEdit(
                Math.Max(0, Column(grid, min.X) - 1),
                Math.Max(0, Row(grid, min.Z) - 1),
                Math.Min(grid.Width - 1, Column(grid, end.X - 1) + 1),
                Math.Min(grid.Depth - 1, Row(grid, end.Z - 1) + 1)
            );
        }

        private static int Column(NavGrid grid, int x) =>
            Math.Clamp((int)Math.Floor((x - grid.OriginX) / (double)NavGrid.CellCentimetres), 0, grid.Width - 1);

        private static int Row(NavGrid grid, int z) =>
            Math.Clamp((int)Math.Floor((z - grid.OriginZ) / (double)NavGrid.CellCentimetres), 0, grid.Depth - 1);
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
/// How long the navigation's work took, in milliseconds: filling the grid at start (with the edits stamped then), and,
/// for the thread's last job, building every field (with the walkable cells of each size) and the longest single field.
/// </summary>
public readonly record struct NavigationTimings(
    double FillMilliseconds,
    double FieldsMilliseconds,
    double LongestFieldMilliseconds
);
