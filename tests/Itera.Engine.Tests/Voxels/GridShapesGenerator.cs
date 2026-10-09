using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// A generator on a grid of any base, for the grid's tests: dirt below <see cref="FloorCentimetres"/>, a stone box, and
/// a ball of leaves, all in centimetres with their faces off the 10 cm lines, so what a voxel holds depends on where its
/// sample point falls. Each voxel holds what <see cref="Material"/> gives at its sample point
/// (<see cref="VoxelGrid.SampleAt"/>).
/// </summary>
internal sealed class GridShapesGenerator(VoxelGrid grid) : IChunkGenerator
{
    public const byte Dirt = 2;
    public const byte Stone = 3;
    public const byte Leaves = 6;

    /// <summary>The dirt's top: points below it are dirt.</summary>
    public const int FloorCentimetres = 137;

    /// <summary>The ball of leaves' radius in centimetres.</summary>
    public const int BallRadius = 87;

    /// <summary>The stone box's low corner, in centimetres.</summary>
    public static VoxelCoord BoxMin { get; } = new(223, 137, 413);

    /// <summary>The stone box's high corner, in centimetres, outside it.</summary>
    public static VoxelCoord BoxEnd { get; } = new(467, 311, 609);

    /// <summary>The ball of leaves' centre, in centimetres.</summary>
    public static VoxelCoord BallCentre { get; } = new(-303, 251, -147);

    public VoxelGrid Grid { get; } = grid;

    public string Id => "test.grid-shapes";

    public int Version => 1;

    /// <summary>The material at the point (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), in centimetres.</summary>
    public static byte Material(int x, int y, int z)
    {
        if (x >= BoxMin.X && x < BoxEnd.X && y >= BoxMin.Y && y < BoxEnd.Y && z >= BoxMin.Z && z < BoxEnd.Z)
        {
            return Stone;
        }

        long dx = x - BallCentre.X,
            dy = y - BallCentre.Y,
            dz = z - BallCentre.Z;
        if (dx * dx + dy * dy + dz * dz <= (long)BallRadius * BallRadius)
        {
            return Leaves;
        }

        return y < FloorCentimetres ? Dirt : VoxelConstants.Air;
    }

    /// <summary>The material of the level-<paramref name="level"/> voxel of index <paramref name="voxel"/> on <see cref="Grid"/>.</summary>
    public byte MaterialOf(VoxelCoord voxel, int level) =>
        Material(Grid.SampleAt(voxel.X, level), Grid.SampleAt(voxel.Y, level), Grid.SampleAt(voxel.Z, level));

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        var edge = Grid.ColumnCentimetres(column.Level);
        long x0 = column.X * edge,
            z0 = column.Z * edge;
        for (var layer = 0; layer < layers.Length; layer++)
        {
            var y0 = column.Chunk(layer, Grid).Y * edge;
            var touched =
                Meets(BoxMin, BoxEnd, x0, y0, z0, edge)
                || Meets(
                    new VoxelCoord(BallCentre.X - BallRadius, BallCentre.Y - BallRadius, BallCentre.Z - BallRadius),
                    new VoxelCoord(
                        BallCentre.X + BallRadius + 1,
                        BallCentre.Y + BallRadius + 1,
                        BallCentre.Z + BallRadius + 1
                    ),
                    x0,
                    y0,
                    z0,
                    edge
                );
            layers[layer] =
                touched ? ChunkFill.Mixed
                : y0 + edge <= FloorCentimetres ? ChunkFill.Uniform(Dirt)
                : y0 >= FloorCentimetres ? ChunkFill.Air
                : ChunkFill.Mixed;
        }
    }

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels)
    {
        const int Size = VoxelConstants.ChunkSize;
        var level = chunk.Level;
        int ox = chunk.X << VoxelConstants.ChunkShift,
            oy = chunk.Y << VoxelConstants.ChunkShift,
            oz = chunk.Z << VoxelConstants.ChunkShift;
        Span<int> sampleX = stackalloc int[Size];
        for (var x = 0; x < Size; x++)
        {
            sampleX[x] = Grid.SampleAt(ox + x, level);
        }

        for (var z = 0; z < Size; z++)
        {
            var sampleZ = Grid.SampleAt(oz + z, level);
            for (var y = 0; y < Size; y++)
            {
                var sampleY = Grid.SampleAt(oy + y, level);
                var row = voxels.Slice((z * Size + y) * Size, Size);
                for (var x = 0; x < Size; x++)
                {
                    row[x] = Material(sampleX[x], sampleY, sampleZ);
                }
            }
        }
    }

    /// <summary>True when the box [<paramref name="min"/>, <paramref name="end"/>) meets the chunk-sized cube at (x0, y0, z0).</summary>
    private static bool Meets(VoxelCoord min, VoxelCoord end, long x0, long y0, long z0, long edge) =>
        min.X < x0 + edge && end.X > x0 && min.Y < y0 + edge && end.Y > y0 && min.Z < z0 + edge && end.Z > z0;
}
