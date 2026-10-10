using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// <see cref="HeadlessRun"/> and <see cref="HeadlessMatch"/> over the engine's real services and a test game: a match
/// played with no window or GPU, its fixed steps each followed by a frame, the run's view of it kept up, and the match
/// ended when disposed.
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
