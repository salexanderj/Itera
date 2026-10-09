using Itera.Engine.Maths;

namespace Itera.Engine.Animation;

/// <summary>The axis of a bone's own space that its part points along, which a ragdoll's joint turns about least.</summary>
public enum JointAxis : byte
{
    X = 0,
    Y = 1,
    Z = 2,
}

/// <summary>
/// One leg of a skeleton, for foot placement (<see cref="FootPlacement"/>): its upper bone (a thigh), its lower bone (a
/// shin), and its foot, the bone the body stands on, each the child of the one before; lengths in level-0 voxels.
/// </summary>
/// <param name="Upper">The upper bone.</param>
/// <param name="Lower">The lower bone.</param>
/// <param name="Foot">The foot: the last bone, whose joint is the ankle.</param>
/// <param name="UpperLength">From the upper bone's joint to the lower bone's (hip to knee).</param>
/// <param name="LowerLength">From the lower bone's joint to the foot's (knee to ankle).</param>
/// <param name="Sole">The sole's height in the foot's space, below the ankle (negative).</param>
/// <param name="Heel">Where the heel is probed along the foot's z (behind the ankle: positive).</param>
/// <param name="Toe">Where the toes are probed along the foot's z (ahead: negative).</param>
/// <param name="Span">How far apart heel and toe are taken to be for the slope between them; 0 for a foot that ends in a
/// point, probed at <paramref name="Heel"/> alone.</param>
/// <param name="Bend">Which way the knee bends: -1 turns the lower bone back about its x (the knee points forward), 1 turns it forward (the knee points back).</param>
public readonly record struct LegChain(
    int Upper,
    int Lower,
    int Foot,
    double UpperLength,
    double LowerLength,
    double Sole,
    double Heel,
    double Toe,
    double Span,
    double Bend
);

/// <summary>
/// A bone that lies along the ground (a segment of a legless body), for <see cref="SegmentPlacement"/>: its far end lies
/// <paramref name="Length"/> along its own z (positive behind its joint, negative ahead of it), and the body's centre line
/// there stands <paramref name="TipRadius"/> above the ground.
/// </summary>
public readonly record struct DrapeBone(int Bone, double Length, double TipRadius);

/// <summary>The share of a look to the side (<paramref name="Yaw"/>) and up or down (<paramref name="Pitch"/>) a bone takes.</summary>
public readonly record struct LookShare(int Bone, double Yaw, double Pitch);

/// <summary>
/// The skeleton of any body plan, in level-0 voxels in the body's frame (origin on the ground between the feet, facing
/// -z, +x to its right, +y up): its bones, each with its parent, where its joint lies in its parent's space
/// (<see cref="Offset"/>), and its rest turn there (<see cref="Rest"/>), and what the code shared by every plan needs to
/// know of them: the joints of a ragdoll, the legs the body stands on, and the bones a look turns. Bone 0 is the root (a
/// humanoid's hips), its own parent; every other bone comes after its parent. A pose is a turn per bone
/// (<see cref="BodyPose"/>), so a pose fits every skeleton of a plan whatever its lengths. A plan builds its skeleton in
/// a class of its own. Immutable once built.
/// </summary>
public abstract class Skeleton
{
    /// <summary>
    /// The most bones a skeleton may have: bone masks are 32 bits, and a pose store keeps this many turns for every
    /// pose.
    /// </summary>
    public const int MaxBones = 32;

    private readonly int[] _parents;
    private readonly (double X, double Y, double Z)[] _offsets;
    private readonly Rotation[] _rest;
    private readonly JointAxis[] _axes;
    private readonly float[] _swings;
    private readonly string[] _names;
    private LegChain[] _legs = [];
    private int[] _feet = [];
    private int[] _standsOn = [];
    private bool _standsOnSet;
    private DrapeBone[] _drape = [];
    private int[] _legBones = [];
    private LookShare[] _look = [];
    private double[] _strideCentres = [];

