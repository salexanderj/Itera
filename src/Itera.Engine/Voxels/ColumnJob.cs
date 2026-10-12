using System.Diagnostics;

namespace Itera.Engine.Voxels;

/// <summary>How a column job ended.</summary>
internal enum ColumnJobResult : byte
{
    /// <summary>Not run yet.</summary>
    None = 0,

    /// <summary>Every layer is in <see cref="ColumnJob.Layers"/>.</summary>
    Done = 1,

    /// <summary>Memory reached the budget; nothing was kept.</summary>
    OutOfMemory = 2,

    /// <summary>The column stopped being wanted before or while it ran; nothing was kept.</summary>
    Cancelled = 3,

    /// <summary>A free job: the unloaded column's layers were freed.</summary>
    Freed = 4,
}

/// <summary>
/// One column to generate on a worker thread: its key, the seed, and a copy of the edits that reach it, made on the
/// main thread when the job was queued. The worker writes the layers; the main thread installs them. Pooled and
/// reused, so its buffers stop growing once they fit the largest column. A free job instead carries an unloaded
/// column's layers, and the worker frees them, so the main thread does not walk the trees.
/// </summary>
internal sealed class ColumnJob
{
    private ChildState[] _layers = new ChildState[16];
    private ChunkFill[] _fills = new ChunkFill[16];
    private int _layerCount;
    private int _columnLayers;
    private volatile bool _cancelled;

    /// <summary>The column to generate.</summary>
    public ColumnKey Key { get; private set; }

    /// <summary>The world seed.</summary>
    public ulong Seed { get; private set; }

    /// <summary>The newest edit sequence in <see cref="Edits"/>' snapshot; later edits are replayed on install.</summary>
    public long SnapshotSequence { get; private set; }

    /// <summary>The edits that reach the column, in sequence order.</summary>
    public List<VoxelEdit> Edits { get; } = [];

    /// <summary>The pieces the piece edits in <see cref="Edits"/> put back.</summary>
    public List<VoxelPiece> Pieces { get; } = [];

    /// <summary>
    /// The generated chunk state of each stored layer, bottom first, from layer <see cref="First"/>: the generator's
    /// range (<see cref="IChunkGenerator.DescribeRange"/>) and every layer the job's edits reach. Valid when
    /// <see cref="Result"/> is done.
    /// </summary>
    public Span<ChildState> Layers => _layers.AsSpan(0, _layerCount);

    /// <summary>The first stored layer.</summary>
    public int First { get; private set; }

    /// <summary>The chunk state of every layer below the stored ones.</summary>
    public ChildState Below { get; private set; }

    /// <summary>The chunk state of every layer above the stored ones.</summary>
    public ChildState Above { get; private set; }

    /// <summary>How the job ended.</summary>
    public ColumnJobResult Result { get; private set; }

    /// <summary>Time the worker spent, in <see cref="Stopwatch"/> ticks.</summary>
    public long ElapsedTicks { get; private set; }

    /// <summary>Layers that were filled densely (mixed, or edited).</summary>
    public int FilledLayers { get; private set; }

    /// <summary>True once the main thread no longer wants the result.</summary>
    public bool IsCancelled => _cancelled;

    /// <summary>True for a job that frees an unloaded column instead of generating one.</summary>
    public bool IsFree { get; private set; }

    /// <summary>Makes the job stand for a new column of a world of <paramref name="grid"/>. Main thread.</summary>
    public void Reset(ColumnKey key, VoxelGrid grid, ulong seed, long snapshotSequence) =>
        Reset(key, key.LayerCount(grid), seed, snapshotSequence);

    private void Reset(ColumnKey key, int columnLayers, ulong seed, long snapshotSequence)
    {
        Key = key;
        _layers.AsSpan(0, _layerCount).Clear();
        _layerCount = 0;
        _columnLayers = columnLayers;
        First = 0;
        Below = ChildState.Empty;
        Above = ChildState.Empty;
        Seed = seed;
        SnapshotSequence = snapshotSequence;
        Edits.Clear();
        Pieces.Clear();
        Result = ColumnJobResult.None;
        ElapsedTicks = 0;
        FilledLayers = 0;
        _cancelled = false;
        IsFree = false;
    }

    /// <summary>Makes the job free <paramref name="layers"/> of an unloaded column. Main thread.</summary>
    public void ResetForFree(ColumnKey key, ReadOnlySpan<ChildState> layers)
    {
        Reset(key, layers.Length, 0, 0);
        Store(layers.Length);
        layers.CopyTo(_layers);
        IsFree = true;
    }

    /// <summary>Empties the stored layers once a column took them. Main thread.</summary>
    public void ClearLayers()
    {
        _layers.AsSpan(0, _layerCount).Clear();
        _layerCount = 0;
    }

