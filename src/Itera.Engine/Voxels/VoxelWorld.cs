using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Itera.Engine.Matches;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Voxels;

/// <summary>
/// The voxel world: the seed, the edit log, and the columns resident around the streaming centres. Worker threads
/// generate columns (<see cref="VoxelWorkers"/>); this class installs them, unloads far ones, applies edits, and
/// records every change for consumers such as the renderer. Call everything from the main thread. Chunks that are not
/// resident are not stored anywhere: they are rebuilt from the seed and their edits when they are wanted again.
/// Several levels of detail are streamed at once: level k has voxels 2^k times the grid's (<see cref="Grid"/>) and
/// reaches 2^k times the level-0 radius, so each level holds about the same number of columns, and every level holds a full disc.
/// </summary>
public sealed partial class VoxelWorld : IDisposable
{
    private const int ChangeLogCapacity = 1 << 16;

    /// <summary>
    /// The most bricks of one chunk an edit's change is recorded brick by brick for; past it, one
    /// <see cref="VoxelChangeKind.ChunkChanged"/> stands for them all. A 64th of a chunk's 4096 bricks: on a 1 cm
    /// grid a 50 cm dig touches up to about 900 bricks at level 0 over a few chunks, a 4 m sphere about 65,000, which alone would overrun
    /// the change list (<see cref="ChangeLogCapacity"/>) and make the renderer and physics reload the world.
    /// </summary>
    public const int ChunkChangeBricks = 64;

    /// <summary>
    /// The largest box of level-0 voxels an edit may cover and still be applied to the resident columns at once; a larger
    /// one (on a 1 cm grid a 4 m sphere or an explosion; at 10 cm a large building) is logged at once but applied column by column over the following fixed
    /// steps (<see cref="ApplyPendingEdits"/>), since at once it took about 20 ms. Counted in level-0 voxels: 128³, a
    /// chunk's worth (a 1.28 m cube at 1 cm, 12.8 m at 10 cm), larger than the tools' 50 cm dig and place.
    /// </summary>
    public const long DeferVoxels = 128L * 128 * 128;
    private const int JobsPerWorker = 4;

    /// <summary>Half the diagonal of a column, in columns: how much nearer than its centre a column's nearest part is.</summary>
    private const double HalfDiagonal = 0.70710678118654752;

    /// <summary>
    /// Load priorities come in three groups, each a thousand wide (a priority within a group stays below 1000): the
    /// coarsest level first, then columns that rays can reach now, then the ring beyond reach.
    /// </summary>
    private const float ReachableGroup = 1000;

    private const float RingGroup = 2000;

    /// <summary>
    /// After the budget is reached, loading resumes once this share of the budget has been freed. Used memory at the
    /// cap is often well below the budget, because each pool takes whole slabs, so the level to resume at is counted
    /// from where use stood when the cap was reached.
    /// </summary>
    private const double ResumeFreedFraction = 0.05;

    /// <summary>A memory-full episode that starts sooner than this after the last Warning is logged at Debug.</summary>
    private const double WarningIntervalSeconds = 60;
    private static readonly TimeSpan WorkerStopTimeout = TimeSpan.FromSeconds(2);

    private readonly VoxelSettings _settings;
    private readonly LevelDistances _distances;
    private readonly int _levels;
    private readonly int[] _residentByLevel;
    private readonly double _statsIntervalSeconds;
    private readonly IChunkGenerator _generator;
    private readonly ILogger<VoxelWorld> _logger;
    private readonly EditLog _edits;
    private readonly VoxelChangeLog _changes = new(ChangeLogCapacity);
    private readonly Dictionary<ColumnKey, Column> _columns = [];
    private readonly WantedLevel[] _wanted;
    private readonly Stack<Column> _columnPool = new();
    private readonly Stack<ColumnJob> _jobPool = new();
    private readonly List<VoxelEdit> _replay = [];
    private readonly List<ColumnKey> _pendingEdits = [];

    // One delegate, made once: List.Sort with an IComparer makes a new one each call.
    private readonly Comparison<ColumnKey> _pendingOrder;
    private bool _pendingUnsorted;
    private Task? _warmUp;
    private readonly WarmUps? _warmUps;

    /// <summary>Rounds of the edit warm-up run back to back (<see cref="StartEditWarmUp"/>); later ones are spaced out.</summary>
    private const int WarmUpRounds = 40;

    /// <summary>The warm-up's rounds that measure the first-pass code (after the first, which compiles).</summary>
    private const int WarmUpFirstPassRounds = 5;

    /// <summary>The pause between the warm-up's later rounds.</summary>
    private const int WarmUpPauseMilliseconds = 25;

    /// <summary>The longest the warm-up runs.</summary>
    private static readonly TimeSpan WarmUpLimit = TimeSpan.FromSeconds(10);

    private readonly int[] _changedBricks = new int[VoxelConstants.BricksPerChunk];
    private byte[]? _pieceScratch;

    // The piece unpacked in _pieceScratch, or 0 when the scratch holds something else.
    private long _unpackedPiece;
    private readonly Column[] _unloadBuffer = new Column[VoxelConstants.MaxColumnsUnloadedPerUpdate];
    private StreamingCentre[] _centres = new StreamingCentre[4];
    private StreamingCentre[] _scratchCentres = new StreamingCentre[4];
    private Vector3[] _centrePositions = new Vector3[4];
    private int _centreCount;
    private bool _unloadScanPending;
    private bool _memoryFull;
    private int _maxInFlight;
    private int _pending;
    private int _mixedChunks;
    private long _resumeBytes;
    private long _capacityBytes;
    private long _failedJobs;
    private double _now;
    private double _lastWarningSeconds = double.NegativeInfinity;
    private bool _episodeWarned;
    private VoxelMemory? _memory;
    private VoxelWorkers? _workers;
    private ulong _seed;
    private bool _followsHost;

    /// <summary>True once a full edit log's refusal is logged, until the next reset.</summary>
    private bool _logFullLogged;
    private volatile bool _disposed;
    private volatile bool _memoryLeft;
    private IntervalStats _stats;
    private double _statsStart = double.NaN;

    public VoxelWorld(
        IOptions<EngineOptions> options,
        IChunkGenerator generator,
        ILogger<VoxelWorld> logger,
        WarmUps? warmUps = null
    )
        : this(options.Value.Voxels, options.Value.Logging.StatsIntervalSeconds, generator, logger, warmUps) { }

