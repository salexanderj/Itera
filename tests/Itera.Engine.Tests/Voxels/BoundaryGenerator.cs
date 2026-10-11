using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>Flat ground on a chunk's face at height 0, stone under it; its range stores no layer.</summary>
internal sealed class BoundaryGenerator(VoxelGrid grid) : IChunkGenerator
{
    public const byte Stone = 3;

    public VoxelGrid Grid => grid;

    public string Id => "test.boundary";

    public int Version => 1;

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        for (var layer = 0; layer < layers.Length; layer++)
        {
            layers[layer] = column.Chunk(layer, grid).Y < 0 ? ChunkFill.Uniform(Stone) : ChunkFill.Air;
        }
    }

    public void DescribeRange(
        ColumnKey column,
        ulong seed,
        out int first,
        out int last,
        out ChunkFill below,
        out ChunkFill above
    )
    {
        // Every layer from chunk y 0 up is air: the boundary, with no layer stored.
        first = -grid.MinChunkYAt(column.Level);
        last = first - 1;
        below = ChunkFill.Uniform(Stone);
        above = ChunkFill.Air;
    }

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) =>
        voxels[..VoxelConstants.ChunkVoxels].Fill(chunk.Y < 0 ? Stone : VoxelConstants.Air);
}
