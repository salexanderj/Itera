using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A game's own bodies and statics through <see cref="PhysicsWorld"/>, and the records the engine keeps by handle: a body
/// that takes a removed cut body's handle starts as a loose body (not cut, level 0, in no ragdoll) and touches other
/// bodies; a static the voxel colliders removed leaves no record behind; a stand-in's removal resets its record; a game's
/// static is touched by bodies of every voxel level; a game's body freezes where the voxels are not loaded, is forgotten
/// when removed, or keeps moving when marked so; <see cref="PhysicsWorld.Holds"/> tells a reused handle apart; and (in
/// Debug) a recorded body removed past <see cref="PhysicsWorld.RemoveBody"/> is caught after the step.
/// </summary>
public sealed class PhysicsBodiesTests : IDisposable
{
    private readonly PhysicsTestHarness _harness = new();
    private readonly TypedIndex _sphere;

    public PhysicsBodiesTests()
    {
        _sphere = _harness.Physics.Simulation.Shapes.Add(new Sphere(0.1f));
    }

    private PhysicsWorld Physics => _harness.Physics;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void A_body_taking_a_removed_cut_bodys_handle_starts_as_a_loose_body()
    {
        var cut = _harness.CutBody(new Vector3(0, 1, 0));
        Assert.Equal(BodyKind.Cut, Physics.KindOf(cut));
        Assert.NotEqual(0, Physics.Kinds.Ragdoll(cut.Value));
        _harness.Bodies.Remove(cut);

        var mine = Physics.AddBody(Ball(new Vector3(0, 1, 0)));
        var other = Physics.AddBody(Ball(new Vector3(0, 2, 0)));

        Assert.Equal(cut, mine);
        Assert.Equal(BodyKind.Loose, Physics.KindOf(mine));
        Assert.Equal(0, Physics.Kinds.Ragdoll(mine.Value));
        Assert.Equal(0, Physics.Kinds.Level(mine.Value));
        Assert.Equal(0, Physics.Kinds.BaseLevel(mine.Value));
        var callbacks = new NarrowPhaseCallbacks(Physics.Kinds);
        var margin = 0f;
        Assert.True(
            callbacks.AllowContactGeneration(
                0,
                new CollidableReference(CollidableMobility.Dynamic, mine),
                new CollidableReference(CollidableMobility.Dynamic, other),
                ref margin
            )
        );
    }

    [Fact]
    public void A_static_the_voxel_colliders_removed_leaves_no_record_and_a_game_static_touches_every_level()
    {
        var ball = _harness.ThrowBall(new Vector3(0, 0.3f, 0));
        _harness.RunPhysics(10);
        Assert.NotEqual(0, _harness.Colliders.StaticCount);
        var built = Physics.Simulation.Statics.IndexToHandle[0];
        Assert.NotEqual(0, Physics.Kinds.Static(built.Value).Born);

        // With the ball gone, its cells are dropped after they go unneeded.
        _harness.Bodies.Remove(ball);
        _harness.RunPhysics(120);
        Assert.Equal(0, _harness.Colliders.StaticCount);
        Assert.Equal(0, Physics.Kinds.Static(built.Value).Born);

        var floor = Physics.AddStatic(
            new StaticDescription(new Vector3(0, 3, 0), Physics.Simulation.Shapes.Add(new Box(4, 0.1f, 4)))
        );
        var coarse = Physics.AddBody(Ball(new Vector3(0, 3.5f, 0)));
        Physics.Kinds.SetLevel(coarse, 2);
        var callbacks = new NarrowPhaseCallbacks(Physics.Kinds);
        var margin = 0f;
        Assert.True(
            callbacks.AllowContactGeneration(
                0,
                new CollidableReference(CollidableMobility.Dynamic, coarse),
                new CollidableReference(floor),
                ref margin
            )
        );
        Physics.RemoveStatic(floor);
        Assert.Equal(0, Physics.Kinds.StaticLevel(floor.Value));
        Assert.Equal(0, Physics.Kinds.Static(floor.Value).Born);
    }

    [Fact]
    public void A_stand_ins_removal_resets_its_record()
    {
        var standIn = Physics.AddStandIn(new Vector3(0, 0, 0), 0.26f, 1.7f, 80);
        Assert.Equal(BodyKind.StandIn, Physics.KindOf(standIn));

        Physics.RemoveStandIn(standIn);

        Assert.Equal(BodyKind.Loose, Physics.KindOf(standIn));
        Assert.Equal(0, Physics.BornOf(standIn));
    }

    [Fact]
    public void A_game_body_freezes_where_the_voxels_are_not_loaded_and_is_forgotten_when_removed()
    {
        // 20 m away, far outside the 4 m that is loaded.
        var far = Physics.AddBody(Ball(new Vector3(20, 2.5f, 0)));
        _harness.RunPhysics(30);
        var frozen = _harness.Colliders.IsFrozen(far);

        Physics.RemoveBody(far);
        _harness.RunPhysics(1);

        Assert.True(frozen);
        Assert.Equal(0, _harness.Colliders.FrozenCount);
        Assert.Equal(0, Physics.BornOf(far));
    }

    [Fact]
    public void A_game_body_marked_to_keep_moving_falls_where_the_voxels_are_not_loaded()
    {
        var far = Physics.AddBody(
            Ball(new Vector3(20, 2.5f, 0)),
            new PhysicsBodySettings { Marks = PhysicsMarks.KeepsMovingUnloaded }
        );

        _harness.RunPhysics(30);

        Assert.False(_harness.Colliders.IsFrozen(far));
        Assert.Equal(PhysicsMarks.KeepsMovingUnloaded, Physics.MarksOf(far));
        Assert.True(Physics.Simulation.Bodies[far].Pose.Position.Y < 2.0f);
    }

    [Fact]
    public void Holds_tells_a_reused_handle_apart()
    {
        var first = Physics.AddBody(Ball(new Vector3(0, 1, 0)));
        var born = Physics.BornOf(first);
        Assert.True(Physics.Holds(first, born));

        Physics.RemoveBody(first);
        var second = Physics.AddBody(Ball(new Vector3(0, 1, 0)));

        Assert.Equal(first, second);
        Assert.False(Physics.Holds(first, born));
        Assert.True(Physics.Holds(second, Physics.BornOf(second)));
        Assert.NotEqual(born, Physics.BornOf(second));
        Assert.False(Physics.Holds(second, 0));
    }

#if DEBUG
    [Fact]
    public void A_recorded_body_removed_past_remove_body_is_caught_after_the_step()
    {
        var body = Physics.AddBody(Ball(new Vector3(0, 1, 0)));
        Physics.Simulation.Bodies.Remove(body);

        var thrown = Assert.Throws<InvalidOperationException>(() => Physics.Step(PhysicsTestHarness.Step));
        Assert.Contains("RemoveBody", thrown.Message, StringComparison.Ordinal);
        Physics.Kinds.Forget(body);
    }
#endif

    private BodyDescription Ball(Vector3 at) =>
        BodyDescription.CreateDynamic(
            new RigidPose(at),
            new Sphere(0.1f).ComputeInertia(1),
            new CollidableDescription(_sphere, 0.1f),
            new BodyActivityDescription(0.01f)
        );
}
