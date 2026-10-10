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
/// The stack of chunks at one (level, x, z): the unit of streaming. Each layer is a chunk state: empty, one material,
/// or a root node in <see cref="VoxelMemory"/>. Most layers are all air or all stone and cost nothing but their entry.
/// Pooled and reused; owned by the main thread.
/// </summary>
internal sealed class Column
{
    private readonly ChildState[] _layers;

    private int _layerCount;
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

    /// <summary>The chunk state of each layer, bottom first.</summary>
    public Span<ChildState> Layers => _layers.AsSpan(0, _layerCount);

    /// <param name="layerCapacity">The most layers a column of its world has (<see cref="VoxelGrid.MaxLayers"/>).</param>
    public Column(int layerCapacity) => _layers = new ChildState[layerCapacity];

    /// <summary>Makes this object stand for a new, pending column of a world of <paramref name="grid"/>.</summary>
    public void Reset(ColumnKey key, VoxelGrid grid)
    {
        Key = key;
        _layerCount = key.LayerCount(grid);
        if (_layerCount > _layers.Length)
        {
            throw new InvalidOperationException(
                $"A column of {_layerCount} layers does not fit a pool's column of {_layers.Length}."
            );
        }

        _firstChunkY = grid.MinChunkYAt(key.Level);
        State = ColumnState.Pending;
        Job = null;
        AppliedEdit = 0;
        EditsPending = false;
        _layers.AsSpan().Clear();
    }

    /// <summary>The material of a level-0 voxel inside this column.</summary>
    public byte ReadVoxel(VoxelMemory memory, VoxelCoord voxel)
    {
        var level = Key.Level;
        var layer = (voxel.Y >> (VoxelConstants.ChunkShift + level)) - _firstChunkY;
        if ((uint)layer >= (uint)_layerCount)
        {
            return VoxelConstants.Air;
        }

        const int mask = VoxelConstants.ChunkSize - 1;
        return ChunkTree.ReadVoxel(
            memory,
            _layers[layer],
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
        if ((uint)layer >= (uint)_layerCount)
        {
            return VoxelDensity.Empty;
        }

        const int mask = VoxelConstants.ChunkSize - 1;
        return ChunkTree.ReadDensity(
            memory,
            _layers[layer],
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
        if ((uint)layer >= (uint)_layerCount)
        {
            return false;
        }

        const int mask = VoxelConstants.ChunkSize - 1;
        int x = (voxel.X >> level) & mask,
            y = (voxel.Y >> level) & mask,
            z = (voxel.Z >> level) & mask;
        var brick = ChunkTree.GetBrick(memory, _layers[layer], x >> 3, y >> 3, z >> 3);
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
        foreach (var layer in Layers)
        {
            if (layer.Kind == ChildKind.Handle)
            {
                mixedChunks++;
                ChunkTree.Count(memory, layer, ref nodes, ref bricks);
            }
        }
    }
}
