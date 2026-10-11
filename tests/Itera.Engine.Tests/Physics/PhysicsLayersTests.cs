using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Collision layers and groups (<see cref="PhysicsLayers"/>): the registry's rules and its table, the same both ways; the
/// engine's own rules come first (layers only take pairs away); a ball passes a wall of a layer it does not touch where
/// a plain one stops; a group falls through itself and lands on the floor; a change of the match's table wakes a body
/// asleep on a shelf it no longer touches; the mover is blocked by every mesh collider whatever its layer, and reads no
/// loose body whose layer does not touch the characters'; and stepping with layers and groups allocates nothing.
/// </summary>
public sealed class PhysicsLayersTests
{
    private const float Step = PhysicsTestHarness.Step;

    [Fact]
    public void The_registry_names_its_layers_once_keeps_its_table_both_ways_and_seals_at_the_first_match()
    {
        var layers = new PhysicsLayers();
        var gate = layers.Register("gate");
        var balls = layers.Register("balls");

        layers.SetTouches(balls, gate, false);

        Assert.Equal((1, 2, 3), (gate.Value, balls.Value, layers.Count));
        Assert.True(layers.TryFind("gate", out var found) && found == gate);
        Assert.Equal("balls", layers.NameOf(balls));
        Assert.False(layers.Touches(gate, balls));
        Assert.False(layers.Touches(balls, gate));
        Assert.True(layers.Touches(gate, gate));
        Assert.True(layers.Touches(PhysicsLayerId.Default, gate));
        Assert.Throws<ArgumentException>(() => layers.Register("gate"));
        Assert.Throws<ArgumentOutOfRangeException>(() => layers.SetTouches(gate, new PhysicsLayerId(9), false));
        for (var i = layers.Count; i < PhysicsLayers.MaxLayers; i++)
        {
            layers.Register("l" + i);
        }

        Assert.Throws<InvalidOperationException>(() => layers.Register("one more"));
        var sealedOnes = new PhysicsLayers();
        using var physics = World(sealedOnes);
        Assert.True(sealedOnes.IsSealed);
        Assert.Throws<InvalidOperationException>(() => sealedOnes.Register("late"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            physics.AddBody(Ball(physics, Vector3.Zero), new PhysicsBodySettings { Layer = new PhysicsLayerId(1) })
        );
    }

    [Fact]
    public void The_engines_rules_come_first_and_layers_and_groups_only_take_pairs_away()
    {
        var layers = new PhysicsLayers();
        var apart = layers.Register("apart");
        layers.SetTouches(apart, PhysicsLayerId.Default, false);
        using var physics = World(layers);
        var loose = physics.AddBody(Ball(physics, Vector3.Zero));
        var other = physics.AddBody(Ball(physics, Vector3.One));
        var away = physics.AddBody(Ball(physics, -Vector3.One), new PhysicsBodySettings { Layer = apart });
        var first = physics.AddBody(Ball(physics, 2 * Vector3.One), new PhysicsBodySettings { Group = 7 });
        var second = physics.AddBody(Ball(physics, 3 * Vector3.One), new PhysicsBodySettings { Group = 7 });
        var standIn = physics.AddStandIn(new Vector3(5, 0, 0), 0.3f, 1.7f, 70);
        var cut = physics.AddBody(Ball(physics, new Vector3(6, 0, 0)));
        physics.Kinds.Set(cut, BodyKind.Cut);
        var callbacks = new NarrowPhaseCallbacks(physics.Kinds);

        Assert.True(Allows(callbacks, loose, other));
        Assert.False(Allows(callbacks, loose, away));
        Assert.True(Allows(callbacks, away, away));
        Assert.False(Allows(callbacks, first, second));
        Assert.True(Allows(callbacks, first, loose));
        // A cut body and a stand-in never touch, whatever their layers say.
        Assert.False(Allows(callbacks, cut, standIn));
        Assert.True(Allows(callbacks, loose, standIn));
    }

    [Fact]
    public void A_ball_passes_a_wall_of_a_layer_it_does_not_touch_where_a_plain_ball_stops()
    {
        var layers = new PhysicsLayers();
        var gate = layers.Register("gate");
        var balls = layers.Register("balls");
        layers.SetTouches(balls, gate, false);
        using var physics = World(layers);
        Floor(physics);
        // A wall 20 cm thick across x = 1, 2 m high.
        physics.AddStatic(
            new StaticDescription(new Vector3(1, 1, 0), physics.Simulation.Shapes.Add(new Box(0.2f, 2, 4))),
            new PhysicsBodySettings { Layer = gate }
        );
        var plain = physics.AddBody(Ball(physics, new Vector3(0, 0.1f, -1), new Vector3(3, 0, 0)));
        var passing = physics.AddBody(
            Ball(physics, new Vector3(0, 0.1f, 1), new Vector3(3, 0, 0)),
            new PhysicsBodySettings { Layer = balls }
        );

        Run(physics, 60);

        Assert.True(Position(physics, plain).X < 0.9f, $"The plain ball went through to {Position(physics, plain)}.");
        Assert.True(Position(physics, passing).X > 1.5f, $"The ball stopped at {Position(physics, passing)}.");
        Assert.Equal(0.1f, Position(physics, passing).Y, 0.01f);
    }

    [Fact]
    public void A_group_falls_through_itself_onto_the_floor_where_others_stack()
    {
        using var physics = World(new PhysicsLayers());
        Floor(physics);
        var lower = physics.AddBody(Box(physics, new Vector3(0, 0.25f, 0)), new PhysicsBodySettings { Group = 3 });
        var upper = physics.AddBody(Box(physics, new Vector3(0, 0.8f, 0)), new PhysicsBodySettings { Group = 3 });
        var below = physics.AddBody(Box(physics, new Vector3(2, 0.25f, 0)));
        var above = physics.AddBody(Box(physics, new Vector3(2, 0.8f, 0)), new PhysicsBodySettings { Group = 3 });

        Run(physics, 90);

        // Half-metre boxes: the group's two side by side on the floor, the other pair stacked.
        Assert.Equal(0.25f, Position(physics, lower).Y, 0.02f);
        Assert.Equal(0.25f, Position(physics, upper).Y, 0.02f);
        Assert.Equal(0.25f, Position(physics, below).Y, 0.02f);
        Assert.Equal(0.75f, Position(physics, above).Y, 0.02f);
        Assert.Equal(3, physics.GroupOf(upper));
    }

    [Fact]
    public void Setting_layers_apart_in_a_match_wakes_a_body_asleep_on_a_shelf_and_it_falls()
    {
        var layers = new PhysicsLayers();
        var shelf = layers.Register("shelf");
        using var physics = World(layers);
        Floor(physics);
        physics.AddStatic(
            new StaticDescription(new Vector3(0, 1, 0), physics.Simulation.Shapes.Add(new Box(1, 0.1f, 1))),
            new PhysicsBodySettings { Layer = shelf }
        );
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 1.2f, 0)));
        Run(physics, 240);
        Assert.False(physics.Simulation.Bodies[ball].Awake);
        Assert.Equal(1.15f, Position(physics, ball).Y, 0.01f);

        physics.SetTouches(PhysicsLayerId.Default, shelf, false);
        Run(physics, 60);

        Assert.False(physics.Touches(shelf, PhysicsLayerId.Default));
        Assert.True(layers.Touches(shelf, PhysicsLayerId.Default), "The run's table changed with the match's.");
        Assert.Equal(0.1f, Position(physics, ball).Y, 0.01f);
    }

    [Fact]
    public void The_mover_is_blocked_by_a_gate_of_any_layer_and_reads_no_body_that_does_not_touch_characters()
    {
        var layers = new PhysicsLayers();
        var characters = layers.Register("characters");
        var gate = layers.Register("gate");
        var ghosts = layers.Register("ghosts");
        layers.CharacterLayer = characters;
        layers.SetTouches(ghosts, characters, false);
        layers.SetTouches(gate, characters, false);
        using var harness = new MeshGroundHarness(layers: layers);
        Vector3[] wall = [new(2, 0, -2), new(2, 3, -2), new(2, 3, 2), new(2, 0, 2)];
        harness.Colliders.Add(
            wall,
            [0, 1, 2, 0, 2, 3],
            Matrix4x4.Identity,
            settings: new PhysicsBodySettings { Layer = gate }
        );
        var plain = harness.ThrowBall(new Vector3(-3, 0.12f, 0));
        var ghost = harness.ThrowBall(new Vector3(-3, 0.12f, 3));
        harness.Physics.SetLayer(ghost, ghosts);
        harness.RunPhysics(2);
        var body = new CharacterShape(1.7f, 0.3f, 0.3f);

        var feet = new Vector3(0, 0, 0);
        var velocity = new Vector3(5, 0, 0);
        var walk = harness.Mover.Move(ref feet, ref velocity, in body, 1);

        Assert.True(walk.HitWall);
        Assert.True(feet.X < 2, $"The body went through the gate to {feet}.");
        Assert.True(
            harness.Mover.OverlapsAnything(Position(harness.Physics, plain) - new Vector3(0, 0.1f, 0), in body)
        );
        Assert.False(
            harness.Mover.OverlapsAnything(Position(harness.Physics, ghost) - new Vector3(0, 0.1f, 0), in body)
        );
        Assert.True(harness.Bodies.AnyPieceNear(Position(harness.Physics, plain), 0.5f));
        Assert.False(harness.Bodies.AnyPieceNear(new Vector3(-3, 0.12f, 3), 1.5f));
    }

    [Fact]
    public void Stepping_with_layers_and_groups_allocates_nothing()
    {
        var layers = new PhysicsLayers();
        var odd = layers.Register("odd");
        layers.SetTouches(odd, odd, false);
        using var physics = World(layers);
        Floor(physics);
        for (var i = 0; i < 20; i++)
        {
            physics.AddBody(
                Ball(physics, new Vector3(i * 0.15f - 1.5f, 1 + i * 0.1f, 0)),
                new PhysicsBodySettings { Layer = i % 2 == 1 ? odd : PhysicsLayerId.Default, Group = i % 3 }
            );
        }

        Run(physics, 30);

        var before = SystemAllocation.StartMeasurement();
        Run(physics, 120);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static bool Allows(NarrowPhaseCallbacks callbacks, BodyHandle a, BodyHandle b)
    {
        var margin = 0f;
        return callbacks.AllowContactGeneration(
            0,
            new CollidableReference(CollidableMobility.Dynamic, a),
            new CollidableReference(CollidableMobility.Dynamic, b),
            ref margin
        );
    }

    private static void Run(PhysicsWorld physics, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            physics.Step(Step);
        }
    }

    private static Vector3 Position(PhysicsWorld physics, BodyHandle body) =>
        physics.Simulation.Bodies[body].Pose.Position;

    private static void Floor(PhysicsWorld physics) =>
        physics.AddStatic(
            new StaticDescription(new Vector3(0, -0.5f, 0), physics.Simulation.Shapes.Add(new Box(20, 1, 20)))
        );

    private static PhysicsWorld World(PhysicsLayers layers)
    {
        var options = new EngineOptions();
        options.Physics.Threads = 1;
        var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, Options.Create(options), null, layers);
        physics.Initialize();
        return physics;
    }

    private static BodyDescription Ball(PhysicsWorld physics, Vector3 at, Vector3 velocity = default) =>
        BodyDescription.CreateDynamic(
            new RigidPose(at),
            new BodyVelocity(velocity),
            new Sphere(0.1f).ComputeInertia(1),
            new CollidableDescription(
                physics.Simulation.Shapes.Add(new Sphere(0.1f)),
                ContinuousDetection.Continuous(1e-3f, 1e-3f)
            ),
            new BodyActivityDescription(0.01f)
        );

    private static BodyDescription Box(PhysicsWorld physics, Vector3 at) =>
        BodyDescription.CreateDynamic(
            new RigidPose(at),
            new Box(0.5f, 0.5f, 0.5f).ComputeInertia(1),
            new CollidableDescription(physics.Simulation.Shapes.Add(new Box(0.5f, 0.5f, 0.5f)), 0.1f),
            new BodyActivityDescription(0.01f)
        );
}
