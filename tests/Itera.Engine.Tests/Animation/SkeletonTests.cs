using Itera.Engine.Animation;
using Itera.Engine.Maths;

namespace Itera.Engine.Tests.Animation;

/// <summary>
/// <see cref="Skeleton"/>: a two-legged test skeleton's data (parents, ragdoll joints, legs, look), posing only the legs
/// as posing all does, the grid shifts, and the limits on bones.
/// </summary>
public sealed class SkeletonTests
{
    private static readonly HumanoidTestSkeleton s_human = HumanoidTestSkeleton.Build();

    private static readonly TestBone[] s_legBones =
    [
        TestBone.RightThigh,
        TestBone.RightShin,
        TestBone.RightFoot,
        TestBone.LeftThigh,
        TestBone.LeftShin,
        TestBone.LeftFoot,
    ];

    [Fact]
    public void Every_bone_comes_after_its_parent_and_the_root_is_its_own()
    {
        Assert.Equal(HumanoidTestSkeleton.Bones, s_human.BoneCount);
        Assert.Equal(0, s_human.Parent(0));
        for (var bone = 1; bone < s_human.BoneCount; bone++)
        {
            Assert.InRange(s_human.Parent(bone), 0, bone - 1);
        }

        Assert.Equal((int)TestBone.Chest, s_human.Parent((int)TestBone.Neck));
        Assert.Equal((int)TestBone.RightThigh, s_human.Parent((int)TestBone.RightShin));
        Assert.Equal((int)TestBone.Chest, s_human.Parent((int)TestBone.LeftClavicle));
    }

    [Fact]
    public void A_skeleton_keeps_its_ragdoll_joints_legs_and_look()
    {
        foreach (
            var (bone, swing, axis) in (ReadOnlySpan<(TestBone, float, JointAxis)>)
                [
                    (TestBone.Hips, 0.35f, JointAxis.Y),
                    (TestBone.Spine, 0.35f, JointAxis.Y),
                    (TestBone.Chest, 0.35f, JointAxis.Y),
                    (TestBone.Neck, 0.6f, JointAxis.Y),
                    (TestBone.Head, 0.6f, JointAxis.Y),
                    (TestBone.RightClavicle, 0.3f, JointAxis.X),
                    (TestBone.LeftClavicle, 0.3f, JointAxis.X),
                    (TestBone.RightUpperArm, 1.6f, JointAxis.Y),
                    (TestBone.LeftUpperArm, 1.6f, JointAxis.Y),
                    (TestBone.RightForearm, 1.3f, JointAxis.Y),
                    (TestBone.LeftShin, 1.3f, JointAxis.Y),
                    (TestBone.RightHand, 0.9f, JointAxis.Y),
                    (TestBone.LeftThigh, 1.4f, JointAxis.Y),
                    (TestBone.RightFoot, 0.7f, JointAxis.Z),
                    (TestBone.LeftFoot, 0.7f, JointAxis.Z),
                ]
        )
        {
            Assert.Equal(swing, s_human.MaxSwing((int)bone));
            Assert.Equal(axis, s_human.Axis((int)bone));
        }

        Assert.Equal([(int)TestBone.RightFoot, (int)TestBone.LeftFoot], s_human.Feet.ToArray());
        Assert.Equal(s_human.Feet.ToArray(), s_human.StandsOn.ToArray());
        Assert.Equal(
            [(int)TestBone.Spine, (int)TestBone.Chest, (int)TestBone.Neck, (int)TestBone.Head],
            s_human.Look.ToArray().Select(share => share.Bone)
        );
        Assert.Equal("right forearm", s_human.Name((int)TestBone.RightForearm));
        Assert.Equal(s_human.ThighLength + s_human.ShinLength, s_human.LegVoxels);
        Assert.False(s_human.TiltsTrunk);
    }

    [Fact]
    public void A_grid_shift_is_a_twentieth_of_a_voxel_per_bone_for_19_bones()
    {
        for (var bone = 0; bone < s_human.BoneCount; bone++)
        {
            Assert.Equal((bone + 1) / 20.0, s_human.GridShift(bone));
        }
    }

    [Fact]
    public void Posing_the_legs_alone_places_them_as_posing_every_bone_does()
    {
        var turns = TestPoses.Stride.Turns.ToArray();
        var all = new RigidTransform[s_human.BoneCount];
        var legs = new RigidTransform[s_human.BoneCount];
        s_human.Pose(turns, all);
        s_human.PoseLegs(turns, legs);

        foreach (var bone in (ReadOnlySpan<TestBone>)[TestBone.Hips, .. s_legBones])
        {
            Assert.Equal(all[(int)bone], legs[(int)bone]);
        }

        // The arms are left alone.
        Assert.Equal(default, legs[(int)TestBone.RightHand]);
    }

    [Fact]
    public void A_skeleton_has_one_to_32_bones_each_after_its_parent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Chain(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Chain(Skeleton.MaxBones + 1, 1));
        Assert.Throws<ArgumentException>(() => new Chain(3, 2));

        var chain = new Chain(Skeleton.MaxBones, 1);
        Assert.Equal(Skeleton.MaxBones, chain.BoneCount);
        Assert.Equal(Skeleton.MaxBones - 2, chain.Parent(Skeleton.MaxBones - 1));
        // Every bone's grid lies apart from every other's by a fraction of a voxel.
        var shifts = Enumerable.Range(0, chain.BoneCount).Select(chain.GridShift).ToList();
        Assert.Equal(shifts.Count, shifts.Distinct().Count());
        Assert.All(shifts, shift => Assert.InRange(shift, 0.01, 0.99));
    }

    /// <summary>A chain of bones, each 10 voxels above the one before; bone <c>last</c>, past 1, is its own parent.</summary>
    private sealed class Chain : Skeleton
    {
        public Chain(int bones, int last)
            : base(bones)
        {
            for (var bone = 0; bone < bones; bone++)
            {
                Set(bone, bone == last && bone > 1 ? bone : Math.Max(0, bone - 1), (0, 10, 0), Rotation.Identity);
            }
        }
    }
}
