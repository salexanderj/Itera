using System.Numerics;

namespace Itera.Engine.Assets;

/// <summary>
/// The speed over the ground a stepping clip shows at its own pace: how fast its planted feet move back under the body.
/// A game plays such a clip at its body's speed over this one, so the feet stay where they are planted at any speed. A
/// foot is planted between two poses when its joint is within <see cref="Contact"/> of the rig's height of its lowest
/// point in the clip at both, and moves back (along the clip's back direction) from one to the other; a foot low but
/// moving forward is starting its swing. The model toolkit's clip checks go by the same rule
/// (<c>tools/models/itera_models/motion.py</c>).
/// </summary>
public static class ClipGround
{
    /// <summary>The share of the rig's height within which a foot is on the ground.</summary>
    public const float Contact = 0.02f;

    /// <summary>The poses <see cref="Speed"/> samples over a clip's length.</summary>
    public const int Samples = 120;

    /// <summary>The way a planted foot moves in a clip that goes forward: back, along -z (a model's front is +z).</summary>
    public static Vector3 Back => -Vector3.UnitZ;

    /// <summary>The rig's height in the bound pose: from its lowest joint to its highest, in the asset's units.</summary>
    public static float Height(Rig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var (lowest, highest) = (float.MaxValue, float.MinValue);
        foreach (var place in rig.BindPositions)
        {
            (lowest, highest) = (Math.Min(lowest, place.Y), Math.Max(highest, place.Y));
        }

        return highest > lowest ? highest - lowest : 0;
    }

    /// <summary>
    /// The speed over the ground clip <paramref name="clip"/> of <paramref name="rig"/> shows, in the asset's units a
    /// second, from <see cref="Samples"/> poses over its length: the joints <paramref name="feet"/> planted, moving along
    /// <paramref name="back"/> (the way opposite the clip's travel; <see cref="Back"/> for a clip that goes forward). 0
    /// for a clip with no planted step. Allocates its samples.
    /// </summary>
    public static float Speed(Rig rig, int clip, ReadOnlySpan<int> feet, Vector3 back)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if ((uint)clip >= (uint)rig.Clips.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(clip), clip, "The rig has no such clip.");
        }

        foreach (var foot in feet)
        {
            if ((uint)foot >= (uint)rig.JointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(feet), foot, "The rig has no such joint.");
            }
        }

        var step = rig.Clips[clip].Duration / Samples;
        var locals = new JointTransform[rig.JointCount];
        var globals = new Matrix4x4[rig.JointCount];
        var places = new Vector3[feet.Length * Samples];
        for (var i = 0; i < Samples; i++)
        {
            rig.Pose(clip, step * i, locals);
            rig.Globals(locals, globals);
            for (var f = 0; f < feet.Length; f++)
            {
                places[f * Samples + i] = globals[feet[f]].Translation;
            }
        }

        return FromPlaces(places, Samples, Height(rig), back, step);
    }

    /// <summary>
    /// The speed over the ground of a looping clip whose feet were at <paramref name="places"/>: each foot's
    /// <paramref name="samples"/> places, one foot after another, <paramref name="secondsPerSample"/> apart, the last
    /// followed by the first. <paramref name="height"/> is the rig's (<see cref="Height"/>) and <paramref name="back"/>
    /// the way a planted foot moves, along the ground. 0 when no foot is planted between two samples.
    /// </summary>
    public static float FromPlaces(
        ReadOnlySpan<Vector3> places,
        int samples,
        float height,
        Vector3 back,
        float secondsPerSample
    )
    {
        var along = new Vector3(back.X, 0, back.Z);
        if (samples < 2 || places.Length % samples != 0 || !(along.LengthSquared() > 0) || !(secondsPerSample > 0))
        {
            return 0;
        }

        along = Vector3.Normalize(along);
        var moved = 0f;
        var steps = 0;
        for (var foot = 0; foot < places.Length / samples; foot++)
        {
            var path = places.Slice(foot * samples, samples);
            var lowest = float.MaxValue;
            foreach (var place in path)
            {
                lowest = Math.Min(lowest, place.Y);
            }

            var ground = lowest + Contact * height;
            for (var i = 0; i < samples; i++)
            {
                var (from, to) = (path[(i + samples - 1) % samples], path[i]);
                var move = Vector3.Dot(to - from, along);
                if (from.Y <= ground && to.Y <= ground && move > 0)
                {
                    moved += move;
                    steps++;
                }
            }
        }

        return steps == 0 ? 0 : moved / (steps * secondsPerSample);
    }
}
