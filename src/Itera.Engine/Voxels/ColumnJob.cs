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
    private readonly ChildState[] _layers;
    private readonly ChunkFill[] _fills;
    private int _layerCount;
    private volatile bool _cancelled;

    /// <param name="layerCapacity">The most layers a column of its world has (<see cref="VoxelGrid.MaxLayers"/>).</param>
    public ColumnJob(int layerCapacity)
    {
        _layers = new ChildState[layerCapacity];
        _fills = new ChunkFill[layerCapacity];
    }

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

    /// <summary>The generated chunk state of each layer, bottom first. Valid when <see cref="Result"/> is done.</summary>
    public Span<ChildState> Layers => _layers.AsSpan(0, _layerCount);

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

    private void Reset(ColumnKey key, int layerCount, ulong seed, long snapshotSequence)
    {
        if (layerCount > _layers.Length)
        {
            throw new InvalidOperationException(
                $"A column of {layerCount} layers does not fit a pool's job of {_layers.Length}."
            );
        }

        Key = key;
        _layerCount = layerCount;
        Seed = seed;
        SnapshotSequence = snapshotSequence;
        Edits.Clear();
        Pieces.Clear();
        _layers.AsSpan().Clear();
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
        layers.CopyTo(_layers);
        IsFree = true;
    }

    /// <summary>Asks the worker to drop the job. Main thread.</summary>
    public void Cancel() => _cancelled = true;

    /// <summary>
    /// Generates the column: describes it, fills each mixed or edited layer's materials into <paramref name="scratch"/>
    /// and densities into <paramref name="densities"/>, replays the edits, and encodes it. Worker thread. Never passes
    /// the memory budget; on failure it frees what it made.
    /// </summary>
    public void Run(IChunkGenerator generator, VoxelMemory memory, Span<byte> scratch, Span<byte> densities)
    {
        var start = Stopwatch.GetTimestamp();
        if (IsFree)
        {
            FreeLayers(memory);
            Result = ColumnJobResult.Freed;
        }
        else
        {
            Result = Generate(generator, memory, scratch, densities);
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
        Span<byte> densities
    )
    {
        if (_cancelled)
        {
            return ColumnJobResult.Cancelled;
        }

        var layers = Layers;
        var fills = _fills.AsSpan(0, layers.Length);
        fills.Clear();
        generator.DescribeColumn(Key, Seed, fills);

        for (var layer = 0; layer < layers.Length; layer++)
        {
            if (_cancelled)
            {
                FreeLayers(memory);
                return ColumnJobResult.Cancelled;
            }

            var chunk = Key.Chunk(layer, generator.Grid);
            var fill = fills[layer];
            var edited = HasEdits(chunk, generator.Grid);
            if (fill.Kind != ChunkFillKind.Mixed && !edited)
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