    /// <param name="settings">Streaming radius, memory budget, worker threads, and seed.</param>
    /// <param name="statsIntervalSeconds">Seconds between stats lines; zero or less disables them.</param>
    /// <param name="generator">Builds chunks from the seed.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="warmUps">What the run has warmed up already; null to warm up the edit code every time.</param>
    internal VoxelWorld(
        VoxelSettings settings,
        double statsIntervalSeconds,
        IChunkGenerator generator,
        ILogger<VoxelWorld> logger,
        WarmUps? warmUps = null
    )
    {
        _warmUps = warmUps;
        _settings = settings;
        _levels = LevelsFor(settings);
        Grid = generator.Grid;
        _distances = LevelDistances.From(settings, Grid);
        _edits = new EditLog(_levels, Grid);
        _residentByLevel = new int[_levels];
        _wanted = new WantedLevel[_levels];
        for (var level = 0; level < _levels; level++)
        {
            _wanted[level] = new WantedLevel();
        }

        _statsIntervalSeconds = statsIntervalSeconds;
        _pendingOrder = new PendingOrder(this).Compare;
        _generator = generator;
        _logger = logger;
        _seed = settings.Seed;
    }

    /// <summary>
    /// The world seed: the one <see cref="Initialize(ulong)"/> was given (in a session, the host's), or the configured
    /// one before that.
    /// </summary>
    public ulong Seed => _seed;

    /// <summary>
    /// True after <see cref="FollowHost"/>: this world is a session client's copy of the host's, and takes edits only
    /// from the host (<see cref="ApplySequenced"/> and <see cref="ApplyHostLift"/>).
    /// </summary>
    public bool FollowsHost => _followsHost;

    /// <summary>The generator that builds the world.</summary>
    public IChunkGenerator Generator => _generator;

    /// <summary>Edits applied so far; also the sequence of the newest one.</summary>
    public long EditCount => _edits.Count;

    /// <summary>
    /// The newest edit sequence that is in the voxels of every resident column it reaches: <see cref="EditCount"/> while no
    /// large edit waits (<see cref="ApplyPendingEdits"/>), else less. A reader that must see an edit in the voxels (the
    /// navigation's rescans) reads edits up to this.
    /// </summary>
    public long AppliedEditCount
    {
        get
        {
            var applied = _edits.Count;
            foreach (var key in _pendingEdits)
            {
                if (_columns.TryGetValue(key, out var column) && column.EditsPending)
                {
                    applied = Math.Min(applied, column.AppliedEdit);
                }
            }

            return applied;
        }
    }

    /// <summary>
    /// The sequence of the newest reset (<see cref="Reset"/>, or one from the host or a save), or 0 when there is none.
    /// Readers that keep something built from the voxels (the navigation grid, the loading screen) watch it.
    /// </summary>
    public long LastReset => _edits.LastReset;

    /// <summary>Resident columns with a large edit still to apply.</summary>
    public int PendingEditColumns => _pendingEdits.Count;

    /// <summary>The edit with sequence <paramref name="sequence"/>, from 1 to <see cref="EditCount"/>.</summary>
    public ref readonly VoxelEdit GetEdit(long sequence) => ref _edits[sequence];

    /// <summary>Columns whose voxels can be read.</summary>
    public int ResidentColumns => _columns.Count - _pending;

    /// <summary>Columns being generated.</summary>
    public int PendingColumns => _pending;

    /// <summary>Bytes of voxel storage in use.</summary>
    public long UsedBytes => _memory?.UsedBytes ?? 0;

    /// <summary>True while streaming has stopped because memory reached the budget.</summary>
    public bool IsMemoryFull => _memoryFull;

