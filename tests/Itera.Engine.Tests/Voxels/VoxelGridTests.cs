using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// <see cref="VoxelGrid"/>: centimetres to level-0 voxels and back, the sample points, and the level nearest a size, at a
/// base of 1 cm (where it must equal the store's shifts) and of 10 cm.
/// </summary>
public sealed class VoxelGridTests
{
    [Fact]
    public void At_1_cm_the_grid_is_the_shifts_it_stands_for()
    {
        var grid = VoxelGrid.Unit;
        for (var level = 0; level <= VoxelConstants.MaxLevel; level++)
        {
            Assert.Equal(1 << level, grid.Edge(level));
            for (var coordinate = -300; coordinate <= 300; coordinate += 7)
            {
                Assert.Equal(VoxelConstants.SampleAt(coordinate, level), grid.SampleAt(coordinate, level));
                Assert.Equal(coordinate, grid.ToVoxel(coordinate));
                Assert.Equal(coordinate, grid.ToCentimetres(coordinate));
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(-1, -1)]
    [InlineData(-10, -1)]
    [InlineData(-11, -2)]
    [InlineData(-50_000, -5_000)]
    public void At_10_cm_a_centimetre_lies_in_the_voxel_below_it(int centimetres, int voxel)
    {
        var grid = new VoxelGrid(10);

        Assert.Equal(voxel, grid.ToVoxel(centimetres));
        Assert.Equal(voxel * 10, grid.ToCentimetres(voxel));
        Assert.InRange(centimetres - grid.ToCentimetres(grid.ToVoxel(centimetres)), 0, 9);
    }

    [Fact]
    public void At_10_cm_a_voxel_is_sampled_at_its_centre_rounded_down()
    {
        var grid = new VoxelGrid(10);

        Assert.Equal(10, grid.Edge(0));
        Assert.Equal(80, grid.Edge(3));
        Assert.Equal(0.8f, grid.EdgeMetres(3), 6);
        Assert.Equal(5, grid.SampleAt(0, 0));
        Assert.Equal(-5, grid.SampleAt(-1, 0));
        Assert.Equal(60, grid.SampleAt(1, 2));
        Assert.Equal(-20, grid.SampleAt(-1, 2));
    }

    [Theory]
    [InlineData(1, 64, 6)]
    [InlineData(1, 4, 2)]
    [InlineData(1, 1, 0)]
    [InlineData(10, 64, 3)]
    [InlineData(10, 80, 3)]
    [InlineData(10, 4, 0)]
    [InlineData(10, 100_000, 9)]
    public void The_level_nearest_a_size_is_the_one_of_the_closest_ratio(
        int baseCentimetres,
        int centimetres,
        int level
    )
    {
        Assert.Equal(level, new VoxelGrid(baseCentimetres).LevelNearest(centimetres));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void A_base_outside_1_to_10_cm_is_refused(int baseCentimetres)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoxelGrid(baseCentimetres));
    }
}
