using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Trigger zones (<see cref="PhysicsWorld.AddTrigger"/>) and their events: a ball falling through enters and leaves once
/// and falls as fast as one without the zone; a ball asleep inside stays inside; removing the ball or the zone gives a
/// removed leave; a zone moved onto a resting ball and away; layers; a kinematic body (a mirror) is seen; each shape
/// sees what overlaps it; a move of the origin changes nothing; the events are the same on one thread and on four;
/// overlaps past the buffers are dropped and counted; and stepping with zones allocates nothing.
/// </summary>
public sealed class PhysicsTriggersTests
{
    private const float Step = 1f / 60;

    [Fact]
    public void A_ball_falling_through_a_zone_enters_and_leaves_once_and_falls_as_fast_as_without_it()
    {
        using var physics = World();
        using var plainWorld = World();
        var zone = physics.AddTrigger(
            TriggerShape.Box(new Vector3(1, 0.5f, 1)),
            new Vector3(0, 2, 0),
            Quaternion.Identity
        );
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 4, 0)));
        var plain = plainWorld.AddBody(Ball(plainWorld, new Vector3(0, 4, 0)));
        var events = new List<TriggerEvent>();

        for (var i = 0; i < 60; i++)
        {
            physics.Step(Step);
            plainWorld.Step(Step);
            events.AddRange(physics.TriggerEvents.ToArray());
        }

        Assert.Equal(
            [
                new TriggerEvent(TriggerChange.Entered, zone, ball, physics.BornOf(ball), false),
                new TriggerEvent(TriggerChange.Left, zone, ball, physics.BornOf(ball), false),
            ],
            events
        );
        Assert.Equal(Position(plainWorld, plain), Position(physics, ball));
    }

    [Fact]
    public void A_ball_asleep_inside_a_zone_stays_inside()
    {
        using var physics = World();
        Floor(physics);
        var zone = physics.AddTrigger(
            TriggerShape.Box(new Vector3(2, 1, 2)),
            new Vector3(0, 0.5f, 0),
            Quaternion.Identity
        );
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 0.3f, 0)));
        var events = Run(physics, 300);

        Assert.False(physics.Simulation.Bodies[ball].Awake);
        Assert.Equal([TriggerChange.Entered], events.Select(change => change.Change));
        Assert.True(physics.IsInside(zone, ball));
    }

    [Fact]
    public void Removing_the_ball_or_the_zone_gives_a_removed_leave_at_the_next_step()
    {
        using var physics = World();
        Floor(physics);
        var zone = physics.AddTrigger(TriggerShape.Sphere(1), new Vector3(0, 0.5f, 0), Quaternion.Identity);
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 0.1f, 0)));
        var other = physics.AddBody(Ball(physics, new Vector3(0.3f, 0.1f, 0)));
        Run(physics, 5);
        var born = physics.BornOf(ball);

        physics.RemoveBody(ball);
        // The handle taken again at once: the new body is not the one that left.
        var next = physics.AddBody(Ball(physics, new Vector3(5, 0.1f, 0)));
        var afterBall = Run(physics, 1);
        physics.RemoveTrigger(zone);
        var afterZone = Run(physics, 1);

        Assert.Equal(ball, next);
        Assert.Equal([new TriggerEvent(TriggerChange.Left, zone, ball, born, true)], afterBall);
        Assert.Equal([new TriggerEvent(TriggerChange.Left, zone, other, physics.BornOf(other), true)], afterZone);
        Assert.False(physics.HoldsTrigger(zone));
        Assert.Equal(0, physics.TriggerCount);
        Assert.Empty(Run(physics, 3));
    }

    [Fact]
    public void A_zone_moved_onto_a_resting_ball_sees_it_enter_and_moved_away_leave()
    {
        using var physics = World();
        Floor(physics);
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 0.1f, 0)));
        var zone = physics.AddTrigger(TriggerShape.Box(Vector3.One), new Vector3(5, 0.5f, 0), Quaternion.Identity);
        Assert.Empty(Run(physics, 240));
        Assert.False(physics.Simulation.Bodies[ball].Awake);

        physics.MoveTrigger(zone, new Vector3(0, 0.5f, 0), Quaternion.Identity);
        var came = Run(physics, 2);
        physics.MoveTrigger(zone, new Vector3(0, 3, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f));
        var went = Run(physics, 2);

        Assert.Equal([TriggerChange.Entered], came.Select(change => change.Change));
        Assert.Equal([TriggerChange.Left], went.Select(change => change.Change));
        Assert.False(physics.Simulation.Bodies[ball].Awake, "The zone woke the ball.");
    }

    [Fact]
    public void A_zone_sees_only_bodies_of_layers_that_touch_its_own()
    {
        var layers = new PhysicsLayers();
        var zones = layers.Register("zones");
        var ghosts = layers.Register("ghosts");
        layers.SetTouches(zones, ghosts, false);
        using var physics = World(layers: layers);
        physics.AddTrigger(TriggerShape.Box(new Vector3(4, 0.5f, 1)), new Vector3(0, 2, 0), Quaternion.Identity, zones);
        var seen = physics.AddBody(Ball(physics, new Vector3(-1, 4, 0)));
        physics.AddBody(Ball(physics, new Vector3(1, 4, 0)), new PhysicsBodySettings { Layer = ghosts });

        var events = Run(physics, 60);

        Assert.Equal(2, events.Count);
        Assert.All(events, change => Assert.Equal(seen, change.Body));
    }

    [Fact]
    public void A_zone_sees_a_kinematic_body_such_as_a_clients_mirror()
    {
        using var physics = World();
        var zone = physics.AddTrigger(TriggerShape.Sphere(0.5f), Vector3.Zero, Quaternion.Identity);
        var mirror = physics.Simulation.Bodies.Add(
            BodyDescription.CreateKinematic(
                new RigidPose(new Vector3(3, 0, 0)),
                new CollidableDescription(physics.Simulation.Shapes.Add(new Sphere(0.1f)), 0.1f),
                new BodyActivityDescription(-1)
            )
        );
        physics.Kinds.Set(mirror, BodyKind.Loose);
        Assert.Empty(Run(physics, 2));

        var reference = physics.Simulation.Bodies[mirror];
        reference.Pose.Position = Vector3.Zero;
        reference.UpdateBounds();
        var events = Run(physics, 2);

        Assert.Equal([new TriggerEvent(TriggerChange.Entered, zone, mirror, physics.BornOf(mirror), false)], events);
        physics.RemoveBody(mirror);
    }

    [Theory]
    [InlineData(TriggerShapeKind.Box)]
    [InlineData(TriggerShapeKind.Sphere)]
    [InlineData(TriggerShapeKind.Capsule)]
    public void Each_shape_sees_the_bodies_that_overlap_it(TriggerShapeKind kind)
    {
        using var physics = World();
        physics.SetGravity(Vector3.Zero);
        // A box 1 x 2 x 1, a sphere of radius 1, a capsule of radius 0.5 and 1 m long, each turned a quarter round z: its
        // long axis along x.
        var shape = kind switch
        {
            TriggerShapeKind.Box => TriggerShape.Box(new Vector3(1, 2, 1)),
            TriggerShapeKind.Sphere => TriggerShape.Sphere(1),
            _ => TriggerShape.Capsule(0.5f, 1),
        };
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var zone = physics.AddTrigger(in shape, Vector3.Zero, turn);
        // Balls of 10 cm: one at the middle, one along the long axis 0.95 m out (in each), one 0.9 m up (in the sphere
        // only), and one 1.2 m out along z (in none).
        var middle = physics.AddBody(Ball(physics, Vector3.Zero));
        var along = physics.AddBody(Ball(physics, new Vector3(0.95f, 0, 0)));
        var up = physics.AddBody(Ball(physics, new Vector3(0, 0.9f, 0)));
        var far = physics.AddBody(Ball(physics, new Vector3(0, 0, 1.2f)));

        Run(physics, 2);

        Assert.True(physics.IsInside(zone, middle));
        Assert.True(physics.IsInside(zone, along));
        Assert.Equal(kind == TriggerShapeKind.Sphere, physics.IsInside(zone, up));
        Assert.False(physics.IsInside(zone, far));
    }

    [Fact]
    public void A_zone_refuses_a_pose_that_is_not_finite_and_a_size_of_nothing()
    {
        using var physics = World();
        var zone = physics.AddTrigger(TriggerShape.Sphere(1), Vector3.Zero, Quaternion.Identity);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            physics.AddTrigger(TriggerShape.Sphere(1), new Vector3(float.NaN, 0, 0), Quaternion.Identity)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.MoveTrigger(zone, Vector3.Zero, default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            physics.AddTrigger(TriggerShape.Box(new Vector3(1, 0, 1)), Vector3.Zero, Quaternion.Identity)
        );
        Assert.Equal(1, physics.TriggerCount);
    }

    [Fact]
    public void A_move_of_the_origin_keeps_what_is_inside_and_gives_no_event()
    {
        using var physics = World();
        Floor(physics);
        var zone = physics.AddTrigger(TriggerShape.Box(Vector3.One), new Vector3(0, 0.5f, 0), Quaternion.Identity);
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 0.1f, 0)));
        Run(physics, 10);

        physics.Shift(new Vector3(-512, 0, 300));
        var events = Run(physics, 10);

        Assert.Empty(events);
        Assert.True(physics.IsInside(zone, ball));
        Assert.Equal(new Vector3(512, 0.5f, -300), physics.TriggerOf(zone).Pose.Position);
    }

    [Fact]
    public void The_events_are_the_same_on_one_thread_and_on_four()
    {
        Assert.Equal(Rain(1), Rain(4));
    }

    [Fact]
    public void Overlaps_past_the_buffers_are_dropped_and_counted()
    {
        using var physics = World();
        physics.SetGravity(Vector3.Zero);
        // 256 zones in one place and 20 balls in them: 5,120 overlaps, past the 4,096 followed.
        for (var i = 0; i < PhysicsWorld.MostTriggers; i++)
        {
            physics.AddTrigger(TriggerShape.Sphere(1), Vector3.Zero, Quaternion.Identity);
        }

        Assert.Throws<InvalidOperationException>(() =>
            physics.AddTrigger(TriggerShape.Sphere(1), Vector3.Zero, Quaternion.Identity)
        );
        for (var i = 0; i < 20; i++)
        {
            physics.AddBody(Ball(physics, new Vector3(i * 0.02f, 0, 0)));
        }

        var events = Run(physics, 1);

        Assert.Equal(PhysicsTriggers.MostPairs, events.Count);
        Assert.True(physics.TriggerEventsDropped >= 5120 - PhysicsTriggers.MostPairs);
    }

    [Fact]
    public void Stepping_with_zones_and_their_events_allocates_nothing()
    {
        using var physics = World();
        Floor(physics);
        var zones = new TriggerId[4];
        for (var i = 0; i < zones.Length; i++)
        {
            zones[i] = physics.AddTrigger(
                TriggerShape.Box(new Vector3(2, 0.4f, 2)),
                new Vector3(0, 0.5f + i, 0),
                Quaternion.Identity
            );
        }

        for (var i = 0; i < 12; i++)
        {
            physics.AddBody(Ball(physics, new Vector3(i * 0.15f - 0.8f, 2 + i * 0.3f, 0)));
        }

        // Every ball fallen through the zones and come to rest (Bepu makes its first contacts' types once), then the
        // lowest zone moved down over the balls on the floor and off them every 10 steps: they come in and leave.
        Run(physics, 240);
        var events = 0;
        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 120; i++)
        {
            if (i % 10 == 0)
            {
                var away = i % 20 == 0;
                physics.MoveTrigger(zones[0], new Vector3(away ? 5 : 0, 0.1f, 0), Quaternion.Identity);
            }

            physics.Step(Step);
            events += physics.TriggerEvents.Length;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(events >= 12, $"{events} events.");
    }

    /// <summary>
    /// Balls rained through three zones on <paramref name="threads"/> threads: every event of 3 s. The balls never touch
    /// each other (30 cm apart across, falling straight), so their paths do not hang on the threads' order.
    /// </summary>
    private static List<TriggerEvent> Rain(int threads)
    {
        using var physics = World(threads);
        Floor(physics);
        for (var i = 0; i < 3; i++)
        {
            physics.AddTrigger(
                TriggerShape.Box(new Vector3(3, 0.3f, 3)),
                new Vector3(0, 0.5f + i, 0),
                Quaternion.Identity
            );
        }

        for (var i = 0; i < 40; i++)
        {
            physics.AddBody(Ball(physics, new Vector3((i % 8) * 0.3f - 1.1f, 4 + i * 0.25f, (i / 8) * 0.3f - 0.6f)));
        }

        return Run(physics, 180);
    }

    private static List<TriggerEvent> Run(PhysicsWorld physics, int steps)
    {
        var events = new List<TriggerEvent>();
        for (var i = 0; i < steps; i++)
        {
            physics.Step(Step);
            events.AddRange(physics.TriggerEvents.ToArray());
        }

        return events;
    }

    private static Vector3 Position(PhysicsWorld physics, BodyHandle body) =>
        physics.Simulation.Bodies[body].Pose.Position;

    private static void Floor(PhysicsWorld physics) =>
        physics.AddStatic(
            new StaticDescription(new Vector3(0, -0.5f, 0), physics.Simulation.Shapes.Add(new Box(20, 1, 20)))
        );

    private static PhysicsWorld World(int threads = 1, PhysicsLayers? layers = null)
    {
        var options = new EngineOptions();
        options.Physics.Threads = threads;
        var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, Options.Create(options), null, layers);
        physics.Initialize();
        return physics;
    }

    private static BodyDescription Ball(PhysicsWorld physics, Vector3 at) =>
        BodyDescription.CreateDynamic(
            new RigidPose(at),
            new Sphere(0.1f).ComputeInertia(1),
            new CollidableDescription(physics.Simulation.Shapes.Add(new Sphere(0.1f)), 0.1f),
            new BodyActivityDescription(0.01f)
        );
}
