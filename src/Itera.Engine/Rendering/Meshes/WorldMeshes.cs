using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Materials;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The world on the GPU, as triangle meshes (docs/rendering-redesign.md).
/// It meshes the resident columns of the meshed levels (<c>Engine:Renderer:MeshFinestLevel</c> to the coarsest) block by
/// block: a block is a 32-voxel cell of a chunk, read with its apron on the main thread
/// (<see cref="VoxelWorld.TryReadBlock(int, int, int, int, Span{byte}, Span{byte})"/>) and meshed by <see cref="MeshWorkers"/> (<see cref="SurfaceNets"/>). Each
/// frame, <see cref="Upload"/> follows the world's change list (columns loaded and unloaded, bricks and chunks changed),
/// starts jobs nearest the view first within a time budget, uploads finished meshes through the frame slot's staging
/// region into <see cref="MeshPass"/>'s heap, and writes the frame's draws (WorldMeshes.Draws.cs). It meshes the models
/// too (WorldMeshes.Models.cs) and counts each as uploaded once its meshes are in (cuts, loose bodies, and loading tasks
/// wait for that), prepares the palette at start, and removes retired models once the world's meshes hold
/// their voxels. Main thread; allocates nothing per frame once its pools have grown.
/// </summary>
public sealed unsafe partial class WorldMeshes : IWorldOnGpu, IDisposable
{
    /// <summary>Copy regions one frame may record.</summary>
    private const int MaxRegionsPerFrame = 8192;

    /// <summary>
    /// After this long in one frame, counted from the upload's start, no new block is read; the rest waits for the next
    /// frame. The sky field's read takes up to half of it in the frames it runs (<see cref="SkyReadMilliseconds"/>).
    /// </summary>
    private const double ReadMillisecondsPerFrame = 2;

    /// <summary>Jobs that may be with the workers or wait to upload at once: the pool's size.</summary>
    private const int MostJobs = 256;

    /// <summary>Stitch jobs that may be with the workers or wait to upload at once; each holds two blocks' samples.</summary>
    private const int MostStitchJobs = 64;

    /// <summary>Blocks that found no room in a full heap are meshed again at most this often, once ranges were freed.</summary>
    private const double StarvedRetrySeconds = 1;

    /// <summary>A full heap's eviction frees this much more than the mesh that found no room needs (16 MB), in words.</summary>
    private const int EvictMarginWords = 4 << 20;

    /// <summary>Most a frame's evictions free (128 MB), in words; past it, meshes that find no room wait.</summary>
    private const long MostEvictedWordsPerFrame = 32L << 20;

    private readonly IVoxelPalette _palette;

    /// <summary>The materials' texture sets (<see cref="MeshPass.TextureSets"/>); none in tests that give none.</summary>
    private readonly MaterialTextureSets? _textures;
    private readonly IMeshDrawTarget _pass;
    private readonly MeshView _view;
    private readonly IStagingRegions _staging;
    private readonly ILogger<WorldMeshes> _logger;
    private LevelDistances _distances;

    // The match's world's grid: the size of its voxels at each level (VoxelWorld.Grid).
    private VoxelGrid _grid = VoxelGrid.Unit;
    private readonly int _finestLevel;
    private readonly int _coarsestLevel;
    private readonly double _reachShare;
    private readonly double _statsIntervalSeconds;
    private readonly StagingWriter _writer = new();
    private readonly BufferCopy[][] _regions = new BufferCopy[FrameSync.FramesInFlight][];
    private readonly VoxelChange[] _changes = new VoxelChange[1024];
    private readonly Dictionary<ColumnKey, MeshColumn> _columns = [];
    private readonly List<MeshColumn> _order = [];
    private readonly List<MeshColumn>[] _byLevel = new List<MeshColumn>[VoxelConstants.MaxLevel + 1];
    private readonly Stack<MeshJob> _freeJobs = new();
    private readonly Stack<MeshJob> _freeStitchJobs = new();
    private readonly Queue<MeshJob> _waiting = new();
    private readonly List<ColumnKey> _resident = [];
    private MeshMaterial[] _materials = [];
    private VoxelWorld? _world;
    private VoxelModels? _models;
    private IVoxelTintMap? _tintMap;
    private VoxelChangeCursor? _cursor;
    private GpuRangeHeap? _heap;
    private MeshWorkers? _workers;
    private bool _worldDataStaged;
    private bool _orderStale;
    private Vector3 _orderedAt;
    private Vector3 _wantedAt;
    private bool _wantedStale;
    private double _viewDistanceCentimetres;
    private int _pendingBlocks;
    private bool _heapWasFull;

    // Blocks whose finished meshes, plain or stitched, found no room in the heap: meshed again once ranges have been
    // freed (RetryStarved).
    private readonly List<(MeshColumn Column, int Index, bool Stitch)> _starved = [];
    private bool _heapFreed;
    private double _starvedRetryAt;
    private long _evictedWordsThisFrame;
    private bool _nothingToEvict;

    // The edits' latency: when the first edit's change since the last report was read, the frames since, the blocks the
    // edits marked, and those of them not yet meshed and staged (MeshBlock.Edited).
    private long _editSince;
    private int _editFrames;
    private int _editMarked;
    private int _editedBlocks;

    // A column's dirty blocks and their squared distances from the view, sorted nearest first in StartJobs.
    private int[] _blockOrder = [];
    private float[] _blockDistances = [];
    private MeshStats _stats;
    private double _statsStart = double.NaN;

    public WorldMeshes(
        IVoxelPalette palette,
        MeshPass pass,
        MeshView view,
        StagingBuffer staging,
        IOptions<EngineOptions> options,
        ILogger<WorldMeshes> logger
    )
        : this(palette, (IMeshDrawTarget)pass, view, (IStagingRegions)staging, options, logger, pass.TextureSets) { }

    /// <summary>The world meshes over a draw target, view, and staging of a test's own.</summary>
    internal WorldMeshes(
        IVoxelPalette palette,
        IMeshDrawTarget pass,
        MeshView view,
        IStagingRegions staging,
        IOptions<EngineOptions> options,
        ILogger<WorldMeshes> logger,
        MaterialTextureSets? textures = null
    )
    {
        _palette = palette;
        _textures = textures;
        _pass = pass;
        _view = view;
        _staging = staging;
        _logger = logger;
        var engine = options.Value;
        _distances = LevelDistances.From(engine.Voxels, VoxelGrid.Unit);
        _coarsestLevel = VoxelWorld.LevelsFor(engine.Voxels) - 1;
        _finestLevel = Math.Min(engine.Renderer.MeshFinestLevel, _coarsestLevel);
        _reachShare = engine.Renderer.MeshReachShare;
        _statsIntervalSeconds = engine.Logging.StatsIntervalSeconds;
        for (var i = 0; i < _byLevel.Length; i++)
        {
            _byLevel[i] = [];
        }
    }

    public bool IsUpToDate { get; private set; }

    public long Uploads { get; private set; }

    /// <summary>What the last <see cref="Upload"/> wrote into the slot's draws, for the frame (<see cref="MeshPass.Record"/>).</summary>
    public MeshFrame Frame { get; private set; }

