using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Systems;
using Itera.Modding;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// The console's <c>balls</c>: the next step drops the asked number of balls in a row 3 m in front of the camera, each a
/// loose body with its entity, they fall, and a step without a request does nothing and allocates nothing.
/// </summary>
public sealed class DebugBallSystemTests
{
    private static readonly QueryDescription s_bodies = new QueryDescription().WithAll<LooseBody, BodyPose>();

    [Fact]
    public void A_request_drops_a_row_of_balls_in_front_of_the_camera_that_fall()
    {
        using var harness = new PhysicsTestHarness();
        using var world = World.Create();
        world.Create(new CameraPose { Position = new Vector3(0, 1, 0) }, new ActiveCamera());
        using var system = new DebugBallSystem(world, harness.Bodies, harness.Voxels);
        system.Initialize();

        system.Request(3);
        system.Update(new FixedStepTime(PhysicsTestHarness.Step, 0));

        Assert.Equal(3, world.CountEntities(in s_bodies));
        Assert.Equal(3, harness.Bodies.Count);
        var poses = new List<Vector3>();
        world.Query(in s_bodies, (ref BodyPose pose) => poses.Add(pose.Position));
        // Yaw 0 looks along -z: 3 m ahead, at the camera's height, across x.
        Assert.All(poses, p => Assert.Equal(-DebugBallSystem.AheadMetres, p.Z, 3));
        Assert.All(poses, p => Assert.Equal(1, p.Y, 3));
        Assert.Equal(3, poses.Select(p => MathF.Round(p.X, 2)).Distinct().Count());

        harness.RunPhysics(30);
        world.Query(
            in s_bodies,
            (ref LooseBody body) => Assert.True(harness.Bodies.PivotOf(body.Body).Y < 0.9f, "A ball did not fall.")
        );
    }

    [Fact]
    public void A_step_without_a_request_does_nothing_and_allocates_nothing()
    {
        using var harness = new PhysicsTestHarness();
        using var world = World.Create();
        world.Create(new CameraPose { Position = new Vector3(0, 1, 0) }, new ActiveCamera());
        using var system = new DebugBallSystem(world, harness.Bodies, harness.Voxels);
        system.Initialize();

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FixedStepTime(PhysicsTestHarness.Step, 0)));
        Assert.Equal(0, world.CountEntities(in s_bodies));
    }

    [Fact]
    public void A_ball_whose_space_is_solid_is_left_out()
    {
        using var harness = new PhysicsTestHarness();
        using var world = World.Create();
        // Below the ground: every ball's space is stone.
        world.Create(new CameraPose { Position = new Vector3(0, -1, 0) }, new ActiveCamera());
        using var system = new DebugBallSystem(world, harness.Bodies, harness.Voxels);
        system.Initialize();

        system.Request(2);
        system.Update(new FixedStepTime(PhysicsTestHarness.Step, 0));

        Assert.Equal(0, harness.Bodies.Count);
    }
}
