using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// A generator for system tests: stone below y = 0 and air above, so every chunk is uniform, on voxels of
/// <paramref name="grid"/> (1 cm by default; 10 cm where a test checks what a coarser grid's voxels do). The ground's top
/// lies on a voxel's face at any grid, so no voxel holds part of it.
/// </summary>
internal sealed class FlatGenerator(VoxelGrid? grid = null) : IChunkGenerator
{
    public const byte Stone = 3;

    public VoxelGrid Grid { get; } = grid ?? VoxelGrid.Unit;

    public string Id => "test.flat";

    public int Version => 1;

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        for (var layer = 0; layer < layers.Length; layer++)
        {
            layers[layer] = column.Chunk(layer, Grid).Y < 0 ? ChunkFill.Uniform(Stone) : ChunkFill.Air;
        }
    }

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) =>
        voxels[..VoxelConstants.ChunkVoxels].Fill(chunk.Y < 0 ? Stone : VoxelConstants.Air);
}
