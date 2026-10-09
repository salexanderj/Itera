using Itera.Engine.Maths;

namespace Itera.Engine.Animation;

/// <summary>
/// What gaits set limbs with: a bone's turn from where it should lie in the body's frame, and a two-bone reach in a
/// plane. Allocates nothing.
/// </summary>
public static class LimbPose
{
    /// <summary>
    /// Sets <paramref name="bone"/>'s turn so that it lies at <paramref name="inBody"/> in the body's frame, given its
    /// parent as <paramref name="bones"/> holds it posed: a bone's turn in the body is its parent's, its rest, then its
    /// own turn.
    /// </summary>
    public static void SetInBody(
        Skeleton skeleton,
        Span<Rotation> turns,
        ReadOnlySpan<RigidTransform> bones,
        int bone,
        in Rotation inBody
    )
    {
        var parent = bones[skeleton.Parent(bone)].Rotation.Times(skeleton.Rest(bone));
        turns[bone] = parent.Transposed().Times(inBody);
    }

    /// <summary>
    /// The turns, in a plane, of an upper bone of <paramref name="upper"/> and a lower one of <paramref name="lower"/>
    /// from pointing along the plane's first axis towards its second, that put the far end <paramref name="along"/> the
    /// first axis from the joint and <paramref name="toward"/> the second, the middle joint bending further towards the
    /// second (out to the side, or up).
    /// </summary>
    public static (double Upper, double Lower) Reach(double upper, double lower, double along, double toward)
    {
        // Held a hair short of the limb's full stretch and its full fold, where the joint's acos turns steep: a target out
        // of reach gives the straightest limb that still bends the same way.
        var distance = Math.Clamp(
            Math.Sqrt(along * along + toward * toward),
            Math.Abs(upper - lower) + 1e-3,
            upper + lower - 1e-3
        );
        var line = Math.Atan2(toward, along);
        var atJoint = Math.Acos(
            Math.Clamp((upper * upper + distance * distance - lower * lower) / (2 * upper * distance), -1, 1)
        );
        var atMiddle = Math.Acos(
            Math.Clamp((lower * lower + distance * distance - upper * upper) / (2 * lower * distance), -1, 1)
        );
        return ((line + atJoint) / (2 * Math.PI), (line - atMiddle) / (2 * Math.PI));
    }
}
