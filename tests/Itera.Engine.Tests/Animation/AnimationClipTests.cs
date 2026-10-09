using Itera.Engine.Animation;
using Itera.Engine.Maths;

namespace Itera.Engine.Tests.Animation;

/// <summary>
/// <see cref="AnimationClip"/>: a key's turns are added exactly at its time and eased between keys, bones it does not
/// drive are left alone, a weight scales it, and its keys must start at 0 and run in order.
/// </summary>
public sealed class AnimationClipTests
{
    private static readonly AnimationClip s_clip = new(
        "test",
        [(int)TestBone.RightThigh, (int)TestBone.Head],
        [
            new(0, BodyPose.Rest),
            new(
                0.2,
                BodyPose
                    .Rest.With((int)TestBone.RightThigh, Rotation.AboutX(0.1))
                    .With((int)TestBone.LeftThigh, Rotation.AboutX(0.3)),
                0.2
            ),
            new(0.6, BodyPose.Rest),
        ]
    );

    [Fact]
    public void A_key_is_added_exactly_at_its_time_onto_the_pose_under_it()
    {
        var turns = TestPoses.Stride.Turns.ToArray();

        var drop = s_clip.Sample(0.2, 1, turns);

        var thigh = (int)TestBone.RightThigh;
        Near(Rotation.AboutX(0.1).Times(TestPoses.Stride.Turns[thigh]), turns[thigh]);
        Assert.Equal(0.2, drop, 1e-12);
        // The left thigh is in the key but not driven; the head is driven but not turned in any key.
        Assert.Equal(TestPoses.Stride.Turns[(int)TestBone.LeftThigh], turns[(int)TestBone.LeftThigh]);
        Near(TestPoses.Stride.Turns[(int)TestBone.Head], turns[(int)TestBone.Head]);
    }

    [Fact]
    public void Between_keys_it_eases_and_at_the_ends_it_adds_nothing()
    {
        var quarter = BodyPose.Rest.Turns.ToArray();
        var start = BodyPose.Rest.Turns.ToArray();
        var after = BodyPose.Rest.Turns.ToArray();

        // A quarter of the way from 0.2 s to 0.6 s: eased, 0.15625 of the way back to rest.
        var drop = s_clip.Sample(0.3, 1, quarter);
        var none = s_clip.Sample(0, 1, start);
        var past = s_clip.Sample(5, 1, after);

        Assert.Equal(0.2 * (1 - 0.15625), drop, 1e-12);
        Assert.Equal(
            Rotation.Blend(Rotation.AboutX(0.1), Rotation.Identity, 0.15625).Myy,
            quarter[(int)TestBone.RightThigh].Myy,
            1e-12
        );
        Assert.Equal(0, none);
        Assert.Equal(0, past);
        Near(Rotation.Identity, start[(int)TestBone.RightThigh]);
        Near(Rotation.Identity, after[(int)TestBone.RightThigh]);
        Assert.Equal(0.6, s_clip.Seconds);
    }

    [Fact]
    public void A_weight_scales_it()
    {
        var half = BodyPose.Rest.Turns.ToArray();
        var none = BodyPose.Rest.Turns.ToArray();

        var drop = s_clip.Sample(0.2, 0.5, half);
        var nothing = s_clip.Sample(0.2, 0, none);

        Assert.Equal(0.1, drop, 1e-12);
        Near(Rotation.AboutX(0.05), half[(int)TestBone.RightThigh]);
        Assert.Equal(0, nothing);
        Assert.Equal(BodyPose.Rest.Turns.ToArray(), none);
    }

    [Fact]
    public void Keys_must_start_at_0_and_run_in_order()
    {
        Assert.Throws<ArgumentException>(() =>
            new AnimationClip("late", (int[])[], [new(0.1, BodyPose.Rest), new(1, BodyPose.Rest)])
        );
        Assert.Throws<ArgumentException>(() =>
            new AnimationClip("back", (int[])[], [new(0, BodyPose.Rest), new(0, BodyPose.Rest)])
        );
        Assert.Throws<ArgumentException>(() => new AnimationClip("one", (int[])[], [new(0, BodyPose.Rest)]));
    }

    private static void Near(in Rotation expected, in Rotation actual)
    {
        double[] want = [expected.Mxx, expected.Mxy, expected.Mxz, expected.Myx, expected.Myy, expected.Myz];
        double[] got = [actual.Mxx, actual.Mxy, actual.Mxz, actual.Myx, actual.Myy, actual.Myz];
        for (var i = 0; i < want.Length; i++)
        {
            Assert.Equal(want[i], got[i], 1e-9);
        }
    }
}
