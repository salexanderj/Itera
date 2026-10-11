using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The match's gravity and each body's share of it (<c>Engine:Physics:Gravity</c>, <see cref="PhysicsWorld.SetGravity"/>,
/// <see cref="PhysicsWorld.SetGravityScale"/>): the option is taken and its share of the Earth's is exactly 1 at the
/// default; a free body falls by a gravity set during play; bodies of shares 1, 0, 0.3, and -1 stepped together each fall
/// (or rise) by their own; a sleeping box wakes and goes when gravity turns sideways; a stand-in falling by a share reads
/// no push; values not finite are refused; and stepping with shares allocates nothing.
/// </summary>
public sealed class PhysicsGravityTests
{
    private const float Step = PhysicsTestHarness.Step;

    [Fact]
    public void The_option_is_taken_and_its_share_is_exactly_one_at_the_default()
    {
        using var earth = World();
        var options = new EngineOptions();
        options.Physics.Gravity.X = 3;
        options.Physics.Gravity.Y = -4;
        using var set = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, Options.Create(options));

        Assert.Equal(PhysicsWorld.DefaultGravity, earth.Gravity);
        Assert.Equal(1f, earth.GravityShare);
        Assert.Equal(new Vector3(3, -4, 0), set.Gravity);
        Assert.Equal(5 / PhysicsWorld.StandardGravity, set.GravityShare, 6);
    }

    [Fact]
    public void A_free_body_falls_by_a_gravity_set_during_play()
    {
        using var physics = World();
        physics.SetDamping(0, 0);
        var body = physics.AddBody(Ball(physics, new Vector3(0, 10, 0)));
        physics.Step(Step);

        physics.SetGravity(new Vector3(1, -2, 3));
        for (var i = 0; i < 30; i++)
        {
            physics.Step(Step);
        }

        var expected = PhysicsWorld.DefaultGravity * Step + new Vector3(1, -2, 3) * (30 * Step);
        AssertNear(expected, physics.Simulation.Bodies[body].Velocity.Linear);
        Assert.Equal(new Vector3(1, -2, 3), physics.Gravity);
    }

    [Fact]
    public void Bodies_stepped_together_each_fall_by_their_own_share()
    {
        using var physics = World();
        physics.SetDamping(0, 0);
        float[] shares = [1, 0, 0.3f, -1, 1, 0, 0.3f, -1, 2];
        var bodies = new BodyHandle[shares.Length];
        for (var i = 0; i < shares.Length; i++)
        {
            bodies[i] = physics.AddBody(Ball(physics, new Vector3(i, 10, 0)), gravityScale: shares[i]);
        }

        for (var i = 0; i < 30; i++)
        {
            physics.Step(Step);
        }

        for (var i = 0; i < shares.Length; i++)
        {
            Assert.Equal(shares[i], physics.GravityScaleOf(bodies[i]));
            AssertNear(PhysicsWorld.DefaultGravity * (shares[i] * 30 * Step), Velocity(physics, bodies[i]));
        }

        // Back to all of gravity: the integrator reads no shares again.
        foreach (var body in bodies)
        {
            physics.SetGravityScale(body, 1);
        }

        Assert.Equal(0, physics.Kinds.ScaledBodies);
    }

    [Fact]
    public void A_sleeping_box_wakes_and_goes_when_gravity_turns_sideways()
    {
        using var harness = new PhysicsTestHarness();
        var physics = harness.Physics;
        var box = physics.AddBody(
            BodyDescription.CreateConvexDynamic(
                new RigidPose(new Vector3(0, 0.2f, 0)),
                1,
                physics.Simulation.Shapes,
                new Box(0.3f, 0.3f, 0.3f)
            )
        );
        harness.RunPhysics(240);
        var slept = !physics.Simulation.Bodies[box].Awake;
        var x = physics.Simulation.Bodies[box].Pose.Position.X;

        physics.SetGravity(new Vector3(PhysicsWorld.StandardGravity, 0, 0));
        var woke = physics.Simulation.Bodies[box].Awake;
        harness.RunPhysics(30);

        Assert.True(slept);
        Assert.True(woke);
        Assert.True(physics.Simulation.Bodies[box].Pose.Position.X > x + 0.5f);
        physics.RemoveBody(box);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_stand_in_falling_by_a_share_of_a_gravity_set_during_play_reads_no_push(int substeps)
    {
        using var physics = World();
        physics.SetSolver(8, substeps);
        physics.SetGravity(new Vector3(2, -5, 1));
        var standIn = physics.AddStandIn(new Vector3(0, 10, 0), 0.26f, 1.7f, 80);
        physics.SetGravityScale(standIn, 0.5f);
        var placed = new Vector3(1, 2, 0);

        for (var i = 0; i < 5; i++)
        {
            physics.PlaceStandIn(standIn, new Vector3(0, 10.85f, 0), placed);
            physics.Step(Step);
            Assert.True(
                physics.PushOn(standIn, placed, Step).Length() < 1e-4f,
                $"{physics.PushOn(standIn, placed, Step)}"
            );
        }
    }

    [Fact]
    public void Values_not_finite_are_refused()
    {
        using var physics = World();
        var body = physics.AddBody(Ball(physics, Vector3.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetGravity(new Vector3(float.NaN, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetGravity(new Vector3(0, float.PositiveInfinity, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetGravityScale(body, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            physics.AddBody(Ball(physics, Vector3.One), gravityScale: float.NaN)
        );
        Assert.Equal(PhysicsWorld.DefaultGravity, physics.Gravity);
    }

    [Fact]
    public void Stepping_with_shares_allocates_nothing()
    {
        using var physics = World();
        for (var i = 0; i < 40; i++)
        {
            physics.AddBody(Ball(physics, new Vector3(i, 10, 0)), gravityScale: i % 3 == 0 ? 1 : 0.5f);
        }

        for (var i = 0; i < 60; i++)
        {
            physics.Step(Step);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 120; i++)
        {
            physics.Step(Step);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static PhysicsWorld World()
    {
        var options = new EngineOptions();
        options.Physics.Threads = 1;
        var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, Options.Create(options));
        physics.Initialize();
        return physics;
    }

    private static BodyDescription Ball(PhysicsWorld physics, Vector3 at) =>
        BodyDescription.CreateDynamic(
            new RigidPose(at),
            new Sphere(0.1f).ComputeInertia(1),
            new CollidableDescription(physics.Simulation.Shapes.Add(new Sphere(0.1f)), 0.1f),
            new BodyActivityDescription(-1)
        );

    private static Vector3 Velocity(PhysicsWorld physics, BodyHandle body) =>
        physics.Simulation.Bodies[body].Velocity.Linear;

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{actual} is not {expected}");
}
