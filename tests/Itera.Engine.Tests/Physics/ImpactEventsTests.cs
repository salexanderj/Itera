using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Impact events (<see cref="PhysicsMarks.ContactEvents"/>, <see cref="PhysicsWorld.ImpactEvents"/>): drops from 1 and
/// 4 m report about 4.4 and 8.9 m/s, where they met, the normal, and both materials; a ball that rolls and comes to rest
/// (and sleeps) gives no new event; two marked balls meeting give one event for their pair as they meet and one as they
/// part; a marked static reports a plain ball; unmarked pairs give none; a removed ball ends its pair, removed; a move of
/// the origin moves the events' points; contacts past the buffers are dropped and counted; and stepping with impacts
/// allocates nothing.
/// </summary>
public sealed class ImpactEventsTests
{
    private const float Step = 1f / 60;

    private static readonly PhysicsBodySettings Marked = new() { Marks = PhysicsMarks.ContactEvents };

    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    public void A_dropped_ball_reports_the_speed_place_normal_and_materials_of_its_landing(float metres)
    {
        var materials = new PhysicsMaterials();
        var stone = materials.Register("stone", new PhysicsMaterial(0.8f, 0));
        using var physics = World(materials);
        var floor = Floor(physics, stone);
        var ball = physics.AddBody(Ball(physics, new Vector3(1, metres + 0.1f, 2)), Marked);

        var events = Run(physics, 120);

        // The first landing (contact springs may lift it a few millimetres after a fast one).
        var landing = events.First(change => change.Change == ImpactChange.Began);
        var expected = MathF.Sqrt(2 * PhysicsWorld.StandardGravity * metres);
        Assert.InRange(landing.Speed, 0.9f * expected, 1.1f * expected);
        Assert.Equal(new ImpactSide(ImpactSideKind.Body, ball.Value, physics.BornOf(ball)), landing.A);
        Assert.Equal(ImpactSideKind.Static, landing.B.Kind);
        Assert.Equal(floor, landing.B.Static);
        Assert.True(Vector3.Distance(landing.Point, new Vector3(1, 0, 2)) < 0.15f, $"It met at {landing.Point}.");
        Assert.True(landing.Normal.Y > 0.99f, $"Its normal was {landing.Normal}.");
        Assert.Equal((PhysicsMaterialId.Default, stone), (landing.MaterialA, landing.MaterialB));
    }

