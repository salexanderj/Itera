using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Systems;
using Itera.Modding;

namespace Itera.Engine.Tests.Systems;

public sealed class PhysicsReadSystemTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);

    private readonly PhysicsTestHarness _physics = new();

    public void Dispose() => _physics.Dispose();

    [Fact]
    public void Each_step_keeps_the_last_pose_and_reads_the_new_one()
    {
        using var world = World.Create();
        using var system = new PhysicsReadSystem(world, _physics.Bodies, _physics.Physics);
        system.Initialize();
        var body = _physics.ThrowBall(new Vector3(1, 2, 0), new Vector3(0, 0, 2));
        var entity = world.Create(
            new LooseBody { Body = body, BallRadius = PhysicsTestHarness.BallRadius },
            new BodyPose { Position = new Vector3(1, 2, 0), Orientation = Quaternion.Identity }
        );

        _physics.RunPhysics();
        system.Update(in Step);
        var first = world.Get<BodyPose>(entity);
        _physics.RunPhysics();
        system.Update(in Step);
        var second = world.Get<BodyPose>(entity);

        Assert.Equal(new Vector3(1, 2, 0), first.PreviousPosition);
        Assert.Equal(_physics.Bodies.PivotOf(body), second.Position);
        Assert.Equal(first.Position, second.PreviousPosition);
        Assert.True(second.Position.Z > first.Position.Z && second.Position.Y < first.Position.Y);
    }

    /// <summary>
    /// An entity whose body a remover took out of the simulation and left behind is a bug the check finds (a Debug build
    /// throws), also after a new body has taken the old one's handle. A Release build logs an Error and destroys the
    /// entity, leaving the new body alone.
    /// </summary>
    [Fact]
    public void An_entity_left_behind_by_its_bodys_remover_is_found()
    {
        using var world = World.Create();
        using var system = new PhysicsReadSystem(world, _physics.Bodies, _physics.Physics);
        system.Initialize();
        var body = _physics.ThrowBall(new Vector3(1, 2, 0));
        var entity = world.Create(
            new LooseBody
            {
                Body = body,
                BallRadius = PhysicsTestHarness.BallRadius,
                Born = _physics.Bodies.BornOf(body),
            },
            new BodyPose()
        );

        _physics.Bodies.Remove(body);

#if DEBUG
        Assert.Throws<InvalidOperationException>(() => system.Update(in Step));
#endif

        // A new body takes the handle: the stale entity's stamp is not the new body's.
        var next = _physics.ThrowBall(new Vector3(1, 3, 0));
        Assert.Equal(body, next);
        Assert.False(_physics.Bodies.Holds(body, 1 + _physics.Bodies.BornOf(next)));
#if DEBUG
        Assert.Throws<InvalidOperationException>(() => system.Update(in Step));
#else
        system.Update(in Step);
        Assert.False(world.IsAlive(entity));
        Assert.True(_physics.Bodies.Holds(next, _physics.Bodies.BornOf(next)));
#endif
    }

    /// <summary>
    /// A step with a thrown ball, a cut body, a character whose stand-in a body pushes, grounded and not, and one with no
    /// stand-in allocates nothing.
    /// </summary>
    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = new PhysicsReadSystem(world, _physics.Bodies, _physics.Physics);
        system.Initialize();
        var body = _physics.ThrowBall(new Vector3(1, 2, 0));
        world.Create(new LooseBody { Body = body, BallRadius = PhysicsTestHarness.BallRadius }, new BodyPose());
        var cut = _physics.CutBody(new Vector3(-1, 2, 0));
        var cutEntity = world.Create(new LooseBody { Body = cut, Born = _physics.Bodies.BornOf(cut) }, new BodyPose());
        var size = new CharacterSize
        {
            HeightMetres = 1.7f,
            RadiusMetres = 0.26f,
            MassKilograms = 70,
        };
        foreach (var grounded in new[] { true, false })
        {
            var standIn = _physics.Physics.AddStandIn(new Vector3(1, 1.5f, 0), 0.26f, 1.7f, 70);
            world.Create(
                new CharacterStandIn { Body = standIn, Present = true },
                new CharacterVelocity(),
                size,
                new CharacterContact { Grounded = grounded }
            );
        }

        var bare = world.Create(new CharacterStandIn(), new CharacterVelocity(), size, new CharacterContact());
        _physics.RunPhysics();
        system.Update(in Step);
        Assert.Equal(_physics.Bodies.PivotOf(cut), world.Get<BodyPose>(cutEntity).Position);
        Assert.Equal(Vector3.Zero, world.Get<CharacterVelocity>(bare).Push);

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }
}
