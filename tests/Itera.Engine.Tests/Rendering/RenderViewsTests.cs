using System.Numerics;
using Itera.Engine.Rendering;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// <see cref="RenderViews"/>: declaring and finding, each refusal, sealing, the cadence of drawing a view every N frames,
/// a view not asked for that keeps counting, the asked camera, the panels, a match's end, and a frame's calls that allocate
/// nothing.
/// </summary>
public sealed class RenderViewsTests
{
    private static CameraView CameraAt(float x) => new(new Vector3(x, 0, 0), Quaternion.Identity, MathF.PI / 2, 0.05f);

    /// <summary>Runs <paramref name="frames"/> frames asking for view 0 each time, drawing it when due; returns the frames it was drawn on.</summary>
    private static List<int> FramesDrawn(RenderViews views, int frames)
    {
        var drawn = new List<int>();
        for (var frame = 0; frame < frames; frame++)
        {
            views.BeginFrame();
            views.Draw(0, CameraAt(frame));
            if (views.IsDue(0, out _))
            {
                views.Drawn(0);
                drawn.Add(frame);
            }
        }

        return drawn;
    }

    [Fact]
    public void Views_are_declared_in_order_and_found_by_name()
    {
        var views = new RenderViews();

        Assert.Equal(0, views.Declare("map", 256, 128, 4));
        Assert.Equal(1, views.Declare("mirror", 64, 64));

        Assert.Equal(2, views.Count);
        Assert.Equal(0, views.Find("map"));
        Assert.Equal(1, views.Find("mirror"));
        Assert.Equal(-1, views.Find("MAP"));
        Assert.Equal(-1, views.Find("other"));
        Assert.Equal(new RenderViewInfo("map", 256, 128, 4), views.Info(0));
        Assert.Equal(new RenderViewInfo("mirror", 64, 64, 1), views.Info(1));
    }

    [Fact]
    public void A_view_may_have_the_least_and_most_sizes_and_the_most_frames()
    {
        var views = new RenderViews();

        views.Declare("small", RenderViews.LeastTexels, RenderViews.LeastTexels, 1);
        views.Declare("large", RenderViews.MostTexels, RenderViews.MostTexels, RenderViews.MostEveryFrames);

        Assert.Equal(2, views.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_refused(string name)
    {
        var views = new RenderViews();

        Assert.ThrowsAny<ArgumentException>(() => views.Declare(name, 64, 64));
        Assert.Equal(0, views.Count);
    }

    [Fact]
    public void A_name_declared_already_is_refused_by_ordinal_comparison()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);

        Assert.ThrowsAny<ArgumentException>(() => views.Declare("map", 64, 64));
        Assert.Equal(1, views.Declare("Map", 64, 64));
    }

    [Theory]
    [InlineData(RenderViews.LeastTexels - 1, 64, 1)]
    [InlineData(64, RenderViews.LeastTexels - 1, 1)]
    [InlineData(RenderViews.MostTexels + 1, 64, 1)]
    [InlineData(64, RenderViews.MostTexels + 1, 1)]
    [InlineData(0, 64, 1)]
    [InlineData(64, -5, 1)]
    [InlineData(64, 64, 0)]
    [InlineData(64, 64, -1)]
    [InlineData(64, 64, RenderViews.MostEveryFrames + 1)]
    public void A_size_or_cadence_out_of_range_is_refused(int width, int height, int everyFrames)
    {
        var views = new RenderViews();

        Assert.ThrowsAny<ArgumentException>(() => views.Declare("map", width, height, everyFrames));
        Assert.Equal(0, views.Count);
        Assert.Equal(-1, views.Find("map"));
    }

    [Fact]
    public void A_fifth_view_is_refused()
    {
        var views = new RenderViews();
        for (var i = 0; i < RenderViews.MostViews; i++)
        {
            views.Declare("view" + i, 64, 64);
        }

        Assert.Throws<InvalidOperationException>(() => views.Declare("one more", 64, 64));
        Assert.Equal(RenderViews.MostViews, views.Count);
    }

