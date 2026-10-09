using Itera.Engine.Animation;
using Itera.Engine.Maths;

namespace Itera.Engine.Tests.Animation;

/// <summary>
/// <see cref="FootPlacement"/> for a skeleton of four legs (a test skeleton: a trunk 80 voxels long and four legs of two
/// 25-voxel bones and a foot): flat ground leaves the pose as it is; ground that rises ahead pitches the trunk nose up by
/// the slope, and ground that rises to the right rolls it, with every ankle on the ground under it; a slope steeper than
/// <see cref="FootPlacement.MaxTilt"/> tilts it that far; and placing its feet allocates nothing.
/// </summary>
public sealed class LegChainPlacementTests
{
    private const double Lift = 60;

    private static readonly FourLegs s_skeleton = new();

    [Fact]
    public void Flat_ground_leaves_the_pose_as_it_is()
    {
        var turns = Stance();
        var before = (Rotation[])turns.Clone();
        var lift = FootPlacement.Apply(s_skeleton, turns, Lift, new FootPlacement.LegGround[4], Bones());

        Assert.Equal(Lift, lift);
        Assert.Equal(before, turns);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(-8, 0)]
    [InlineData(0, 12)]
    [InlineData(6, -9)]
    public void The_trunk_turns_to_the_slope_and_every_ankle_stands_on_it(double pitchDegrees, double rollDegrees)
    {
        var turns = Stance();
        var ankles = Ankles(turns, Lift);
        var ground = Plane(ankles, pitchDegrees, rollDegrees);

        var lift = FootPlacement.Apply(s_skeleton, turns, Lift, ground, Bones());

        var bones = Bones();
        s_skeleton.Pose(turns, bones);
        var root = bones[0].Rotation;
        // Forward (-z) rises by the pitch; right (+x) rises by the roll.
        Assert.Equal(Math.Sin(pitchDegrees * Math.PI / 180), -root.Myz, 2);
        Assert.Equal(Math.Sin(rollDegrees * Math.PI / 180), root.Myx, 2);
        var placed = Ankles(turns, lift);
        for (var i = 0; i < 4; i++)
        {
            var target = ankles[i] with { Y = ankles[i].Y + ground[i].Height };
            Assert.True(
                Distance(target, placed[i]) < 0.5,
                $"Leg {i}: expected {target}, got {placed[i]} (lift {lift:F2})."
            );
        }
    }

    [Fact]
    public void A_steep_slope_tilts_the_trunk_no_further_than_its_limit()
    {
        var turns = Stance();
        var ground = Plane(Ankles(turns, Lift), 35, 0);

        FootPlacement.Apply(s_skeleton, turns, Lift, ground, Bones());

        var bones = Bones();
        s_skeleton.Pose(turns, bones);
        Assert.Equal(Math.Sin(FootPlacement.MaxTilt * 2 * Math.PI), -bones[0].Rotation.Myz, 6);
    }

    [Fact]
    public void Placing_four_feet_allocates_nothing()
    {
        var turns = Stance();
        var stance = (Rotation[])turns.Clone();
        var ground = Plane(Ankles(turns, Lift), 8, -5);
        var bones = Bones();
        FootPlacement.Apply(s_skeleton, turns, Lift, ground, bones);

        var bytes = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            stance.CopyTo(turns, 0);
            FootPlacement.Apply(s_skeleton, turns, Lift, ground, bones);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - bytes);
    }

    /// <summary>The legs bent a little at the knee, as a four-legged body stands.</summary>
    private static Rotation[] Stance()
    {
        var turns = new Rotation[Skeleton.MaxBones];
        Array.Fill(turns, Rotation.Identity);
        foreach (var leg in s_skeleton.Legs)
        {
            turns[leg.Upper] = Rotation.AboutX(-0.03 * leg.Bend);
            turns[leg.Lower] = Rotation.AboutX(0.06 * leg.Bend);
            turns[leg.Foot] = Rotation.AboutX(-0.03 * leg.Bend);
        }

        return turns;
    }

    private static RigidTransform[] Bones() => new RigidTransform[s_skeleton.BoneCount];

    /// <summary>The ground under each ankle for a plane rising ahead by the pitch and to the right by the roll.</summary>
    private static FootPlacement.LegGround[] Plane(
        (double X, double Y, double Z)[] ankles,
        double pitchDegrees,
        double rollDegrees
    )
    {
        var ahead = Math.Tan(pitchDegrees * Math.PI / 180);
        var right = Math.Tan(rollDegrees * Math.PI / 180);
        return [.. ankles.Select(a => new FootPlacement.LegGround(-a.Z * ahead + a.X * right, 0))];
    }

    private static (double X, double Y, double Z)[] Ankles(ReadOnlySpan<Rotation> turns, double lift)
    {
        var bones = Bones();
        s_skeleton.Pose(turns, bones);
        var ankles = new (double X, double Y, double Z)[4];
        for (var i = 0; i < 4; i++)
        {
            var foot = bones[s_skeleton.Legs[i].Foot];
            ankles[i] = (foot.X, foot.Y + lift, foot.Z);
        }

        return ankles;
    }

    private static double Distance((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>
    /// A trunk (the root) with four legs hanging from its corners, 40 voxels ahead and behind and 15 to each side: each
    /// an upper and a lower bone of 25 voxels and a foot of 5, the front knees bent back and the hind ones forward.
    /// </summary>
    private sealed class FourLegs : Skeleton
    {
        public FourLegs()
            : base(13)
        {
            Set(0, 0, (0, 0, 0), Rotation.Identity);
            var legs = new LegChain[4];
            var corners = new (double X, double Z)[] { (15, -40), (-15, -40), (15, 40), (-15, 40) };
            for (var i = 0; i < 4; i++)
            {
                var upper = 1 + 3 * i;
                Set(upper, 0, (corners[i].X, 0, corners[i].Z), Rotation.Identity);
                Set(upper + 1, upper, (0, -25, 0), Rotation.Identity);
                Set(upper + 2, upper + 1, (0, -25, 0), Rotation.Identity);
                legs[i] = new LegChain(upper, upper + 1, upper + 2, 25, 25, -5, 2, -4, 6, corners[i].Z < 0 ? 1 : -1);
            }

            SetLegs(legs);
            StepVoxels = 20;
        }
    }
}
