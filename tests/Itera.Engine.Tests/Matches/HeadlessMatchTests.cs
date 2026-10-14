using System.Numerics;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// <see cref="HeadlessRun"/> and <see cref="HeadlessMatch"/> over the engine's real services and a test game: a match
/// played with no window or GPU, its fixed steps each followed by a frame, the run's view of it kept up, a retired
/// model removed after a frame, an image view's ask and panel forgotten at the next frame, the particles queued before a
/// frame dropped by it, and the match ended when disposed; and a host and a client in one process over an in-memory transport, the client's world downloaded with
/// terrain and following the host's edits, and a join with no host refused.
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
    public void A_headless_client_joins_a_host_with_terrain_and_follows_its_edits()
    {
        var network = new FakeNetwork();
        using var hostProvider = EngineComposition.Build(configure: services => InMemory(services, network));
        using var clientProvider = EngineComposition.Build(configure: services => InMemory(services, network));
        using var host = new HeadlessRun(hostProvider).Host(seed: 7);
        host.Match.Voxels.SetCenters([Vector3.Zero]);
        Assert.True(host.WaitUntilLoaded());
        // An edit before the join comes in the download.
        host.Match.Voxels.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 0, 0), 50, VoxelEditOperation.Clear));

        using var client = new HeadlessRun(clientProvider).Join(host);

        Assert.Equal(SessionRole.Client, client.Match.Session.Role);
        Assert.Equal(7UL, client.Match.Session.Match.Seed);
        Assert.Equal(host.Match.Voxels.EditCount, client.Match.Voxels.EditCount);
        client.Match.Voxels.SetCenters([Vector3.Zero]);
        Assert.True(client.WaitUntilLoaded());

        // An edit after the join goes after the host's step and arrives at the client's next poll.
        host.Match.Voxels.Apply(VoxelEdit.Sphere(new VoxelCoord(200, 0, 0), 50, VoxelEditOperation.Clear));
        host.Step(1);
        client.Step(1);

        Assert.Equal(2, client.Match.Voxels.EditCount);
        Assert.Equal(host.Match.Voxels.EditCount, client.Match.Voxels.EditCount);
        Assert.False(client.SessionEnded);
    }

    [Fact]
    public void A_headless_join_with_no_host_throws_and_leaves_no_match()
    {
        var network = new FakeNetwork();
        using var hostProvider = EngineComposition.Build(configure: services => InMemory(services, network));
        using var lonely = EngineComposition.Build(configure: services => InMemory(services, new FakeNetwork()));
        using var host = new HeadlessRun(hostProvider).Host(seed: 7);

        // The client's network has no host: its connection fails, and the join ends with the reason.
        var error = Assert.Throws<InvalidOperationException>(() => new HeadlessRun(lonely).Join(host));

        Assert.StartsWith("The headless join did not finish", error.Message, StringComparison.Ordinal);
        Assert.Null(lonely.GetRequiredService<MatchControl>().Current);
    }

    private static void InMemory(IServiceCollection services, FakeNetwork network) =>
        services.AddSingleton<ISessionTransports>(new Transports(new FakeTransport(network)));

    private sealed class Transports(FakeTransport transport) : ISessionTransports
    {
        public ISessionTransport Transport(SessionTransportKind kind) => transport;
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
    public void A_frame_drops_the_particles_queued_before_it_as_no_pool_makes_them()
    {
        using var provider = EngineComposition.Build(terrain: false);
        var run = new HeadlessRun(provider);
        using var match = run.Play(seed: 9);
        var particles = provider.GetRequiredService<Itera.Engine.Particles.ParticleEmitter>();
        particles.Enabled = true;
        var recipe = particles.Recipes.Register("headless test", new Itera.Engine.Particles.ParticleRecipe());
        for (var i = 0; i < particles.MaxBursts; i++)
        {
            particles.Emit(new Itera.Engine.Particles.ParticleBurst { Recipe = recipe, Count = 1 });
        }

        var queued = particles.QueuedBursts;
        match.Frame();

        // A run without graphics would otherwise fill the queue and drop every burst after it, with a warning.
        Assert.Equal(particles.MaxBursts, queued);
        Assert.Equal(0, particles.QueuedBursts);
        Assert.Equal(0, particles.DroppedBursts);
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
