using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Physics materials (<see cref="PhysicsMaterials"/>): the registry's rules; two of the default material give the
/// engine's one contact material exactly; a pair grips by the product of the square roots of its frictions; a ball keeps
/// the bounciness's share of its speed as it bounces (a drop of 1 m rebounds the share squared of it) at 1 and 4
/// substeps, and none without bounciness; two balls of bounciness 1 meeting head on trade their speeds; a bouncing ball
/// comes to rest; a mesh collider takes its material from its settings; a material the run did not register is refused;
/// and stepping with bounces allocates nothing.
/// </summary>
public sealed class PhysicsMaterialsTests
{
    private const float Step = PhysicsTestHarness.Step;

    [Fact]
    public void The_registry_names_its_materials_once_and_seals_at_the_first_match()
    {
        var materials = new PhysicsMaterials();
        var ice = materials.Register("ice", new PhysicsMaterial(0.03f, 0));

        Assert.Equal(PhysicsMaterialId.Default, materials.TryFind("default", out var plain) ? plain : default);
        Assert.Equal(new PhysicsMaterial(1, 0), materials[PhysicsMaterialId.Default]);
        Assert.Equal(1, ice.Value);
        Assert.Equal("ice", materials.NameOf(ice));
        Assert.Throws<ArgumentException>(() => materials.Register("ice", new PhysicsMaterial(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => materials.Register("glue", new PhysicsMaterial(5, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => materials.Register("rubber", new PhysicsMaterial(1, 1.5f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => materials.Register("nan", new PhysicsMaterial(float.NaN, 0)));
        for (var i = materials.Count; i < PhysicsMaterials.MaxMaterials; i++)
        {
            materials.Register("m" + i, new PhysicsMaterial(1, 0));
        }

        Assert.Throws<InvalidOperationException>(() => materials.Register("one more", new PhysicsMaterial(1, 0)));
        var sealedOnes = new PhysicsMaterials();
        using var physics = World(sealedOnes);
        Assert.True(sealedOnes.IsSealed);
        Assert.Throws<InvalidOperationException>(() => sealedOnes.Register("late", new PhysicsMaterial(1, 0)));
    }

    [Fact]
    public void Two_default_materials_give_the_engines_contact_material_and_a_pair_grips_by_its_roots()
    {
        var materials = new PhysicsMaterials();
        var ice = materials.Register("ice", new PhysicsMaterial(0.04f, 0));
        var rough = materials.Register("rough", new PhysicsMaterial(2.25f, 0));
        using var physics = World(materials);
        var a = physics.AddBody(Ball(physics, Vector3.Zero));
        var b = physics.AddBody(Ball(physics, Vector3.One), new PhysicsBodySettings { Material = rough });
        var c = physics.AddBody(Ball(physics, -Vector3.One), new PhysicsBodySettings { Material = ice });
        var callbacks = new NarrowPhaseCallbacks(physics.Kinds);

        var plain = Material(callbacks, a, a);
        var mixed = Material(callbacks, b, c);

        Assert.Equal(1f, plain.FrictionCoefficient);
        Assert.Equal(2f, plain.MaximumRecoveryVelocity);
        var spring = new SpringSettings(30, 1);
        Assert.Equal(
            (spring.AngularFrequency, spring.TwiceDampingRatio),
            (plain.SpringSettings.AngularFrequency, plain.SpringSettings.TwiceDampingRatio)
        );
        Assert.Equal(0.3f, mixed.FrictionCoefficient, 6);
    }

    [Theory]
    [InlineData(1, 0f)]
    [InlineData(1, 0.5f)]
    [InlineData(1, 0.8f)]
    [InlineData(4, 0.5f)]
    [InlineData(4, 0.8f)]
    public void A_dropped_ball_keeps_the_bounciness_share_of_its_speed(int substeps, float bounciness)
    {
        var materials = new PhysicsMaterials();
        var floorMaterial = materials.Register("floor", new PhysicsMaterial(1, bounciness));
        using var physics = World(materials, substeps);
        Floor(physics, floorMaterial);
        var ball = physics.AddBody(Ball(physics, new Vector3(0, 1.1f, 0)));

        var (impact, rebound, peak) = Drop(physics, ball);

        if (bounciness == 0)
        {
            Assert.True(peak < 0.01f, $"rose {peak} m");
            Assert.Equal(0, physics.Bounces);
            return;
        }

        // It meets the floor at about the speed of a 1 m fall (4.43 m/s) and leaves at its share of it; it rises as high
        // as that speed takes it in steps (a step's rise short of the curve's, less what damping takes).
        Assert.InRange(rebound / MathF.Sqrt(2 * PhysicsWorld.StandardGravity), 0.97f * bounciness, 1.01f * bounciness);
        Assert.InRange(impact, 4.2f, 4.6f);
        var rise = rebound * rebound / (2 * PhysicsWorld.StandardGravity) - rebound * Step / 2;
        Assert.InRange(peak, 0.97f * rise, 1.01f * rise);
        Assert.Equal(1, physics.Bounces);
    }

    [Fact]
    public void Every_landing_from_a_little_drop_bounces_wherever_the_steps_fall()
    {
        var materials = new PhysicsMaterials();
        var floorMaterial = materials.Register("floor", new PhysicsMaterial(1, 0.8f));
        using var physics = World(materials);
        Floor(physics, floorMaterial);
        // Dropped from 5 cm (about 1 m/s at the floor, 1.7 cm a step) and 0.8 mm higher each: the last step before the
        // floor ends at every distance from it, gravity's part of the step (2.7 mm) among them.
        var balls = new BodyHandle[24];
        for (var i = 0; i < balls.Length; i++)
        {
            balls[i] = physics.AddBody(Ball(physics, new Vector3(i * 0.5f - 6, 0.15f + i * 0.0008f, 0)));
        }

        // Each lands within 7 steps and is rising again by the 12th, before its second landing.
        var rising = new float[balls.Length];
        for (var step = 0; step < 12; step++)
        {
            physics.Step(Step);
            for (var i = 0; i < balls.Length; i++)
            {
                rising[i] = MathF.Max(rising[i], Velocity(physics, balls[i]).Y);
            }
        }

        // Each leaves the floor at about 0.8 of its 1 m/s.
        Assert.All(rising, speed => Assert.InRange(speed, 0.7f, 0.9f));
        Assert.Equal(balls.Length, physics.Bounces);
    }

    [Fact]
    public void A_body_that_takes_a_removed_bodys_handle_bounces_on_what_that_one_rested_on()
    {
        var materials = new PhysicsMaterials();
        var floorMaterial = materials.Register("floor", new PhysicsMaterial(1, 0.8f));
        using var physics = World(materials);
        Floor(physics, floorMaterial);
        var first = physics.AddBody(Ball(physics, new Vector3(0, 0.1f, 0)));
        for (var i = 0; i < 10; i++)
        {
            physics.Step(Step);
        }

        Assert.Equal(0, physics.Bounces);
        physics.RemoveBody(first);
        var second = physics.AddBody(Ball(physics, new Vector3(0, 0.1f, 0), new Vector3(0, -2, 0)));
        Assert.Equal(first, second);

        physics.Step(Step);

        Assert.Equal(1, physics.Bounces);
        Assert.InRange(Velocity(physics, second).Y, 1.5f, 1.7f);
    }

    [Fact]
    public void Two_balls_of_bounciness_one_meeting_head_on_trade_their_speeds()
    {
        var materials = new PhysicsMaterials();
        var rubber = materials.Register("rubber", new PhysicsMaterial(1, 1));
        using var physics = World(materials);
        physics.SetGravity(Vector3.Zero);
        physics.SetDamping(0, 0);
        var settings = new PhysicsBodySettings { Material = rubber };
        var left = physics.AddBody(Ball(physics, new Vector3(-1, 0, 0), new Vector3(3, 0, 0)), settings);
        var right = physics.AddBody(Ball(physics, new Vector3(1, 0, 0), new Vector3(-1, 0, 0)), settings);

        for (var i = 0; i < 60; i++)
        {
            physics.Step(Step);
        }

        Assert.Equal(-1, Velocity(physics, left).X, 1);
        Assert.Equal(3, Velocity(physics, right).X, 1);
        Assert.Equal(1, physics.Bounces);
    }

    [Fact]
    public void A_bouncing_ball_comes_to_rest()
    {
        var materials = new PhysicsMaterials();
        var rubber = materials.Register("rubber", new PhysicsMaterial(1, 0.8f));
        using var physics = World(materials);
        Floor(physics, PhysicsMaterialId.Default);
        var ball = physics.AddBody(
            Ball(physics, new Vector3(0, 2.1f, 0)),
            new PhysicsBodySettings { Material = rubber }
        );

        for (var i = 0; i < 60 * 15; i++)
        {
            physics.Step(Step);
        }

        Assert.InRange(physics.Bounces, 3, 20);
        Assert.True(Velocity(physics, ball).Length() < 0.05f, $"{Velocity(physics, ball)}");
        Assert.Equal(0.1f, physics.Simulation.Bodies[ball].Pose.Position.Y, 2);
    }

    [Fact]
    public void A_mesh_collider_takes_its_material_from_its_settings()
    {
        var materials = new PhysicsMaterials();
        var ice = materials.Register("ice", new PhysicsMaterial(0.03f, 0));
        using var harness = new MeshGroundHarness(materials: materials);
        var physics = harness.Physics;
        var ground = physics.Simulation.Statics.IndexToHandle[0];
        Assert.Equal(PhysicsMaterialId.Default, physics.MaterialOf(ground));
        Vector3[] pad = [new(0, 1, 0), new(0, 1, 1), new(1, 1, 1)];
        var made = harness.Colliders.Add(
            pad,
            [0, 1, 2],
            Matrix4x4.Identity,
            settings: new PhysicsBodySettings { Material = ice }
        );

        harness.Colliders.SetSettings(0, new PhysicsBodySettings { Material = ice });

        Assert.Equal(ice, physics.MaterialOf(ground));
        Assert.Equal(ice, physics.MaterialOf(physics.Simulation.Statics.IndexToHandle[1]));
        Assert.Equal(ice, harness.Colliders.SettingsOf(made).Material);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            harness.Colliders.SetSettings(0, new PhysicsBodySettings { Material = new PhysicsMaterialId(3) })
        );
        Assert.Equal(ice, harness.Colliders.SettingsOf(0).Material);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            physics.AddBody(Ball(physics, Vector3.One), new PhysicsBodySettings { Material = new PhysicsMaterialId(9) })
        );
    }

    [Fact]
    public void Stepping_with_bounces_allocates_nothing()
    {
        var materials = new PhysicsMaterials();
        var rubber = materials.Register("rubber", new PhysicsMaterial(1, 0.9f));
        using var physics = World(materials);
        Floor(physics, rubber);
        for (var i = 0; i < 20; i++)
        {
            physics.AddBody(Ball(physics, new Vector3(i * 0.5f - 5, 1 + i * 0.1f, 0)));
        }

        for (var i = 0; i < 30; i++)
        {
            physics.Step(Step);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 240; i++)
        {
            physics.Step(Step);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(physics.Bounces > 20, $"{physics.Bounces} bounces");
    }

    private static (float Impact, float Rebound, float Peak) Drop(PhysicsWorld physics, BodyHandle ball)
    {
        var impact = 0f;
        var rebound = 0f;
        var peak = 0f;
        var risen = false;
        for (var i = 0; i < 300; i++)
        {
            var before = Velocity(physics, ball).Y;
            physics.Step(Step);
            var after = Velocity(physics, ball).Y;
            if (!risen && after > 0)
            {
                risen = true;
                // The speed it came in at, with the step's gravity.
                impact = -(before + PhysicsWorld.DefaultGravity.Y * Step);
                rebound = after;
            }

            if (risen)
            {
                peak = MathF.Max(peak, physics.Simulation.Bodies[ball].Pose.Position.Y - 0.1f);
                if (after < 0)
                {
                    break;
                }
            }
        }

        return (impact, rebound, peak);
    }

    private static PairMaterialProperties Material(NarrowPhaseCallbacks callbacks, BodyHandle a, BodyHandle b)
    {
        var manifold = default(ConvexContactManifold);
        callbacks.ConfigureContactManifold(
            0,
            new CollidablePair(
                new CollidableReference(CollidableMobility.Dynamic, a),
                new CollidableReference(CollidableMobility.Dynamic, b)
            ),
            ref manifold,
            out var material
        );
        return material;
    }

    private static void Floor(PhysicsWorld physics, PhysicsMaterialId material) =>
        physics.AddStatic(
            new StaticDescription(new Vector3(0, -0.5f, 0), physics.Simulation.Shapes.Add(new Box(20, 1, 20))),
            new PhysicsBodySettings { Material = material }
        );

    private static PhysicsWorld World(PhysicsMaterials materials, int substeps = 1)
    {
        var options = new EngineOptions();
        options.Physics.Threads = 1;
        options.Physics.Substeps = substeps;
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

    private static Vector3 Velocity(PhysicsWorld physics, BodyHandle body) =>
        physics.Simulation.Bodies[body].Velocity.Linear;
}
