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

        Assert.Equal(["start", "start-low", "down", "up", "high", "far", "distant"], views.Select(v => v.Name));
        Assert.Null(BenchmarkViews.Place(views, "nowhere"));
    }
}
