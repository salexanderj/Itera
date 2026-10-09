using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// A generator for tests: a round hill whose top is 4 m up at the origin and falls away as (x^2 + z^2) / 2000 voxels,
/// dirt 50 cm deep over stone. Rays that skim its crest stay close to the ground for metres, a hard case for
/// <c>VoxelWorld.Raycast</c>. Integer maths only. Level 0 only: it reads a column's x and z as level-0 chunks whatever the column's
/// level, so a world of more levels gets wrong coarse columns from it.
/// </summary>
internal sealed class DomeGenerator : IChunkGenerator
{
    public const byte Dirt = 2;
    public const byte Stone = 3;
    private const int DirtDepth = 50;

    public string Id => "test.dome";

    public int Version => 1;

    /// <summary>First level-0 y above the ground at (x, z).</summary>
    public static int Height(int x, int z) => 400 - (int)(((long)x * x + (long)z * z) / 2000);

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        int x0 = column.X << VoxelConstants.ChunkShift,
            z0 = column.Z << VoxelConstants.ChunkShift;
        int x1 = x0 + VoxelConstants.ChunkSize - 1,
            z1 = z0 + VoxelConstants.ChunkSize - 1;
        var highest = Height(Math.Clamp(0, x0, x1), Math.Clamp(0, z0, z1));
        var lowest = Math.Min(Math.Min(Height(x0, z0), Height(x1, z0)), Math.Min(Height(x0, z1), Height(x1, z1)));
        for (var layer = 0; layer < layers.Length; layer++)
        {
            var bottom = column.Chunk(layer, VoxelGrid.Unit).Y << VoxelConstants.ChunkShift;
            var top = bottom + VoxelConstants.ChunkSize - 1;
            layers[layer] =
                bottom >= highest ? ChunkFill.Air
                : top < lowest - DirtDepth ? ChunkFill.Uniform(Stone)
                : ChunkFill.Mixed;
        }
    }

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels)
    {
        int ox = chunk.X << VoxelConstants.ChunkShift,
            oy = chunk.Y << VoxelConstants.ChunkShift,
            oz = chunk.Z << VoxelConstants.ChunkShift;
        for (var z = 0; z < VoxelConstants.ChunkSize; z++)
        {
            for (var x = 0; x < VoxelConstants.ChunkSize; x++)
            {
                var height = Height(ox + x, oz + z);
                for (var y = 0; y < VoxelConstants.ChunkSize; y++)
                {
                    var wy = oy + y;
                    voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] =
                        wy < height - DirtDepth ? Stone
                        : wy < height ? Dirt
                        : VoxelConstants.Air;
                }
            }
        }
    }
}
