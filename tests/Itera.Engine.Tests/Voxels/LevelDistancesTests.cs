using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed class LevelDistancesTests
{
    /// <summary>The defaults give what every level twice as far as the one before gave.</summary>
    [Theory]
    [InlineData(3, 1)]
    [InlineData(3, 3)]
    [InlineData(16, 5)]
    [InlineData(16.5, 5)]
    [InlineData(37, 4)]
    [InlineData(100, 8)]
    public void The_defaults_double_each_level(double radiusMetres, int levels)
    {
        var settings = new VoxelSettings { StreamingRadiusMetres = radiusMetres, Levels = levels };
        var distances = LevelDistances.From(settings, VoxelGrid.Unit);
        var radius = (long)(radiusMetres * VoxelConstants.CentimetresPerMetre);

        Assert.Equal(LevelDistances.Doubling(radiusMetres, levels, VoxelGrid.Unit), distances);
        for (var level = 0; level <= levels; level++)
        {
            Assert.Equal(radius << level, distances.RadiusCentimetres(level));
            Assert.Equal(VoxelGrid.Unit.ReachCentimetres(radius, level), distances.ReachCentimetres(level));
        }

        Assert.Equal(radius << (levels - 1), distances.ViewRadiusCentimetres);
    }

    [Fact]
    public void Each_level_is_drawn_growth_times_as_far_and_loads_two_of_its_columns_farther()
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = 16,
            Levels = 5,
            Level0ReachMetres = 16,
            ReachGrowth = 1.5,
        };
        var distances = LevelDistances.From(settings, VoxelGrid.Unit);

        Assert.Equal([1600L, 2400, 3600, 5400, 25600 - 4096], Reaches(distances));
        Assert.Equal([1856L, 2400 + 512, 3600 + 1024, 5400 + 2048, 25600], Radii(distances));
        Assert.Equal(25600, distances.ViewRadiusCentimetres);
    }

    private static long[] Reaches(LevelDistances distances) =>
        [.. Enumerable.Range(0, distances.Levels).Select(level => distances.ReachCentimetres(level))];

    private static long[] Radii(LevelDistances distances) =>
        [.. Enumerable.Range(0, distances.Levels).Select(level => distances.RadiusCentimetres(level))];
}
