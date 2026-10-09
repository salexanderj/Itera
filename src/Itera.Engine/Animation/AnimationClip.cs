using Itera.Engine.Maths;

namespace Itera.Engine.Animation;

/// <summary>
/// A keyframe clip written in C# (poses and times; no asset files): keys of a pose (a turn per bone,
/// <see cref="BodyPose"/>) and how far the hips press down (a share of the leg's length), and the bones it drives. A
/// clip adds onto the pose under it rather than replacing it, so a landing's dip keeps a run's arm swing; its
/// first and last keys are usually no change, so it fades in and out. Between keys the turns ease in and out. A pose
/// fits every skeleton, so a clip does too. Allocates nothing when sampled.
/// </summary>
public sealed class AnimationClip
{
    private readonly Key[] _keys;
    private readonly int[] _bones;

    /// <param name="name">Its name, for logs.</param>
    /// <param name="bones">The bones it drives, by index in the skeleton; others are left as the pose has them.</param>
    /// <param name="keys">Its keys, in time order, the first at 0 s.</param>
    public AnimationClip(string name, IReadOnlyList<int> bones, IReadOnlyList<Key> keys)
    {
        if (keys.Count < 2 || keys[0].Seconds != 0)
        {
            throw new ArgumentException("A clip needs two keys or more, the first at 0 s.", nameof(keys));
        }

        for (var i = 1; i < keys.Count; i++)
        {
            if (keys[i].Seconds <= keys[i - 1].Seconds)
            {
                throw new ArgumentException("A clip's keys must be in time order.", nameof(keys));
            }
        }

        Name = name;
        _bones = [.. bones];
        _keys = [.. keys];
    }

    public string Name { get; }

    /// <summary>How long it runs: the time of its last key, in seconds.</summary>
    public double Seconds => _keys[^1].Seconds;

    /// <summary>The bones it drives.</summary>
    public ReadOnlySpan<int> Bones => _bones;

    /// <summary>Its keys, in time order.</summary>
    public ReadOnlySpan<Key> Keys => _keys;

    /// <summary>
    /// The same clip on the body's other side, named <paramref name="name"/>: each bone's turns go to its partner in
    /// <paramref name="partner"/> (by bone; a bone on the body's middle is its own), mirrored across the body's middle
    /// (<see cref="Rotation.Mirrored"/>), so a right punch becomes a left one.
    /// </summary>
    public AnimationClip Mirrored(string name, ReadOnlySpan<int> partner)
    {
        var bones = new int[_bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            bones[i] = partner[_bones[i]];
        }

        var keys = new Key[_keys.Length];
        for (var k = 0; k < keys.Length; k++)
        {
            keys[k] = _keys[k] with { Pose = _keys[k].Pose.Mirrored(partner) };
        }

        return new AnimationClip(name, bones, keys);
    }

    /// <summary>
    /// The legs of <paramref name="skeleton"/> the clip drives (a kick's, say), one bit per leg: foot
    /// placement leaves them as the clip holds them.
    /// </summary>
    public uint ClaimedLegs(Skeleton skeleton)
    {
        var legs = skeleton.Legs;
        var claimed = 0u;
        for (var leg = 0; leg < legs.Length; leg++)
        {
            foreach (var bone in _bones)
            {
                if (bone == legs[leg].Upper || bone == legs[leg].Lower || bone == legs[leg].Foot)
                {
                    claimed |= 1u << leg;
                    break;
                }
            }
        }

        return claimed;
    }

    /// <summary>The bones it drives, one bit per bone.</summary>
    public uint BoneMask
    {
        get
        {
            var mask = 0u;
            foreach (var bone in _bones)
            {
                mask |= 1u << bone;
            }

            return mask;
        }
    }

    /// <summary>
    /// Adds the clip at <paramref name="seconds"/> into its run onto the pose in <paramref name="turns"/> (one per bone),
    /// scaled by <paramref name="weight"/> (0 no change, 1 the whole clip), and returns how far the hips press down, as
    /// a share of the leg's length. Before 0 and after <see cref="Seconds"/> it is its first and last key.
    /// </summary>
    public double Sample(double seconds, double weight, Span<Rotation> turns)
    {
        var next = 1;
        while (next < _keys.Length - 1 && _keys[next].Seconds < seconds)
        {
            next++;
        }

        ref readonly var from = ref _keys[next - 1];
        ref readonly var to = ref _keys[next];
        var t = Math.Clamp((seconds - from.Seconds) / (to.Seconds - from.Seconds), 0, 1);
        t = t * t * (3 - 2 * t);
        if (weight <= 0)
        {
            return 0;
        }

        foreach (var bone in _bones)
        {
            if ((uint)bone >= (uint)turns.Length)
            {
                continue;
            }

            var turn = Rotation.Blend(from.Pose.Turns[bone], to.Pose.Turns[bone], t);
            var scaled = weight >= 1 ? turn : Rotation.Blend(Rotation.Identity, turn, weight);
            turns[bone] = scaled.Times(turns[bone]);
        }

        return weight * (from.Drop + (to.Drop - from.Drop) * t);
    }

    /// <summary>
    /// As <see cref="Sample"/>, on a body of <paramref name="skeleton"/>, and returns how far the hips press down (never
    /// below 0). When the clip turns the root (a body rearing), the legs it does not drive keep their feet where the
    /// pose under it put them, so the body turns over them instead of rocking on them all, and a negative drop raises
    /// the body on those legs by that share of the leg's length (their feet reach down as far). The legs in
    /// <paramref name="leaveLegs"/> (one bit per leg: those missing a part) are left as the pose has them.
    /// <paramref name="bones"/> (one per bone) is scratch. Allocates nothing.
    /// </summary>
    public double SampleOn(
        Skeleton skeleton,
        double seconds,
        double weight,
        Span<Rotation> turns,
        Span<RigidTransform> bones,
        uint leaveLegs = 0
    )
    {
        if ((BoneMask & 1u) == 0)
        {
            return Math.Max(0, Sample(seconds, weight, turns));
        }

        var legs = skeleton.Legs;
        Span<(double X, double Y, double Z)> ankles = stackalloc (double, double, double)[FootPlacement.MaxLegs];
        Span<Rotation> feet = stackalloc Rotation[FootPlacement.MaxLegs];
        skeleton.PoseLegs(turns, bones);
        for (var i = 0; i < legs.Length; i++)
        {
            var ankle = bones[legs[i].Foot];
            ankles[i] = (ankle.X, ankle.Y, ankle.Z);
            feet[i] = ankle.Rotation;
        }

        var share = Sample(seconds, weight, turns);
        var rise = Math.Max(0, -share) * skeleton.LegVoxels;
        for (var i = 0; i < legs.Length; i++)
        {
            ankles[i].Y -= rise;
        }

        FootPlacement.ReachWithFeet(
            skeleton,
            turns,
            ankles[..legs.Length],
            feet[..legs.Length],
            bones,
            ClaimedLegs(skeleton) | leaveLegs
        );
        return Math.Max(0, share);
    }

    /// <summary>
    /// A key: at <paramref name="Seconds"/> into the clip, the turns of <paramref name="Pose"/> added to the driven
    /// bones, and the hips pressed down by <paramref name="Drop"/> of the leg's length; a negative drop raises the body
    /// on the legs the clip leaves alone, when it turns the root (<see cref="SampleOn"/>).
    /// </summary>
    public readonly record struct Key(double Seconds, BodyPose Pose, double Drop = 0);
}
