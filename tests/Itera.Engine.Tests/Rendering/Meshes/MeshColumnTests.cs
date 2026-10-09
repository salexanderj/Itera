using System.Numerics;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// <see cref="MeshColumn"/>: the blocks to read come nearest the view first, those not dirty or in flight left out, and
/// allocating nothing; a block's sides beside coarser columns follow from its place in the column and the column's sides.
/// </summary>
public sealed class MeshColumnTests
{
    private static readonly VoxelGrid s_grid = new(10);

    [Fact]
    public void The_blocks_to_read_come_nearest_the_view_first()
    {
        var column = new MeshColumn(new ColumnKey(0, 0, 0), s_grid);
        var random = new Random(3);
        for (var i = 0; i < column.Blocks.Length; i++)
        {
            column.Blocks[i].Dirty = random.Next(3) == 0;
            column.Blocks[i].InFlight = random.Next(5) == 0;
        }

        // The view inside the block at cell (2, 1, 3) of the column's sixth layer.
        var blockMetres = SurfaceNets.BlockSize * s_grid.EdgeMetres(0);
        var target = 5 * MeshColumn.BlocksPerChunk + 2 + 4 * 1 + 16 * 3;
        column.Blocks[target].Dirty = true;
        column.Blocks[target].InFlight = false;
        var (tx, ty, tz) = column.BlockAt(target);
        var view = (new Vector3(tx, ty, tz) + new Vector3(0.4f, 0.6f, 0.5f)) * blockMetres;
        var order = new int[column.Blocks.Length];
        var distances = new float[column.Blocks.Length];

        var count = column.DirtyNearestFirst(s_grid, view, order, distances);

        var expected = column.Blocks.Count(block => block.Dirty && !block.InFlight);
        Assert.Equal(expected, count);
        Assert.Equal(target, order[0]);
        var last = 0f;
        for (var k = 0; k < count; k++)
        {
            ref readonly var block = ref column.Blocks[order[k]];
            Assert.True(block.Dirty && !block.InFlight);
            var (x, y, z) = column.BlockAt(order[k]);
            var distance = Vector3.DistanceSquared((new Vector3(x, y, z) + new Vector3(0.5f)) * blockMetres, view);
            Assert.True(distance >= last);
            last = distance;
        }

        var start = SystemAllocation.StartMeasurement();
        column.DirtyNearestFirst(s_grid, view, order, distances);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Theory]
    // A coarser column past the low x side: the blocks along it take that side, and their corners past it.
    [InlineData(TransitionSides.LowX, 0, 0, TransitionSides.LowX | TransitionSides.LowXHighZ)]
    [InlineData(
        TransitionSides.LowX,
        0,
        1,
        TransitionSides.LowX | TransitionSides.LowXLowZ | TransitionSides.LowXHighZ
    )]
    [InlineData(TransitionSides.LowX, 3, 1, TransitionSides.None)]
    [InlineData(TransitionSides.LowX, 1, 1, TransitionSides.None)]
    // Only the high corner's column is coarser: only the block at that corner meets it, at its corner.
    [InlineData(TransitionSides.HighXHighZ, 3, 3, TransitionSides.HighXHighZ)]
    [InlineData(TransitionSides.HighXHighZ, 3, 2, TransitionSides.None)]
    // Both high sides: the corner block takes both, and its corner past each of them.
    [InlineData(
        TransitionSides.HighX | TransitionSides.HighZ,
        3,
        3,
        TransitionSides.HighX | TransitionSides.HighZ | TransitionSides.HighXLowZ | TransitionSides.LowXHighZ
    )]
    public void A_blocks_sides_follow_its_place_in_the_column(
        TransitionSides column,
        int x,
        int z,
        TransitionSides block
    )
    {
        // Any layer and height in the chunk: the sides do not depend on them.
        for (var layer = 0; layer < 3; layer++)
        {
            for (var y = 0; y < 4; y++)
            {
                var index = layer * MeshColumn.BlocksPerChunk + x + 4 * y + 16 * z;
                Assert.Equal(block, MeshColumn.BlockSides(column, index));
            }
        }
    }
}
