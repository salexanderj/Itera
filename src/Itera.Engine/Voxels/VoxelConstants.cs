namespace Itera.Engine.Voxels;

/// <summary>
/// Sizes and bounds of the voxel world, stated once. A voxel at level k is its store's grid's edge at k
/// (<see cref="VoxelGrid"/>; 2^k cm on a grid of 1 cm). A chunk is
/// 128 x 128 x 128 voxels of its level: a root node of 4 x 4 x 4 cells, each cell a node of 4 x 4 x 4 bricks, each brick
/// 8 x 8 x 8 voxels. A column is the stack of chunks at one (level, x, z); it is the unit of streaming. Coordinates are
/// integers in centimetres with the origin at the map centre. The world's bounds are its grid's
/// (<see cref="VoxelGrid.MinChunkXZ"/> and the others).
/// </summary>
public static class VoxelConstants
{
    /// <summary>The material of empty space. Every other material id is solid.</summary>
    public const byte Air = 0;

    /// <summary>Centimetres per metre: the unit of VoxelCoord, edits, rays, and models' level-0 voxels.</summary>
    public const int CentimetresPerMetre = 100;

    /// <summary>Highest level a chunk key may have. A level-9 voxel is 5.12 m on a grid of 1 cm, 51.2 m on one of 10.</summary>
    public const int MaxLevel = 9;

    /// <summary>Voxels along one edge of a brick, as a shift.</summary>
    public const int BrickShift = 3;

    /// <summary>Voxels along one edge of a brick.</summary>
    public const int BrickSize = 1 << BrickShift;

    /// <summary>Voxels in a brick. Index <c>x + 8y + 64z</c>.</summary>
    public const int BrickVoxels = BrickSize * BrickSize * BrickSize;

    /// <summary>64-bit words in a brick's occupancy mask; word z holds layer z.</summary>
    public const int BrickMaskWords = BrickVoxels / 64;

    /// <summary>Children along one edge of a node.</summary>
    public const int NodeSize = 4;

    /// <summary>Children of a node. Index <c>x + 4y + 16z</c>.</summary>
    public const int NodeChildren = NodeSize * NodeSize * NodeSize;

    /// <summary>Voxels along one edge of a root cell (a level-1 node), as a shift.</summary>
    public const int CellShift = 5;

    /// <summary>Voxels along one edge of a chunk, as a shift.</summary>
    public const int ChunkShift = 7;

    /// <summary>Voxels along one edge of a chunk.</summary>
    public const int ChunkSize = 1 << ChunkShift;

    /// <summary>Voxels in a chunk. Dense chunk buffers use index <c>x + 128y + 16384z</c>.</summary>
    public const int ChunkVoxels = ChunkSize * ChunkSize * ChunkSize;

    /// <summary>Bricks along one edge of a chunk.</summary>
    public const int BricksPerChunkEdge = ChunkSize / BrickSize;

    /// <summary>Bricks in a chunk. A brick's cell index is <c>x + 16y + 256z</c> in brick units.</summary>
    public const int BricksPerChunk = BricksPerChunkEdge * BricksPerChunkEdge * BricksPerChunkEdge;

    /// <summary>Largest edit radius or box edge, in centimetres (100 m).</summary>
    public const int MaxEditExtent = 10_000;

    /// <summary>Finished columns installed per main-loop iteration; the rest wait for the next one.</summary>
    public const int MaxColumnsInstalledPerUpdate = 8;

    /// <summary>Columns unloaded per main-loop iteration; the rest wait for the next one.</summary>
    public const int MaxColumnsUnloadedPerUpdate = 16;

    /// <summary>
    /// Columns of its own level by which a level's reach (where rays use it) stays inside its streaming radius (where
    /// its columns load). A point within reach lies in a column whose centre is at least 2 - 0.71 columns inside the
    /// radius. That leaves room for a streaming centre to move half a column between rebuilds of the wanted columns, and
    /// for the column to load, before a ray needs it.
    /// </summary>
    public const int ReachMarginColumns = 2;

    /// <summary>
    /// The level-0 coordinate at which a level-k voxel is sampled: its centre, rounded down. Generators and edits test
    /// this point, so a voxel's content depends only on integers.
    /// </summary>
    /// <param name="coordinate">Voxel coordinate at <paramref name="level"/>.</param>
    /// <param name="level">Level of the voxel.</param>
    public static int SampleAt(int coordinate, int level) => (coordinate << level) + ((1 << level) >> 1);
}
