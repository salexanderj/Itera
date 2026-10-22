using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="ClipGround"/> on a rig made by hand, 1.8 m tall: a walk whose foot goes back 0.7 m along the ground in
/// half a second and forward through the air shows 1.4 m/s; a foot that swings forward along the ground counts only its
/// steps back; a clip that goes sideways shows its speed along its own way; a clip whose foot stays put shows none; and
/// a clip or a joint the rig lacks is refused.
/// </summary>
public sealed class ClipGroundTests
{
    private static readonly Rig s_rig = Walker();
    private static readonly int[] s_feet = [2];

    [Fact]
    public void A_walk_shows_the_speed_its_planted_foot_goes_back_at()
    {
        Assert.Equal(1.8f, ClipGround.Height(s_rig), 5);
        Assert.Equal(1.4f, ClipGround.Speed(s_rig, s_rig.FindClip("Walk"), s_feet, ClipGround.Back), 3);
    }

    [Fact]
    public void A_foot_that_swings_forward_along_the_ground_counts_only_its_steps_back()
    {
        Assert.Equal(1.4f, ClipGround.Speed(s_rig, s_rig.FindClip("Shuffle"), s_feet, ClipGround.Back), 3);
    }

    [Fact]
    public void A_clip_that_goes_left_shows_its_speed_along_its_own_way()
    {
        var left = s_rig.FindClip("Left");

        Assert.Equal(1.4f, ClipGround.Speed(s_rig, left, s_feet, -Vector3.UnitX), 3);
        Assert.Equal(0f, ClipGround.Speed(s_rig, left, s_feet, ClipGround.Back), 4);
    }

    [Fact]
    public void A_clip_whose_foot_stays_put_shows_no_speed()
    {
        // The posed joints' rounding moves a still foot by less than a micrometre a sample.
        Assert.Equal(0f, ClipGround.Speed(s_rig, s_rig.FindClip("Idle"), s_feet, ClipGround.Back), 4);
    }

    [Fact]
    public void A_clip_or_a_joint_the_rig_lacks_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipGround.Speed(s_rig, 9, s_feet, ClipGround.Back));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipGround.Speed(s_rig, 0, [7], ClipGround.Back));
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
        // Back along the ground for half a second, up at once, forward through the air, and down at once.
        float[] times = [0, 0.5f, 0.501f, 0.75f, 0.999f, 1];
        Vector4[] walk =
        [
            new(0, 0, 0.35f, 0),
            new(0, 0, -0.35f, 0),
            new(0, 0.1f, -0.35f, 0),
            new(0, 0.2f, 0, 0),
            new(0, 0.1f, 0.35f, 0),
            new(0, 0, 0.35f, 0),
        ];
        // The same steps going left (+x): the planted foot goes back along -x.
        Vector4[] left = [.. walk.Select(place => new Vector4(place.Z, place.Y, 0, 0))];
        // Back along the ground for half a second, then forward along it.
        Vector4[] shuffle = [new(0, 0, 0.35f, 0), new(0, 0, -0.35f, 0), new(0, 0, 0.35f, 0)];
        Vector4[] still = [new(0, 0, 0.35f, 0), new(0, 0, 0.35f, 0)];
        return new Rig(
            ["root", "head", "foot"],
            [-1, 0, 0],
            [Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity],
            rest,
            inverseBinds,
            [-1, -1, -1],
            [
                Clip("Walk", times, walk),
                Clip("Left", times, left),
                Clip("Shuffle", [0, 0.5f, 1], shuffle),
                Clip("Idle", [0, 1], still),
            ]
        );
    }

    private static RigClip Clip(string name, float[] times, Vector4[] places) =>
        new(name, [new RigTrack(2, RigPath.Translation, RigInterpolation.Linear, times, places)]);
}
