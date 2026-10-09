using Itera.Engine.Animation;
using Itera.Engine.Maths;

namespace Itera.Engine.Tests.Animation;

/// <summary>The bones of <see cref="HumanoidTestSkeleton"/>, parents before children.</summary>
internal enum TestBone : byte
{
    Hips = 0,
    Spine = 1,
    Chest = 2,
    Neck = 3,
    Head = 4,
    RightClavicle = 5,
    RightUpperArm = 6,
    RightForearm = 7,
    RightHand = 8,
    LeftClavicle = 9,
    LeftUpperArm = 10,
    LeftForearm = 11,
    LeftHand = 12,
    RightThigh = 13,
    RightShin = 14,
    RightFoot = 15,
    LeftThigh = 16,
    LeftShin = 17,
    LeftFoot = 18,
}

/// <summary>
/// A two-legged skeleton of 19 bones, 1.7 m tall, for tests: the bone table of a humanoid body (the bone numbers and
/// lengths a real body would use) over <see cref="Skeleton"/>. Its legs are two bones and a foot, its look is the spine,
/// chest, neck, and head.
/// </summary>
internal sealed class HumanoidTestSkeleton : Skeleton
{
    public const int Bones = 19;

    // The proportions of a 170 voxel body: head 0.13, legs 0.51, arms 0.34, shoulders 0.22, hips 0.105, no hunch, feet at
    // their usual size.
    private const double Height = 170;
    private const double Head = 0.13;
    private const double LegShare = 0.51;
    private const double Arms = 0.34;
    private const double Shoulders = 0.22;
    private const double Hips = 0.105;
    private const double Hunch = 0;
    private const double FootShare = 1;

    private static readonly TestBone[] s_parents =
    [
        TestBone.Hips,
        TestBone.Hips,
        TestBone.Spine,
        TestBone.Chest,
        TestBone.Neck,
        TestBone.Chest,
        TestBone.RightClavicle,
        TestBone.RightUpperArm,
        TestBone.RightForearm,
        TestBone.Chest,
        TestBone.LeftClavicle,
        TestBone.LeftUpperArm,
        TestBone.LeftForearm,
        TestBone.Hips,
        TestBone.RightThigh,
        TestBone.RightShin,
        TestBone.Hips,
        TestBone.LeftThigh,
        TestBone.LeftShin,
    ];

    private static readonly string[] s_names =
    [
        "hips",
        "waist",
        "chest",
        "neck",
        "head",
        "right shoulder",
        "right upper arm",
        "right forearm",
        "right hand",
        "left shoulder",
        "left upper arm",
        "left forearm",
        "left hand",
        "right thigh",
        "right shin",
        "right foot",
        "left thigh",
        "left shin",
        "left foot",
    ];

    private static readonly LookShare[] s_look =
    [
        new((int)TestBone.Spine, 0.15, 0.1),
        new((int)TestBone.Chest, 0.25, 0.15),
        new((int)TestBone.Neck, 0.25, 0.3),
        new((int)TestBone.Head, 0.35, 0.45),
    ];

    private HumanoidTestSkeleton(double armSpread)
        : base(Bones)
    {
        Scale = Height / 170;
        var headHeight = Head * Height;
        var shoulderHalf = Shoulders * Height / 2;
        var hipHalf = Hips * Height / 2;
        AnkleHeight = 0.045 * Height * FootShare;
        FootLength = 0.15 * Height * FootShare;

        // Down from the top of the head: the chin, the skull's joint on the neck, the base of the neck, the shoulders.
        var chin = Height - headHeight;
        var headJoint = chin + 0.15 * headHeight;
        var neckBase = chin - 0.2 * headHeight;
        var shoulderY = neckBase - 3 * Scale;

        // Up from the ground: the ankles, the hip joints, and the pelvis a little above them.
        var hipJoint = LegShare * Height;
        var legLength = hipJoint - AnkleHeight;
        ThighLength = 0.51 * legLength;
        ShinLength = legLength - ThighLength;
        var pelvis = hipJoint + 0.03 * Height;
        var torso = Math.Max(neckBase - pelvis, 10 * Scale);
        var spineLength = 0.38 * torso;
        var chestLength = torso - spineLength;
        var neckLength = headJoint - neckBase;
        var upperArmLength = 0.56 * Arms * Height;
        var forearmLength = Arms * Height - upperArmLength;

        var back = 1.5 * Scale;
        Set(TestBone.Hips, (0, pelvis, back), Rotation.Identity);
        Set(TestBone.Spine, (0, 0, 0), Rotation.AboutX(-0.03 * Hunch));
        Set(TestBone.Chest, (0, spineLength, 0), Rotation.AboutX(-0.04 * Hunch));
        Set(TestBone.Neck, (0, chestLength, 0), Rotation.AboutX(-0.06 * Hunch));
        Set(TestBone.Head, (0, neckLength, 0), Rotation.AboutX(0.13 * Hunch));

        var clavicleX = shoulderHalf - 2 * Scale;
        var clavicleY = shoulderY - neckBase;
        foreach (var side in (ReadOnlySpan<int>)[1, -1])
        {
            var right = side == 1;
            var spread = Rotation.AboutZ(armSpread);
            Set(
                right ? TestBone.RightClavicle : TestBone.LeftClavicle,
                (side * 2 * Scale, chestLength, 0),
                Rotation.Identity
            );
            Set(
                right ? TestBone.RightUpperArm : TestBone.LeftUpperArm,
                (side * clavicleX, clavicleY, 0.5 * Scale),
                right ? spread : spread.Mirrored()
            );
            Set(right ? TestBone.RightForearm : TestBone.LeftForearm, (0, -upperArmLength, 0), Rotation.AboutX(0.02));
            Set(right ? TestBone.RightHand : TestBone.LeftHand, (0, -forearmLength, 0), Rotation.Identity);
            Set(
                right ? TestBone.RightThigh : TestBone.LeftThigh,
                (side * hipHalf, hipJoint - pelvis, -back),
                Rotation.Identity
            );
            Set(right ? TestBone.RightShin : TestBone.LeftShin, (0, -ThighLength, 0), Rotation.Identity);
            Set(right ? TestBone.RightFoot : TestBone.LeftFoot, (0, -ShinLength, 0), Rotation.Identity);
        }

        SetBones();
    }

