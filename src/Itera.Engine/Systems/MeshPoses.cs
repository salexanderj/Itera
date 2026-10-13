using System.Numerics;
using Arch.Core;
using Itera.Engine.Assets;
using Itera.Engine.Components;

namespace Itera.Engine.Systems;

/// <summary>
/// A game's shaping of the poses it draws (<see cref="MeshDrawSystem"/>, for each entity with a <see cref="ShapedPose"/>):
/// after the entity's clip is sampled (and faded from its last), before its skinning matrices are made, it changes the
/// joints as it wants, through the rig's tools (<see cref="Rig.Blend"/> and <see cref="Rig.Mask"/> for an upper body
/// playing its own clip, <see cref="Rig.Turn"/> for a spine bent to an aim, <see cref="Rig.ReachTwoBone"/> for a hand on a
/// rifle), reading the entity's own components. The same code serves a host posing a body to judge a hit
/// (<see cref="MeshPoses"/>). A scoped service the game registers; called inside the draw system's query, so it makes no
/// structural change, and it allocates nothing.
/// </summary>
public interface IMeshPoseShaper
{
    /// <summary>
    /// Shapes <paramref name="entity"/>'s pose: <paramref name="locals"/> (one a joint of <paramref name="rig"/>), at the
    /// frame's <paramref name="alpha"/> of the way from the last step to this one; <paramref name="globals"/> is scratch,
    /// one a joint.
    /// </summary>
    void Shape(World world, Entity entity, Rig rig, Span<JointTransform> locals, Span<Matrix4x4> globals, float alpha);
}

/// <summary>
/// Posing a placed, animated mesh as <see cref="MeshDrawSystem"/> draws it: its place between its last two steps, its
/// clip at the blend of its last two times faded from its last clip, and a joint's place on it. For the draw system, and
/// for a game posing a body it does not draw (a host judging hits). Allocates nothing.
/// </summary>
public static class MeshPoses
{
    /// <summary>A placement's transform between its last two steps: scale, then the blended turn, then the blended place.</summary>
    public static Matrix4x4 Placed(in MeshPlacement placement, float alpha)
    {
        var position = Vector3.Lerp(placement.PreviousPosition, placement.Position, alpha);
        var orientation = Quaternion.Slerp(placement.PreviousOrientation, placement.Orientation, alpha);
        return Matrix4x4.CreateScale(placement.Scale)
            * Matrix4x4.CreateFromQuaternion(orientation)
            * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// <paramref name="animator"/>'s pose of asset <paramref name="asset"/> at <paramref name="alpha"/> of the way between
    /// its last two steps, into <paramref name="locals"/>: its clip at the blend of its two times (wrapped by a looping
    /// clip's length), and while it fades, the last clip's pose (sampled into <paramref name="from"/>) blended into it by
    /// the fade; then its blended clip, posed at the same share of its length (sampled into <paramref name="from"/>),
    /// blended into that by its weight. False, and nothing written, for an asset without a rig, or a rig with more joints
    /// than the spans hold.
    /// </summary>
    public static bool Sample(
        MeshAssets assets,
        int asset,
        in MeshAnimator animator,
        float alpha,
        Span<JointTransform> locals,
        Span<JointTransform> from,
        out Rig rig
    )
    {
        ArgumentNullException.ThrowIfNull(assets);
        rig = null!;
        if (
            !assets.TryGet(asset, out var model)
            || model.Rig is not { } found
            || found.JointCount > locals.Length
            || found.JointCount > from.Length
        )
        {
            return false;
        }

        rig = found;
        var joints = locals[..rig.JointCount];
        var seconds = Between(
            assets,
            asset,
            animator.PreviousSeconds,
            animator.Seconds,
            animator.Clip,
            animator.Once,
            alpha
        );
        rig.Pose(animator.Clip, seconds, joints);
        var weight = animator.FadeWeight(alpha);
        if (weight < 1)
        {
            var last = from[..rig.JointCount];
            var lastSeconds = Between(
                assets,
                asset,
                animator.FromPreviousSeconds,
                animator.FromSeconds,
                animator.FromClip,
                animator.FromOnce,
                alpha
            );
            rig.Pose(animator.FromClip, lastSeconds, last);
            for (var j = 0; j < joints.Length; j++)
            {
                joints[j] = new JointTransform(
                    Vector3.Lerp(last[j].Translation, joints[j].Translation, weight),
                    Quaternion.Slerp(last[j].Rotation, joints[j].Rotation, weight),
                    Vector3.Lerp(last[j].Scale, joints[j].Scale, weight)
                );
            }
        }

        var blend = animator.BlendShare(alpha);
        if (blend > 0)
        {
            // The blended clip at the same share of its length as the clip is of its own, so two walks keep step.
            var duration = MeshAnimatorSystem.DurationOf(assets, asset, animator.Clip);
            var blendDuration = MeshAnimatorSystem.DurationOf(assets, asset, animator.BlendClip);
            var blendSeconds = duration > 0 && blendDuration > 0 ? seconds / duration * blendDuration : seconds;
            var other = from[..rig.JointCount];
            rig.Pose(animator.BlendClip, blendSeconds, other);
            for (var j = 0; j < joints.Length; j++)
            {
                joints[j] = new JointTransform(
                    Vector3.Lerp(joints[j].Translation, other[j].Translation, blend),
                    Quaternion.Slerp(joints[j].Rotation, other[j].Rotation, blend),
                    Vector3.Lerp(joints[j].Scale, other[j].Scale, blend)
                );
            }
        }

        return true;
    }

    /// <summary>
    /// Where joint <paramref name="joint"/> of a pose is in the world: its transform in the asset's space
    /// (<see cref="Rig.Globals"/>, written into <paramref name="globals"/>) times the mesh's <paramref name="placed"/>.
    /// </summary>
    public static Matrix4x4 JointInWorld(
        Rig rig,
        ReadOnlySpan<JointTransform> locals,
        int joint,
        in Matrix4x4 placed,
        Span<Matrix4x4> globals
    )
    {
        ArgumentNullException.ThrowIfNull(rig);
        rig.Globals(locals[..rig.JointCount], globals);
        return (uint)joint < (uint)rig.JointCount ? globals[joint] * placed : placed;
    }

    /// <summary>A clip's time between its last two steps, wrapped by a looping clip's length.</summary>
    private static float Between(
        MeshAssets assets,
        int asset,
        float previous,
        float seconds,
        int clip,
        bool once,
        float alpha
    )
    {
        var between = previous + (seconds - previous) * alpha;
        var duration = MeshAnimatorSystem.DurationOf(assets, asset, clip);
        if (duration > 0 && !once)
        {
            between -= MathF.Floor(between / duration) * duration;
        }

        return between;
    }
}
