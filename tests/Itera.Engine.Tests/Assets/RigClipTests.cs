using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="RigTrack"/> and <see cref="RigClip"/> sample as glTF says: step holds a key until the next, linear moves
/// straight and turns along the shorter arc, a cubic spline follows the keys' tangents scaled by the time between them,
/// and before the first key or after the last the end value holds; <see cref="Rig.Pose"/> and
/// <see cref="Rig.WritePalette"/> allocate nothing.
/// </summary>
public sealed class RigClipTests
{
    [Fact]
    public void A_linear_track_moves_straight_between_keys()
    {
        var track = new RigTrack(
            0,
            RigPath.Translation,
            RigInterpolation.Linear,
            [1, 3],
            [Vector4.Zero, new(2, 4, 6, 0)]
        );

        Assert.Equal(new Vector3(1, 2, 3), track.SampleVector(2));
        Assert.Equal(new Vector3(0.5f, 1, 1.5f), track.SampleVector(1.5f));
    }

    [Fact]
    public void Before_the_first_key_and_after_the_last_the_end_values_hold()
    {
        var track = new RigTrack(0, RigPath.Scale, RigInterpolation.Linear, [1, 3], [Vector4.One, new(3, 3, 3, 0)]);

        Assert.Equal(Vector3.One, track.SampleVector(0));
        Assert.Equal(Vector3.One, track.SampleVector(float.NaN));
        Assert.Equal(new Vector3(3), track.SampleVector(10));
    }

    [Fact]
    public void A_step_track_holds_each_key_until_the_next()
    {
        var track = new RigTrack(
            0,
            RigPath.Translation,
            RigInterpolation.Step,
            [0, 1, 2],
            [Vector4.Zero, Vector4.UnitX, Vector4.UnitY]
        );

        Assert.Equal(Vector3.Zero, track.SampleVector(0.99f));
        Assert.Equal(Vector3.UnitX, track.SampleVector(1));
        Assert.Equal(Vector3.UnitX, track.SampleVector(1.5f));
        Assert.Equal(Vector3.UnitY, track.SampleVector(2));
    }

    [Fact]
    public void A_linear_rotation_turns_along_the_shorter_arc()
    {
        // A quarter turn about z, stored with every sign flipped (the same turn): halfway is an eighth of a turn, not the
        // long way round.
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var track = new RigTrack(
            0,
            RigPath.Rotation,
            RigInterpolation.Linear,
            [0, 1],
            [new(0, 0, 0, 1), new(-quarter.X, -quarter.Y, -quarter.Z, -quarter.W)]
        );

        var half = track.SampleRotation(0.5f);

        var turned = Vector3.Transform(Vector3.UnitX, half);
        Assert.Equal(MathF.Sqrt(0.5f), turned.X, 1e-5f);
        Assert.Equal(MathF.Sqrt(0.5f), turned.Y, 1e-5f);
        Assert.Equal(1, half.Length(), 1e-5f);
    }

    [Fact]
    public void A_cubic_spline_follows_its_tangents_scaled_by_the_time_between_keys()
    {
        // Keys at 0 s (0) and 2 s (1); the first key's out-tangent 1 a second, the second's in-tangent 0. Halfway:
        // (2t³ - 3t² + 1)·0 + 2·(t³ - 2t² + t)·1 + (-2t³ + 3t²)·1 + 2·(t³ - t²)·0 at t = 0.5 = 0.25 + 0.5.
        var track = new RigTrack(
            0,
            RigPath.Translation,
            RigInterpolation.CubicSpline,
            [0, 2],
            [Vector4.Zero, Vector4.Zero, Vector4.UnitX, Vector4.Zero, Vector4.UnitX, Vector4.Zero]
        );

        Assert.Equal(0.75f, track.SampleVector(1).X, 1e-6f);
        Assert.Equal(0, track.SampleVector(0).X);
        Assert.Equal(1, track.SampleVector(2).X);
    }

    [Fact]
    public void A_cubic_rotation_comes_out_of_unit_length()
    {
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var track = new RigTrack(
            0,
            RigPath.Rotation,
            RigInterpolation.CubicSpline,
            [0, 1],
            [
                Vector4.Zero,
                new(0, 0, 0, 1),
                Vector4.Zero,
                Vector4.Zero,
                new(quarter.X, quarter.Y, quarter.Z, quarter.W),
                Vector4.Zero,
            ]
        );

        Assert.Equal(1, track.SampleRotation(0.5f).Length(), 1e-5f);
    }

    [Fact]
    public void A_clip_sets_only_the_joints_its_tracks_name()
    {
        var clip = new RigClip(
            "lift",
            [new RigTrack(1, RigPath.Translation, RigInterpolation.Linear, [0, 2], [Vector4.Zero, new(0, 2, 0, 0)])]
        );
        Span<JointTransform> locals = [JointTransform.Identity, JointTransform.Identity];
        locals[0] = locals[0] with { Translation = new Vector3(5, 0, 0) };

        clip.Sample(1, locals);

        Assert.Equal(2, clip.Duration);
        Assert.Equal(new Vector3(5, 0, 0), locals[0].Translation);
        Assert.Equal(new Vector3(0, 1, 0), locals[1].Translation);
    }

    [Fact]
    public void Posing_a_rig_and_writing_its_palette_allocate_nothing()
    {
        var rig = TwoJointRig();
        var locals = new JointTransform[rig.JointCount];
        var palette = new Matrix4x4[rig.JointCount];
        for (var i = 0; i < 20; i++)
        {
            rig.Pose(0, i * 0.05f, locals);
            rig.WritePalette(locals, palette);
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                rig.Pose(0, i * 0.001f, locals);
                rig.WritePalette(locals, palette);
            }
        });

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void A_palette_takes_each_joint_from_its_bound_pose_to_its_posed_one()
    {
        var rig = TwoJointRig();
        var locals = new JointTransform[2];
        var palette = new Matrix4x4[2];

        // At rest, as bound: nothing moves.
        rig.Pose(-1, 0, locals);
        rig.WritePalette(locals, palette);
        var tip = new Vector3(0, 2, 0);
        Assert.True(Vector3.Distance(tip, Vector3.Transform(tip, palette[1])) < 1e-5f);

        // The second joint turned a quarter about z: the tip, 1 m above it, swings to 1 m along -x of it.
        rig.Pose(0, 1, locals);
        rig.WritePalette(locals, palette);
        var moved = Vector3.Transform(tip, palette[1]);
        Assert.Equal(-1, moved.X, 1e-5f);
        Assert.Equal(1, moved.Y, 1e-5f);
    }

    /// <summary>Two joints 1 m apart up +y, bound at rest; a clip turns the second a quarter about z over a second.</summary>
    private static Rig TwoJointRig()
    {
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var clip = new RigClip(
            "bend",
            [
                new RigTrack(
                    1,
                    RigPath.Rotation,
                    RigInterpolation.Linear,
                    [0, 1],
                    [new(0, 0, 0, 1), new(quarter.X, quarter.Y, quarter.Z, quarter.W)]
                ),
            ]
        );
        JointTransform[] rest = [JointTransform.Identity, JointTransform.Identity with { Translation = Vector3.UnitY }];
        return new Rig(
            ["root", "tip"],
            [-1, 0],
            [Matrix4x4.Identity, Matrix4x4.Identity],
            rest,
            [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)],
            [1, 1],
            [clip]
        );
    }
}