    /// <summary>Height over 1.7 m: the scale of lengths that follow the height alone.</summary>
    public double Scale { get; }

    public double ThighLength { get; }

    public double ShinLength { get; }

    /// <summary>Height of the ankle joint above the sole.</summary>
    public double AnkleHeight { get; }

    public double FootLength { get; }

    public static HumanoidTestSkeleton Build() => new(0.02);

    private void Set(TestBone bone, (double X, double Y, double Z) offset, Rotation rest) =>
        Set((int)bone, (int)s_parents[(int)bone], offset, rest);

    private void SetBones()
    {
        foreach (
            var (bone, axis, swing) in (ReadOnlySpan<(TestBone, JointAxis, float)>)
                [
                    (TestBone.Hips, JointAxis.Y, 0.35f),
                    (TestBone.Spine, JointAxis.Y, 0.35f),
                    (TestBone.Chest, JointAxis.Y, 0.35f),
                    (TestBone.Neck, JointAxis.Y, 0.6f),
                    (TestBone.Head, JointAxis.Y, 0.6f),
                    (TestBone.RightClavicle, JointAxis.X, 0.3f),
                    (TestBone.RightUpperArm, JointAxis.Y, 1.6f),
                    (TestBone.RightForearm, JointAxis.Y, 1.3f),
                    (TestBone.RightHand, JointAxis.Y, 0.9f),
                    (TestBone.LeftClavicle, JointAxis.X, 0.3f),
                    (TestBone.LeftUpperArm, JointAxis.Y, 1.6f),
                    (TestBone.LeftForearm, JointAxis.Y, 1.3f),
                    (TestBone.LeftHand, JointAxis.Y, 0.9f),
                    (TestBone.RightThigh, JointAxis.Y, 1.4f),
                    (TestBone.RightShin, JointAxis.Y, 1.3f),
                    (TestBone.RightFoot, JointAxis.Z, 0.7f),
                    (TestBone.LeftThigh, JointAxis.Y, 1.4f),
                    (TestBone.LeftShin, JointAxis.Y, 1.3f),
                    (TestBone.LeftFoot, JointAxis.Z, 0.7f),
                ]
        )
        {
            SetJoint((int)bone, s_names[(int)bone], axis, swing);
        }

        // The heel a little behind the ankle and the toes well ahead of it, on the sole; the knees bend forward.
        LegChain Leg(TestBone thigh) =>
            new(
                (int)thigh,
                (int)thigh + 1,
                (int)thigh + 2,
                ThighLength,
                ShinLength,
                -AnkleHeight,
                0.2 * FootLength,
                -0.65 * FootLength,
                0.85 * FootLength,
                -1
            );
        SetLegs([Leg(TestBone.RightThigh), Leg(TestBone.LeftThigh)]);
        StepVoxels = 0.24 * 170 * Scale;
        SetLook(s_look);
        EyeMetres = 0.93 * 1.7 * Scale;
        LegVoxels = ThighLength + ShinLength;
    }
}

/// <summary>Poses of <see cref="HumanoidTestSkeleton"/> that tests share.</summary>
internal static class TestPoses
{
    /// <summary>
    /// Mid-stride, with an arm raised: the right thigh forward and its knee bent, the left thigh back with its heel up;
    /// the right upper arm raised forward with the elbow at a right angle; the left arm swung out and bent a little; the
    /// trunk turned a little to the left and the head further. Asymmetric on purpose, so elbows, knees, shoulders, hips,
    /// and the neck are all seen bent.
    /// </summary>
    public static BodyPose Stride { get; } =
        BodyPose
            .Rest.With((int)TestBone.Spine, Rotation.AboutY(0.015))
            .With((int)TestBone.Chest, Rotation.AboutY(0.02))
            .With((int)TestBone.Head, Rotation.AboutY(0.05).Times(Rotation.AboutX(-0.02)))
            .With((int)TestBone.RightThigh, Rotation.AboutX(0.07))
            .With((int)TestBone.RightShin, Rotation.AboutX(-0.06))
            .With((int)TestBone.RightFoot, Rotation.AboutX(-0.01))
            .With((int)TestBone.LeftThigh, Rotation.AboutX(-0.05))
            .With((int)TestBone.LeftShin, Rotation.AboutX(-0.08))
            .With((int)TestBone.LeftFoot, Rotation.AboutX(0.07))
            .With((int)TestBone.RightUpperArm, Rotation.AboutX(0.2))
            .With((int)TestBone.RightForearm, Rotation.AboutX(0.25))
            .With((int)TestBone.LeftUpperArm, Rotation.AboutZ(-0.07).Times(Rotation.AboutX(-0.03)))
            .With((int)TestBone.LeftForearm, Rotation.AboutX(0.08));
}
