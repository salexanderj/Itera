namespace Itera.Engine.Voxels;

/// <summary>Where a column is in its life.</summary>
internal enum ColumnState : byte
{
    /// <summary>A worker is generating it; its layers are not valid yet.</summary>
    Pending = 0,

    /// <summary>Its layers hold the chunks and may be read and edited on the main thread.</summary>
    Resident = 1,
}

/// <summary>
/// The stack of chunks at one (level, x, z): the unit of streaming. Each layer (counted from the grid's floor at the
/// column's level, <see cref="VoxelGrid.MinChunkYAt"/>) is a chunk state: empty, one material, or a root node in
/// <see cref="VoxelMemory"/>. Only a window of layers is stored (<see cref="First"/>, <see cref="Stored"/>): those the
/// generator says may hold more than one fill, and those edits reached; every layer below it is <see cref="Below"/> and
/// every layer above it <see cref="Above"/>, one material or air each, so a column's memory follows its ground and
/// edits, not its world's height. Pooled and reused; owned by the main thread.
/// </summary>
internal sealed class Column
{
    private ChildState[] _layers = new ChildState[16];
    private int _count;
    private int _firstChunkY;

    /// <summary>Which column this is.</summary>
    public ColumnKey Key { get; private set; }

    /// <summary>Pending or resident.</summary>
    public ColumnState State { get; set; }

    /// <summary>The job generating this column while it is pending.</summary>
    public ColumnJob? Job { get; set; }

    /// <summary>
    /// The newest edit sequence applied to this resident column: every edit in the log that reaches it, up to this one,
    /// is in its voxels.
    /// </summary>
    public long AppliedEdit { get; set; }

    /// <summary>True while a large edit that reaches it waits to be applied (<c>VoxelWorld.ApplyPendingEdits</c>).</summary>
    public bool EditsPending { get; set; }

    /// <summary>How many layers the column has in its world: from the grid's floor to its top at its level.</summary>
    public int LayerCount { get; private set; }

    /// <summary>The first stored layer.</summary>
    public int First { get; private set; }

    /// <summary>The layer after the last stored one.</summary>
    public int End => First + _count;

    /// <summary>The chunk state of every layer below the stored ones: empty or one material.</summary>
    public ChildState Below { get; private set; }

    /// <summary>The chunk state of every layer above the stored ones: empty or one material.</summary>
    public ChildState Above { get; private set; }

    /// <summary>The stored layers' chunk states, bottom first: layer <see cref="First"/> + i at i.</summary>
    public Span<ChildState> Stored => _layers.AsSpan(0, _count);

    /// <summary>Makes this object stand for a new, pending column of a world of <paramref name="grid"/>, storing nothing.</summary>
    public void Reset(ColumnKey key, VoxelGrid grid)
    {
        Key = key;
        LayerCount = key.LayerCount(grid);
        _firstChunkY = grid.MinChunkYAt(key.Level);
        State = ColumnState.Pending;
        Job = null;
        AppliedEdit = 0;
        EditsPending = false;
        _layers.AsSpan(0, _count).Clear();
        _count = 0;
        First = 0;
        Below = ChildState.Empty;
        Above = ChildState.Empty;
    }

    /// <summary>The chunk state of <paramref name="layer"/>: stored, or the state below or above the stored ones.</summary>
    public ChildState LayerAt(int layer) =>
        layer < First ? Below
        : layer < First + _count ? _layers[layer - First]
        : Above;

    /// <summary>
    /// The chunk state of <paramref name="layer"/> to change, stored first if it was not (the window widened to it, the
    /// layers between taking the state below or above).
    /// </summary>
    public ref ChildState Widen(int layer)
    {
        if (_count == 0)
        {
            // With nothing stored, First is where the state below gives way to the state above: the layers from it to
            // the new one keep the state of their side.
            var (first, end, state) = layer < First ? (layer, First, Below) : (First, layer + 1, Above);
            Grow(end - first);
            _layers.AsSpan(0, end - first).Fill(state);
            (First, _count) = (first, end - first);
        }
        else if (layer < First)
        {
            var added = First - layer;
            Grow(_count + added);
            _layers.AsSpan(0, _count).CopyTo(_layers.AsSpan(added));
            _layers.AsSpan(0, added).Fill(Below);
            (First, _count) = (layer, _count + added);
        }
        else if (layer >= First + _count)
        {
            var added = layer - (First + _count) + 1;
            Grow(_count + added);
            _layers.AsSpan(_count, added).Fill(Above);
            _count += added;
        }

        return ref _layers[layer - First];
    }