    /// <summary>
    /// True once a streaming centre is set and every column it wants, at every level, is resident: none still to queue
    /// and none being generated. False while memory is full, since loading has stopped then. As of the last
    /// <see cref="Update"/>.
    /// </summary>
    public bool WantedColumnsResident
    {
        get
        {
            if (_centreCount == 0 || _pending > 0 || _memoryFull)
            {
                return false;
            }

            for (var level = 0; level < _levels; level++)
            {
                var wanted = _wanted[level];
                if (wanted.Dirty || wanted.Cursor < wanted.Count)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// About how much of what the streaming centres want is resident, 0 to 1: the wanted columns queued so far, less
    /// those still being generated, over all wanted. 0 before a centre is set.
    /// </summary>
    public float WantedFraction
    {
        get
        {
            long queued = 0;
            long total = 0;
            for (var level = 0; level < _levels; level++)
            {
                var wanted = _wanted[level];
                if (!wanted.Dirty)
                {
                    queued += wanted.Cursor;
                    total += wanted.Count;
                }
            }

            return _centreCount == 0 || total == 0 ? 0 : Math.Clamp((queued - _pending) / (float)total, 0, 1);
        }
    }

    /// <summary>True once the edit warm-up (<see cref="StartEditWarmUp"/>) has ended, or when none was started.</summary>
    public bool EditWarmUpDone => _warmUp is null || _warmUp.IsCompleted;

    /// <summary>Levels of detail streamed; level k has voxels of <see cref="Grid"/>'s edge at k.</summary>
    public int Levels => _levels;

    /// <summary>
    /// The size of the world's voxels, its generator's (<see cref="IChunkGenerator.Grid"/>): the edge of a level-k voxel
    /// in centimetres (the unit of <see cref="VoxelCoord"/>) is <c>Grid.Edge(k)</c>. Models have voxels of their own size
    /// (<see cref="VoxelModels.VoxelMetres"/>), which code that meets both must not take for this.
    /// </summary>
    public VoxelGrid Grid { get; }

    /// <summary>Resident columns at <paramref name="level"/>.</summary>
    public int ResidentColumnsAt(int level) => _residentByLevel[level];

    /// <summary>Horizontal distance from the view within which level-<paramref name="level"/> columns load, in metres.</summary>
    public double RadiusMetres(int level) => _distances.RadiusMetres(level);

    /// <summary>How far the view draws and loads each level.</summary>
    public LevelDistances Distances => _distances;

    /// <summary>
    /// Horizontal distance from <paramref name="centre"/> within which its level-<paramref name="level"/> columns load,
    /// in metres; 0 for a level it does not stream.
    /// </summary>
    public double RadiusMetres(in StreamingCentre centre, int level) =>
        RadiusCentimetres(in centre, level) / (double)VoxelConstants.CentimetresPerMetre;

    /// <summary>
    /// Native voxel memory every world of the process holds now: 0 once each has been disposed, unless one was left to
    /// the OS because a thread did not stop.
    /// </summary>
    public static long LiveMemoryBytes => VoxelMemory.LiveReservedBytes;

    /// <summary>The levels a world of <paramref name="settings"/> keeps: <c>Engine:Voxels:Levels</c>, within 1 to the most.</summary>
    public static int LevelsFor(VoxelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Math.Clamp(settings.Levels, 1, VoxelConstants.MaxLevel + 1);
    }

    /// <summary>
    /// Horizontal distance from the view within which the coarsest level's columns of a world of
    /// <paramref name="settings"/> load, in metres: the view distance, which no grid changes.
    /// </summary>
    public static double ViewRadiusMetres(VoxelSettings settings) =>
        LevelDistances.From(settings, VoxelGrid.Unit).ViewRadiusCentimetres
        / (double)VoxelConstants.CentimetresPerMetre;

    /// <summary>The streaming centres last passed to <see cref="SetStreamingCentres"/>, in metres.</summary>
    public ReadOnlySpan<Vector3> Centers => _centrePositions.AsSpan(0, _centreCount);

    /// <summary>The streaming centres last passed to <see cref="SetStreamingCentres"/>, with their radii and levels.</summary>
    public ReadOnlySpan<StreamingCentre> StreamingCentres => _centres.AsSpan(0, _centreCount);

    private VoxelMemory Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _memory ?? throw new InvalidOperationException("The voxel world is not initialised.");
        }
    }

    private VoxelWorkers Workers
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _workers ?? throw new InvalidOperationException("The voxel world is not initialised.");
        }
    }

    /// <summary>Takes the memory and starts the worker threads, with the configured seed.</summary>
    public void Initialize() => Initialize(_settings.Seed);

    /// <summary>
    /// Takes the memory and starts the worker threads, for the world of <paramref name="seed"/>: in a session, the
    /// host's, which a client learns only after connecting.
    /// </summary>
    public void Initialize(ulong seed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_memory is not null)
        {
            throw new InvalidOperationException("The voxel world is already initialised.");
        }

        _seed = seed;
        var threads =
            _settings.WorkerThreads > 0 ? _settings.WorkerThreads : Math.Max(1, Environment.ProcessorCount / 2);
        _memory = new VoxelMemory((long)_settings.MemoryBudgetMegabytes << 20);
        _workers = new VoxelWorkers(_generator, _memory, threads);
        _maxInFlight = threads * JobsPerWorker;

        // Room for one centre's columns at every level, so the first streaming pass does not grow these: as many as the
        // level that reaches the most of its own columns (each reaches the same number when every level's radius is
        // twice the one before). A column stays while its nearest point is within the radius and one column, so a
        // disc of the radius and two columns holds every column kept.
        var radiusInColumns = 0.0;
        for (var level = 0; level < _levels; level++)
        {
            var columns = _distances.RadiusCentimetres(level) / (double)Grid.ColumnCentimetres(level) + 2;
            radiusInColumns = Math.Max(radiusInColumns, columns);
        }

        var perLevel = (int)(Math.PI * radiusInColumns * radiusInColumns) + 16;
        _columns.EnsureCapacity(_levels * perLevel);
        foreach (var wanted in _wanted)
        {
            wanted.EnsureCapacity(perLevel);
        }

        // And jobs for every load in flight and a free of every column kept: a move unloads columns faster than slow
        // workers free them, and each waiting free holds a job.
        for (var job = _maxInFlight + _levels * perLevel; job > 0; job--)
        {
            _jobPool.Push(new ColumnJob());
        }

        _workers.Start();
        var reach = RadiusMetres(_levels - 1);
        var radius = RadiusMetres(0);
        LogInitialized(
            threads,
            _settings.MemoryBudgetMegabytes,
            radius,
            Grid.BaseCentimetres,
            _levels,
            reach,
            _seed,
            _generator.Id,
            _generator.Version
        );
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var reaches = DescribeDistances(static (d, k) => d.ReachMetres(k));
            var radii = DescribeDistances(static (d, k) => d.RadiusMetres(k));
            LogLevelDistances(reaches, radii);
        }
    }

    /// <summary>Each level's distance from <paramref name="metres"/>, as "16.00, 24.00, ..." for the log.</summary>
    private string DescribeDistances(Func<LevelDistances, int, double> metres)
    {
        var text = new System.Text.StringBuilder();
        for (var level = 0; level < _levels; level++)
        {
            text.Append(level == 0 ? "" : ", ")
                .Append(metres(_distances, level).ToString("F2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// Sets the positions, in metres, that columns are loaded around: players, cameras, or anything the host must
    /// simulate near. A level's wanted columns are recomputed when a centre has moved half of that level's column since
    /// they were last computed (64 cm at level 0 on a 1 cm grid, 6.4 m at 10 cm), or when the number of centres changes.
    /// </summary>
    public void SetCenters(ReadOnlySpan<Vector3> centres)
    {
        EnsureCentreRoom(centres.Length);
        for (var i = 0; i < centres.Length; i++)
        {
            _scratchCentres[i] = new StreamingCentre(centres[i], RadiusMetres(0), _levels, View: true);
        }

        SetStreamingCentres(_scratchCentres.AsSpan(0, centres.Length));
    }

    /// <summary>
    /// Sets the streaming centres with a level-0 radius and a level count each: a session host streams only a small
    /// level-0 disc around a remote player or a loose body, and the full view around its own player. A level's wanted
    /// columns are recomputed as in the other overload, and also when a centre's radius or levels change.
    /// </summary>
    public void SetStreamingCentres(ReadOnlySpan<StreamingCentre> centres)
    {
        EnsureCentreRoom(centres.Length);
        var changed = centres.Length != _centreCount;
        for (var i = 0; i < centres.Length && !changed; i++)
        {
            changed =
                centres[i].Level0RadiusMetres != _centres[i].Level0RadiusMetres
                || centres[i].Levels != _centres[i].Levels
                || centres[i].View != _centres[i].View;
        }

        centres.CopyTo(_centres);
        for (var i = 0; i < centres.Length; i++)
        {
            _centrePositions[i] = centres[i].Position;
        }

        _centreCount = centres.Length;
        for (var level = 0; level < _levels; level++)
        {
            var wanted = _wanted[level];
            // A level already waiting for its rebuild is not checked again: its RebuiltAt has an entry only for the
            // centres it was last built around, which may be fewer than there are now.
            if (!wanted.Dirty && (changed || HasMovedHalfAColumn(wanted, level)))
            {
                wanted.Dirty = true;
            }
        }
    }

    private void EnsureCentreRoom(int count)
    {
        if (count > _centres.Length)
        {
            var size = Math.Max(count, _centres.Length * 2);
            Array.Resize(ref _centres, size);
            Array.Resize(ref _centrePositions, size);
            Array.Resize(ref _scratchCentres, size);
        }
    }

    /// <summary>
    /// Installs finished columns, unloads far ones, queues new ones, and logs the stats line when due. Call once per
    /// main-loop iteration, before the fixed steps. Rethrows an exception that ended a worker.
    /// </summary>
    /// <param name="nowSeconds">Clock time now, for the stats interval and for spacing memory warnings.</param>
    public void Update(double nowSeconds)
    {
        var start = Stopwatch.GetTimestamp();
        var workers = Workers;
        workers.ThrowIfFaulted();
        _now = nowSeconds;
        InstallFinished(workers);
        var installed = Stopwatch.GetTimestamp();

        for (var level = 0; level < _levels; level++)
        {
            if (_wanted[level].Dirty)
            {
                RebuildWanted(level);
                _unloadScanPending = true;
            }
        }

        var rebuilt = Stopwatch.GetTimestamp();
        if (_unloadScanPending)
        {
            UnloadFarColumns();
        }

        var unloaded = Stopwatch.GetTimestamp();

        // Resuming as soon as one column is unloaded would start jobs that mostly fail again; wait for real room.
        if (_memoryFull && Memory.UsedBytes <= _resumeBytes)
        {
            SetMemoryFull(false);
        }

        QueueLoads(workers);
        var end = Stopwatch.GetTimestamp();
        _stats.MaxUpdateTicks = Math.Max(_stats.MaxUpdateTicks, end - start);
        _stats.MaxInstallTicks = Math.Max(_stats.MaxInstallTicks, installed - start);
        _stats.MaxRebuildTicks = Math.Max(_stats.MaxRebuildTicks, rebuilt - installed);
        _stats.MaxUnloadTicks = Math.Max(_stats.MaxUnloadTicks, unloaded - rebuilt);
        _stats.MaxQueueTicks = Math.Max(_stats.MaxQueueTicks, end - unloaded);
        UpdateStats(nowSeconds);
    }

    /// <summary>
    /// Reads the material of the level-0 voxel that holds the point <paramref name="point"/> (centimetres). Returns
    /// false, with air, when its column is not resident. Voxels above or below the world are air.
    /// </summary>
    public bool TryGetMaterial(VoxelCoord point, out byte material) => TryGetMaterial(point, 0, out material);

    /// <summary>
    /// Reads the material of the level-<paramref name="level"/> voxel that holds the point <paramref name="point"/>
    /// (centimetres). Returns false, with air, when that column is not resident. For tests of coarse levels.
    /// </summary>
    internal bool TryGetMaterial(VoxelCoord point, int level, out byte material)
    {
        var voxel = Grid.ToVoxel(point);
        if (
            !_columns.TryGetValue(ColumnKey.Containing(voxel, level), out var column)
            || column.State != ColumnState.Resident
        )
        {
            material = VoxelConstants.Air;
            return false;
        }

        material = column.ReadVoxel(Memory, voxel);
        return true;
    }

    /// <summary>
    /// Reads the density (<see cref="VoxelDensity"/>: how much of it is solid, 0 to 255) of the level-<paramref name="level"/>
    /// voxel that holds the point <paramref name="point"/> (centimetres). Returns false, with empty, when that column is
    /// not resident. Voxels above or below the world are empty.
    /// </summary>
    public bool TryGetDensity(VoxelCoord point, int level, out byte density)
    {
        var voxel = Grid.ToVoxel(point);
        if (
            !_columns.TryGetValue(ColumnKey.Containing(voxel, level), out var column)
            || column.State != ColumnState.Resident
        )
        {
            density = VoxelDensity.Empty;
            return false;
        }

        density = column.ReadDensity(Memory, voxel);
        return true;
    }

    /// <summary>True when the column's voxels can be read.</summary>
    public bool IsResident(ColumnKey column) =>
        _columns.TryGetValue(column, out var found) && found.State == ColumnState.Resident;

    /// <summary>A cursor that sees every change from now on. See <see cref="ReadChanges"/>.</summary>
    public VoxelChangeCursor CreateChangeCursor() => _changes.CreateCursor();

    /// <summary>
    /// Copies the cursor's unread changes into <paramref name="buffer"/> and returns how many; call again while it
    /// fills the buffer. Check <see cref="VoxelChangeCursor.NeedsResync"/> after a read that returns 0.
    /// </summary>
    public int ReadChanges(VoxelChangeCursor cursor, Span<VoxelChange> buffer) => _changes.Read(cursor, buffer);

    /// <summary>
    /// Keeps <see cref="Dispose"/> from freeing the voxel memory: a thread outside the world that writes into it (a
    /// character build, <see cref="VoxelModels.Encode(ReadOnlySpan{byte})"/>) did not stop in time, so the memory is left
    /// to the OS. Any thread.
    /// </summary>
    public void LeaveMemoryAtExit()
    {
        _memoryLeft = true;
    }

    /// <summary>
    /// Stops the workers and the edit warm-up, then frees the voxel memory. When one of them does not stop in time, or
    /// <see cref="LeaveMemoryAtExit"/> was called, the memory is left to the OS, because something may still be writing
    /// into it. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // The warm-up uses the voxel memory; it stops at its next round once _disposed is set, and never faults.
        if (_warmUp is not null && !_warmUp.Wait(WorkerStopTimeout))
        {
            LogWarmUpStuck(WorkerStopTimeout.TotalSeconds);
            _memoryLeft = true;
        }

        if (_workers is not null && !_workers.Stop(WorkerStopTimeout))
        {
            LogWorkersStuck(WorkerStopTimeout.TotalSeconds);
            return;
        }

        if (!_memoryLeft)
        {
            _memory?.Dispose();
        }
    }

    /// <summary>
    /// Changes recorded so far (columns loaded or unloaded, brick cells changed). While it stays the same, the resident
    /// voxels are the same.
    /// </summary>
    internal long ChangeCount => _changes.Written;

    /// <summary>The voxel memory, for reading resident columns (the GPU upload). Main thread.</summary>
    internal VoxelMemory ReadMemory => Memory;

    /// <summary>
    /// The chunk states of a resident column, bottom layer first, or false when it is not resident. Valid until the
    /// next <see cref="Update"/>, <see cref="Apply"/>, or <see cref="ApplySequenced"/>. Main thread.
    /// </summary>
    internal bool TryGetResidentLayers(ColumnKey key, out ReadOnlySpan<ChildState> layers)
    {
        if (TryGetResident(key, out var column))
        {
            layers = column.Layers;
            return true;
        }

        layers = default;
        return false;
    }

    /// <summary>Adds the key of every resident column to <paramref name="keys"/>. For a resync; allocates as the list grows.</summary>
    internal void CollectResidentColumns(List<ColumnKey> keys)
    {
        foreach (var column in _columns.Values)
        {
            if (column.State == ColumnState.Resident)
            {
                keys.Add(column.Key);
            }
        }
    }

    /// <summary>Unloaded columns whose memory a worker has not freed yet. For tests.</summary>
    internal int PendingFrees => _workers?.FreesInFlight ?? 0;

    /// <summary>
    /// Generation jobs queued or running, not yet taken back: a column unloaded while its job runs is no longer
    /// pending, but its job holds memory until the worker finishes it (tests).
    /// </summary>
    internal int JobsInFlight => _workers?.InFlight ?? 0;

    /// <summary>Jobs that ran out of memory since the world started. For tests.</summary>
    internal long FailedJobCount => _failedJobs;

    /// <summary>Mixed chunks in resident columns, as the running count has them. For tests.</summary>
    internal int MixedChunkCount => _mixedChunks;

    /// <summary>Stored bricks, including those of columns being generated or waiting to be freed. For tests.</summary>
    internal int StoredBricks => Memory.LiveBlocks(VoxelPool.Bricks);

    /// <summary>Counts mixed chunks and stored bricks by walking every resident column. For tests.</summary>
    internal (int MixedChunks, int Bricks) CountResident()
    {
        int mixedChunks = 0,
            nodes = 0,
            bricks = 0;
        foreach (var column in _columns.Values)
        {
            if (column.State == ColumnState.Resident)
            {
                column.Count(Memory, ref mixedChunks, ref nodes, ref bricks);
            }
        }

        return (mixedChunks, bricks);
    }

    private void InstallFinished(VoxelWorkers workers)
    {
        var installed = 0;
        while (installed < VoxelConstants.MaxColumnsInstalledPerUpdate && workers.TryTakeFinished(out var job))
        {
            if (job.IsFree)
            {
                _jobPool.Push(job);
                continue;
            }

            Column? column = null;
            var wanted = !job.IsCancelled && _columns.TryGetValue(job.Key, out column) && column.Job == job;
            if (!wanted || job.Result != ColumnJobResult.Done)
            {
                job.FreeLayers(Memory);
                if (job.Result == ColumnJobResult.OutOfMemory)
                {
                    _stats.FailedJobs++;
                    _failedJobs++;
                }

                if (wanted && job.Result == ColumnJobResult.OutOfMemory)
                {
                    // Dropped for now; it is queued again once used memory falls below the resume level.
                    _columns.Remove(job.Key);
                    _columnPool.Push(column!);
                    _pending--;
                    SetMemoryFull(true);
                }

                _jobPool.Push(job);
                continue;
            }

            Install(column!, job);
            _jobPool.Push(job);
            installed++;
        }
    }

    private void Install(Column column, ColumnJob job)
    {
        job.Layers.CopyTo(column.Layers);
        job.Layers.Clear();
        _mixedChunks += CountMixed(column.Layers);
        column.State = ColumnState.Resident;
        column.Job = null;
        _pending--;
        _residentByLevel[column.Key.Level]++;
        _changes.Add(new VoxelChange(VoxelChangeKind.ColumnLoaded, column.Key, -1, -1));

        _replay.Clear();
        _edits.CollectForColumn(column.Key, job.SnapshotSequence, _replay);
        foreach (var edit in _replay)
        {
            ApplyToColumn(column, in edit);
        }

        column.AppliedEdit = _edits.Count;

        _stats.Loaded++;
        _stats.JobTicks += job.ElapsedTicks;
        _stats.MaxJobTicks = Math.Max(_stats.MaxJobTicks, job.ElapsedTicks);
    }

    /// <summary>Recomputes a level's wanted columns around the current centres and sorts them by priority.</summary>
    private void RebuildWanted(int level)
    {
        var wanted = _wanted[level];
        wanted.Clear(_centreCount);
        for (var i = 0; i < _centreCount; i++)
        {
            var centre = VoxelCoord.FromMetres(_centres[i].Position);
            wanted.RebuiltAt[i] = centre;
            var radius = CentreRadiusCentimetres(i, level);
            if (radius > 0)
            {
                AddWanted(wanted, centre, level, radius);
            }
        }

        wanted.Sort();
    }

    private void AddWanted(WantedLevel wanted, VoxelCoord centre, int level, long radius)
    {
        var radiusSquared = radius * radius;
        var reach = Math.Max(0, radius - Grid.ReachMarginCentimetres(level));
        var coarsest = level == _levels - 1;
        var minX = Math.Max(Grid.MinChunkXZAt(level), Grid.ColumnAt(centre.X - radius, level));
        var maxX = Math.Min(Grid.MaxChunkXZAt(level), Grid.ColumnAt(centre.X + radius, level));
        var minZ = Math.Max(Grid.MinChunkXZAt(level), Grid.ColumnAt(centre.Z - radius, level));
        var maxZ = Math.Min(Grid.MaxChunkXZAt(level), Grid.ColumnAt(centre.Z + radius, level));
        for (var z = minZ; z <= maxZ; z++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                // Every column the disc meets, so a small disc in a large column still loads the one it stands in.
                var key = new ColumnKey(level, x, z);
                if (NearestSquared(key, centre) <= radiusSquared && wanted.Set.Add(key))
                {
                    var distance = Math.Sqrt(HorizontalDistanceSquared(key, centre));
                    wanted.Add(key, Priority(Grid, distance, level, reach, coarsest));
                }
            }
        }
    }

    /// <summary>
    /// The load priority of a level-<paramref name="level"/> column whose centre is <paramref name="distance"/> level-0
    /// voxels from a streaming centre; lower loads first. Three groups, in this order:
    /// <list type="number">
    /// <item>The coarsest level's columns, nearest first. The coarsest level is drawn to the view distance, so a
    /// missing column is a hole in the picture.</item>
    /// <item>Columns that rays can reach now (their nearest part is within the level's reach), nearest first in the
    /// level's own columns, and at equal distance the coarser level first. A missing one is drawn from a coarser level.
    /// A column's parent at the next coarser level is half as many of its own columns away, so it comes first.</item>
    /// <item>The ring beyond reach, by metres beyond reach, so the ring the centre will need soonest comes first. While
    /// loading keeps up, only this group has columns missing, and level 0's ring is the nearest.</item>
    /// </list>
    /// When loading falls behind (flying fast), this keeps the far view filled and refines detail near the centre,
    /// instead of shrinking every level to a small disc around the centre.
    /// </summary>
    /// <param name="grid">The world's grid, whose columns the distances are counted in.</param>
    /// <param name="distance">Horizontal distance from the centre to the column's centre, in centimetres.</param>
    /// <param name="level">The column's level.</param>
    /// <param name="reach">The level's reach (<see cref="VoxelGrid.ReachCentimetres"/>).</param>
    /// <param name="coarsest">True for the coarsest level streamed.</param>
    internal static float Priority(VoxelGrid grid, double distance, int level, long reach, bool coarsest)
    {
        var columnCentimetres = (double)grid.ColumnCentimetres(level);
        if (coarsest)
        {
            return (float)(distance / columnCentimetres);
        }

        var nearest = distance - HalfDiagonal * columnCentimetres;
        return nearest <= reach
            ? (float)(ReachableGroup + distance / columnCentimetres - level * 1e-3)
            : (float)(RingGroup + (nearest - reach) / VoxelConstants.CentimetresPerMetre);
    }

    /// <summary>
    /// The load priority (<see cref="Priority"/>) of a column against the current centres, the lowest over all of
    /// them; lower is more urgent. The GPU upload takes waiting columns in this order. Main thread.
    /// </summary>
    internal float PriorityOf(ColumnKey key)
    {
        var coarsest = key.Level == _levels - 1;
        var best = float.MaxValue;
        for (var i = 0; i < _centreCount; i++)
        {
            var radius = CentreRadiusCentimetres(i, key.Level);
            if (radius <= 0)
            {
                continue;
            }

            var reach = Math.Max(0, radius - Grid.ReachMarginCentimetres(key.Level));
            var distance = Math.Sqrt(HorizontalDistanceSquared(key, VoxelCoord.FromMetres(_centres[i].Position)));
            best = Math.Min(best, Priority(Grid, distance, key.Level, reach, coarsest));
        }

        return best;
    }

    /// <summary>True when a centre has moved half of a level's column, horizontally, since its wanted columns were built.</summary>
    private bool HasMovedHalfAColumn(WantedLevel wanted, int level)
    {
        var half = Grid.ColumnCentimetres(level) / 2;
        for (var i = 0; i < _centreCount; i++)
        {
            var centre = VoxelCoord.FromMetres(_centres[i].Position);
            long dx = centre.X - wanted.RebuiltAt[i].X;
            long dz = centre.Z - wanted.RebuiltAt[i].Z;
            if (dx * dx + dz * dz >= half * half)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The level whose next wanted column that is not loaded or loading has the lowest priority, or -1 when every level
    /// has queued all of its own. Moves each level's cursor past columns that are already there.
    /// </summary>
    private int NextWantedLevel()
    {
        var best = -1;
        var bestPriority = float.MaxValue;
        for (var level = 0; level < _levels; level++)
        {
            var wanted = _wanted[level];
            while (wanted.Cursor < wanted.Count && _columns.ContainsKey(wanted.Keys[wanted.Cursor]))
            {
                wanted.Cursor++;
            }

            if (wanted.Cursor < wanted.Count && wanted.Priorities[wanted.Cursor] < bestPriority)
            {
                best = level;
                bestPriority = wanted.Priorities[wanted.Cursor];
            }
        }

        return best;
    }

    /// <summary>Centre <paramref name="index"/>'s radius at a level in centimetres, or 0 when it streams no such level.</summary>
    private long CentreRadiusCentimetres(int index, int level) => RadiusCentimetres(in _centres[index], level);

    /// <summary>
    /// A centre's radius at <paramref name="level"/> in centimetres: the view's from <see cref="Distances"/>, another
    /// centre's level-0 radius times 2^level; 0 for a level it does not stream.
    /// </summary>
    private long RadiusCentimetres(in StreamingCentre centre, int level)
    {
        if (level >= centre.Levels)
        {
            return 0;
        }

        return centre.View
            ? _distances.RadiusCentimetres(level)
            : (long)(centre.Level0RadiusMetres * VoxelConstants.CentimetresPerMetre) << level;
    }

    private void UnloadFarColumns()
    {
        var count = 0;
        foreach (var column in _columns.Values)
        {
            if (!IsNearAnyCentre(column.Key))
            {
                _unloadBuffer[count++] = column;
                if (count == _unloadBuffer.Length)
                {
                    break;
                }
            }
        }

        // A full buffer may have left far columns behind; look again next iteration.
        _unloadScanPending = count == _unloadBuffer.Length;
        for (var i = 0; i < count; i++)
        {
            Unload(_unloadBuffer[i]);
            _unloadBuffer[i] = null!;
        }
    }

    /// <summary>
    /// Unloads every column after a reset: waiting large edits are dropped with them, and each wanted list starts again
    /// from its nearest column, so streaming generates the view again, with the edits after the reset. Allocates one list.
    /// </summary>
    private void UnloadAll(long sequence)
    {
        _pendingEdits.Clear();
        _pendingUnsorted = false;
        var columns = new List<Column>(_columns.Values);
        foreach (var column in columns)
        {
            Unload(column);
        }

        foreach (var wanted in _wanted)
        {
            wanted.Cursor = 0;
        }

        LogReset(sequence, columns.Count);
    }

    private void Unload(Column column)
    {
        _columns.Remove(column.Key);
        if (column.State == ColumnState.Pending)
        {
            // The worker drops it; the job comes back through InstallFinished, which frees what it made.
            column.Job?.Cancel();
            _pending--;
        }
        else
        {
            // Walking the trees to free them costs about 0.3 ms per column, so a worker does it.
            _mixedChunks -= CountMixed(column.Layers);
            _residentByLevel[column.Key.Level]--;
            var job = _jobPool.Count > 0 ? _jobPool.Pop() : new ColumnJob();
            job.ResetForFree(column.Key, column.Layers);
            column.Layers.Clear();
            Workers.Enqueue(job);
            _changes.Add(new VoxelChange(VoxelChangeKind.ColumnUnloaded, column.Key, -1, -1));
            _stats.Unloaded++;
        }

        _columnPool.Push(column);
    }

    private void QueueLoads(VoxelWorkers workers)
    {
        if (_memoryFull)
        {
            return;
        }

        // Once memory has run out, the use at that moment is the practical capacity: pools take whole slabs, so it
        // sits below the budget. Queue only as many jobs as the room below it can hold, at the average column size;
        // otherwise most jobs after a resume build a whole column and then fail.
        var room = long.MaxValue;
        long columnBytes = 0;
        if (_capacityBytes > 0)
        {
            var used = Memory.UsedBytes;
            if (used < _capacityBytes / 2)
            {
                // After a large unload (a teleport, or a walk away) forget the learned capacity: it came from one
                // moment's mix of block sizes, and the next fill learns it again.
                _capacityBytes = 0;
            }
            else
            {
                columnBytes = Math.Max(1, used / Math.Max(1, ResidentColumns));
                room = _capacityBytes - used - workers.InFlight * columnBytes;
            }
        }

        while (workers.InFlight < _maxInFlight && room >= columnBytes)
        {
            var level = NextWantedLevel();
            if (level < 0)
            {
                break;
            }

            var wanted = _wanted[level];
            var key = wanted.Keys[wanted.Cursor++];
            var column = _columnPool.Count > 0 ? _columnPool.Pop() : new Column();
            column.Reset(key, Grid);
            var job = _jobPool.Count > 0 ? _jobPool.Pop() : new ColumnJob();
            job.Reset(key, Grid, _seed, _edits.Count);
            _edits.CollectForColumn(key, 0, job.Edits);
            _edits.CollectPieces(job.Edits, job.Pieces);
            column.Job = job;
            _columns.Add(key, column);
            _pending++;
            workers.Enqueue(job);
            room -= columnBytes;
        }
    }

    private void SetMemoryFull(bool full)
    {
        if (full == _memoryFull)
        {
            return;
        }

        _memoryFull = full;
        if (full)
        {
            var used = Memory.UsedBytes;
            _capacityBytes = used;
            _resumeBytes = Math.Max(0, used - (long)(Memory.BudgetBytes * ResumeFreedFraction));

            // Walking at the cap starts an episode every few seconds; one Warning a minute is enough.
            _episodeWarned = _now - _lastWarningSeconds >= WarningIntervalSeconds;
            if (_episodeWarned)
            {
                _lastWarningSeconds = _now;
            }

            var usedMegabytes = used / (1024.0 * 1024.0);
            var resumeMegabytes = _resumeBytes / (1024.0 * 1024.0);
            LogMemoryFull(
                _episodeWarned ? LogLevel.Warning : LogLevel.Debug,
                _settings.MemoryBudgetMegabytes,
                usedMegabytes,
                ResidentColumns,
                resumeMegabytes
            );
        }
        else
        {
            // Columns skipped while memory was full are still wanted; walk the lists again from the first.
            foreach (var wanted in _wanted)
            {
                wanted.Cursor = 0;
            }

            var resumeMegabytes = _resumeBytes / (1024.0 * 1024.0);
            LogLoadingResumed(_episodeWarned ? LogLevel.Information : LogLevel.Debug, resumeMegabytes);
        }
    }

    private static int CountMixed(ReadOnlySpan<ChildState> layers)
    {
        var count = 0;
        foreach (var layer in layers)
        {
            count += layer.Kind == ChildKind.Handle ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// True when a centre that streams the column's level is within its radius plus one column of that level of the
    /// column's nearest point: kept that far, a centre that wobbles does not reload it.
    /// </summary>
    private bool IsNearAnyCentre(ColumnKey key)
    {
        for (var i = 0; i < _centreCount; i++)
        {
            var radius = CentreRadiusCentimetres(i, key.Level);
            if (radius <= 0)
            {
                continue;
            }

            var keep = radius + Grid.ColumnCentimetres(key.Level);
            if (NearestSquared(key, VoxelCoord.FromMetres(_centres[i].Position)) <= keep * keep)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The squared horizontal distance in centimetres from <paramref name="centre"/> (centimetres) to the column's
    /// nearest point: 0 inside it.
    /// </summary>
    private long NearestSquared(ColumnKey key, VoxelCoord centre)
    {
        var size = Grid.ColumnCentimetres(key.Level);
        var dx = Outside(centre.X, key.X * size, size);
        var dz = Outside(centre.Z, key.Z * size, size);
        return dx * dx + dz * dz;
    }

    /// <summary>
    /// How far the centimetre <paramref name="point"/> lies from the nearest centimetre of [<paramref name="low"/>, low +
    /// <paramref name="size"/>).
    /// </summary>
    private static long Outside(long point, long low, long size) =>
        point < low ? low - point
        : point >= low + size ? point - (low + size - 1)
        : 0;

    /// <summary>The squared horizontal distance in centimetres from <paramref name="centre"/> (centimetres) to the column's centre line.</summary>
    private long HorizontalDistanceSquared(ColumnKey key, VoxelCoord centre)
    {
        var dx = (long)Grid.ToCentimetres(key.CentreVoxelX) - centre.X;
        var dz = (long)Grid.ToCentimetres(key.CentreVoxelZ) - centre.Z;
        return dx * dx + dz * dz;
    }

    private void UpdateStats(double nowSeconds)
    {
        if (_statsIntervalSeconds <= 0)
        {
            return;
        }

        if (double.IsNaN(_statsStart))
        {
            _statsStart = nowSeconds;
            return;
        }

        var elapsed = nowSeconds - _statsStart;
        if (elapsed < _statsIntervalSeconds)
        {
            return;
        }

        var bricks = Memory.LiveBlocks(VoxelPool.Bricks);
        var megabyte = 1024.0 * 1024.0;
        var usedMegabytes = Memory.UsedBytes / megabyte;
        var reservedMegabytes = Memory.ReservedBytes / megabyte;
        var averageJobMs = _stats.Loaded == 0 ? 0 : _stats.JobTicks * 1000.0 / Stopwatch.Frequency / _stats.Loaded;
        var maxJobMs = _stats.MaxJobTicks * 1000.0 / Stopwatch.Frequency;
        var ticksPerMs = Stopwatch.Frequency / 1000.0;
        var maxUpdateMs = _stats.MaxUpdateTicks / ticksPerMs;
        var maxInstallMs = _stats.MaxInstallTicks / ticksPerMs;
        var maxRebuildMs = _stats.MaxRebuildTicks / ticksPerMs;
        var maxUnloadMs = _stats.MaxUnloadTicks / ticksPerMs;
        var maxQueueMs = _stats.MaxQueueTicks / ticksPerMs;
        var maxDeferredMs = _stats.MaxDeferredTicks / ticksPerMs;
        LogStats(
            elapsed,
            ResidentColumns,
            PendingColumns,
            _mixedChunks,
            bricks,
            usedMegabytes,
            reservedMegabytes,
            _settings.MemoryBudgetMegabytes,
            _stats.Loaded,
            _stats.Unloaded,
            _stats.FailedJobs,
            averageJobMs,
            maxJobMs,
            maxUpdateMs,
            maxInstallMs,
            maxRebuildMs,
            maxUnloadMs,
            maxQueueMs,
            _stats.Edits,
            _stats.DeferredColumns,
            _pendingEdits.Count,
            maxDeferredMs
        );
        if (_levels > 1 && _logger.IsEnabled(LogLevel.Debug))
        {
            // A few short strings per stats interval, only when Debug is on.
            var pairs = new string[_levels];
            for (var level = 0; level < _levels; level++)
            {
                pairs[level] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_residentByLevel[level]}/{_wanted[level].Count}"
                );
            }

            var perLevel = string.Join(", ", pairs);
            LogLevels(perLevel);
        }

        _stats = default;
        _statsStart = nowSeconds;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Voxels: the view draws its levels to {Reaches} m (the coarsest to the view's end) and loads them to {Radii} m"
    )]
    private partial void LogLevelDistances(string reaches, string radii);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Voxels: {Threads} worker thread(s), budget {BudgetMegabytes} MB, radius {RadiusMetres} m at {BaseCentimetres} cm, {Levels} level(s) reaching {ReachMetres} m, seed {Seed}, generator {GeneratorId} v{GeneratorVersion}"
    )]
    private partial void LogInitialized(
        int threads,
        int budgetMegabytes,
        double radiusMetres,
        int baseCentimetres,
        int levels,
        double reachMetres,
        ulong seed,
        string generatorId,
        int generatorVersion
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Voxel stats over {Seconds:F1} s: {Resident} columns resident, {Pending} pending, {MixedChunks} mixed chunks, {Bricks} bricks stored (including columns being generated or freed), {UsedMegabytes:F1} MB used ({ReservedMegabytes:F1} MB reserved) of {BudgetMegabytes} MB, loaded {Loaded}, unloaded {Unloaded}, {FailedJobs} jobs out of memory, column jobs avg {AverageJobMs:F2} ms max {MaxJobMs:F2} ms, update max {MaxUpdateMs:F2} ms (install {MaxInstallMs:F2}, rebuild {MaxRebuildMs:F2}, unload {MaxUnloadMs:F2}, queue {MaxQueueMs:F2}), {Edits} edits; large edits applied to {DeferredColumns} column(s) over steps, {WaitingColumns} waiting, longest step {MaxDeferredMs:F2} ms"
    )]
    private partial void LogStats(
        double seconds,
        int resident,
        int pending,
        int mixedChunks,
        int bricks,
        double usedMegabytes,
        double reservedMegabytes,
        int budgetMegabytes,
        int loaded,
        int unloaded,
        int failedJobs,
        double averageJobMs,
        double maxJobMs,
        double maxUpdateMs,
        double maxInstallMs,
        double maxRebuildMs,
        double maxUnloadMs,
        double maxQueueMs,
        int edits,
        int deferredColumns,
        int waitingColumns,
        double maxDeferredMs
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Voxel columns resident per level against wanted, finest first: {PerLevel}"
    )]
    private partial void LogLevels(string perLevel);

    [LoggerMessage(
        Message = "Voxel memory reached its budget of {BudgetMegabytes} MB ({UsedMegabytes:F1} MB in use, {Resident} columns resident); streaming stops loading until use falls to {ResumeMegabytes:F1} MB"
    )]
    private partial void LogMemoryFull(
        LogLevel level,
        int budgetMegabytes,
        double usedMegabytes,
        int resident,
        double resumeMegabytes
    );

    [LoggerMessage(Message = "Voxel memory use fell to {ResumeMegabytes:F1} MB; streaming loads again")]
    private partial void LogLoadingResumed(LogLevel level, double resumeMegabytes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Voxel workers did not stop within {Seconds} s; their memory is left to the OS"
    )]
    private partial void LogWorkersStuck(double seconds);

    /// <summary>
    /// One level's wanted columns, sorted by <see cref="Priority"/> (lowest first), how far the queue has got through
    /// them, and where each centre was when they were built.
    /// </summary>
    private sealed class WantedLevel
    {
        public readonly HashSet<ColumnKey> Set = [];
        public ColumnKey[] Keys = new ColumnKey[64];
        public float[] Priorities = new float[64];
        public VoxelCoord[] RebuiltAt = new VoxelCoord[4];
        public int Count;
        public int Cursor;
        public bool Dirty = true;

        public void EnsureCapacity(int columns)
        {
            Set.EnsureCapacity(columns);
            if (Keys.Length < columns)
            {
                Array.Resize(ref Keys, columns);
                Array.Resize(ref Priorities, columns);
            }
        }

        /// <summary>Empties the list for a rebuild around <paramref name="centres"/> centres.</summary>
        public void Clear(int centres)
        {
            Set.Clear();
            Count = 0;
            Cursor = 0;
            Dirty = false;
            if (RebuiltAt.Length < centres)
            {
                Array.Resize(ref RebuiltAt, centres);
            }
        }

        public void Add(ColumnKey key, float priority)
        {
            if (Count == Keys.Length)
            {
                EnsureCapacity(Count * 2);
            }

            Keys[Count] = key;
            Priorities[Count] = priority;
            Count++;
        }

        /// <summary>Sorts by priority, moving the columns with their priorities; this needs no comparison delegate.</summary>
        public void Sort() => Array.Sort(Priorities, Keys, 0, Count);
    }

    private struct IntervalStats
    {
        public int Loaded;
        public int Unloaded;
        public int Edits;
        public int FailedJobs;
        public long JobTicks;
        public long MaxJobTicks;
        public long MaxUpdateTicks;
        public long MaxInstallTicks;
        public long MaxRebuildTicks;
        public long MaxUnloadTicks;
        public long MaxQueueTicks;
        public int DeferredColumns;
        public long MaxDeferredTicks;
    }
}