    /// <summary>Blocks the last <see cref="Upload"/> wrote draws for in the camera's list.</summary>
    public int DrawCount => Frame.Draws;

    /// <summary>The frame's view the world's draws are culled by. For tests.</summary>
    internal MeshView View => _view;

    /// <summary>Bytes of the mesh heap in use. For tests.</summary>
    internal long HeapUsedBytes => _heap?.UsedBytes ?? 0;

    /// <summary>Columns of the meshed levels followed. For tests.</summary>
    internal int ColumnCount => _columns.Count;

    /// <summary>Blocks waiting to be meshed or in flight. For tests.</summary>
    internal int PendingBlocks => _pendingBlocks;

    /// <summary>Blocks an edit marked that wait for a mesh of their voxels since. For tests.</summary>
    internal int EditedBlocks => _editedBlocks;

    /// <summary>The last edits' latency the meshes line reports: milliseconds, frames, and blocks. For tests.</summary>
    internal (double Milliseconds, int Frames, int Blocks) LastEdit { get; private set; }

    /// <summary>
    /// Bytes of the heap the plain meshes of columns the last frame did not draw hold (neither the camera's pass nor the
    /// far cascade's): what eviction can free. For the stats line and tests.
    /// </summary>
    internal long UndrawnBytes()
    {
        long words = 0;
        foreach (var column in _order)
        {
            if (column.DrawnUpload >= Uploads - 1)
            {
                continue;
            }

            foreach (var index in column.Drawn)
            {
                words += column.Blocks[index].RangeWords;
            }
        }

        return words * sizeof(uint);
    }

    /// <summary>Columns whose meshes were freed to make room in the heap since the run began. For tests.</summary>
    internal long Evictions { get; private set; }

    /// <summary>Frees as <see cref="Evict"/> would for a mesh of <paramref name="words"/>, between frames. For tests.</summary>
    internal bool EvictForTests(int words) => Evict(words, null);

    /// <summary>Of those, how many the view needed again and were meshed again. For tests.</summary>
    internal long EvictionsWoken { get; private set; }

    /// <summary>Blocks that found no room in the heap and wait for a range to be freed. For tests.</summary>
    internal int StarvedBlocks => _starved.Count;

    /// <summary>Blocks of the world read for their plain meshes since the run began. For tests.</summary>
    internal long BlocksRead { get; private set; }

    /// <summary>
    /// Prepares the palette and reads every material's look, makes the copy regions, and starts the mesh workers. Once
    /// per run, at the renderer's step, after <see cref="MeshPass.Initialize"/>.
    /// </summary>
    public void Initialize()
    {
        _palette.Prepare();
        _materials = new MeshMaterial[MeshPass.MaterialCount];
        _modelVoxels = new byte[VoxelConstants.ChunkVoxels];
        for (var i = 1; i < _materials.Length; i++)
        {
            var look = _palette.Look((byte)i);
            MaterialTextureSlot? texture = null;
            if (look.Texture is { } name)
            {
                if (_textures?.TryFind(name, out var slot) == true)
                {
                    texture = slot;
                }
                else
                {
                    LogTextureMissing(i, name);
                }
            }

            _materials[i] = MeshMaterial.From(in look, texture);
        }

        for (var i = 0; i < _regions.Length; i++)
        {
            _regions[i] = new BufferCopy[MaxRegionsPerFrame];
        }

        for (var i = 0; i < MostJobs; i++)
        {
            _freeJobs.Push(new MeshJob());
        }

        for (var i = 0; i < MostStitchJobs; i++)
        {
            _freeStitchJobs.Push(new MeshJob(stitches: true));
        }

        _workers = new MeshWorkers(Math.Clamp(Environment.ProcessorCount / 4, 1, 4));
        LogInitialized(_finestLevel, _coarsestLevel, _workers.Count, _pass.HeapBytes >> 20);
    }

