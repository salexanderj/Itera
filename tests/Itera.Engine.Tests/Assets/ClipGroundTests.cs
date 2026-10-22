using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="ClipGround"/> on a rig made by hand, 1.8 m tall: a walk whose foot goes back 0.7 m along the ground in
/// half a second and forward through the air shows 1.4 m/s; a clip whose foot stays put shows none; and a clip or a
/// joint the rig lacks is refused.
/// </summary>
public sealed class ClipGroundTests
{
    private static readonly string[] s_feet = ["foot"];

    [Fact]
    public void A_walk_shows_the_speed_its_planted_foot_goes_back_at()
    {
        Assert.Equal(1.4f, ClipGround.Speed(Walker(), "Walk", s_feet), 3);
    }

    [Fact]
    public void A_clip_whose_foot_stays_put_shows_no_speed()
    {
        Assert.Equal(0f, ClipGround.Speed(Walker(), "Idle", s_feet), 6);
    }

    [Fact]
    public void A_clip_or_a_joint_the_rig_lacks_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ClipGround.Speed(Walker(), "Swim", s_feet));
        Assert.Throws<ArgumentException>(() => ClipGround.Speed(Walker(), "Walk", ["paw"]));
    }

    /// <summary>A root at the ground, a head 1.8 m up, and a foot that the clips move.</summary>
    private static Rig Walker()
    {
        JointTransform[] rest =
        [
            JointTransform.Identity,
            JointTransform.Identity with
            {
                Translation = new Vector3(0, 1.8f, 0),
            },
            JointTransform.Identity with
            {
                Translation = new Vector3(0, 0, 0.35f),
            },
        ];
        var inverseBinds = rest.Select(joint => Matrix4x4.CreateTranslation(-joint.Translation)).ToArray();
        // Back along the ground (-z) for half a second, up at once, forward through the air, and down at once.
        float[] times = [0, 0.5f, 0.501f, 0.75f, 0.999f, 1];
        Vector4[] places =
        [
            new(0, 0, 0.35f, 0),
            new(0, 0, -0.35f, 0),
            new(0, 0.1f, -0.35f, 0),
            new(0, 0.2f, 0, 0),
            new(0, 0.1f, 0.35f, 0),
            new(0, 0, 0.35f, 0),
        ];
        var walk = new RigClip("Walk", [new RigTrack(2, RigPath.Translation, RigInterpolation.Linear, times, places)]);
        Vector4[] still = [new(0, 0, 0.35f, 0), new(0, 0, 0.35f, 0)];
        var idle = new RigClip("Idle", [new RigTrack(2, RigPath.Translation, RigInterpolation.Linear, [0, 1], still)]);
        return new Rig(
            ["root", "head", "foot"],
            [-1, 0, 0],
            [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity],
            rest,
            inverseBinds,
            [-1, -1, -1],
            [walk, idle]
        );
    }
}