    /// <summary>Makes room for <paramref name="count"/> stored layers and stores that many, empty.</summary>
    private void Store(int count)
    {
        if (count > _layers.Length)
        {
            _layers = new ChildState[Math.Max(count, _layers.Length * 2)];
            _fills = new ChunkFill[_layers.Length];
        }

        _layers.AsSpan(0, count).Clear();
        _layerCount = count;
    }

    /// <summary>Asks the worker to drop the job. Main thread.</summary>
    public void Cancel() => _cancelled = true;

    /// <summary>
    /// Generates the column: describes it, fills each mixed or edited layer's materials into <paramref name="scratch"/>
    /// and densities into <paramref name="densities"/>, writes the copies of the objects that reach into it at its level
    /// (<paramref name="copies"/>, null for none, found into the worker's <paramref name="placements"/>), replays the
    /// edits, and encodes it. Worker thread. Never passes the memory budget; on failure it frees what it made.
    /// </summary>
    public void Run(
        IChunkGenerator generator,
        VoxelMemory memory,
        Span<byte> scratch,
        Span<byte> densities,
        Objects.ObjectCopies? copies = null,
        List<Objects.ObjectPlacement>? placements = null
    )
    {
        var start = Stopwatch.GetTimestamp();
        if (IsFree)
        {
            FreeLayers(memory);
            Result = ColumnJobResult.Freed;
        }
        else
        {
            Result = Generate(generator, memory, scratch, densities, copies, placements);
        }

        ElapsedTicks = Stopwatch.GetTimestamp() - start;
    }

    /// <summary>Frees the layers the job holds and empties them.</summary>
    public void FreeLayers(VoxelMemory memory)
    {
        foreach (ref var layer in Layers)
        {
            ChunkTree.Free(memory, layer, ChunkTree.RootDepth);
            layer = ChildState.Empty;
        }
    }

    private ColumnJobResult Generate(
        IChunkGenerator generator,
        VoxelMemory memory,
        Span<byte> scratch,
        Span<byte> densities,
        Objects.ObjectCopies? copies,
        List<Objects.ObjectPlacement>? placements
    )
    {
        if (_cancelled)
        {
            return ColumnJobResult.Cancelled;
        }

        // The objects whose copies reach into the column at its level.
        var grid = generator.Grid;
        ReadOnlySpan<Objects.ObjectPlacement> objects = default;
        if (copies is not null && placements is not null && copies.CopiesAt(Key.Level))
        {
            copies.Find(Key, Seed, placements);
            objects = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(placements);
        }

        // The generator's range, widened to the layers the edits and the objects' copies reach.
        generator.DescribeRange(Key, Seed, out var first, out var last, out var below, out var above);
        (first, last) = WithEdits(grid, Math.Max(first, 0), Math.Min(last, _columnLayers - 1));
        if (!objects.IsEmpty)
        {
            (first, last) = WithObjects(grid, copies!, objects, first, last);
        }

        Below = State(below);
        Above = State(above);
        First = Math.Min(first, _columnLayers);
        Store(Math.Max(last - first + 1, 0));
        var layers = Layers;
        var fills = _fills.AsSpan(0, layers.Length);
        fills.Clear();
        if (layers.Length > 0)
        {
            generator.DescribeLayers(Key, Seed, First, fills);
        }

        for (var layer = 0; layer < layers.Length; layer++)
        {
            if (_cancelled)
            {
                FreeLayers(memory);
                return ColumnJobResult.Cancelled;
            }

            var chunk = Key.Chunk(First + layer, grid);
            var fill = fills[layer];
            var edited = HasEdits(chunk, generator.Grid);
            // Copies go into air only: a layer of one solid material keeps it.
            var copied =
                !objects.IsEmpty
                && !(fill.Kind == ChunkFillKind.Uniform && fill.Material != VoxelConstants.Air)
                && copies!.Touches(chunk, objects);
            if (fill.Kind != ChunkFillKind.Mixed && !edited && !copied)
            {
                layers[layer] = ChildState.Uniform(
                    fill.Kind == ChunkFillKind.Uniform ? fill.Material : VoxelConstants.Air
                );
                continue;
            }

            if (fill.Kind == ChunkFillKind.Mixed)
            {
                // Fitted to their materials before any edit, as stored bricks are, so a replay starts from the same
                // densities as an edit of the loaded column.
                generator.Fill(chunk, Seed, scratch, densities);
                VoxelDensity.FitAll(scratch[..VoxelConstants.ChunkVoxels], densities[..VoxelConstants.ChunkVoxels]);
            }
            else
            {
                scratch[..VoxelConstants.ChunkVoxels].Fill(fill.Material);
                densities[..VoxelConstants.ChunkVoxels].Fill(VoxelDensity.Default(fill.Material));
            }

            if (
                copied
                && copies!.Stamp(chunk, objects, scratch, densities) == 0
                && fill.Kind != ChunkFillKind.Mixed
                && !edited
            )
            {
                // A layer of air the copies reach in height but write nothing into stays air.
                layers[layer] = ChildState.Uniform(VoxelConstants.Air);
                continue;
            }

            if (edited)
            {
                var origin = chunk.LevelOrigin;
                foreach (var edit in Edits)
                {
                    var piece = edit.Shape == VoxelEditShape.Piece ? FindPiece(edit.Piece) : default;
                    var view = new PieceView(in piece, memory);
                    VoxelEditing.ApplyToRegion(
                        in edit,
                        in view,
                        new GridLevel(generator.Grid, chunk.Level),
                        origin,
                        VoxelConstants.ChunkSize,
                        scratch,
                        densities
                    );
                }
            }

            FilledLayers++;
            layers[layer] = ChunkTree.Encode(memory, scratch, densities, mayExceedBudget: false, out var outOfMemory);
            if (outOfMemory)
            {
                FreeLayers(memory);
                return ColumnJobResult.OutOfMemory;
            }
        }

        return ColumnJobResult.Done;
    }

