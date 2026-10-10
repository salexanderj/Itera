using System.Numerics;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// <see cref="HeadlessRun"/> and <see cref="HeadlessMatch"/> over the engine's real services and a test game: a match
/// played with no window or GPU, its fixed steps each followed by a frame, the run's view of it kept up, a retired
/// model removed after a frame, an image view's ask and panel forgotten at the next frame, and the match ended when
/// disposed.
/// </summary>
public sealed class HeadlessMatchTests
{
    [Fact]
    public void A_headless_match_steps_its_fixed_and_frame_groups_and_ends_when_disposed()
    {
        using var provider = EngineComposition.Build();
        var run = new HeadlessRun(provider);
        var matches = provider.GetRequiredService<MatchControl>();

        using (var match = run.Play(seed: 5))
        {
            Assert.Same(match.Match, matches.Current);
            Assert.Equal(5UL, match.Match.Session.Match.Seed);
            // The test game streams round nothing by itself.
            match.Match.Voxels.SetCenters([System.Numerics.Vector3.Zero]);
            Assert.True(match.WaitUntilLoaded());
            var loadSteps = match.Tick;

            match.Step(30);

            var steps = match
                .Services.GetServices<IFixedUpdateSystem>()
                .OfType<EngineComposition.StepCounter>()
                .Single();
            var frames = match
                .Services.GetServices<IFrameUpdateSystem>()
                .OfType<EngineComposition.FrameCounter>()
                .Single();
            Assert.Equal((int)loadSteps + 30, steps.Steps);
            Assert.Equal(steps.Steps, frames.Frames);
            Assert.Equal(match.Tick / 60.0, match.Seconds, 6);
            Assert.Equal(MatchPhase.Playing, matches.Phase);
            Assert.Equal(1, match.World.Size);
        }

        Assert.Null(matches.Current);
        Assert.Equal(MatchPhase.Menu, matches.Phase);
    }

    [Fact]
    public void A_model_retired_without_graphics_goes_after_the_next_frame()
    {
        using var provider = EngineComposition.Build();
        var run = new HeadlessRun(provider);
        using var match = run.Play(seed: 5);
        match.Match.Voxels.SetCenters([Vector3.Zero]);
        Assert.True(match.WaitUntilLoaded());
        var bodies = match.Services.GetRequiredService<LooseBodies>();
        var models = match.Match.Models;
        var before = models.Count;

        // A ball filled back into the grid retires its model, which an uploader would draw until the world's meshes
        // hold its voxels; with no uploader, the next frame removes it.
        Assert.True(bodies.TryCreateBall(12, 1, new Vector3(0, 2, 0), Vector3.Zero, out var ball));
        bodies.Settle(ball);
        Assert.Equal(1, models.Retired.Length);
        Assert.Equal(before + 1, models.Count);

        match.Frame();

        Assert.True(models.Retired.IsEmpty);
        Assert.Equal(before, models.Count);
    }

    [Fact]
    public void A_frame_forgets_the_image_views_asked_for_and_shown_before_it_as_the_main_loop_does()
    {
        using var provider = EngineComposition.Build(terrain: false);
        var run = new HeadlessRun(provider);
        using var match = run.Play(seed: 9);
        var views = provider.GetRequiredService<RenderViews>();
        var view = views.Declare("test view", 64, 64);
        views.Draw(view, CameraView.Default);
        views.Show(view, 0, 0, 16, 16);
        Assert.True(views.Asked(view, out _));
        Assert.Equal(1, views.Panels.Length);

        match.Frame();

        // The test game's frame systems ask for nothing, so nothing is asked for or shown after the frame.
        Assert.False(views.Asked(view, out _));
        Assert.Equal(0, views.Panels.Length);
    }

    [Fact]
    public void A_match_without_terrain_loads_at_once_and_steps_its_groups()
    {
        using var provider = EngineComposition.Build(terrain: false);
        var run = new HeadlessRun(provider);
        var matches = provider.GetRequiredService<MatchControl>();

        using (var match = run.Play(seed: 9))
        {
            Assert.Null(match.Match.Terrain);
            Assert.Throws<InvalidOperationException>(() => match.Match.Voxels);
            Assert.True(match.WaitUntilLoaded(mostSeconds: 1));

            match.Step(30);

            var steps = match
                .Services.GetServices<IFixedUpdateSystem>()
                .OfType<EngineComposition.StepCounter>()
                .Single();
            Assert.Equal(32, steps.Steps);
            Assert.Equal(MatchPhase.Playing, matches.Phase);
        }

        Assert.Equal(MatchPhase.Menu, matches.Phase);
    }
}
