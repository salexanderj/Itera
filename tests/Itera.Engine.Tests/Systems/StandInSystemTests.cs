using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Modding;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// Stand-ins: a character has one only near a loose body of a lifted piece or a thrown ball (not near a cut piece, which
/// touches only the voxels), and with the physics read and stand-in systems run in the fixed group's order with the
/// physics step after them, a ball thrown at a character pushes it.
/// </summary>
public sealed class StandInSystemTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private readonly PhysicsTestHarness _physics = new();
    private readonly ITestOutputHelper _output;

    public StandInSystemTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose() => _physics.Dispose();

    [Fact]
    public void A_character_has_a_stand_in_only_while_a_loose_body_is_near_and_it_is_not_flying()
    {
        using var world = World.Create();
        using var system = new StandInSystem(world, _physics.Physics, _physics.Bodies);
        system.Initialize();
        var character = world.Create(
            new CharacterStandIn(),
            new CharacterPosition(),
            CharacterSize.ForHeight(1.7f),
            new CharacterVelocity(),
            new PlayerMode()
        );

        system.Update(in Step);
        var alone = world.Get<CharacterStandIn>(character).Present;
        var ball = _physics.ThrowBall(new Vector3(3.5f, 0.2f, 0));
        system.Update(in Step);
        var far = world.Get<CharacterStandIn>(character).Present;
        world.Set(character, new CharacterPosition { Feet = new Vector3(2, 0, 0) });
        system.Update(in Step);
        var near = world.Get<CharacterStandIn>(character);
        var kind = _physics.Physics.KindOf(near.Body);
        world.Set(character, new PlayerMode { Flying = true });
        system.Update(in Step);
        var flying = world.Get<CharacterStandIn>(character).Present;
        world.Set(character, new PlayerMode());
        system.Update(in Step);
        _physics.Bodies.Remove(ball);
        system.Update(in Step);
        var gone = world.Get<CharacterStandIn>(character).Present;

        Assert.False(alone);
        Assert.False(far);
        Assert.True(near.Present);
        Assert.Equal(BodyKind.StandIn, kind);
        Assert.False(flying);
        Assert.False(_physics.Physics.Simulation.Bodies.BodyExists(near.Body));
        Assert.False(gone);
    }

    [Fact]
    public void A_bodiless_character_is_left_alone()
    {
        using var world = World.Create();
        using var system = new StandInSystem(world, _physics.Physics, _physics.Bodies);
        system.Initialize();
        var character = world.Create(
            new CharacterStandIn(),
            new CharacterPosition(),
            CharacterSize.ForHeight(1.7f),
            new CharacterVelocity(),
            new PlayerMode(),
            new Bodiless()
        );
        _physics.ThrowBall(new Vector3(1, 0.2f, 0));

        system.Update(in Step);
        var bodiless = world.Get<CharacterStandIn>(character).Present;
        world.Remove<Bodiless>(character);
        system.Update(in Step);
        var withBody = world.Get<CharacterStandIn>(character).Present;

        Assert.False(bodiless);
        Assert.True(withBody);
    }

    [Fact]
    public void A_character_that_becomes_bodiless_loses_its_stand_in()
    {
        using var world = World.Create();
        using var system = new StandInSystem(world, _physics.Physics, _physics.Bodies);
        system.Initialize();
        var character = world.Create(
            new CharacterStandIn(),
            new CharacterPosition(),
            CharacterSize.ForHeight(1.7f),
            new CharacterVelocity(),
            new PlayerMode()
        );
        _physics.ThrowBall(new Vector3(1, 0.2f, 0));
        system.Update(in Step);
        var body = world.Get<CharacterStandIn>(character).Body;
        Assert.True(world.Get<CharacterStandIn>(character).Present);

        world.Add(character, new Bodiless());
        system.Update(in Step);

        Assert.False(world.Get<CharacterStandIn>(character).Present);
        Assert.False(_physics.Physics.Simulation.Bodies.BodyExists(body));
    }

    [Fact]
    public void A_ball_thrown_at_a_character_pushes_it()
    {
        using var world = World.Create();
        var (read, standIns) = CreateSystems(world);
        var character = world.Create(
            new CharacterStandIn(),
            new CharacterPosition(),
            CharacterSize.ForHeight(1.7f),
            new CharacterVelocity(),
            new CharacterContact(),
            new PlayerMode()
        );
        Tick(read, standIns);
        // Chest high, 1.5 m away, at 8 m/s.
        var ball = _physics.ThrowBall(new Vector3(1.5f, 1.2f, 0), new Vector3(-8, 0, 0));

        var strongest = 0f;
        var push = 0f;
        for (var i = 0; i < 40; i++)
        {
            Tick(read, standIns);
            strongest = MathF.Max(strongest, world.Get<CharacterStandIn>(character).Impulse);
            push = MathF.Min(push, world.Get<CharacterVelocity>(character).Push.X);
        }

        _output.WriteLine($"Strongest hit {strongest:F1} N s; the strongest push {push:F2} m/s.");
        Assert.True(strongest > 10, $"The strongest hit was {strongest:F1} N s.");
        Assert.True(push < -0.005f, $"The strongest push was {push:F3} m/s.");
        Assert.True(_physics.Physics.Simulation.Bodies[ball].Velocity.Linear.X > -8);
    }

    [Fact]
    public void A_cut_piece_gives_no_stand_in()
    {
        using var world = World.Create();
        using var system = new StandInSystem(world, _physics.Physics, _physics.Bodies);
        system.Initialize();
        var character = world.Create(
            new CharacterStandIn(),
            new CharacterPosition(),
            CharacterSize.ForHeight(1.7f),
            new CharacterVelocity(),
            new PlayerMode()
        );
        _physics.CutBody(new Vector3(0.5f, 0.1f, 0));

        system.Update(in Step);
        var nearCut = world.Get<CharacterStandIn>(character).Present;
        _physics.ThrowBall(new Vector3(1, 0.2f, 0));
        system.Update(in Step);
        var nearBall = world.Get<CharacterStandIn>(character).Present;

        Assert.False(nearCut);
        Assert.True(nearBall);
    }

    /// <summary>
    /// A stand-in whose character is destroyed, or loses its stand-in component, is removed at the next step, where it
    /// stayed in the physics for the rest of the match; one made that step for a character that took the destroyed
    /// one's id is kept.
    /// </summary>
    [Fact]
    public void A_stand_in_whose_character_is_gone_is_removed_at_the_next_step()
    {
        using var world = World.Create();
        using var system = new StandInSystem(world, _physics.Physics, _physics.Bodies);
        system.Initialize();
        _physics.ThrowBall(new Vector3(1, 0.2f, 0));
        var destroyed = Character(world, Vector3.Zero);
        var stripped = Character(world, new Vector3(0, 0, 1));
        system.Update(in Step);
        var first = world.Get<CharacterStandIn>(destroyed).Body;
        var second = world.Get<CharacterStandIn>(stripped).Body;
        var firstBorn = _physics.Physics.BornOf(first);
        var secondBorn = _physics.Physics.BornOf(second);

        world.Destroy(destroyed);
        world.Remove<CharacterStandIn>(stripped);
        var next = Character(world, new Vector3(0, 0, -1));
        system.Update(in Step);
        system.Update(in Step);

        Assert.False(_physics.Physics.Holds(first, firstBorn));
        Assert.False(_physics.Physics.Holds(second, secondBorn));
        var kept = world.Get<CharacterStandIn>(next);
        Assert.True(kept.Present);
        Assert.Equal(BodyKind.StandIn, _physics.Physics.KindOf(kept.Body));
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }

    [Fact]
    public void Placing_a_stand_in_allocates_nothing()
    {
        using var world = World.Create();
        using var system = new StandInSystem(world, _physics.Physics, _physics.Bodies);
        system.Initialize();
        world.Create(
            new CharacterStandIn(),
            new CharacterPosition(),
            CharacterSize.ForHeight(1.7f),
            new CharacterVelocity(),
            new PlayerMode()
        );
        _physics.ThrowBall(new Vector3(1, 0.2f, 0));
        system.Update(in Step);

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }

    private static Entity Character(World world, Vector3 feet) =>
        world.Create(
            new CharacterStandIn(),
            new CharacterPosition { Feet = feet },
            CharacterSize.ForHeight(1.7f),
            new CharacterVelocity(),
            new PlayerMode()
        );

    private (PhysicsReadSystem Read, StandInSystem StandIns) CreateSystems(World world)
    {
        var read = new PhysicsReadSystem(world, _physics.Bodies, _physics.Physics);
        var standIns = new StandInSystem(world, _physics.Physics, _physics.Bodies);
        read.Initialize();
        standIns.Initialize();
        return (read, standIns);
    }

    /// <summary>One fixed step: the two systems in the group's order, then the physics.</summary>
    private void Tick(PhysicsReadSystem read, StandInSystem standIns)
    {
        read.Update(in Step);
        standIns.Update(in Step);
        _physics.RunPhysics();
    }
}