    /// <summary>Makes room for <paramref name="count"/> stored layers, keeping those stored.</summary>
    private void Grow(int count)
    {
        if (count > _layers.Length)
        {
            var grown = new ChildState[Math.Max(count, _layers.Length * 2)];
            _layers.AsSpan(0, _count).CopyTo(grown);
            _layers = grown;
        }
    }

    /// <summary>
    /// Takes the generated window: <paramref name="layers"/> from layer <paramref name="first"/>, with
    /// <paramref name="below"/> and <paramref name="above"/> round it. The column stores nothing before.
    /// </summary>
    public void Install(int first, ReadOnlySpan<ChildState> layers, ChildState below, ChildState above)
    {
        _count = 0;
        Grow(layers.Length);
        layers.CopyTo(_layers);
        _count = layers.Length;
        First = first;
        Below = below;
        Above = above;
    }

    /// <summary>Stores nothing more: the layers' trees were handed on (to a free job).</summary>
    public void ClearStored()
    {
        _layers.AsSpan(0, _count).Clear();
        _count = 0;
    }

    /// <summary>The material of a level-0 voxel inside this column.</summary>
    public byte ReadVoxel(VoxelMemory memory, VoxelCoord voxel)
    {
        var level = Key.Level;
        var layer = (voxel.Y >> (VoxelConstants.ChunkShift + level)) - _firstChunkY;
        if ((uint)layer >= (uint)LayerCount)
        {
            return VoxelConstants.Air;
        }

        const int mask = VoxelConstants.ChunkSize - 1;
        return ChunkTree.ReadVoxel(
            memory,
            LayerAt(layer),
            (voxel.X >> level) & mask,
            (voxel.Y >> level) & mask,
            (voxel.Z >> level) & mask
        );
    }

    /// <summary>The density (<see cref="VoxelDensity"/>) of a level-0 voxel inside this column: empty above and below it.</summary>
    public byte ReadDensity(VoxelMemory memory, VoxelCoord voxel)
    {
        var level = Key.Level;
        var layer = (voxel.Y >> (VoxelConstants.ChunkShift + level)) - _firstChunkY;
        if ((uint)layer >= (uint)LayerCount)
        {
            return VoxelDensity.Empty;
        }

        const int mask = VoxelConstants.ChunkSize - 1;
        return ChunkTree.ReadDensity(
            memory,
            LayerAt(layer),
            (voxel.X >> level) & mask,
            (voxel.Y >> level) & mask,
            (voxel.Z >> level) & mask
        );
    }

    /// <summary>
    /// The material and density of a level-0 voxel inside this column, and whether its brick keeps densities of its own
    /// (<paramref name="stored"/>; else the density is its material's default). False above and below the column.
    /// </summary>
    public bool TryReadVoxelAndDensity(
        VoxelMemory memory,
        VoxelCoord voxel,
        out byte material,
        out byte density,
        out bool stored
    )
    {
        material = VoxelConstants.Air;
        density = VoxelDensity.Empty;
        stored = false;
        var level = Key.Level;
        var layer = (voxel.Y >> (VoxelConstants.ChunkShift + level)) - _firstChunkY;
        if ((uint)layer >= (uint)LayerCount)
        {
            return false;
        }

        const int mask = VoxelConstants.ChunkSize - 1;
        int x = (voxel.X >> level) & mask,
            y = (voxel.Y >> level) & mask,
            z = (voxel.Z >> level) & mask;
        var brick = ChunkTree.GetBrick(memory, LayerAt(layer), x >> 3, y >> 3, z >> 3);
        if (brick.Kind != ChildKind.Handle)
        {
            material = brick.Material;
            density = VoxelDensity.Default(material);
            return true;
        }

        var index = (x & 7) | ((y & 7) << 3) | ((z & 7) << 6);
        material = BrickCodec.Read(memory, brick.Handle, index);
        stored = BrickCodec.HasDensities(memory, brick.Handle);
        density = stored ? BrickCodec.ReadDensity(memory, brick.Handle, index) : VoxelDensity.Default(material);
        return true;
    }

    /// <summary>Adds this column's mixed chunks, nodes, and stored bricks to the totals.</summary>
    public void Count(VoxelMemory memory, ref int mixedChunks, ref int nodes, ref int bricks)
    {
        foreach (var layer in Stored)
        {
            if (layer.Kind == ChildKind.Handle)
            {
                mixedChunks++;
                ChunkTree.Count(memory, layer, ref nodes, ref bricks);
            }
        }
    }
}
