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
    [InlineData(101)]
    public void A_base_outside_1_cm_to_1_m_is_refused(int baseCentimetres)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoxelGrid(baseCentimetres));
    }

    [Fact]
    public void A_grid_in_chunks_holds_the_chunks_it_is_given_and_a_bases_default_is_one()
    {
        var grid = new VoxelGrid(10, 3, -2, 5);
        var defaults = new VoxelGrid(10);

        Assert.Equal(
            (-3, 2, -2, 5, 8),
            (grid.MinChunkXZ, grid.MaxChunkXZ, grid.MinChunkY, grid.MaxChunkY, grid.MaxLayers)
        );
        Assert.Equal(defaults, new VoxelGrid(10, defaults.MaxChunkXZ + 1, defaults.MinChunkY, defaults.MaxChunkY));
        Assert.NotEqual(defaults, new VoxelGrid(10, defaults.MaxChunkXZ + 2, defaults.MinChunkY, defaults.MaxChunkY));
    }

    [Theory]
    [InlineData(10, 4000, -51.2, 102.4, 313, -4, 7)]
    [InlineData(10, 12.8, -12.8, 12.8, 1, -1, 0)]
    [InlineData(10, 12.81, -12.81, 12.81, 2, -2, 1)]
    [InlineData(1, 5, -0.5, 2, 4, -1, 1)]
    [InlineData(1, 1.28, 0, 1.28, 1, 0, 0)]
    [InlineData(10, 0.001, 0.001, 0.002, 1, 0, 0)]
    public void A_sized_grid_rounds_each_bound_out_to_whole_chunks(
        int baseCentimetres,
        double halfWidth,
        double floor,
        double top,
        int halfWidthChunks,
        int minChunkY,
        int maxChunkY
    )
    {
        var grid = VoxelGrid.Sized(baseCentimetres, halfWidth, floor, top);

        Assert.Equal(
            (-halfWidthChunks, halfWidthChunks - 1, minChunkY, maxChunkY),
            (grid.MinChunkXZ, grid.MaxChunkXZ, grid.MinChunkY, grid.MaxChunkY)
        );
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1)]
    public void The_widest_grid_reaches_160_km_in_whole_centimetres(int baseCentimetres)
    {
        var grid = VoxelGrid.Sized(baseCentimetres, 160_000, -10, 100);

        Assert.Equal(16_000_000, grid.ToCentimetres(grid.EndVoxelXZ));
        Assert.Equal(-16_000_000, grid.ToCentimetres(grid.MinVoxelXZ));
        Assert.Equal(161_000, new Itera.Engine.Systems.PlayArea(grid).EdgeMetres);
        Assert.Equal(grid.MaxChunkXZ, grid.ColumnAt(15_999_999, 0));
        Assert.Equal(grid.MinChunkXZ, grid.ColumnAt(-16_000_000, 0));
    }

    [Fact]
    public void A_grid_past_its_limits_is_refused()
    {
        // Across: at least one chunk each side, and no farther than 160 km (12,500 chunks of 12.8 m).
        Assert.ThrowsAny<ArgumentException>(() => new VoxelGrid(10, 0, -1, 1));
        Assert.ThrowsAny<ArgumentException>(() => new VoxelGrid(10, 12_501, -1, 1));
        // Up and down: within 160 km, the top over the floor, and at most 128 layers.
        Assert.ThrowsAny<ArgumentException>(() => new VoxelGrid(10, 1, -12_501, -12_400));
        Assert.ThrowsAny<ArgumentException>(() => new VoxelGrid(10, 1, 12_400, 12_500));
        Assert.ThrowsAny<ArgumentException>(() => new VoxelGrid(10, 1, 2, 1));
        Assert.ThrowsAny<ArgumentException>(() => new VoxelGrid(10, 1, 0, VoxelGrid.MostLayers));
        _ = new VoxelGrid(10, 12_500, 0, VoxelGrid.MostLayers - 1);
        // In metres: finite, a half-width over 0, a top over the floor, and the same limits.
        Assert.ThrowsAny<ArgumentException>(() => VoxelGrid.Sized(10, double.NaN, 0, 1));
        Assert.ThrowsAny<ArgumentException>(() => VoxelGrid.Sized(10, 0, 0, 1));
        Assert.ThrowsAny<ArgumentException>(() => VoxelGrid.Sized(10, 10, 5, 5));
        Assert.ThrowsAny<ArgumentException>(() => VoxelGrid.Sized(10, 1e12, 0, 1));
        Assert.ThrowsAny<ArgumentException>(() => VoxelGrid.Sized(1, 10, 0, 165));
        Assert.ThrowsAny<ArgumentException>(() => VoxelGrid.Sized(101, 10, 0, 1));
    }

    [Theory]
    [InlineData(20, 2_560)]
    [InlineData(50, 6_400)]
    [InlineData(100, 12_800)]
    public void Voxels_up_to_1_m_have_columns_of_128_voxels_and_the_default_bounds(int baseCentimetres, int column)
    {
        var grid = new VoxelGrid(baseCentimetres);

        Assert.Equal(column, grid.ColumnCentimetres(0));
        Assert.Equal(column << 9, grid.ColumnCentimetres(9));
        // The default bounds hold the default world (±500 m, -40.96 m to 81.92 m) in whole chunks.
        Assert.True(grid.ToCentimetres(grid.EndVoxelXZ) >= VoxelGrid.DefaultHalfWidthCentimetres);
        Assert.True(grid.ToCentimetres(grid.MinVoxelY) <= VoxelGrid.DefaultFloorCentimetres);
        Assert.True(grid.ToCentimetres(grid.EndVoxelY) >= VoxelGrid.DefaultTopCentimetres);
        Assert.True(grid.MaxLayers <= VoxelGrid.MostLayers);
    }
}