    [Fact]
    public void A_ball_that_rolls_and_comes_to_rest_asleep_gives_no_new_event()
    {
        using var physics = World(new PhysicsMaterials());
        // Damped hard, so it rolls to a stop and sleeps within the run (Bepu has no rolling friction).
        physics.SetDamping(0.9f, 0.9f);
        Floor(physics, PhysicsMaterialId.Default);
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 0.12f, 0), new Vector3(2, 0, 0)), Marked);

        var events = Run(physics, 600);

        Assert.False(physics.Simulation.Bodies[ball].Awake);
        Assert.Equal([ImpactChange.Began], events.Select(change => change.Change));
    }

    [Fact]
    public void Two_marked_balls_meeting_give_one_event_for_their_pair_as_they_meet_and_one_as_they_part()
    {
        var materials = new PhysicsMaterials();
        var rubber = materials.Register("rubber", new PhysicsMaterial(1, 1));
        using var physics = World(materials);
        physics.SetGravity(Vector3.Zero);
        // Rubber, so they part again.
        var settings = Marked with
        {
            Material = rubber,
        };
        var left = physics.AddBody(Ball(physics, new Vector3(-1, 0, 0), new Vector3(3, 0, 0)), settings);
        var right = physics.AddBody(Ball(physics, new Vector3(1, 0, 0), new Vector3(-3, 0, 0)), settings);

        var events = Run(physics, 60);

        Assert.Equal([ImpactChange.Began, ImpactChange.Ended], events.Select(change => change.Change));
        Assert.Equal((left.Value, right.Value), (events[0].A.Handle, events[0].B.Handle));
        Assert.InRange(events[0].Speed, 5.5f, 6.5f);
        // From B (the right ball) towards A (the left one).
        Assert.True(events[0].Normal.X < -0.99f, $"Its normal was {events[0].Normal}.");
    }

    [Fact]
    public void A_marked_static_reports_a_plain_ball_and_unmarked_pairs_give_none()
    {
        using var physics = World(new PhysicsMaterials());
        physics.AddStatic(
            new StaticDescription(new Vector3(-3, -0.5f, 0), physics.Simulation.Shapes.Add(new Box(4, 1, 4))),
            Marked
        );
        physics.AddStatic(
            new StaticDescription(new Vector3(3, -0.5f, 0), physics.Simulation.Shapes.Add(new Box(4, 1, 4)))
        );
        var seen = physics.AddBody(Ball(physics, new Vector3(-3, 1, 0)));
        physics.AddBody(Ball(physics, new Vector3(3, 1, 0)));

        var events = Run(physics, 90);

        // The plain ball on the marked box only (it may lift a hair after landing and land again).
        var landing = events[0];
        Assert.Equal(ImpactChange.Began, landing.Change);
        Assert.All(events, change => Assert.Equal(seen.Value, change.A.Handle));
        Assert.Equal(PhysicsMarks.ContactEvents, physics.MarksOf(landing.B.Static));
    }

    [Fact]
    public void A_removed_ball_ends_its_pair_removed_at_the_next_step()
    {
        using var physics = World(new PhysicsMaterials());
        Floor(physics, PhysicsMaterialId.Default);
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 0.1f, 0)), Marked);
        Run(physics, 5);
        var side = new ImpactSide(ImpactSideKind.Body, ball.Value, physics.BornOf(ball));

        physics.RemoveBody(ball);
        var events = Run(physics, 1);

        var ended = Assert.Single(events);
        Assert.Equal((ImpactChange.Ended, true, side), (ended.Change, ended.Removed, ended.A));
        Assert.Empty(Run(physics, 3));
    }

    [Fact]
    public void A_move_of_the_origin_moves_the_events_points()
    {
        using var physics = World(new PhysicsMaterials());
        Floor(physics, PhysicsMaterialId.Default);
        physics.AddBody(Ball(physics, new Vector3(0, 0.6f, 0)), Marked);
        for (var i = 0; i < 120 && physics.ImpactEvents.Length == 0; i++)
        {
            physics.Step(Step);
        }

        var before = Assert.Single(physics.ImpactEvents.ToArray()).Point;
        physics.Shift(new Vector3(512, 0, -256));

        Assert.Equal(before - new Vector3(512, 0, -256), physics.ImpactEvents[0].Point);
    }

    [Fact]
    public void Contacts_past_the_buffers_are_dropped_and_counted()
    {
        using var physics = World(new PhysicsMaterials());
        Floor(physics, PhysicsMaterialId.Default);
        // 65 x 65 marked balls resting on the floor: 4,225 pairs, past the 4,096 followed.
        for (var x = 0; x < 65; x++)
        {
            for (var z = 0; z < 65; z++)
            {
                physics.AddBody(Ball(physics, new Vector3(x * 0.25f - 8, 0.1f, z * 0.25f - 8)), Marked);
            }
        }

        var events = Run(physics, 1);

        Assert.Equal(PhysicsContacts.MostPairs, events.Count);
        Assert.True(physics.ContactsDropped >= 65 * 65 - PhysicsContacts.MostPairs, $"{physics.ContactsDropped}");
    }

    [Fact]
    public void Stepping_with_impacts_allocates_nothing()
    {
        var materials = new PhysicsMaterials();
        var rubber = materials.Register("rubber", new PhysicsMaterial(1, 1));
        using var physics = World(materials);
        // Bouncing without end, so none falls asleep (Bepu allocates a little as the last body does).
        physics.SetDamping(0, 0);
        Floor(physics, rubber);
        for (var i = 0; i < 16; i++)
        {
            physics.AddBody(Ball(physics, new Vector3(i * 0.5f - 4, 0.5f + i * 0.1f, 0)), Marked);
        }

        Run(physics, 60);

        var count = 0;
        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 240; i++)
        {
            physics.Step(Step);
            count += physics.ImpactEvents.Length;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(count > 16, $"{count} events.");
    }

    private static List<ImpactEvent> Run(PhysicsWorld physics, int steps)
    {
        var events = new List<ImpactEvent>();
        for (var i = 0; i < steps; i++)
        {
            physics.Step(Step);
            events.AddRange(physics.ImpactEvents.ToArray());
        }

        return events;
    }

    private static StaticHandle Floor(PhysicsWorld physics, PhysicsMaterialId material) =>
        physics.AddStatic(
            new StaticDescription(new Vector3(0, -0.5f, 0), physics.Simulation.Shapes.Add(new Box(20, 1, 20))),
            new PhysicsBodySettings { Material = material }
        );

    private static PhysicsWorld World(PhysicsMaterials materials)
    {
        var options = new EngineOptions();
        options.Physics.Threads = 1;
        var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, Options.Create(options), materials);
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
}
