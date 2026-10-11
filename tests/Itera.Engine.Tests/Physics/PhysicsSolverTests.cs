using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The solver's settings and damping (<c>Engine:Physics</c>, <see cref="PhysicsWorld.SetSolver"/>,
/// <see cref="PhysicsWorld.SetDamping"/>): the defaults are the engine's before they were options (8 iterations, 1
/// substep, 0.03 damping); the options are taken; a pile keeps stepping when they change mid-match; values out of range
/// are refused; a free body and two joined ones fall as the integrator's sums say, at 4 substeps and half damping; and
/// an untouched stand-in reads no push at 1 and 4 substeps, with and without damping.
/// </summary>
public sealed class PhysicsSolverTests
{
    private const float Step = PhysicsTestHarness.Step;

    [Fact]
    public void The_defaults_are_the_engines_and_the_options_are_taken()
    {
        using var plain = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        plain.Initialize();
        var options = new EngineOptions();
        options.Physics.Substeps = 4;
        options.Physics.VelocityIterations = 12;
        options.Physics.LinearDamping = 0.1f;
        options.Physics.AngularDamping = 0.2f;
        using var set = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, Options.Create(options));
        set.Initialize();

        Assert.Equal(
            (8, 1, 0.03f, 0.03f),
            (plain.VelocityIterations, plain.Substeps, plain.LinearDamping, plain.AngularDamping)
        );
        Assert.Equal(
            (12, 4, 0.1f, 0.2f),
            (set.VelocityIterations, set.Substeps, set.LinearDamping, set.AngularDamping)
        );
    }

    [Fact]
    public void A_pile_keeps_stepping_when_the_solver_changes_mid_match()
    {
        using var harness = new PhysicsTestHarness();
        var balls = new List<BodyHandle>();
        for (var i = 0; i < 6; i++)
        {
            balls.Add(harness.ThrowBall(new Vector3(0.05f * i, 0.3f + 0.3f * i, 0)));
        }

        harness.RunPhysics(30);
        harness.Physics.SetSolver(16, 4);
        harness.RunPhysics(120);
        harness.Physics.SetSolver(4, 1);
        harness.RunPhysics(120);

        Assert.Equal((4, 1), (harness.Physics.VelocityIterations, harness.Physics.Substeps));
        foreach (var ball in balls)
        {
            var at = harness.Physics.Simulation.Bodies[ball].Pose.Position;
            Assert.True(float.IsFinite(at.X + at.Y + at.Z), $"{at}");
            // Resting on the floor (its top at 0) or on the others, not under it.
            Assert.InRange(at.Y, 0.05f, 2.5f);
        }
    }

    [Fact]
    public void Values_out_of_range_are_refused()
    {
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        physics.Initialize();

        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetSolver(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetSolver(33, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetSolver(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetSolver(8, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetDamping(-0.1f, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetDamping(0, 1.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.SetDamping(float.NaN, 0));
        Assert.Equal((8, 1, 0.03f), (physics.VelocityIterations, physics.Substeps, physics.LinearDamping));
    }

    [Fact]
    public void A_free_body_and_two_joined_ones_fall_as_the_integrator_says_at_four_substeps()
    {
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        physics.Initialize();
        physics.SetSolver(8, 4);
        physics.SetDamping(0.5f, 0.5f);
        var free = physics.AddBody(Ball(physics, new Vector3(0, 10, 0)));
        var a = physics.AddBody(Ball(physics, new Vector3(5, 10, 0)));
        var b = physics.AddBody(Ball(physics, new Vector3(5.5f, 10, 0)));
        physics.Simulation.Solver.Add(
            a,
            b,
            new BallSocket
            {
                LocalOffsetA = new Vector3(0.25f, 0, 0),
                LocalOffsetB = new Vector3(-0.25f, 0, 0),
                SpringSettings = new SpringSettings(30, 1),
            }
        );

        physics.Step(Step);

        // Without constraints, once over the step; with them, once a substep of a quarter of it.
        var keep = MathF.Pow(0.5f, Step);
        var once = PhysicsWorld.Gravity.Y * Step * keep;
        var substep = Step / 4;
        var sum = 0f;
        for (var k = 1; k <= 4; k++)
        {
            sum += MathF.Pow(0.5f, k * substep);
        }

        var substeps = PhysicsWorld.Gravity.Y * substep * sum;
        Assert.Equal(once, Velocity(physics, free).Y, 5);
        Assert.Equal(substeps, Velocity(physics, a).Y, 4);
        Assert.Equal(substeps, Velocity(physics, b).Y, 4);
        Assert.True(MathF.Abs(once - substeps) > 5e-4f);
    }

    [Theory]
    [InlineData(1, 0.03f)]
    [InlineData(1, 0.5f)]
    [InlineData(4, 0)]
    [InlineData(4, 0.5f)]
    public void An_untouched_stand_in_reads_no_push(int substeps, float damping)
    {
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        physics.Initialize();
        physics.SetSolver(8, substeps);
        physics.SetDamping(damping, damping);
        var standIn = physics.AddStandIn(new Vector3(0, 10, 0), 0.26f, 1.7f, 80);
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

    private static BodyDescription Ball(PhysicsWorld physics, Vector3 at) =>
        BodyDescription.CreateDynamic(
            new RigidPose(at),
            new Sphere(0.1f).ComputeInertia(1),
            new CollidableDescription(physics.Simulation.Shapes.Add(new Sphere(0.1f)), 0.1f),
            new BodyActivityDescription(-1)
        );

    private static Vector3 Velocity(PhysicsWorld physics, BodyHandle body) =>
        physics.Simulation.Bodies[body].Velocity.Linear;
}
