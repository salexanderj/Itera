using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.TestSupport;

/// <summary>
/// How a stepping clip's feet meet the ground as the engine poses it, by the rule of the model toolkit's clip checks
/// (<c>tools/models/itera_models/motion.py</c>): a foot is on the ground while its joint is within
/// <see cref="Contact"/> of the rig's height of its lowest point in the clip. A game plays such a clip at its body's
/// speed over the clip's own, so a game's test compares the speed it plays a clip by with <see cref="Speed"/>: a
/// change on either side fails it.
/// </summary>
public static class ClipGround
{
    /// <summary>The share of the rig's height within which a foot is on the ground, as the toolkit's CONTACT.</summary>
    public const float Contact = 0.02f;

    /// <summary>
    /// The speed over the ground clip <paramref name="clip"/> of <paramref name="rig"/> (a model's
    /// <see cref="MeshAsset.Rig"/>) shows at its own pace: how fast, on average, the joints <paramref name="feet"/> move
    /// back (along -z, the model's front being +z) between two of <paramref name="samples"/> poses over its length at
    /// which they are on the ground, in the asset's units a second. 0 when no foot is on the ground at two poses in a
    /// row.
    /// </summary>
    public static float Speed(Rig rig, string clip, IReadOnlyList<string> feet, int samples = 240)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(feet);
        var index = rig.FindClip(clip);
        if (index < 0)
        {
            throw new ArgumentException($"The rig has no clip {clip}.", nameof(clip));
        }

        var joints = new int[feet.Count];
        for (var f = 0; f < feet.Count; f++)
        {
            joints[f] = rig.FindJoint(feet[f]);
            if (joints[f] < 0)
            {
                throw new ArgumentException($"The rig has no joint {feet[f]}.", nameof(feet));
            }
        }

        var height = rig.BindPositions.Max(place => place.Y) - rig.BindPositions.Min(place => place.Y);
        var step = rig.Clips[index].Duration / samples;
        var locals = new JointTransform[rig.JointCount];
        var globals = new Matrix4x4[rig.JointCount];
        var places = new Vector3[feet.Count, samples];
        for (var i = 0; i < samples; i++)
        {
            rig.Pose(index, step * i, locals);
            rig.Globals(locals, globals);
            for (var f = 0; f < feet.Count; f++)
            {
                places[f, i] = globals[joints[f]].Translation;
            }
        }

        var back = 0f;
        var steps = 0;
        for (var f = 0; f < feet.Count; f++)
        {
            var lowest = float.MaxValue;
            for (var i = 0; i < samples; i++)
            {
                lowest = Math.Min(lowest, places[f, i].Y);
            }

            var ground = lowest + Contact * height;
            for (var i = 1; i < samples; i++)
            {
                if (places[f, i - 1].Y <= ground && places[f, i].Y <= ground)
                {
                    back += places[f, i - 1].Z - places[f, i].Z;
                    steps++;
                }
            }
        }

        return steps == 0 ? 0 : back / (steps * step);
    }
}
