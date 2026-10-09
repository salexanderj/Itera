using System.Numerics;
using Itera.Engine.Animation;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Animation;

/// <summary>
/// <see cref="FootPlacement"/>: flat ground leaves a pose as it is; a foot on higher ground rises by it with its knee
/// bent forward; a planted foot on lower ground lowers the hips; a slope tilts the foot; ground out of reach leaves no
/// broken turns; placing feet allocates nothing; and on a real slope, faced up it and across it, every heel and toe
/// stands within 1.5 cm of the voxels under it.
/// </summary>
[RunsLong]
public sealed class FootPlacementTests(ITestOutputHelper output) : IDisposable
{
    // Rising along +x by 0.3 per voxel (about 17 degrees), to a flat top 3 m up.
    private static readonly RampGenerator s_ramp = new(300, 300);

    private static readonly HumanoidTestSkeleton s_skeleton = HumanoidTestSkeleton.Build();

    private VoxelWorld? _voxels;

    public void Dispose() => _voxels?.Dispose();

    [Fact]
    public void Flat_ground_leaves_the_pose_as_it_is()
    {
        var turns = TestPoses.Stride.Turns.ToArray();

        var lift = FootPlacement.Apply(
            s_skeleton,
            turns,
            90,
            new FootPlacement.LegGround[] { new(0, 0), new(0, 0) },
            Bones()
        );

        Assert.Equal(90, lift);
        Assert.Equal(TestPoses.Stride.Turns.ToArray(), turns);
    }

    [Fact]
    public void A_foot_on_higher_ground_rises_by_it_with_its_knee_bent_forward()
    {
        var turns = BodyPose.Rest.Turns.ToArray();
        var (right, left) = Ankles(turns, 90);

        var lift = FootPlacement.Apply(
            s_skeleton,
            turns,
            90,
            new FootPlacement.LegGround[] { new(10, 0), new(0, 0) },
            Bones()
        );
        var (placedRight, placedLeft) = Ankles(turns, lift);

        Assert.Equal(90, lift);
        Near(right + new Vector3(0, 10, 0), placedRight);
        Near(left, placedLeft);
        // The right shin turns back (negative about x), so the knee points forward; the left leg stays straight.
        Assert.True(turns[(int)TestBone.RightShin].Mzy < -0.1, $"{turns[(int)TestBone.RightShin].Mzy}");
        Assert.Equal(0, turns[(int)TestBone.LeftShin].Mzy, 1e-6);
        // The right foot keeps its turn in the body: level.
        var bones = Bones();
        s_skeleton.Pose(turns, bones);
        Assert.Equal(1, bones[(int)TestBone.RightFoot].Rotation.Myy, 1e-6);
    }

    [Fact]
    public void A_planted_foot_on_lower_ground_lowers_the_hips()
    {
        var turns = BodyPose.Rest.Turns.ToArray();
        var (right, left) = Ankles(turns, 90);

        var lift = FootPlacement.Apply(
            s_skeleton,
            turns,
            90,
            new FootPlacement.LegGround[] { new(0, 0), new(-12, 0) },
            Bones()
        );
        var (placedRight, placedLeft) = Ankles(turns, lift);

        Assert.Equal(78, lift, 1e-9);
        Near(right, placedRight);
        Near(left - new Vector3(0, 12, 0), placedLeft);
        Assert.True(turns[(int)TestBone.RightShin].Mzy < -0.1);
    }

    [Fact]
    public void The_hips_drop_at_most_the_step_height()
    {
        var turns = BodyPose.Rest.Turns.ToArray();

        var lift = FootPlacement.Apply(
            s_skeleton,
            turns,
            90,
            new FootPlacement.LegGround[] { new(-80, 0), new(-80, 0) },
            Bones()
        );

        Assert.Equal(90 - FootPlacement.StepVoxels(s_skeleton), lift, 1e-9);
        Assert.All(turns, t => Assert.False(double.IsNaN(t.Mxx) || double.IsNaN(t.Myy) || double.IsNaN(t.Mzz)));
    }