    /// <summary>The layers from <paramref name="first"/> to <paramref name="last"/> widened to every layer the edits reach.</summary>
    private (int First, int Last) WithEdits(VoxelGrid grid, int first, int last)
    {
        var level = Key.Level;
        var floor = grid.MinChunkYAt(level);
        foreach (var edit in Edits)
        {
            edit.GetBounds(out var min, out var end);
            var low = (grid.ToVoxel(min.Y) >> (VoxelConstants.ChunkShift + level)) - floor;
            var high = (grid.ToVoxel(end.Y - 1) >> (VoxelConstants.ChunkShift + level)) - floor;
            if (high < 0 || low > _columnLayers - 1)
            {
                continue;
            }

            // An empty range is (boundary, boundary - 1): widened the same way, it keeps the layers between the boundary
            // and the edit, which DescribeLayers then describes.
            (first, last) = (Math.Min(first, Math.Max(low, 0)), Math.Max(last, Math.Min(high, _columnLayers - 1)));
        }

        return (first, last);
    }

    /// <summary>The layers from <paramref name="first"/> to <paramref name="last"/> widened to every layer the objects' copies reach.</summary>
    private (int First, int Last) WithObjects(
        VoxelGrid grid,
        Objects.ObjectCopies copies,
        ReadOnlySpan<Objects.ObjectPlacement> objects,
        int first,
        int last
    )
    {
        var (low, high) = copies.Heights(Key.Level, objects);
        if (low > high)
        {
            return (first, last);
        }

        var height = grid.ColumnCentimetres(Key.Level);
        var floor = grid.MinChunkYAt(Key.Level);
        var lowLayer = Maths.DeterministicMath.FloorDivide(low, height) - floor;
        var highLayer = Maths.DeterministicMath.FloorDivide(high, height) - floor;
        if (highLayer < 0 || lowLayer > _columnLayers - 1)
        {
            return (first, last);
        }

        // As the edits' (WithEdits): an empty range keeps its boundary, so the layers between it and the copies are
        // described too.
        var bottom = (int)Math.Max(lowLayer, 0);
        var top = (int)Math.Min(highLayer, _columnLayers - 1);
        return (Math.Min(first, bottom), Math.Max(last, top));
    }

    /// <summary>The chunk state of a whole layer of <paramref name="fill"/>: air or one material.</summary>
    private static ChildState State(ChunkFill fill) =>
        fill.Kind == ChunkFillKind.Uniform ? ChildState.Uniform(fill.Material) : ChildState.Empty;

    private VoxelPiece FindPiece(long sequence)
    {
        foreach (var piece in Pieces)
        {
            if (piece.Sequence == sequence)
            {
                return piece;
            }
        }

        throw new InvalidOperationException($"Column job has no piece {sequence}.");
    }

    private bool HasEdits(ChunkKey chunk, VoxelGrid grid)
    {
        if (Edits.Count == 0)
        {
            return false;
        }

        // The chunk's box in centimetres, as edits are given.
        var min = grid.ToCentimetres(chunk.MinVoxel);
        var edge = grid.ToCentimetres(chunk.EdgeInLevel0Voxels);
        var end = new VoxelCoord(min.X + edge, min.Y + edge, min.Z + edge);
        foreach (var edit in Edits)
        {
            if (VoxelEditing.Overlaps(in edit, min, end))
            {
                return true;
            }
        }

        return false;
    }
}
