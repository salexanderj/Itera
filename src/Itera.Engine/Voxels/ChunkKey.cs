namespace Itera.Engine.Voxels;

/// <summary>
/// Names one chunk: its level and its position in chunks of that level. A level-k chunk covers
/// <c>128 * 2^k</c> level-0 voxels along each edge.
/// </summary>
public readonly record struct ChunkKey(int Level, int X, int Y, int Z)
{
    /// <summary>The chunk at <paramref name="level"/> that contains a level-0 voxel.</summary>
    public static ChunkKey Containing(VoxelCoord voxel, int level)
    {
        var shift = VoxelConstants.ChunkShift + level;
        return new ChunkKey(level, voxel.X >> shift, voxel.Y >> shift, voxel.Z >> shift);
    }

    /// <summary>The level-0 voxel at the chunk's lowest corner.</summary>
    public VoxelCoord MinVoxel
    {
        get
        {
            var shift = VoxelConstants.ChunkShift + Level;
            return new VoxelCoord(X << shift, Y << shift, Z << shift);
        }
    }

    /// <summary>
    /// The chunk's lowest corner in its own level's voxels: what edits are applied to a chunk's 128^3 array from.
    /// </summary>
    public VoxelCoord LevelOrigin =>
        new(X << VoxelConstants.ChunkShift, Y << VoxelConstants.ChunkShift, Z << VoxelConstants.ChunkShift);

    /// <summary>Level-0 voxels along one edge of the chunk.</summary>
    public int EdgeInLevel0Voxels => VoxelConstants.ChunkSize << Level;

    /// <summary>The column that holds this chunk.</summary>
    public ColumnKey Column => new(Level, X, Z);

    /// <summary>This chunk's index in its column of a world of <paramref name="grid"/>; 0 is the lowest layer.</summary>
    public int Layer(VoxelGrid grid) => Y - grid.MinChunkYAt(Level);

    /// <summary>True when the chunk lies inside the bounds of a world of <paramref name="grid"/>.</summary>
    public bool IsInBounds(VoxelGrid grid) =>
        Column.IsInBounds(grid) && Y >= grid.MinChunkYAt(Level) && Y <= grid.MaxChunkYAt(Level);
}

/// <summary>Names one column: the stack of chunks at one level and one (x, z) in chunks of that level.</summary>
public readonly record struct ColumnKey(int Level, int X, int Z)
{
    /// <summary>The column at <paramref name="level"/> that contains a level-0 voxel.</summary>
    public static ColumnKey Containing(VoxelCoord voxel, int level)
    {
        var shift = VoxelConstants.ChunkShift + level;
        return new ColumnKey(level, voxel.X >> shift, voxel.Z >> shift);
    }

    /// <summary>Layers in this column in a world of <paramref name="grid"/>.</summary>
    public int LayerCount(VoxelGrid grid) => grid.LayerCount(Level);

    /// <summary>The chunk at <paramref name="layer"/>, counted from the bottom, in a world of <paramref name="grid"/>.</summary>
    public ChunkKey Chunk(int layer, VoxelGrid grid) => new(Level, X, grid.MinChunkYAt(Level) + layer, Z);

    /// <summary>True when the column lies inside the bounds of a world of <paramref name="grid"/>.</summary>
    public bool IsInBounds(VoxelGrid grid) =>
        Level is >= 0 and <= VoxelConstants.MaxLevel
        && X >= grid.MinChunkXZAt(Level)
        && X <= grid.MaxChunkXZAt(Level)
        && Z >= grid.MinChunkXZAt(Level)
        && Z <= grid.MaxChunkXZAt(Level);

    /// <summary>The level-0 voxel x of the column's centre line.</summary>
    public int CentreVoxelX => (X << (VoxelConstants.ChunkShift + Level)) + (VoxelConstants.ChunkSize << Level) / 2;

    /// <summary>The level-0 voxel z of the column's centre line.</summary>
    public int CentreVoxelZ => (Z << (VoxelConstants.ChunkShift + Level)) + (VoxelConstants.ChunkSize << Level) / 2;
}