    [Fact]
    public void A_slope_tilts_the_foot()
    {
        var turns = BodyPose.Rest.Turns.ToArray();

        FootPlacement.Apply(
            s_skeleton,
            turns,
            90,
            new FootPlacement.LegGround[] { new(0, 0.05), new(0, -0.03) },
            Bones()
        );

        var bones = Bones();
        s_skeleton.Pose(turns, bones);
        var right = bones[(int)TestBone.RightFoot].Rotation;
        var left = bones[(int)TestBone.LeftFoot].Rotation;
        // The toes point along -z: rising (y) by the sine of the tilt.
        Assert.Equal(Math.Sin(0.05 * 2 * Math.PI), -right.Myz, 1e-6);
        Assert.Equal(Math.Sin(-0.03 * 2 * Math.PI), -left.Myz, 1e-6);
    }

    [Theory]
    [InlineData(-MathF.PI / 2)]
    [InlineData(MathF.PI / 2)]
    public void Probing_a_real_slope_reads_the_height_and_the_pitch_under_each_foot(float yaw)
    {
        // A yaw of -pi/2 turns the body to +x, up the ramp; pi/2 turns it down.
        var (mover, feet) = StandOnRamp(4);
        var bones = Bones();
        s_skeleton.Pose(BodyPose.Rest, bones);
        var ground = new FootPlacement.LegGround[2];

        FootPlacement.Probe(mover, s_skeleton, bones, feet, yaw, ground);

        // The toes of a body facing up the ramp are on higher ground than its heels: a pitch up, and the reverse facing down.
        var expected = Math.Atan(0.3) / (2 * Math.PI) * (yaw < 0 ? 1 : -1);
        foreach (var leg in ground)
        {
            output.WriteLine($"yaw {yaw:F2}: height {leg.Height:F2}, pitch {leg.Pitch:F4} (expected {expected:F4})");
            Assert.Equal(expected, leg.Pitch, 0.015);
            Assert.False(leg.IsFlat);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-MathF.PI / 2, false)]
    [InlineData(-MathF.PI / 2, true)]
    [InlineData(0, true)]
    public void On_a_real_slope_every_heel_and_toe_stands_on_the_voxels(float yaw, bool striding)
    {
        var (mover, feet) = StandOnRamp(4);
        var turns = (striding ? TestPoses.Stride : BodyPose.Rest).Turns.ToArray();
        var bones = Bones();
        s_skeleton.Pose(turns, bones);
        var ground = new FootPlacement.LegGround[2];
        FootPlacement.Probe(mover, s_skeleton, bones, feet, yaw, ground);

        // The pose stands its lowest sole on the feet's level, as a body on flat ground does.
        var lowestSole = double.PositiveInfinity;
        foreach (var leg in s_skeleton.Legs)
        {
            lowestSole = Math.Min(lowestSole, bones[leg.Foot].Apply(0, leg.Sole, 0).Y);
        }

        var lift = FootPlacement.Apply(s_skeleton, turns, -lowestSole, ground, bones);
        s_skeleton.Pose(turns, bones);
        var placement = new RigidTransform(
            Rotation.AboutY(yaw / (2 * Math.PI)),
            feet.X * 100.0,
            feet.Y * 100.0,
            feet.Z * 100.0
        );
        var raise = RigidTransform.Move(0, lift, 0);
        var gaps = new List<string>();
        foreach (var leg in s_skeleton.Legs)
        {
            var inWorld = placement.Times(raise).Times(bones[leg.Foot]);
            var lowest = double.PositiveInfinity;
            foreach (var z in (ReadOnlySpan<double>)[leg.Heel, leg.Toe])
            {
                var (x, y, _) = inWorld.Apply(0, leg.Sole, z);
                // The ground under the point: the ramp's height where it stands. Nothing sinks into it.
                var gap = y - s_ramp.Height((int)Math.Floor(x));
                lowest = Math.Min(lowest, gap);
                if (gap < -1.5)
                {
                    gaps.Add($"foot {leg.Foot} at z {z:F1}: {gap:F2} cm");
                }
            }

            // A planted foot touches with heel or toe. Standing at rest, both feet are planted.
            if (!striding && lowest > 1.5)
            {
                gaps.Add($"foot {leg.Foot} planted {lowest:F2} cm above the ground");
            }
        }

        Assert.Empty(gaps);
    }

