using Game.WorldGeneration;
using Itera.Engine.Maths;

namespace Game.Tests;

/// <summary>The starter's benchmark views, which are also <c>tp</c>'s places (for screenshots of the same views).</summary>
public sealed class BenchmarkViewsTests
{
    [Fact]
    public void Each_view_is_a_place_by_its_name_in_any_case()
    {
        var start = new StartPose(new WorldPoint(0, 8, 0), 0.25f, 0);
        var views = BenchmarkViews.For(start, 1);

        foreach (var view in views)
        {
            Assert.Equal(
                (view.Position, view.Yaw, view.Pitch),
                BenchmarkViews.Place(views, view.Name.ToUpperInvariant())
            );
        }

        Assert.Equal(
            ["start", "start-low", "down", "up", "high", "far", "distant", "forest", "forest-high"],
            views.Select(v => v.Name)
        );
        Assert.Null(BenchmarkViews.Place(views, "nowhere"));
    }

    /// <summary>The forest views stand where the forest is whole, past the clear space round the middle, one over the other.</summary>
    [Fact]
    public void The_forest_views_stand_inside_a_forest()
    {
        var views = BenchmarkViews.For(new StartPose(new WorldPoint(0, 8, 0), 0.25f, 0), 1);
        var forest = views.Single(v => v.Name == "forest").Position;
        var high = views.Single(v => v.Name == "forest-high").Position;
        var (x, z) = ((int)(forest.X * 100), (int)(forest.Z * 100));

        Assert.Equal(1, GameForests.Cover(x, z, 1));
        Assert.True(Math.Sqrt((double)x * x + (double)z * z) > GameForests.ClearCentimetres);
        Assert.Equal((forest.X, forest.Z), (high.X, high.Z));
        Assert.Equal(BenchmarkViews.HighMetres - BenchmarkViews.EyeMetres, high.Y - forest.Y, 3);
    }
}