    [Fact]
    public void A_declaration_after_the_seal_is_refused()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);

        views.Seal();

        Assert.Throws<InvalidOperationException>(() => views.Declare("other", 64, 64));
        Assert.Equal(1, views.Count);
        Assert.Equal("map", views.Info(0).Name);
    }

    [Fact]
    public void An_index_out_of_range_is_refused()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);
        var camera = CameraAt(0);

        foreach (var bad in new[] { -1, 1, RenderViews.MostViews })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => views.Info(bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => views.Draw(bad, camera));
            Assert.Throws<ArgumentOutOfRangeException>(() => views.Show(bad, 0, 0, 10, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => views.IsDue(bad, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => views.Drawn(bad));
        }
    }

    [Fact]
    public void A_view_asked_for_every_frame_is_drawn_every_frame_at_one()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64, 1);

        Assert.Equal(Enumerable.Range(0, 12), FramesDrawn(views, 12));
    }

    [Fact]
    public void A_view_every_four_frames_is_drawn_at_once_and_then_every_fourth_frame()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64, 4);

        List<int> expected = [0, 4, 8];
        Assert.Equal(expected, FramesDrawn(views, 12));
    }

    [Fact]
    public void A_view_not_asked_for_is_not_due_and_keeps_counting()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64, 4);
        views.BeginFrame();
        views.Draw(0, CameraAt(0));
        Assert.True(views.IsDue(0, out _));
        views.Drawn(0);

        // Six frames pass with no ask: never due.
        for (var frame = 1; frame <= 6; frame++)
        {
            views.BeginFrame();
            Assert.False(views.IsDue(0, out _));
        }

        // The first ask afterwards finds the count past four: due at once, and then four frames apart.
        views.BeginFrame();
        views.Draw(0, CameraAt(0));
        Assert.True(views.IsDue(0, out _));
        views.Drawn(0);
        for (var frame = 1; frame <= 3; frame++)
        {
            views.BeginFrame();
            views.Draw(0, CameraAt(0));
            Assert.False(views.IsDue(0, out _));
        }

        views.BeginFrame();
        views.Draw(0, CameraAt(0));
        Assert.True(views.IsDue(0, out _));
    }

    [Fact]
    public void A_view_asked_for_but_not_drawn_stays_due()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64, 4);

        for (var frame = 0; frame < 6; frame++)
        {
            views.BeginFrame();
            views.Draw(0, CameraAt(0));
            Assert.True(views.IsDue(0, out _));
        }
    }

    [Fact]
    public void A_view_that_was_never_asked_for_is_not_due()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);

        views.BeginFrame();

        Assert.False(views.IsDue(0, out _));
    }

    [Fact]
    public void The_views_count_apart()
    {
        var views = new RenderViews();
        views.Declare("fast", 64, 64, 1);
        views.Declare("slow", 64, 64, 3);

        var fast = new List<int>();
        var slow = new List<int>();
        for (var frame = 0; frame < 7; frame++)
        {
            views.BeginFrame();
            views.Draw(0, CameraAt(0));
            views.Draw(1, CameraAt(0));
            if (views.IsDue(0, out _))
            {
                views.Drawn(0);
                fast.Add(frame);
            }

            if (views.IsDue(1, out _))
            {
                views.Drawn(1);
                slow.Add(frame);
            }
        }

        Assert.Equal(Enumerable.Range(0, 7), fast);
        List<int> expectedSlow = [0, 3, 6];
        Assert.Equal(expectedSlow, slow);
    }

    [Fact]
    public void The_asked_camera_comes_back_and_a_second_ask_replaces_the_first()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);
        var ortho = new CameraView(
            new Vector3(1, 50, 2),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2),
            0,
            -10,
            CameraProjection.Orthographic,
            80,
            100
        );

        views.BeginFrame();
        views.Draw(0, CameraAt(7));
        views.Draw(0, in ortho);

        Assert.True(views.IsDue(0, out var camera));
        Assert.Equal(ortho, camera);
    }

    [Fact]
    public void An_ask_lasts_one_frame()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);
        views.BeginFrame();
        views.Draw(0, CameraAt(0));

        views.BeginFrame();

        Assert.False(views.IsDue(0, out _));
    }

    [Fact]
    public void A_shown_view_has_a_panel_this_frame_only()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);
        views.Declare("mirror", 64, 64);
        views.BeginFrame();
        Assert.True(views.Panels.IsEmpty);

        views.Show(1, 10, 20, 200, 100);
        views.Show(0, 0, 0, 50, 50);

        Assert.Equal(2, views.Panels.Length);
        Assert.Equal(new ViewPanel(1, 10, 20, 200, 100), views.Panels[0]);
        Assert.Equal(new ViewPanel(0, 0, 0, 50, 50), views.Panels[1]);

        views.BeginFrame();
        Assert.True(views.Panels.IsEmpty);
    }

    [Fact]
    public void A_second_show_of_a_view_replaces_its_rectangle()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);
        views.Declare("mirror", 64, 64);
        views.BeginFrame();

        views.Show(0, 1, 2, 3, 4);
        views.Show(1, 5, 6, 7, 8);
        views.Show(0, 10, 20, 30, 40);

        Assert.Equal(2, views.Panels.Length);
        Assert.Equal(new ViewPanel(0, 10, 20, 30, 40), views.Panels[0]);
        Assert.Equal(new ViewPanel(1, 5, 6, 7, 8), views.Panels[1]);
    }

    [Fact]
    public void A_panel_with_no_size_or_a_position_that_is_not_a_number_shows_nothing()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);
        views.BeginFrame();

        views.Show(0, 0, 0, 0, 10);
        views.Show(0, 0, 0, 10, 0);
        views.Show(0, 0, 0, -5, 10);
        views.Show(0, 0, 0, 10, -5);
        views.Show(0, 0, 0, float.NaN, 10);
        views.Show(0, 0, 0, 10, float.NaN);
        views.Show(0, 0, 0, float.PositiveInfinity, 10);
        views.Show(0, float.NaN, 0, 10, 10);
        views.Show(0, 0, float.NegativeInfinity, 10, 10);

        Assert.True(views.Panels.IsEmpty);

        views.Show(0, 4, 5, 6, 7);
        views.Show(0, 0, 0, float.NaN, 10);

        Assert.Equal(new ViewPanel(0, 4, 5, 6, 7), Assert.Single(views.Panels.ToArray()));
    }

    [Fact]
    public void A_panel_does_not_ask_for_a_draw()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64);
        views.BeginFrame();

        views.Show(0, 0, 0, 10, 10);

        Assert.False(views.IsDue(0, out _));
    }

    [Fact]
    public void The_end_of_a_match_clears_the_frame_and_the_next_ask_draws_at_once()
    {
        var views = new RenderViews();
        views.Declare("map", 64, 64, 10);
        views.BeginFrame();
        views.Draw(0, CameraAt(0));
        views.Show(0, 0, 0, 10, 10);
        Assert.True(views.IsDue(0, out _));
        views.Drawn(0);
        views.BeginFrame();
        views.Draw(0, CameraAt(0));
        views.Show(0, 0, 0, 10, 10);
        Assert.False(views.IsDue(0, out _));

        views.EndMatch();

        Assert.True(views.Panels.IsEmpty);
        Assert.False(views.IsDue(0, out _));
        views.BeginFrame();
        views.Draw(0, CameraAt(0));
        Assert.True(views.IsDue(0, out _));
        Assert.Equal(1, views.Count);
    }

    [Fact]
    public void A_frames_calls_allocate_nothing()
    {
        var views = new RenderViews();
        views.Declare("map", 256, 256, 4);
        views.Declare("mirror", 64, 64, 1);
        views.Seal();
        var camera = CameraAt(1);
        var total = 0;

        // Warm up every call once.
        RunFrame(views, camera, ref total);

        var before = SystemAllocation.StartMeasurement();
        for (var frame = 0; frame < 200; frame++)
        {
            RunFrame(views, camera, ref total);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(total > 0);
    }

    private static void RunFrame(RenderViews views, in CameraView camera, ref int total)
    {
        views.BeginFrame();
        for (var view = 0; view < views.Count; view++)
        {
            views.Draw(view, in camera);
            views.Show(view, 10, 10, 128, 128);
            if (views.IsDue(view, out var asked))
            {
                views.Drawn(view);
                total += asked.Projection == CameraProjection.Perspective ? 1 : 0;
            }
        }

        total += views.Panels.Length;
        _ = views.Find("mirror");
        _ = views.Info(0);
    }

    /// <summary>What a frame system asked for this frame, as a game's tests read it: nothing before the ask, the camera after, nothing next frame.</summary>
    [Fact]
    public void Asked_gives_this_frames_ask_and_its_camera()
    {
        var views = new RenderViews();
        var map = views.Declare("map", 64, 64);
        var camera = CameraView.Default with { Position = new System.Numerics.Vector3(1, 2, 3) };

        var before = views.Asked(map, out _);
        views.Draw(map, in camera);
        var asked = views.Asked(map, out var given);
        views.BeginFrame();

        Assert.False(before);
        Assert.True(asked);
        Assert.Equal(camera, given);
        Assert.False(views.Asked(map, out _));
    }

    /// <summary>A view is shown on a material by the asset's and the material's names, once a material, before the seal.</summary>
    [Fact]
    public void A_view_is_shown_on_a_named_material_once_before_the_seal()
    {
        var views = new RenderViews();
        var first = views.Declare("first", 64, 64);
        var second = views.Declare("second", 64, 64);

        views.ShowOnMaterial(second, "screen-asset", "screen");
        views.ShowOnMaterial(first, "screen-asset", "other");

        Assert.Equal(second, views.ViewOnMaterial("screen-asset", "screen"));
        Assert.Equal(first, views.ViewOnMaterial("screen-asset", "other"));
        Assert.Equal(-1, views.ViewOnMaterial("screen-asset", "Screen"));
        Assert.Equal(2, views.MaterialViews.Count);
        Assert.Throws<ArgumentException>(() => views.ShowOnMaterial(first, "screen-asset", "screen"));
        Assert.Throws<ArgumentException>(() => views.ShowOnMaterial(first, " ", "screen"));
        Assert.Throws<ArgumentOutOfRangeException>(() => views.ShowOnMaterial(2, "a", "b"));
        views.Seal();
        Assert.Throws<InvalidOperationException>(() => views.ShowOnMaterial(first, "a", "b"));
    }
}
