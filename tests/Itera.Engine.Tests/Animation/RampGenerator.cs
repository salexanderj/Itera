using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Animation;

/// <summary>
/// A generator for system tests: stone below y = 0, rising from x = 0 in 1 cm steps, by <c>risePerMille</c> / 1000 per
/// voxel (1732: 60 degrees; 11430: 85 degrees), to a flat top.
/// </summary>
internal sealed class RampGenerator(int risePerMille = 1732, int top = 200) : IChunkGenerator
{
    public const byte Stone = 3;

    public string Id => "test.ramp";

    public int Version => 1;

    /// <summary>First level-0 y above the stone at <paramref name="x"/>.</summary>
    public int Height(int x) => x <= 0 ? 0 : Math.Min(top, (int)((long)x * risePerMille / 1000));

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        var level = column.Level;
        var x0 = VoxelConstants.SampleAt(column.X << VoxelConstants.ChunkShift, level);
        var x1 = VoxelConstants.SampleAt((column.X << VoxelConstants.ChunkShift) + VoxelConstants.ChunkSize - 1, level);
        int lowest = Height(x0),
            highest = Height(x1);
        for (var layer = 0; layer < layers.Length; layer++)
        {
            var chunkY = column.Chunk(layer, VoxelGrid.Unit).Y << VoxelConstants.ChunkShift;
            var bottom = VoxelConstants.SampleAt(chunkY, level);
            var top = VoxelConstants.SampleAt(chunkY + VoxelConstants.ChunkSize - 1, level);
            layers[layer] =
                bottom >= highest ? ChunkFill.Air
                : top < lowest ? ChunkFill.Uniform(Stone)
                : ChunkFill.Mixed;
        }
    }

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels)
    {
        var level = chunk.Level;
        int ox = chunk.X << VoxelConstants.ChunkShift,
            oy = chunk.Y << VoxelConstants.ChunkShift;
        const int Size = VoxelConstants.ChunkSize;
        var index = 0;
        for (var z = 0; z < Size; z++)
        {
            for (var y = 0; y < Size; y++)
            {
                var sy = VoxelConstants.SampleAt(oy + y, level);
                for (var x = 0; x < Size; x++)
                {
                    voxels[index++] = sy < Height(VoxelConstants.SampleAt(ox + x, level)) ? Stone : VoxelConstants.Air;
                }
            }
        }
    }
}