    [Fact]
    public void Where_the_finest_voxels_are_not_loaded_the_feet_stay_as_the_pose_puts_them()
    {
        // Loaded within 8 m of (4, 1, 0); the body stands 40 m away, where the level-0 columns are not in memory.
        var (mover, _) = StandOnRamp(4);
        var feet = new Vector3(4, 3, 40);
        var turns = TestPoses.Stride.Turns.ToArray();
        var bones = Bones();
        s_skeleton.Pose(turns, bones);
        var ground = new FootPlacement.LegGround[2];

        FootPlacement.Probe(mover, s_skeleton, bones, feet, 0.7f, ground);
        var lift = FootPlacement.Apply(s_skeleton, turns, 90, ground, bones);

        Assert.False(mover.GroundTop(feet, 0.02f, 0.4f, 0.4f, out var top));
        Assert.True(float.IsNaN(top));
        Assert.All(ground, leg => Assert.True(leg.IsFlat));
        Assert.Equal(90, lift);
        Assert.Equal(TestPoses.Stride.Turns.ToArray(), turns);
    }

    /// <summary>
    /// Placing a walking body's feet on uneven ground allocates nothing; prints the time it takes (Release gives the
    /// numbers to quote).
    /// </summary>
    [Fact]
    public void Placing_the_feet_is_timed()
    {
        var gait = TestPoses.Stride.Turns.ToArray();
        var turns = new Rotation[gait.Length];
        var bones = Bones();
        var ground = new FootPlacement.LegGround[] { new(6, 0.03), new(-4, -0.02) };
        const int Count = 20_000;
        for (var i = 0; i < Count / 10; i++)
        {
            gait.CopyTo(turns, 0);
            FootPlacement.Apply(s_skeleton, turns, 90, ground, bones);
        }

        var bytes = SystemAllocation.StartMeasurement();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < Count; i++)
        {
            gait.CopyTo(turns, 0);
            FootPlacement.Apply(s_skeleton, turns, 90, ground, bones);
        }

        var microseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMicroseconds / Count;
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        output.WriteLine($"{microseconds:F2} µs to place a body's feet");
        Assert.True(microseconds < 1000);
        Assert.Equal(0, bytes);
    }

    private static RigidTransform[] Bones() => new RigidTransform[HumanoidTestSkeleton.Bones];

    /// <summary>The ankles of <paramref name="turns"/> with the skeleton raised by <paramref name="lift"/>.</summary>
    private static (Vector3 Right, Vector3 Left) Ankles(ReadOnlySpan<Rotation> turns, double lift)
    {
        var bones = Bones();
        s_skeleton.Pose(turns, bones);
        Vector3 At(TestBone bone) =>
            new((float)bones[(int)bone].X, (float)(bones[(int)bone].Y + lift), (float)bones[(int)bone].Z);
        return (At(TestBone.RightFoot), At(TestBone.LeftFoot));
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 0.5f, $"Expected {expected}, got {actual}.");

    /// <summary>
    /// Loads the ramp within 8 m of (<paramref name="x"/>, 1, 0) and returns a mover over it and the feet of a body
    /// standing where the mover would: on the highest ground under its circle.
    /// </summary>
    private (VoxelCharacterMover Mover, Vector3 Feet) StandOnRamp(float x)
    {
        _voxels = TestWorlds.Load(s_ramp, new Vector3(x, 1, 0), radiusMetres: 8);
        var mover = new VoxelCharacterMover(_voxels, new NothingPassable());
        Assert.True(mover.GroundTop(new Vector3(x, 1, 0.3f), 0.3f, 2, 2, out var top));
        return (mover, new Vector3(x, top, 0.3f));
    }
}
