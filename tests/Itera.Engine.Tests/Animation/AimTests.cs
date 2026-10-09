using System.Numerics;
using Itera.Engine.Animation;
using Itera.Engine.Maths;

namespace Itera.Engine.Tests.Animation;

/// <summary>
/// <see cref="Aim"/>: the angles to a point from a facing (left and up positive), held within their limits, none for a
/// point behind; and a look turns the head to face the point, with the spine, chest, and neck taking part.
/// </summary>
public sealed class AimTests
{
    private static readonly HumanoidTestSkeleton s_skeleton = HumanoidTestSkeleton.Build();

    [Fact]
    public void The_angles_to_a_point_are_measured_from_the_facing()
    {
        var eye = new Vector3(1, 1.6f, 2);

        // Facing -z (yaw 0): a point ahead and to the left (-x) and up 45 degrees.
        var (yaw, pitch) = Aim.Angles(eye, 0, eye + new Vector3(-1, 1.41421356f, -1));
        // Facing -x (yaw pi/2), the same point is 45 degrees to the right.
        var (turned, _) = Aim.Angles(eye, MathF.PI / 2, eye + new Vector3(-1, 0, -1));

        Assert.Equal(MathF.PI / 4, yaw, 1e-5f);
        Assert.Equal(MathF.PI / 4, pitch, 1e-5f);
        Assert.Equal(-MathF.PI / 4, turned, 1e-5f);
    }

    [Fact]
    public void Angles_are_held_within_their_limits_and_a_point_behind_is_not_looked_at()
    {
        var eye = new Vector3(0, 1.6f, 0);

        var (side, down) = Aim.Angles(eye, 0, eye + new Vector3(-1, -3, -0.2f));
        var behind = Aim.Angles(eye, 0, eye + new Vector3(0.3f, 0, 1));

        Assert.Equal(Aim.MaxYaw, side, 1e-6f);
        Assert.Equal(-Aim.MaxPitch, down, 1e-6f);
        Assert.Equal((0f, 0f), behind);
    }

    [Theory]
    [InlineData(0.6, 0)]
    [InlineData(-0.8, 0.3)]
    [InlineData(0, -0.5)]
    public void A_look_turns_the_face_to_the_angles(double yaw, double pitch)
    {
        var turns = BodyPose.Rest.Turns.ToArray();
        var bones = new RigidTransform[HumanoidTestSkeleton.Bones];
        s_skeleton.Pose(turns, bones);
        var (rx, ry, rz) = bones[(int)TestBone.Head].Rotation.Apply(0, 0, -1);

        Aim.Apply(s_skeleton, turns, yaw, pitch);
        s_skeleton.Pose(turns, bones);

        // The face's forward in the body, against where it was at rest: turned by the yaw about y and tilted by the pitch.
        var (fx, fy, fz) = bones[(int)TestBone.Head].Rotation.Apply(0, 0, -1);
        var restYaw = Math.Atan2(-rx, -rz);
        var restPitch = Math.Atan2(ry, Math.Sqrt(rx * rx + rz * rz));
        Assert.Equal(yaw, Math.Atan2(-fx, -fz) - restYaw, 0.03);
        Assert.Equal(pitch, Math.Atan2(fy, Math.Sqrt(fx * fx + fz * fz)) - restPitch, 0.03);
        // The chest turns too, but less than the head.
        var (cx, _, cz) = bones[(int)TestBone.Chest].Rotation.Apply(0, 0, -1);
        var chestYaw = Math.Atan2(-cx, -cz);
        Assert.InRange(Math.Abs(chestYaw), 0.2 * Math.Abs(yaw), 0.7 * Math.Abs(yaw) + 1e-3);
    }
}