    protected Skeleton(int boneCount)
    {
        if (boneCount is < 1 or > MaxBones)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boneCount),
                boneCount,
                $"A skeleton has 1 to {MaxBones} bones."
            );
        }

        BoneCount = boneCount;
        _parents = new int[boneCount];
        _offsets = new (double, double, double)[boneCount];
        _rest = new Rotation[boneCount];
        Array.Fill(_rest, Rotation.Identity);
        _axes = new JointAxis[boneCount];
        Array.Fill(_axes, JointAxis.Y);
        _swings = new float[boneCount];
        _names = new string[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
        {
            _names[bone] = "bone " + bone;
        }
    }

    public int BoneCount { get; }

    /// <summary>How far the body steps up or down at most, in level-0 voxels (foot placement's reach).</summary>
    public double StepVoxels { get; protected set; }

    /// <summary>
    /// The last share of a leg's reach (upper and lower bones straight) over which it straightens gradually, however far
    /// its ankle is asked to go (foot placement's soft reach): near straight, a small change of reach bends a knee a long
    /// way, so a leg stretched onto lower ground would snap straight and back. 0 (none) by default.
    /// </summary>
    public double SoftReach { get; protected set; }

    /// <summary>True when foot placement tilts the root to the slope under the feet: a body of four legs or more.</summary>
    public bool TiltsTrunk => _legs.Length >= 4;

    /// <summary>
    /// The longest stroke every leg's ankle can make along the body within its reach, in level-0 voxels
    /// (<see cref="StrokeLimit"/>); 0 for a body that does not stride.
    /// </summary>
    public double MaxStroke { get; protected set; }

    /// <summary>The height of the eyes above the feet at rest, in metres, for looking.</summary>
    public double EyeMetres { get; protected set; }

    /// <summary>
    /// The length of a leg from its upper joint to its ankle, in level-0 voxels: how far a clip's hip press
    /// (<see cref="AnimationClip"/>) reaches.
    /// </summary>
    public double LegVoxels { get; protected set; }

    /// <summary>The legs, for foot placement.</summary>
    public ReadOnlySpan<LegChain> Legs => _legs;

    /// <summary>The bones the body stands on: each leg's foot.</summary>
    public ReadOnlySpan<int> Feet => _feet;

    /// <summary>
    /// The bones whose level-0 shapes the body stands on when nothing else places it: its feet, or a legless body's
    /// every segment.
    /// </summary>
    public ReadOnlySpan<int> StandsOn => _standsOn;

    /// <summary>The bones that lie along the ground, parents first (<see cref="SegmentPlacement"/>); none by default.</summary>
    public ReadOnlySpan<DrapeBone> Drape => _drape;

    /// <summary>The bones a look turns, with their shares, from the root out.</summary>
    public ReadOnlySpan<LookShare> Look => _look;

    /// <summary>The parent of <paramref name="bone"/>; the root is its own.</summary>
    public int Parent(int bone) => _parents[bone];

    /// <summary>Where <paramref name="bone"/>'s joint lies in its parent's space (the root's: in the body's frame).</summary>
    public (double X, double Y, double Z) Offset(int bone) => _offsets[bone];

    /// <summary><paramref name="bone"/>'s rest turn in its parent's space.</summary>
    public Rotation Rest(int bone) => _rest[bone];

    /// <summary>The axis of <paramref name="bone"/>'s space its part points along.</summary>
    public JointAxis Axis(int bone) => _axes[bone];

    /// <summary>How far <paramref name="bone"/>'s joint swings in a ragdoll from how it stood when cut, in radians.</summary>
    public float MaxSwing(int bone) => _swings[bone];

    /// <summary>The bone's name, as a debug readout's title shows it.</summary>
    public string Name(int bone) => _names[bone];

    /// <summary>
    /// How far a part volume's corner is moved back on every axis, in level-0 voxels: a different fraction of a voxel for
    /// each bone, (bone + 1) / (bones + 1), so no two parts' grids line up.
    /// </summary>
    public double GridShift(int bone) => (bone + 1) / (double)(BoneCount + 1);

    /// <summary><see cref="Pose(ReadOnlySpan{Rotation}, Span{RigidTransform})"/> for <paramref name="pose"/>.</summary>
    public void Pose(BodyPose pose, Span<RigidTransform> transforms) => Pose(pose.Turns, transforms);

    /// <summary>
    /// Each bone's transform from its own space to the body's frame for the turn per bone in <paramref name="turns"/>,
    /// into <paramref name="transforms"/> (one per bone): the parent's transform, then the move to the joint, the rest
    /// turn, and the pose's turn.
    /// </summary>
    public void Pose(ReadOnlySpan<Rotation> turns, Span<RigidTransform> transforms)
    {
        for (var i = 0; i < BoneCount; i++)
        {
            var (x, y, z) = _offsets[i];
            var local = new RigidTransform(_rest[i].Times(turns[i]), x, y, z);
            transforms[i] = i == 0 ? local : transforms[_parents[i]].Times(local);
        }
    }

    /// <summary>
    /// <see cref="Pose(ReadOnlySpan{Rotation}, Span{RigidTransform})"/> for the bones foot placement moves alone (the
    /// legs, the bones the body stands on, and the bones between them and the root); the other bones' transforms are
    /// left as they were.
    /// </summary>
    public void PoseLegs(ReadOnlySpan<Rotation> turns, Span<RigidTransform> transforms)
    {
        foreach (var i in _legBones)
        {
            var (x, y, z) = _offsets[i];
            var local = new RigidTransform(_rest[i].Times(turns[i]), x, y, z);
            transforms[i] = i == 0 ? local : transforms[_parents[i]].Times(local);
        }
    }

    /// <summary>Sets <paramref name="bone"/>'s parent, joint, and rest turn. Its parent must come before it.</summary>
    protected void Set(int bone, int parent, (double X, double Y, double Z) offset, Rotation rest)
    {
        if (bone == 0 ? parent != 0 : parent < 0 || parent >= bone)
        {
            throw new ArgumentException($"Bone {bone}'s parent {parent} does not come before it.", nameof(parent));
        }

        _parents[bone] = parent;
        _offsets[bone] = offset;
        _rest[bone] = rest;
    }

    /// <summary>Sets how <paramref name="bone"/>'s ragdoll joint turns and its name.</summary>
    protected void SetJoint(int bone, string name, JointAxis axis, float maxSwing)
    {
        _names[bone] = name;
        _axes[bone] = axis;
        _swings[bone] = maxSwing;
    }

    /// <summary>Sets the legs (at most <see cref="FootPlacement.MaxLegs"/>); the bones foot placement moves follow from them.</summary>
    protected void SetLegs(ReadOnlySpan<LegChain> legs)
    {
        if (legs.Length > FootPlacement.MaxLegs)
        {
            throw new ArgumentException($"A skeleton has at most {FootPlacement.MaxLegs} legs.", nameof(legs));
        }

        _legs = legs.ToArray();
        _feet = new int[legs.Length];
        for (var i = 0; i < legs.Length; i++)
        {
            _feet[i] = legs[i].Foot;
        }

        if (!_standsOnSet)
        {
            _standsOn = _feet;
        }

        UpdateMoved();
    }

    /// <summary>
    /// Where leg <paramref name="leg"/>'s stride is centred while it walks, along the body from where its ankle stands at
    /// rest, in level-0 voxels: part of the way back under its hip, so it has room to reach forward and back
    /// (<see cref="StrokeLimit"/>).
    /// </summary>
    public double StrideCentre(int leg) => _strideCentres.Length > leg ? _strideCentres[leg] : 0;

    /// <summary>
    /// The longest stroke every leg can make along the body (z), for a gait that moves its ankles in straight lines
    /// (<see cref="FootPlacement.Reach"/>), and each leg's stride centre (<see cref="StrideCentre"/>). An ankle may move
    /// along z as far as keeps it within <paramref name="reachShare"/> of its hip's reach (the upper and lower bones
    /// straight): a stretch either side of the point under its hip. Each stride is centred 60 % of the way from where
    /// the ankle stands at rest to that point, and the stroke is twice the least room either way any leg has about its
    /// centre. Call once the bones and legs are set.
    /// </summary>
    protected double StrokeLimit(double reachShare = 0.95)
    {
        var bones = new RigidTransform[BoneCount];
        var rest = new Rotation[BoneCount];
        Array.Fill(rest, Rotation.Identity);
        Pose(rest, bones);
        var half = double.PositiveInfinity;
        _strideCentres = new double[_legs.Length];
        for (var i = 0; i < _legs.Length; i++)
        {
            var leg = _legs[i];
            var reach = reachShare * (leg.UpperLength + leg.LowerLength);
            var (hip, ankle) = (bones[leg.Upper], bones[leg.Foot]);
            double x = ankle.X - hip.X,
                y = ankle.Y - hip.Y,
                z = ankle.Z - hip.Z;
            var room = reach * reach - x * x - y * y;
            _strideCentres[i] = -0.6 * z;
            half = Math.Min(half, room > 0 ? Math.Sqrt(room) - 0.4 * Math.Abs(z) : 0);
        }

        return _legs.Length == 0 ? 0 : 2 * Math.Max(0, half);
    }

    /// <summary>Sets the bones the body stands on, in place of its feet (a legless body's segments).</summary>
    protected void SetStandsOn(ReadOnlySpan<int> bones)
    {
        _standsOn = bones.ToArray();
        _standsOnSet = true;
        UpdateMoved();
    }

    /// <summary>Sets the bones that lie along the ground, parents first.</summary>
    protected void SetDrape(ReadOnlySpan<DrapeBone> bones)
    {
        for (var i = 1; i < bones.Length; i++)
        {
            if (bones[i].Bone <= bones[i - 1].Bone)
            {
                throw new ArgumentException("Draped bones come in bone order, parents first.", nameof(bones));
            }
        }

        _drape = bones.ToArray();
    }

    /// <summary>
    /// The bones <see cref="PoseLegs"/> poses: the root, the legs, the bones the body stands on, and every bone between
    /// them and the root.
    /// </summary>
    private void UpdateMoved()
    {
        var moved = new bool[BoneCount];
        moved[0] = true;
        foreach (var end in (int[])[.. _feet, .. _standsOn])
        {
            for (var bone = end; bone != 0; bone = _parents[bone])
            {
                moved[bone] = true;
            }
        }

        var bones = new List<int>();
        for (var bone = 0; bone < BoneCount; bone++)
        {
            if (moved[bone])
            {
                bones.Add(bone);
            }
        }

        _legBones = [.. bones];
    }

    /// <summary>Sets the bones a look turns and their shares.</summary>
    protected void SetLook(ReadOnlySpan<LookShare> look) => _look = look.ToArray();
}
