using System.Numerics;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Networking;
using Itera.Engine.Particles;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="MapResetSystem"/>: <c>cleanup</c> removes every loose body (a thrown ball and a cut piece, out of the
/// simulation and the ECS) at the next step and clears the particles, leaves a client's mirrors of the host's bodies
/// alone, does nothing unasked, is refused on a client, and allocates nothing. <c>reset</c> also puts the world back as
/// the seed made it and counts the reset.
/// </summary>
public sealed class MapResetSystemTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private static readonly QueryDescription s_bodies = new QueryDescription().WithAll<LooseBody>();

    private readonly PhysicsTestHarness _physics = new();
    private readonly ParticleEmitter _emitter = new(
        Options.Create(new EngineOptions()),
        NullLogger<ParticleEmitter>.Instance
    );
    private readonly WorldResets _resets = new();

    public MapResetSystemTests()
    {
        _emitter.Recipes.Register("test", new ParticleRecipe { LifetimeMin = 1, LifetimeMax = 2 });
    }

    public void Dispose() => _physics.Dispose();

    [Fact]
    public void Cleanup_removes_every_loose_body_and_clears_the_particles_at_the_next_step()
    {
        using var world = World.Create();
        var output = new CommandOutput();
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        using var system = Create(world, commands);
        var ball = Ball(world, new Vector3(1, 1, 0));
        var cut = _physics.CutBody(new Vector3(-1, 0.5f, 0));
        world.Create(new LooseBody { Body = cut }, new CutPiece());
        _emitter.Emit(new ParticleBurst { Recipe = 0, Count = 10 });

        Assert.True(commands.Run(["cleanup"], output));
        var before = world.CountEntities(in s_bodies);
        system.Update(in Step);
        var frame = _emitter.Drain(new GpuParticleBurst[16], 0);

        Assert.Equal(2, before);
        Assert.Equal(0, world.CountEntities(in s_bodies));
        Assert.Equal(0, _physics.Bodies.Count);
        Assert.False(_physics.Physics.Simulation.Bodies.BodyExists(ball));
        Assert.False(_physics.Physics.Simulation.Bodies.BodyExists(cut));
        Assert.True(frame.Clear);
        Assert.Equal(0, frame.Bursts);
        // The ball never goes back into the grid.
        Assert.Equal(0, _physics.Voxels.EditCount);
    }

    [Fact]
    public void Reset_cleans_up_puts_the_world_back_and_counts_the_reset_for_the_systems_after_it()
    {
        using var world = World.Create();
        var output = new CommandOutput();
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        using var system = Create(world, commands);
        var hole = new VoxelCoord(100, -20, 0);
        _physics.Voxels.Apply(VoxelEdit.Sphere(hole, 15, VoxelEditOperation.Clear));
        Ball(world, new Vector3(1, 1, 0));

        Assert.True(commands.Run(["reset"], output));
        var counted = _resets.Count;
        system.Update(in Step);

        Assert.Equal(0, counted);
        Assert.Equal(1, _resets.Count);
        Assert.Equal(0, world.CountEntities(in s_bodies));
        Assert.Equal(0, _physics.Bodies.Count);
        // The reset is the log's newest edit: nothing settled into the grid after it.
        Assert.Equal(_physics.Voxels.EditCount, _physics.Voxels.LastReset);
        Assert.Equal(0, _physics.Voxels.ResidentColumns);
        var deadline = TestTime.Deadline(60);
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The world did not load again.");
            _physics.Voxels.Update(0);
            Thread.Sleep(1);
        } while (!_physics.Voxels.WantedColumnsResident);

        // The hole and the ball's are stone again.
        _physics.Voxels.TryGetMaterial(hole, out var filled);
        _physics.Voxels.TryGetMaterial(new VoxelCoord(-200, -13, 0), out var ball);
        Assert.Equal(FlatTestGenerator.Stone, filled);
        Assert.Equal(FlatTestGenerator.Stone, ball);
        system.Update(in Step);
        Assert.Equal(1, _resets.Count);
    }

    [Fact]
    public void Without_a_request_nothing_is_removed_and_mirrors_are_never_removed()
    {
        using var world = World.Create();
        using var system = Create(world);
        Ball(world, new Vector3(1, 1, 0));
        Assert.True(
            _physics.Bodies.TryCreateBallMirror(
                PhysicsTestHarness.BallRadius,
                FlatTestGenerator.Stone,
                new Vector3(-1, 1, 0),
                Quaternion.Identity,
                out var mirror
            )
        );
        world.Create(new LooseBody { Body = mirror, BallRadius = PhysicsTestHarness.BallRadius }, new RemoteBody());

        system.Update(in Step);
        var unasked = world.CountEntities(in s_bodies);
        system.RequestCleanup();
        system.Update(in Step);

        Assert.Equal(2, unasked);
        Assert.Equal(1, world.CountEntities(in s_bodies));
        Assert.True(_physics.Bodies.Contains(mirror));
    }

    [Fact]
    public void A_client_can_neither_clean_up_nor_reset()
    {
        using var world = World.Create();
        var output = new CommandOutput();
        var commands = new ConsoleCommands(() => SessionRole.Client, NullLogger<ConsoleCommands>.Instance);
        using var system = Create(world, commands);

        Assert.False(commands.Run(["cleanup"], output));
        Assert.False(commands.Run(["reset"], output));
    }

    [Fact]
    public void Steps_and_a_cleanup_allocate_nothing()
    {
        using var world = World.Create();
        using var system = Create(world);
        // The first cleanup grows the list of entities to destroy.
        for (var i = 0; i < 4; i++)
        {
            Ball(world, new Vector3(-1 + i * 0.5f, 1, 0));
        }

        system.RequestCleanup();
        system.Update(in Step);
        var idle = SystemAllocation.BytesAllocated(system, Step);
        Ball(world, new Vector3(0, 1, 1));
        Ball(world, new Vector3(0.5f, 1, 1));
        system.RequestCleanup();
        system.Update(in Step);
        system.RequestCleanup();

        var before = SystemAllocation.StartMeasurement();
        system.Update(in Step);
        var cleanup = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, idle);
        Assert.Equal(0, cleanup);
        Assert.Equal(0, world.CountEntities(in s_bodies));
    }

    [Fact]
    public void Without_voxel_terrain_reset_cleans_up_and_counts_the_reset()
    {
        using var harness = new MeshGroundHarness();
        using var world = World.Create();
        using var system = new MapResetSystem(world, harness.Bodies, _emitter, null, _resets);
        system.Initialize();
        var ball = harness.ThrowBall(new Vector3(1, 1, 0));
        world.Create(new LooseBody { Body = ball, BallRadius = MeshGroundHarness.BallRadius });
        var before = _resets.Count;

        system.RequestReset();
        system.Update(in Step);

        Assert.Equal(0, world.CountEntities(in s_bodies));
        Assert.Equal(0, harness.Bodies.Count);
        Assert.Equal(before + 1, _resets.Count);
    }

    private MapResetSystem Create(World world, ConsoleCommands? commands = null)
    {
        var system = new MapResetSystem(world, _physics.Bodies, _emitter, _physics.Voxels, _resets, commands);
        system.Initialize();
        return system;
    }

    private BepuPhysics.BodyHandle Ball(World world, Vector3 pivot)
    {
        var body = _physics.ThrowBall(pivot);
        world.Create(new LooseBody { Body = body, BallRadius = PhysicsTestHarness.BallRadius });
        return body;
    }

    private sealed class CommandOutput : IConsoleOutput
    {
        public void Print(string text) { }

        public void PrintError(string text) { }
    }
}
