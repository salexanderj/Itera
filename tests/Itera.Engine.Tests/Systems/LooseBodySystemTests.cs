using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;

namespace Itera.Engine.Tests.Systems;

public sealed class LooseBodySystemTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private static readonly QueryDescription s_bodies = new QueryDescription().WithAll<LooseBody>();

    // Bodies settle after half a second at rest here.
    private readonly PhysicsTestHarness _physics = new(settleSeconds: 0.5);

    public void Dispose() => _physics.Dispose();

    [Fact]
    public void A_resting_body_goes_back_into_the_grid_after_the_settle_time()
    {
        using var world = World.Create();
        using var system = Create(world);
        var body = _physics.ThrowBall(new Vector3(1, 0.5f, 0));
        world.Create(new LooseBody { Body = body, BallRadius = PhysicsTestHarness.BallRadius });

        var steps = Run(world, system, until: () => world.CountEntities(in s_bodies) == 0);

        // It falls 37 cm, rests, then waits 30 steps.
        Assert.InRange(steps, 30, 300);
        Assert.Equal(0, _physics.Bodies.Count);
        Assert.False(_physics.Physics.Simulation.Bodies.BodyExists(body));
        _physics.Voxels.TryGetMaterial(new VoxelCoord(100, 12, 0), out var ball);
        Assert.Equal(FlatTestGenerator.Stone, ball);
    }

    [Fact]
    public void A_body_that_touches_a_character_waits_until_it_moves_away()
    {
        using var world = World.Create();
        using var system = Create(world);
        var body = _physics.ThrowBall(new Vector3(1, 0.5f, 0));
        world.Create(new LooseBody { Body = body, BallRadius = PhysicsTestHarness.BallRadius });
        // A character whose cylinder reaches over the ball.
        var character = world.Create(
            new CharacterPosition { Feet = new Vector3(1.3f, 0, 0) },
            new CharacterSize { HeightMetres = 1.7f, RadiusMetres = 0.26f }
        );

        Run(world, system, steps: 200);
        var waiting = world.CountEntities(in s_bodies);
        world.Set(character, new CharacterPosition { Feet = new Vector3(3, 0, 0) });
        Run(world, system, steps: 2);

        Assert.Equal(1, waiting);
        Assert.Equal(0, world.CountEntities(in s_bodies));
    }

    [Fact]
    public void A_body_that_fell_out_of_the_world_is_removed()
    {
        using var world = World.Create();
        using var system = Create(world);
        var outside = VoxelGrid.Unit.EndVoxelXZ / 100f + 5;
        var body = _physics.ThrowBall(new Vector3(outside, 0.5f, 0));
        world.Create(new LooseBody { Body = body, BallRadius = PhysicsTestHarness.BallRadius });

        var steps = Run(world, system, until: () => world.CountEntities(in s_bodies) == 0);

        Assert.InRange(steps, 150, 300);
        Assert.Equal(0, _physics.Bodies.Count);
    }

    [Fact]
    public void A_cut_piece_left_frozen_behind_is_removed_after_its_rest_time()
    {
        using var world = World.Create();
        using var system = Create(world);
        var body = _physics.CutBody(new Vector3(0, 0.5f, 0));
        world.Create(new LooseBody { Body = body }, new CutPiece());
        Run(world, system, steps: 120);
        var rested = _physics.Bodies.IsAtRest(body);

        // The player walks away: the columns under the body unload, and the colliders freeze it.
        _physics.Voxels.SetCenters([new Vector3(40, 0, 0)]);
        var deadline = TestTime.Deadline(60);
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The world did not load.");
            _physics.Voxels.Update(0);
            Thread.Sleep(1);
        } while (_physics.Voxels.PendingColumns > 0);
        for (var i = 0; i < 10; i++)
        {
            _physics.Voxels.Update(0);
        }

        _physics.RunPhysics();
        var frozen = _physics.Colliders.IsFrozen(body);
        // A frozen body counts as waiting for a piece that goes back into the grid, but as resting for a cut one.
        var waiting = !_physics.Bodies.IsAtRest(body) && _physics.Bodies.IsAtRestOrFrozen(body);
        var cutTicks = (int)Math.Ceiling(LooseBodySystem.CutRestSeconds * 60);
        var steps = Run(world, system, steps: cutTicks + 60, until: () => world.CountEntities(in s_bodies) == 0);

        Assert.True(rested);
        Assert.True(frozen);
        Assert.True(waiting);
        Assert.InRange(steps, 1, cutTicks + 1);
        Assert.Equal(0, _physics.Bodies.Count);
        Assert.Equal(0, world.CountEntities(in s_bodies));
    }

    [Fact]
    public void A_cut_piece_that_never_rests_is_removed_at_its_most_time()
    {
        using var world = World.Create();
        using var system = Create(world);
        var body = _physics.CutBody(new Vector3(0, 0.5f, 0));
        var entity = world.Create(new LooseBody { Body = body }, new CutPiece());
        // Kept turning every step, as a ragdoll stuck in a tree kept moving.
        var mostTicks = (int)Math.Ceiling(LooseBodySystem.CutMostSeconds * 60);
        var steps = 0;
        var restedOnce = false;
        while (world.CountEntities(in s_bodies) > 0 && steps < mostTicks + 60)
        {
            var reference = _physics.Physics.Simulation.Bodies[body];
            reference.Awake = true;
            reference.Velocity.Angular = new Vector3(0, 3, 0);
            system.Update(in Step);
            _physics.RunPhysics();
            restedOnce |= world.IsAlive(entity) && world.Get<LooseBody>(entity).RestTicks > 0;
            steps++;
        }

        Assert.Equal(mostTicks, steps);
        Assert.Equal(0, _physics.Bodies.Count);
        Assert.False(restedOnce);
    }

    [Fact]
    public void Remote_mirrors_are_left_alone()
    {
        using var world = World.Create();
        using var system = Create(world);
        Assert.True(
            _physics.Bodies.TryCreateBallMirror(
                PhysicsTestHarness.BallRadius,
                FlatTestGenerator.Stone,
                new Vector3(1, 0.2f, 0),
                Quaternion.Identity,
                out var mirror
            )
        );
        world.Create(new LooseBody { Body = mirror }, new RemoteBody());

        Run(world, system, steps: 120);

        Assert.Equal(1, world.CountEntities(in s_bodies));
        Assert.True(_physics.Bodies.Contains(mirror));
    }

    [Fact]
    public void The_settle_command_shows_and_sets_the_time_a_ball_rests()
    {
        using var world = World.Create();
        var output = new CommandOutput();
        var commands = new Itera.Engine.Commands.ConsoleCommands(
            () => Itera.Engine.Networking.SessionRole.Offline,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Itera.Engine.Commands.ConsoleCommands>.Instance
        );
        using var system = new LooseBodySystem(world, _physics.Bodies, _physics.Options, console: commands);
        system.Initialize();

        Assert.True(commands.Run(["settle"], output));
        Assert.True(commands.Run(["settle", "2"], output));
        Assert.True(commands.Run(["settle", "0.1"], output));

        // A time outside 0.5 to 60 s prints the usage and changes nothing.
        Assert.Equal(120, system.SettleTicks);
        Assert.Equal("settle 0.5 s", output.Lines[0]);
        Assert.Equal("settle 2 s", output.Lines[1]);
        Assert.Equal(3, output.Lines.Count);
    }

    [Fact]
    public void Steps_with_a_moving_body_allocate_nothing()
    {
        using var world = World.Create();
        using var system = Create(world);
        var body = _physics.ThrowBall(new Vector3(1, 3, 0));
        world.Create(new LooseBody { Body = body, BallRadius = PhysicsTestHarness.BallRadius });

        // Falling from 3 m, the body is not at rest for the whole measurement.
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step, warmup: 5, measured: 20));
    }

    private LooseBodySystem Create(World world)
    {
        var system = new LooseBodySystem(world, _physics.Bodies, _physics.Options);
        system.Initialize();
        return system;
    }

    private int Run(World world, LooseBodySystem system, int steps = 1000, Func<bool>? until = null)
    {
        for (var i = 1; i <= steps; i++)
        {
            system.Update(in Step);
            _physics.RunPhysics();
            if (until?.Invoke() == true)
            {
                return i;
            }
        }

        return steps;
    }

    private sealed class CommandOutput : Itera.Engine.Commands.IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Lines.Add("error: " + text);
    }
}