    /// <summary>
    /// Starts following a match's world: its columns at the meshed levels, from empty, and its models; the first frame
    /// stages the materials and the match's tint map. Main thread, no Vulkan call.
    /// </summary>
    public void BeginMatch(VoxelWorld world, VoxelModels models, IVoxelTintMap tintMap)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tintMap);
        if (_workers is null)
        {
            throw new InvalidOperationException("The world meshes are not initialised.");
        }

        EndMatch();
        _world = world;
        _grid = world.Grid;
        _distances = world.Distances;
        _models = models;
        _tintMap = tintMap;
        _heap = new GpuRangeHeap((long)(_pass.HeapBytes / sizeof(uint)));
        // The heap's first ranges, lowest first: the materials and the tint map at the places MeshPass binds them.
        if (
            !_heap.TryAllocate((int)(MeshPass.MaterialsBytes / sizeof(uint)), out var materials)
            || materials * sizeof(uint) != MeshPass.MaterialsOffset
            || !_heap.TryAllocate(MeshPass.TintMapWords, out var tint)
            || tint * sizeof(uint) != MeshPass.TintMapOffset
        )
        {
            throw new InvalidOperationException("The mesh heap's first ranges are not where the mesh pass reads them.");
        }

        BeginSky();
        _editSince = 0;
        _editMarked = 0;
        _editedBlocks = 0;
        LastEdit = default;

        _cursor = world.CreateChangeCursor();
        _listFullReported = false;
        _wantedStale = true;
        _worldDataStaged = false;
        _stats = default;
        _statsStart = double.NaN;
        AddResidentColumns();
    }

    /// <summary>Stops following the match; frames upload and draw nothing until the next <see cref="BeginMatch"/>.</summary>
    public void EndMatch()
    {
        EndSky();
        // Jobs still with the workers come back to the pool when they finish.
        while (_waiting.TryDequeue(out var job))
        {
            job.Owner = null;
            job.Model = null;
            Release(job);
        }

        _starved.Clear();
        _heapFreed = false;
        ClearStitchRequests();

        _columns.Clear();
        _order.Clear();
        foreach (var level in _byLevel)
        {
            level.Clear();
        }

        ClearModels();
        _world = null;
        _models = null;
        _tintMap = null;
        _cursor = null;
        _heap = null;
        _pendingBlocks = 0;
        IsUpToDate = false;
        Frame = default;
    }

    /// <summary>
    /// The frame's upload and draws: follows the world's changes, takes the finished meshes and stages them in the
    /// staging region of <paramref name="slot"/>, starts new jobs, and writes the frame's draws for
    /// <paramref name="view"/> into <see cref="MeshPass"/>'s regions of the slot, culled by the frame's
    /// <see cref="MeshView"/> (after its <see cref="MeshView.Begin"/>; the blocks' lights are offered to it before its
    /// <see cref="MeshView.End"/>). Returns the copy regions staged (<see cref="Regions"/>). Main thread, between taking
    /// the frame request and publishing the frame.
    /// </summary>
    public int Upload(int slot, double nowSeconds, in ViewConstants view, ReadOnlySpan<ModelDraw> models = default)
    {
        var world = _world ?? throw new InvalidOperationException("The world meshes follow no match's world.");
        var start = Stopwatch.GetTimestamp();
        _writer.Begin(_staging.Region(slot), _staging.RegionOffset(slot), _staging.RegionBytes, _regions[slot]);
        _evictedWordsThisFrame = 0;
        _nothingToEvict = false;
        ForgetRemovedModels();
        StageWorldData();
        _viewDistanceCentimetres = view.MaxDistance * VoxelConstants.CentimetresPerMetre;
        ReadChanges(world);
        UpdateSky(world, view.Position, nowSeconds);
        UpdateWanted(world, view.Position);
        TakeFinished();
        // Retired models stay drawn until the world's meshes hold their voxels: none pending once the frame's are in.
        if (_pendingBlocks == 0 && _waiting.Count == 0)
        {
            _models!.RemoveRetired();
        }

        var deadline = start + (long)(ReadMillisecondsPerFrame * Stopwatch.Frequency / 1000);
        StartModelJobs(deadline);
        RetryStarved(world, nowSeconds);
        StartJobs(world, view.Position, deadline);
        StartStitchJobs(world, deadline);
        // After the jobs start: a block they find empty or unreadable is done with this frame.
        ReportEditMeshed();
        _staging.Flush(slot, _writer.Used);
        Frame = WriteDraws(slot, in view, models);
        IsUpToDate = _pendingBlocks == 0 && _waiting.Count == 0 && _worldDataStaged;
        Uploads++;
        ReportFault();
        _stats.Frames++;
        _stats.Ticks += Stopwatch.GetTimestamp() - start;
        _stats.MaxTicks = Math.Max(_stats.MaxTicks, Stopwatch.GetTimestamp() - start);
        UpdateStats(nowSeconds);
        return _writer.RegionCount;
    }

    /// <summary>The copy regions <see cref="Upload"/> staged for <paramref name="slot"/>. Render thread.</summary>
    public ReadOnlySpan<BufferCopy> Regions(int slot, int count) => _regions[slot].AsSpan(0, count);

    /// <summary>Stops the mesh workers. After the render thread has stopped. Safe to call more than once.</summary>
    public void Dispose()
    {
        EndMatch();
        _workers?.Dispose();
        _workers = null;
    }

    /// <summary>The names of the texture sets loaded, large then small.</summary>
    public IEnumerable<string> TextureSetNames =>
        _textures is null ? [] : _textures.Large.Concat(_textures.Small).Select(set => set.Name);

    /// <summary>
    /// For the rest of the run, draws <paramref name="material"/> from the texture set <paramref name="name"/>, its colour
    /// matched by <paramref name="match"/> (0 to 1), or from its recipe when <paramref name="name"/> is null (the
    /// console's <c>materials set</c>, for comparing sets in the game). The materials go to the GPU again in the next frame.
    /// False when the material is air or no set of that name was loaded. Main thread.
    /// </summary>
    public bool SetTexture(byte material, string? name, float match)
    {
        if (material == 0 || _materials.Length == 0)
        {
            return false;
        }

        MaterialTextureSlot? texture = null;
        if (name is not null)
        {
            if (_textures?.TryFind(name, out var slot) != true)
            {
                return false;
            }

            texture = slot;
        }

        var look = _palette.Look(material) with { Texture = name, TextureColourMatch = Math.Clamp(match, 0f, 1f) };
        _materials[material] = MeshMaterial.From(in look, texture);
        _worldDataStaged = false;
        return true;
    }

    /// <summary>
    /// For the rest of the run, draws <paramref name="material"/>'s texture set keeping <paramref name="detail"/> of its
    /// colour's variation and <paramref name="relief"/> of its normal map and occlusion (0 to 1), <paramref name="blur"/>
    /// mip levels softer (0 to 4): the console's <c>materials tune</c>, for trying a calmer look. False for air. Main
    /// thread.
    /// </summary>
    public bool SetTextureStyle(byte material, float detail, float relief, float blur)
    {
        if (material == 0 || _materials.Length == 0)
        {
            return false;
        }

        var look = _palette.Look(material) with { TextureDetail = detail, TextureRelief = relief, TextureBlur = blur };
        _materials[material].TextureStyle = MeshMaterial.From(in look).TextureStyle;
        _worldDataStaged = false;
        return true;
    }

    /// <summary>The materials and the match's tint map, in the match's first frame with room for them.</summary>
    private void StageWorldData()
    {
        if (_worldDataStaged)
        {
            return;
        }

        var bytes = MeshPass.MaterialsBytes + MeshPass.TintMapBytes;
        if (!_writer.HasRoom(bytes, 2))
        {
            return;
        }

        var materials = _writer.Write(MeshPass.MaterialsOffset, (int)(MeshPass.MaterialsBytes / sizeof(uint)));
        MemoryMarshal.AsBytes(_materials.AsSpan()).CopyTo(new Span<byte>(materials, (int)MeshPass.MaterialsBytes));
        var tint = TintMapWords(_tintMap!);
        var words = _writer.Write(MeshPass.TintMapOffset, tint.Length);
        tint.AsSpan().CopyTo(new Span<uint>(words, tint.Length));
        _worldDataStaged = true;
    }

    /// <summary>
    /// The tint map as <c>mesh-data.glsl</c> reads it: the header (texels per side, texel shift, corner x and z), then the
    /// texels, four to a word with the first in the lowest byte; <see cref="MeshPass.TintMapWords"/> long.
    /// </summary>
    internal static uint[] TintMapWords(IVoxelTintMap map)
    {
        const int size = IVoxelTintMap.Size;
        const int header = 4;
        var texels = new byte[size * size];
        map.Write(texels);
        var words = new uint[MeshPass.TintMapWords];
        words[0] = size;
        words[1] = (uint)map.TexelShift;
        words[2] = (uint)map.OriginX;
        words[3] = (uint)map.OriginZ;
        for (var i = 0; i < texels.Length; i++)
        {
            words[header + i / 4] |= (uint)texels[i] << (8 * (i % 4));
        }

        return words;
    }

    /// <summary>Follows the world's change list: columns of the meshed levels loaded and unloaded, and edits.</summary>
    private void ReadChanges(VoxelWorld world)
    {
        int read;
        while ((read = world.ReadChanges(_cursor!, _changes)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                ref readonly var change = ref _changes[i];
                // Edits, and coarse columns loaded, change what the sky field would see.
                _skyChanged |=
                    change.Kind is VoxelChangeKind.BrickChanged or VoxelChangeKind.ChunkChanged
                    || (
                        change.Kind == VoxelChangeKind.ColumnLoaded
                        && change.Column.Level == Math.Min(SkyField.LevelFor(_grid), _coarsestLevel)
                    );
                var level = change.Column.Level;
                if (level < _finestLevel || level > _coarsestLevel)
                {
                    continue;
                }

                switch (change.Kind)
                {
                    case VoxelChangeKind.ColumnLoaded:
                        AddColumn(world, change.Column);
                        break;
                    case VoxelChangeKind.ColumnUnloaded:
                        RemoveColumn(change.Column);
                        break;
                    case VoxelChangeKind.BrickChanged:
                        BrickChanged(world, in change);
                        break;
                    case VoxelChangeKind.ChunkChanged:
                        ChunkChanged(world, in change);
                        break;
                }
            }
        }

        if (_cursor!.NeedsResync)
        {
            // Changes were lost: every column is meshed again.
            foreach (var column in _order)
            {
                FreeColumn(column);
            }

            _columns.Clear();
            _order.Clear();
            foreach (var level in _byLevel)
            {
                level.Clear();
            }

            _pendingBlocks = 0;
            _wantedStale = true;
            AddResidentColumns();
            _cursor.EndResync();
            _stats.Resyncs++;
        }
    }

    /// <summary>Adds every column of the meshed levels the world holds now (a match's start, a resync).</summary>
    private void AddResidentColumns()
    {
        var world = _world!;
        _resident.Clear();
        world.CollectResidentColumns(_resident);
        foreach (var key in _resident)
        {
            if (key.Level >= _finestLevel && key.Level <= _coarsestLevel)
            {
                AddColumn(world, key);
            }
        }
    }

    private void AddColumn(VoxelWorld world, ColumnKey key)
    {
        if (_columns.ContainsKey(key))
        {
            return;
        }

        var column = new MeshColumn(key, _grid);
        if (IsWanted(key, _wantedAt, 0))
        {
            Wake(world, column);
        }

        _columns.Add(key, column);
        _order.Add(column);
        _byLevel[key.Level].Add(column);
        _orderStale = true;
        // The level below's stitched meshes read this column's voxels: any made before it unloaded may be of others.
        if (key.Level > _finestLevel)
        {
            for (var i = 0; i < column.Blocks.Length; i++)
            {
                CoarseChanged(column, i);
            }
        }

        // The blocks along the sides of the columns round it read this column's voxels in their aprons: until now they
        // saw their own edge repeated there.
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                // Columns not drawn too: their blocks' versions rise, and with them the stitched meshes of the level below
                // that read them.
                if (
                    (dx != 0 || dz != 0)
                    && _columns.TryGetValue(key with { X = key.X + dx, Z = key.Z + dz }, out var side)
                )
                {
                    MarkFacing(world, side, -dx, -dz);
                }
            }
        }
    }

    /// <summary>Marks the blocks of <paramref name="column"/> along its side towards (dx, dz) as changed.</summary>
    private void MarkFacing(VoxelWorld world, MeshColumn column, int dx, int dz)
    {
        for (var i = 0; i < column.Blocks.Length; i++)
        {
            var cell = i % MeshColumn.BlocksPerChunk;
            var cx = cell & 3;
            var cz = cell >> 4;
            if ((dx == 0 || cx == (dx > 0 ? 3 : 0)) && (dz == 0 || cz == (dz > 0 ? 3 : 0)))
            {
                MarkChanged(world, column, i);
            }
        }
    }

    /// <summary>
    /// True when a column may be drawn from <paramref name="at"/>, give or take <paramref name="slack"/> centimetres:
    /// a column whose nearest part is within the view distance, and for a finer level than the coarsest also within its
    /// parent's split distance (its own level's reach times <c>Engine:Renderer:MeshReachShare</c>) and two of its
    /// columns. Only these are meshed.
    /// </summary>
    private bool IsWanted(ColumnKey key, Vector3 at, double slack)
    {
        double size = _grid.ColumnCentimetres(key.Level);
        var limit = _viewDistanceCentimetres;
        if (key.Level < _coarsestLevel)
        {
            // Its parent, twice its size, splits when the parent's nearest part is within this level's reach.
            limit = Math.Min(limit, _distances.ReachCentimetres(key.Level) * _reachShare + 2 * size);
        }

        limit += slack;
        return NearestSquared(key, at) < limit * limit;
    }

    /// <summary>
    /// When the view has moved a metre (or the match has just begun): columns that came near enough to be drawn are meshed, and those that went more
    /// than a column beyond lose their meshes.
    /// </summary>
    private void UpdateWanted(VoxelWorld world, Vector3 viewPosition)
    {
        if (!_wantedStale && Vector3.DistanceSquared(viewPosition, _wantedAt) < 1)
        {
            return;
        }

        _wantedStale = false;
        _wantedAt = viewPosition;
        foreach (var column in _order)
        {
            var key = column.Key;
            if (!column.Wanted && IsWanted(key, viewPosition, 0))
            {
                Wake(world, column);
            }
            else if (column.Wanted && !IsWanted(key, viewPosition, _grid.ColumnCentimetres(key.Level)))
            {
                Sleep(column);
            }
        }
    }

    /// <summary>The column comes into range: every block that may hold a surface is meshed.</summary>
    private void Wake(VoxelWorld world, MeshColumn column)
    {
        column.Wanted = true;
        column.Evicted = false;
        var key = column.Key;
        for (var i = 0; i < column.Blocks.Length; i++)
        {
            ref var block = ref column.Blocks[i];
            var (x, y, z) = column.BlockAt(i);
            if (block.Dirty || !world.BlockMayHaveSurface(key.Level, x, y, z))
            {
                continue;
            }

            block.Dirty = true;
            if (!block.InFlight)
            {
                column.Pending++;
                _pendingBlocks++;
            }
        }

        column.MeshedOnce = column.Pending == 0;
    }

    /// <summary>
    /// The column goes out of range: its meshes go, its waiting blocks are no longer meshed, and the jobs in flight for
    /// it come back stale (each block's version rises).
    /// </summary>
    private void Sleep(MeshColumn column)
    {
        column.Wanted = false;
        column.Evicted = false;
        DropMeshes(column);
    }

    /// <summary>
    /// Drops every mesh of the column: its waiting blocks are no longer meshed, and the jobs in flight for it come back
    /// stale (each block's version rises).
    /// </summary>
    private void DropMeshes(MeshColumn column)
    {
        column.MeshedOnce = false;
        for (var i = 0; i < column.Blocks.Length; i++)
        {
            ref var block = ref column.Blocks[i];
            block.Version++;
            if (block.Dirty && !block.InFlight)
            {
                column.Pending--;
                _pendingBlocks--;
            }

            block.Dirty = false;
            EditDone(ref block);
            DropMesh(column, ref block, i);
        }
    }

    private void RemoveColumn(ColumnKey key)
    {
        if (!_columns.Remove(key, out var column))
        {
            return;
        }

        FreeColumn(column);
        _pendingBlocks -= column.Pending;
        _order.Remove(column);
        _byLevel[key.Level].Remove(column);
    }

    /// <summary>Frees the heap ranges of a column's meshes, stitched ones too. Jobs in flight for it find no column and are dropped.</summary>
    private void FreeColumn(MeshColumn column)
    {
        for (var i = 0; i < column.Blocks.Length; i++)
        {
            ref var block = ref column.Blocks[i];
            EditDone(ref block);
            if (block.RangeWords > 0)
            {
                FreeRange(block.RangeOffset, block.RangeWords);
                block.RangeWords = 0;
            }
        }

        if (column.Stitches is { } stitches)
        {
            for (var i = 0; i < stitches.Length; i++)
            {
                FreeStitch(ref stitches[i]);
            }
        }
    }

    /// <summary>Gives a range back to the heap; blocks that found no room may try again (<see cref="RetryStarved"/>).</summary>
    private void FreeRange(uint offset, int words)
    {
        _heap!.Free(offset, words);
        _heapFreed = true;
    }

    /// <summary>One brick changed: every block whose voxels or apron hold it is meshed again.</summary>
    private void BrickChanged(VoxelWorld world, in VoxelChange change)
    {
        var chunk = change.Column.Chunk(change.Layer, _grid);
        var x = (chunk.X << VoxelConstants.ChunkShift) + ((change.Brick & 15) << VoxelConstants.BrickShift);
        var y = (chunk.Y << VoxelConstants.ChunkShift) + (((change.Brick >> 4) & 15) << VoxelConstants.BrickShift);
        var z = (chunk.Z << VoxelConstants.ChunkShift) + ((change.Brick >> 8) << VoxelConstants.BrickShift);
        MarkVoxels(world, change.Column.Level, x, y, z, VoxelConstants.BrickSize, edited: true);
    }

    /// <summary>Many bricks of a chunk changed: the blocks of its changed 32-voxel cells and the blocks round them.</summary>
    private void ChunkChanged(VoxelWorld world, in VoxelChange change)
    {
        var chunk = change.Column.Chunk(change.Layer, _grid);
        var blocks = change.Blocks == 0 ? ulong.MaxValue : change.Blocks;
        for (var i = 0; i < MeshColumn.BlocksPerChunk; i++)
        {
            if ((blocks & (1UL << i)) == 0)
            {
                continue;
            }

            var x = (chunk.X << VoxelConstants.ChunkShift) + ((i & 3) << VoxelConstants.CellShift);
            var y = (chunk.Y << VoxelConstants.ChunkShift) + (((i >> 2) & 3) << VoxelConstants.CellShift);
            var z = (chunk.Z << VoxelConstants.ChunkShift) + ((i >> 4) << VoxelConstants.CellShift);
            MarkVoxels(world, change.Column.Level, x, y, z, SurfaceNets.BlockSize, edited: true);
        }
    }

    /// <summary>
    /// Marks every block whose samples (its 32 voxels and 2 more on each side) meet the cube of <paramref name="size"/>
    /// level-k voxels at (x, y, z), as <paramref name="edited"/> by an edit or not.
    /// </summary>
    private void MarkVoxels(VoxelWorld world, int level, int x, int y, int z, int size, bool edited)
    {
        const int Block = SurfaceNets.BlockSize;
        const int Apron = SurfaceNets.Apron;
        var x0 = FloorDiv(x - Block - Apron, Block) + 1;
        var x1 = FloorDiv(x + size + Apron - 1, Block);
        var y0 = FloorDiv(y - Block - Apron, Block) + 1;
        var y1 = FloorDiv(y + size + Apron - 1, Block);
        var z0 = FloorDiv(z - Block - Apron, Block) + 1;
        var z1 = FloorDiv(z + size + Apron - 1, Block);
        for (var bz = z0; bz <= z1; bz++)
        {
            for (var bx = x0; bx <= x1; bx++)
            {
                var key = new ColumnKey(level, bx >> 2, bz >> 2);
                if (!_columns.TryGetValue(key, out var column))
                {
                    continue;
                }

                for (var by = y0; by <= y1; by++)
                {
                    var layer = (by >> 2) - _grid.MinChunkYAt(level);
                    if (layer < 0 || layer >= key.LayerCount(_grid))
                    {
                        continue;
                    }

                    MarkChanged(
                        world,
                        column,
                        layer * MeshColumn.BlocksPerChunk + (bx & 3) + ((by & 3) << 2) + ((bz & 3) << 4),
                        edited
                    );
                }
            }
        }
    }

    /// <summary>
    /// A change may alter the block's mesh: a newer version, meshed again (whether it may hold a surface is checked when
    /// its job starts). The stitched meshes of the level below that read the block go stale. A block of a drawn column
    /// <paramref name="edited"/> by an edit counts towards the edits' latency until a mesh of its voxels since is staged.
    /// </summary>
    private void MarkChanged(VoxelWorld world, MeshColumn column, int index, bool edited = false)
    {
        _ = world;
        ref var block = ref column.Blocks[index];
        block.Version++;
        if (column.Key.Level > _finestLevel)
        {
            CoarseChanged(column, index);
        }

        // An evicted column is meshed again whole when the view needs it (Wake).
        if (!column.Wanted || column.Evicted)
        {
            return;
        }

        if (edited && !block.Edited)
        {
            if (_editSince == 0)
            {
                _editSince = Stopwatch.GetTimestamp();
                _editFrames = 0;
                _editMarked = 0;
            }

            block.Edited = true;
            _editedBlocks++;
            _editMarked++;
        }

        if (block.Dirty)
        {
            return;
        }

        block.Dirty = true;
        if (!block.InFlight)
        {
            column.Pending++;
            _pendingBlocks++;
        }
    }

    /// <summary>
    /// Raises <see cref="MeshStitch.CoarseChanges"/> of the eight blocks of the level below that block
    /// <paramref name="index"/> of <paramref name="column"/> holds: their stitched meshes read it, apron included.
    /// </summary>
    private void CoarseChanged(MeshColumn column, int index)
    {
        var (x, y, z) = column.BlockAt(index);
        var level = column.Key.Level - 1;
        // Blocks 2x and 2x + 1 of the level below lie in its column x / 2 (block 4 cx to 4 cx + 3 in column cx).
        if (
            !_columns.TryGetValue(new ColumnKey(level, x >> 1, z >> 1), out var fine)
            || fine.Stitches is not { } stitches
        )
        {
            return;
        }

        var firstChunkY = _grid.MinChunkYAt(level);
        var layers = stitches.Length / MeshColumn.BlocksPerChunk;
        for (var fy = 2 * y; fy <= 2 * y + 1; fy++)
        {
            var layer = (fy >> 2) - firstChunkY;
            if ((uint)layer >= (uint)layers)
            {
                continue;
            }

            for (var fz = 2 * z; fz <= 2 * z + 1; fz++)
            {
                for (var fx = 2 * x; fx <= 2 * x + 1; fx++)
                {
                    stitches[
                        layer * MeshColumn.BlocksPerChunk + (fx & 3) + ((fy & 3) << 2) + ((fz & 3) << 4)
                    ].CoarseChanges++;
                }
            }
        }
    }

    /// <summary>Takes the finished jobs (those waiting for staging room first) and uploads their meshes.</summary>
    private void TakeFinished()
    {
        while (_waiting.TryPeek(out var waiting))
        {
            if (!TryFinish(waiting))
            {
                return;
            }

            _waiting.Dequeue();
        }

        while (_workers!.TryTake(out var job))
        {
            if (!TryFinish(job))
            {
                _waiting.Enqueue(job);
            }
        }
    }

    /// <summary>
    /// Uploads a finished job's mesh, or drops it when its block is gone or changed since. False only when the staging
    /// region has no room left this frame; the job then waits.
    /// </summary>
    private bool TryFinish(MeshJob job)
    {
        if (job.Model is not null)
        {
            return TryFinishModel(job);
        }

        if (job.IsStitch)
        {
            return TryFinishStitch(job);
        }

        if (!_columns.TryGetValue(job.Column, out var column) || !ReferenceEquals(column, job.Owner))
        {
            // Its column is gone, or made again since: the old one's counts went with it.
            job.Owner = null;
            _freeJobs.Push(job);
            return true;
        }

        ref var block = ref column.Blocks[job.Index];
        if (job.Version == block.Version && job.Triangles > 0)
        {
            var mesh = job.Mesh;
            switch (TryStage(mesh, column, out var offset, out var words, out var vertexBase, out var firstIndex))
            {
                case Staged.NoStagingRoom:
                    return false;
                case Staged.HeapFull:
                    // It keeps the mesh it had, if any, and is meshed again once ranges have been freed.
                    _starved.Add((column, job.Index, false));
                    EditDone(ref block);
                    Settle(column, ref block, job);
                    return true;
            }

            if (block.RangeWords > 0)
            {
                FreeRange(block.RangeOffset, block.RangeWords);
            }

            column.StagedUpload = Uploads;
            block.RangeOffset = offset;
            block.RangeWords = words;
            block.VertexBase = vertexBase;
            block.FirstIndex = firstIndex;
            block.IndexCount = mesh.IndexCount;
            FindLight(mesh, ref block);
            _stats.PaletteOverflows += mesh.PaletteOverflowed ? 1 : 0;
            column.AddDrawn(job.Index);
            // A stitched mesh of the block's older voxels is not drawn again: its range goes now.
            if (column.Stitches is { } stitches && stitches[job.Index].BlockVersion != block.Version)
            {
                FreeStitch(ref stitches[job.Index]);
            }
            _stats.Uploaded++;
            _stats.UploadedBytes += (ulong)words * sizeof(uint);
        }
        else if (job.Version == block.Version)
        {
            // Empty, or the mesher failed (reported once a frame).
            DropMesh(column, ref block, job.Index);
            _stats.Empty++;
        }

        if (job.Version == block.Version)
        {
            EditDone(ref block);
        }

        Settle(column, ref block, job);
        return true;
    }

    /// <summary>
    /// Stages a finished mesh into a new range of the heap (its palette's two words, its vertices from
    /// <paramref name="vertexBase"/>, then its 16-bit indices from <paramref name="firstIndex"/>, in 16-bit units), or
    /// says why not:
    /// the frame's staging region has no room (the job waits), or the heap has none even after freeing the meshes of
    /// columns the view does not draw (<see cref="Evict"/>; <paramref name="owner"/>, the mesh's own column, is not
    /// freed), which is warned once.
    /// </summary>
    private Staged TryStage(
        MeshBuffers mesh,
        MeshColumn? owner,
        out uint offset,
        out int words,
        out uint vertexBase,
        out uint firstIndex
    )
    {
        const int paletteWords = MeshBuffers.PaletteWords;
        var vertexWords = mesh.VertexCount * MeshVertex.Words;
        var indexWords = (mesh.IndexCount + 1) / 2;
        words = paletteWords + vertexWords + indexWords;
        vertexBase = 0;
        firstIndex = 0;
        if (!_writer.HasRoom((ulong)words * sizeof(uint), 1))
        {
            offset = 0;
            return Staged.NoStagingRoom;
        }

        if (!_heap!.TryAllocate(words, out offset) && !(Evict(words, owner) && _heap.TryAllocate(words, out offset)))
        {
            WarnHeapFull();
            return Staged.HeapFull;
        }

        _heapWasFull = false;
        vertexBase = offset + paletteWords;
        firstIndex = (vertexBase + (uint)vertexWords) * 2;
        var target = _writer.Write((ulong)offset * sizeof(uint), words);
        target[0] = (uint)mesh.Palette;
        target[1] = (uint)(mesh.Palette >> 32);
        MemoryMarshal
            .AsBytes(mesh.Vertices.AsSpan(0, mesh.VertexCount))
            .CopyTo(new Span<byte>(target + paletteWords, vertexWords * sizeof(uint)));
        var indices = new Span<ushort>(target + paletteWords + vertexWords, indexWords * 2);
        mesh.Indices.AsSpan(0, mesh.IndexCount).CopyTo(indices);
        if (mesh.IndexCount < indices.Length)
        {
            indices[^1] = 0;
        }

        return Staged.Done;
    }

    /// <summary>
    /// Frees the meshes of wanted columns that neither the camera's pass nor the far cascade's drew or wanted to draw in
    /// the last frame (<see cref="MeshColumn.NeededUpload"/>), and that are not being meshed, farthest first, until <paramref name="words"/> and <see cref="EvictMarginWords"/> are freed: first those a
    /// coarser drawn column covers (it stays drawn until they are meshed again), then those finer drawn columns cover
    /// (their children stay drawn while they are meshed again, <see cref="Select"/>). An evicted column stays wanted and is
    /// meshed again when the view needs it. Never <paramref name="except"/>, and at most
    /// <see cref="MostEvictedWordsPerFrame"/> a frame. True when anything was freed.
    /// </summary>
    private bool Evict(int words, MeshColumn? except)
    {
        if (_nothingToEvict)
        {
            return false;
        }

        var target = (long)words + EvictMarginWords;
        var start = _heap!.UsedBytes;
        var columns = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = _order.Count - 1; i >= 0; i--)
            {
                var freed = (start - _heap.UsedBytes) / sizeof(uint);
                if (freed >= target || _evictedWordsThisFrame + freed >= MostEvictedWordsPerFrame)
                {
                    pass = 2;
                    break;
                }

                var column = _order[i];
                if (
                    ReferenceEquals(column, except)
                    || column.Evicted
                    || !column.Wanted
                    || column.Drawn.Count == 0
                    || column.Pending > 0
                    || column.NeededUpload >= Uploads - 1
                    // Its ranges may hold copies staged this frame: one copy command must not write a place twice.
                    || column.StagedUpload == Uploads
                    || CoveredByCoarser(column) != (pass == 0)
                )
                {
                    continue;
                }

                column.Evicted = true;
                DropMeshes(column);
                columns++;
            }
        }

        var bytes = start - _heap.UsedBytes;
        _evictedWordsThisFrame += bytes / sizeof(uint);
        if (columns == 0)
        {
            // None this frame: the rest of the frame's meshes that find no room do not look again.
            _nothingToEvict = true;
            return false;
        }

        _stats.Evicted += columns;
        _stats.EvictedBytes += bytes;
        Evictions += columns;
        LogEvicted(bytes / (1024.0 * 1024.0), columns);
        return true;
    }

    /// <summary>True when a coarser column over <paramref name="column"/> was drawn in the last frame.</summary>
    private bool CoveredByCoarser(MeshColumn column)
    {
        var key = column.Key;
        for (var level = key.Level + 1; level <= _coarsestLevel; level++)
        {
            var shift = level - key.Level;
            if (
                _columns.TryGetValue(new ColumnKey(level, key.X >> shift, key.Z >> shift), out var coarser)
                && coarser.DrawnUpload >= Uploads - 1
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The view needs <paramref name="column"/> drawn this frame, or would draw it once meshed: an evicted one is meshed
    /// again, and none is evicted until a frame has not needed it.
    /// </summary>
    private void Needed(MeshColumn column)
    {
        column.NeededUpload = Uploads;
        if (column.Evicted)
        {
            EvictionsWoken++;
            Wake(_world!, column);
        }
    }

    /// <summary>
    /// Once a second at most, after ranges have been freed, the blocks that found no room in the heap are meshed again.
    /// </summary>
    private void RetryStarved(VoxelWorld world, double nowSeconds)
    {
        if (_starved.Count == 0 || !_heapFreed || nowSeconds < _starvedRetryAt)
        {
            return;
        }

        _heapFreed = false;
        _starvedRetryAt = nowSeconds + StarvedRetrySeconds;
        foreach (var (column, index, stitch) in _starved)
        {
            if (!_columns.TryGetValue(column.Key, out var current) || !ReferenceEquals(current, column))
            {
                continue;
            }

            if (stitch)
            {
                // Stale, so asked for again where it is drawn.
                column.StitchesMade()[index].CoarseChanges++;
            }
            else
            {
                MarkChanged(world, column, index);
            }
        }

        _starved.Clear();
    }

    /// <summary>The job is done with: its block is no longer in flight, and counts as settled unless it changed again.</summary>
    private void Settle(MeshColumn column, ref MeshBlock block, MeshJob job)
    {
        block.InFlight = false;
        if (!block.Dirty)
        {
            column.Pending--;
            _pendingBlocks--;
            column.MeshedOnce |= column.Pending == 0 && column.Wanted;
        }

        _freeJobs.Push(job);
    }

    private void DropMesh(MeshColumn column, ref MeshBlock block, int index)
    {
        if (block.RangeWords > 0)
        {
            FreeRange(block.RangeOffset, block.RangeWords);
            block.RangeWords = 0;
        }

        block.IndexCount = 0;
        block.LightPower = 0;
        column.RemoveDrawn(index);
        if (column.Stitches is { } stitches)
        {
            FreeStitch(ref stitches[index]);
        }
    }

    /// <summary>
    /// Reads the changed blocks of the columns nearest the view first and queues their jobs, until the time budget, the
    /// job pool, or the changed blocks run out. A block that can hold no surface loses its mesh at once.
    /// </summary>
    private void StartJobs(VoxelWorld world, Vector3 viewPosition, long deadline)
    {
        if (_pendingBlocks == 0)
        {
            return;
        }

        SortColumns(viewPosition);
        foreach (var column in _order)
        {
            if (column.Pending == 0)
            {
                continue;
            }

            if (_freeJobs.Count == 0 || Stopwatch.GetTimestamp() > deadline)
            {
                return;
            }

            var key = column.Key;
            var count = SortDirtyBlocks(column, viewPosition);
            for (var k = 0; k < count; k++)
            {
                var i = _blockOrder[k];
                ref var block = ref column.Blocks[i];
                if (_freeJobs.Count == 0 || Stopwatch.GetTimestamp() > deadline)
                {
                    return;
                }

                var (x, y, z) = column.BlockAt(i);
                block.Dirty = false;
                if (!world.BlockMayHaveSurface(key.Level, x, y, z))
                {
                    DropMesh(column, ref block, i);
                    EditDone(ref block);
                    column.Pending--;
                    _pendingBlocks--;
                    column.MeshedOnce |= column.Pending == 0 && column.Wanted;
                    continue;
                }

                var job = _freeJobs.Pop();
                if (!world.TryReadBlock(key.Level, x, y, z, job.Samples, job.Densities))
                {
                    // Not resident after all: the column's unload is in the change list.
                    _freeJobs.Push(job);
                    EditDone(ref block);
                    column.Pending--;
                    _pendingBlocks--;
                    column.MeshedOnce |= column.Pending == 0 && column.Wanted;
                    continue;
                }

                block.InFlight = true;
                job.Model = null;
                job.HasDensities = true;
                job.Column = key;
                job.Owner = column;
                job.Index = i;
                job.Version = block.Version;
                _workers!.Enqueue(job);
                _stats.Started++;
                BlocksRead++;
            }
        }
    }

    /// <summary>The column's blocks to read in <see cref="_blockOrder"/>, nearest the view first; returns how many.</summary>
    private int SortDirtyBlocks(MeshColumn column, Vector3 viewPosition)
    {
        if (_blockOrder.Length < column.Blocks.Length)
        {
            _blockOrder = new int[column.Blocks.Length];
            _blockDistances = new float[column.Blocks.Length];
        }

        return column.DirtyNearestFirst(_grid, viewPosition, _blockOrder, _blockDistances);
    }

    /// <summary>Orders the columns nearest the view first when the view moved a metre or columns came or went.</summary>
    private void SortColumns(Vector3 viewPosition)
    {
        if (!_orderStale && Vector3.DistanceSquared(viewPosition, _orderedAt) < 1)
        {
            return;
        }

        _orderStale = false;
        _orderedAt = viewPosition;
        var x = viewPosition.X * VoxelConstants.CentimetresPerMetre;
        var z = viewPosition.Z * VoxelConstants.CentimetresPerMetre;
        foreach (var column in _order)
        {
            double dx = _grid.ToCentimetres(column.Key.CentreVoxelX) - x;
            double dz = _grid.ToCentimetres(column.Key.CentreVoxelZ) - z;
            column.DistanceSquared = dx * dx + dz * dz;
        }

        _order.Sort(s_nearestFirst);
    }

    /// <summary>A mesh of the block's voxels since its last edit is staged, or none will be: it no longer counts.</summary>
    private void EditDone(ref MeshBlock block)
    {
        if (block.Edited)
        {
            block.Edited = false;
            _editedBlocks--;
        }
    }

    /// <summary>
    /// Once every block the edits marked is meshed and staged (blocks streaming in do not count), logs how long that took
    /// from the first edit's change and how many blocks they marked: the edits' latency. The meshes are drawn the frame
    /// after they are staged.
    /// </summary>
    private void ReportEditMeshed()
    {
        if (_editSince == 0)
        {
            return;
        }

        _editFrames++;
        if (_editedBlocks > 0)
        {
            return;
        }

        var milliseconds = (Stopwatch.GetTimestamp() - _editSince) * 1000.0 / Stopwatch.Frequency;
        LastEdit = (milliseconds, _editFrames, _editMarked);
        LogEditMeshed(_editMarked, milliseconds, _editFrames);
        _editSince = 0;
    }

    private void ReportFault()
    {
        if (_workers!.TakeFault() is { } fault)
        {
            LogMesherFailed(fault);
        }
    }

    private void WarnHeapFull()
    {
        if (_heapWasFull)
        {
            return;
        }

        _heapWasFull = true;
        LogHeapFull(_heap!.UsedBytes / (1024.0 * 1024.0), _pass.HeapBytes >> 20);
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

        var meshed = 0;
        // The list, not the dictionary's values: their collection is made on first use, an allocation in a frame.
        foreach (var column in _order)
        {
            meshed += column.Drawn.Count;
        }

        var averageMs = _stats.Frames == 0 ? 0 : _stats.Ticks * 1000.0 / Stopwatch.Frequency / _stats.Frames;
        var undrawnMegabytes = UndrawnBytes() / (1024.0 * 1024.0);
        var maxMs = _stats.MaxTicks * 1000.0 / Stopwatch.Frequency;
        LogStats(
            elapsed,
            _columns.Count,
            meshed,
            _pendingBlocks,
            _stats.Started,
            _stats.Uploaded,
            _stats.PaletteOverflows,
            _stats.Empty,
            _stats.UploadedBytes / (1024.0 * 1024.0),
            _heap!.UsedBytes / (1024.0 * 1024.0),
            _pass.HeapBytes >> 20,
            Frame.Draws,
            StitchedDraws,
            _stats.LastTriangles,
            _stats.StitchesStarted,
            _stats.StitchesUploaded,
            Frame.NearShadows,
            Frame.FarShadows,
            _stats.LastShadowTriangles,
            _trackedModels.Count,
            _stats.ModelsStarted,
            Frame.ModelCamera.Commands,
            Frame.ModelCamera.Records,
            _stats.LastModelTriangles,
            _modelTrianglesByLevel[0],
            _modelTrianglesByLevel[1],
            _modelTrianglesByLevel[2],
            _stats.Resyncs,
            _stats.LastUnbalanced,
            undrawnMegabytes,
            _stats.Evicted,
            _stats.EvictedBytes / (1024.0 * 1024.0),
            _stats.UnmeshedFrames,
            _stats.MostUnmeshed,
            _view.LastLights,
            _view.LastEventLights,
            averageMs,
            maxMs
        );
        var triangles = _stats.LastTriangles;
        var shadowTriangles = _stats.LastShadowTriangles;
        var modelTriangles = _stats.LastModelTriangles;
        var unbalanced = _stats.LastUnbalanced;
        _stats = default;
        _stats.LastUnbalanced = unbalanced;
        _stats.LastTriangles = triangles;
        _stats.LastShadowTriangles = shadowTriangles;
        _stats.LastModelTriangles = modelTriangles;
        _statsStart = nowSeconds;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    /// <summary>
    /// Orders columns by <see cref="MeshColumn.DistanceSquared"/>, nearest first. A cached delegate: sorting with a
    /// comparer object makes a delegate of it on every sort.
    /// </summary>
    private static readonly Comparison<MeshColumn> s_nearestFirst = static (a, b) =>
        a.DistanceSquared.CompareTo(b.DistanceSquared);

    private struct MeshStats
    {
        public int Frames;
        public long Ticks;
        public long MaxTicks;
        public int Started;
        public int Uploaded;
        public int Empty;
        public int PaletteOverflows;
        public ulong UploadedBytes;
        public int Resyncs;
        public long LastTriangles;
        public long LastShadowTriangles;
        public long LastModelTriangles;
        public int LastUnbalanced;
        public int ModelsStarted;
        public int StitchesStarted;
        public int StitchesUploaded;
        public int Evicted;
        public long EvictedBytes;
        public int UnmeshedFrames;
        public int MostUnmeshed;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Meshes: material {Material} names the texture set {Name}, which was not loaded; it is drawn from its recipe"
    )]
    private partial void LogTextureMissing(int material, string name);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Meshes: levels {Finest} to {Coarsest} meshed by {Workers} worker thread(s) into a {HeapMegabytes} MB heap"
    )]
    private partial void LogInitialized(int finest, int coarsest, int workers, ulong heapMegabytes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh heap is full ({UsedMegabytes:F1} MB of {HeapMegabytes} MB used); blocks go without a mesh"
    )]
    private partial void LogHeapFull(double usedMegabytes, ulong heapMegabytes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh draws: a frame's list reached {MaxDraws} blocks; blocks past it went undrawn (logged once a match)"
    )]
    private partial void LogListFull(int maxDraws);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh draws: a frame's model list reached {MaxRecords} draws or {MaxCommands} blocks; models past it went undrawn (logged once a match)"
    )]
    private partial void LogModelListFull(int maxRecords, int maxCommands);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Meshes: {Blocks} edited blocks meshed and staged {Milliseconds:F1} ms ({Frames} frames) after the first edit's change was read"
    )]
    private partial void LogEditMeshed(int blocks, double milliseconds, int frames);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Meshes: the heap was full; freed {Megabytes:F1} MB of {Columns} columns the view does not draw"
    )]
    private partial void LogEvicted(double megabytes, int columns);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mesher failed on a block; it is drawn without a mesh")]
    private partial void LogMesherFailed(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Meshes over {Seconds:F1} s: {Columns} columns, {Meshed} blocks with meshes, {Pending} pending, {Started} jobs started, {Uploaded} uploaded ({Overflowed} with more than 8 surface materials), {Empty} empty, {UploadedMegabytes:F1} MB uploaded, heap {HeapUsedMegabytes:F1} MB of {HeapMegabytes} MB used; {Draws} blocks ({Stitched} stitched) and {Triangles} triangles drawn in the last frame, {StitchesStarted} stitched meshes started and {StitchesUploaded} uploaded, {NearShadows} and {FarShadows} blocks and {ShadowTriangles} triangles into the shadow cascades; {Models} models followed, {ModelsStarted} meshed, {ModelCommands} model blocks drawn as {ModelRecords} instances of {ModelTriangles} triangles ({LevelZero}, {LevelOne}, and {LevelTwo} at levels 0, 1, and 2 or coarser); {Resyncs} resyncs; {Unbalanced} columns beside ones two levels finer in the last frame; {UndrawnMegabytes:F1} MB in columns the last frame did not draw, {Evicted} columns ({EvictedMegabytes:F1} MB) freed to make room; {UnmeshedFrames} frames drew columns not meshed yet (at most {MostUnmeshed}); {Lights} point lights shaded in the last frame, of {EventLights} events' lights; main thread avg {AverageMs:F3} ms max {MaxMs:F2} ms per frame"
    )]
    private partial void LogStats(
        double seconds,
        int columns,
        int meshed,
        int pending,
        int started,
        int uploaded,
        int overflowed,
        int empty,
        double uploadedMegabytes,
        double heapUsedMegabytes,
        ulong heapMegabytes,
        int draws,
        int stitched,
        long triangles,
        int stitchesStarted,
        int stitchesUploaded,
        int nearShadows,
        int farShadows,
        long shadowTriangles,
        int models,
        int modelsStarted,
        int modelCommands,
        int modelRecords,
        long modelTriangles,
        long levelZero,
        long levelOne,
        long levelTwo,
        int resyncs,
        int unbalanced,
        double undrawnMegabytes,
        int evicted,
        double evictedMegabytes,
        int unmeshedFrames,
        int mostUnmeshed,
        int lights,
        int eventLights,
        double averageMs,
        double maxMs
    );
}
