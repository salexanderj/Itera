using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// A generator for tests: flat ground whose top is at y = 2 m (dirt, stone 50 cm down), and one upright trunk of bark
/// around (<see cref="TrunkX"/>, <see cref="TrunkZ"/>), <see cref="TrunkRadius"/> voxels in radius, from 1 m below the
/// ground to 7 m up, as the world generator buries its trunks. Bark is an object and dirt is ground, so the trunk's
/// foot is the corner that surface groups keep sharp. Integer maths only; level-k voxels are sampled at their centres.
/// </summary>
internal sealed class TrunkGenerator : IChunkGenerator
{
    public const byte Dirt = 2;
    public const byte Stone = 3;
    public const byte Bark = 4;
    public const int Ground = 200;
    public const int TrunkX = 64;
    public const int TrunkZ = 64;
    public const int TrunkRadius = 30;
    private const int TrunkBottom = 100;
    private const int TrunkTop = 700;
    private const int DirtDepth = 50;

    public string Id => "test.trunk";

    public int Version => 1;

    /// <summary>The material at level-0 sample point (x, y, z).</summary>
    public static byte Material(int x, int y, int z)
    {
        long dx = x - TrunkX,
            dz = z - TrunkZ;
        if (y >= TrunkBottom && y < TrunkTop && dx * dx + dz * dz <= TrunkRadius * TrunkRadius)
        {
            return Bark;
        }

        return y < Ground - DirtDepth ? Stone
            : y < Ground ? Dirt
            : VoxelConstants.Air;
    }

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        var level = column.Level;
        var edge = VoxelConstants.ChunkSize << level;
        int x0 = column.X * edge,
            z0 = column.Z * edge;
        var nearTrunk =
            x0 <= TrunkX + TrunkRadius
            && x0 + edge > TrunkX - TrunkRadius
            && z0 <= TrunkZ + TrunkRadius
            && z0 + edge > TrunkZ - TrunkRadius;
        var top = nearTrunk ? TrunkTop : Ground;
        for (var layer = 0; layer < layers.Length; layer++)
        {
            var chunkY = column.Chunk(layer, VoxelGrid.Unit).Y << VoxelConstants.ChunkShift;
            var bottom = VoxelConstants.SampleAt(chunkY, level);
            var highest = VoxelConstants.SampleAt(chunkY + VoxelConstants.ChunkSize - 1, level);
            layers[layer] =
                bottom >= top ? ChunkFill.Air
                : highest < Math.Min(Ground - DirtDepth, TrunkBottom) ? ChunkFill.Uniform(Stone)
                : ChunkFill.Mixed;
        }
    }

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels)
    {
        var level = chunk.Level;
        int ox = chunk.X << VoxelConstants.ChunkShift,
            oy = chunk.Y << VoxelConstants.ChunkShift,
            oz = chunk.Z << VoxelConstants.ChunkShift;
        for (var z = 0; z < VoxelConstants.ChunkSize; z++)
        {
            var sz = VoxelConstants.SampleAt(oz + z, level);
            for (var y = 0; y < VoxelConstants.ChunkSize; y++)
            {
                var sy = VoxelConstants.SampleAt(oy + y, level);
                for (var x = 0; x < VoxelConstants.ChunkSize; x++)
                {
                    voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = Material(
                        VoxelConstants.SampleAt(ox + x, level),
                        sy,
                        sz
                    );
                }
            }
        }
    }
}
