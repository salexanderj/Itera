using System.Numerics;

namespace Itera.Engine.Assets;

/// <summary>
/// An animation of a <see cref="Rig"/> (a glTF animation's channels on its joints): tracks that each set one joint's
/// translation, rotation, or scale over time. Sampled for drawing only, in floats: not the same on every machine, so
/// never for anything peers must agree on (hits, physics). Sampling allocates nothing.
/// </summary>
public sealed class RigClip
{
    public RigClip(string name, RigTrack[] tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Name = name;
        Tracks = tracks;
        foreach (var track in tracks)
        {
            Duration = Math.Max(Duration, track.Times.Length == 0 ? 0 : track.Times[^1]);
        }
    }

    /// <summary>The clip's name in its file (<see cref="Rig.FindClip"/>).</summary>
    public string Name { get; }

    public RigTrack[] Tracks { get; }

    /// <summary>The time of the last key of any track, in seconds: the clip's length when it loops.</summary>
    public float Duration { get; }

    /// <summary>
    /// Sets the joints its tracks name in <paramref name="locals"/> to their values at <paramref name="seconds"/> (held
    /// at a track's first value before its first key and at its last after its last key); leaves the others as they are.
    /// </summary>
    public void Sample(float seconds, Span<JointTransform> locals)
    {
        foreach (var track in Tracks)
        {
            ref var local = ref locals[track.Joint];
            switch (track.Path)
            {
                case RigPath.Translation:
                    local = local with { Translation = track.SampleVector(seconds) };
                    break;
                case RigPath.Rotation:
                    local = local with { Rotation = track.SampleRotation(seconds) };
                    break;
                default:
                    local = local with { Scale = track.SampleVector(seconds) };
                    break;
            }
        }
    }
}

/// <summary>What a <see cref="RigTrack"/> sets.</summary>
public enum RigPath
{
    Translation = 0,
    Rotation = 1,
    Scale = 2,
}

/// <summary>How a <see cref="RigTrack"/> moves between its keys (glTF's interpolation).</summary>
public enum RigInterpolation
{
    /// <summary>Each key's value until the next key.</summary>
    Step = 0,

    /// <summary>Straight between keys; rotations along the shorter arc (spherical).</summary>
    Linear = 1,

    /// <summary>A cubic Hermite spline through the keys with the file's tangents.</summary>
    CubicSpline = 2,
}

/// <summary>
/// One joint's translation, rotation, or scale over time: key times in seconds, increasing, and their values (a
/// translation or scale in x, y, and z, a rotation as a quaternion's x, y, z, and w). For
/// <see cref="RigInterpolation.CubicSpline"/> each key has three values: its in-tangent, its value, and its out-tangent.
/// </summary>
public sealed class RigTrack
{
    public RigTrack(int joint, RigPath path, RigInterpolation interpolation, float[] times, Vector4[] values)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(values);
        var perKey = interpolation == RigInterpolation.CubicSpline ? 3 : 1;
        if (times.Length == 0 || values.Length != times.Length * perKey)
        {
            throw new ArgumentException(
                $"A track needs at least one key and {perKey} value(s) a key: {times.Length} key(s), {values.Length} value(s).",
                nameof(values)
            );
        }

        Joint = joint;
        Path = path;
        Interpolation = interpolation;
        Times = times;
        Values = values;
    }

    /// <summary>The joint it sets, the rig's index.</summary>
    public int Joint { get; }

    public RigPath Path { get; }

    public RigInterpolation Interpolation { get; }

    public float[] Times { get; }

    public Vector4[] Values { get; }

    /// <summary>The translation or scale at <paramref name="seconds"/>.</summary>
    public Vector3 SampleVector(float seconds)
    {
        var value = Sample(seconds, rotation: false);
        return new Vector3(value.X, value.Y, value.Z);
    }

    /// <summary>The rotation at <paramref name="seconds"/>, of unit length.</summary>
    public Quaternion SampleRotation(float seconds)
    {
        var value = Sample(seconds, rotation: true);
        var rotation = new Quaternion(value.X, value.Y, value.Z, value.W);
        var length = rotation.Length();
        return length > 1e-12f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }

    private Vector4 Sample(float seconds, bool rotation)
    {
        var cubic = Interpolation == RigInterpolation.CubicSpline;
        var last = Times.Length - 1;
        if (!(seconds > Times[0]))
        {
            return ValueAt(0, cubic);
        }

        if (seconds >= Times[last])
        {
            return ValueAt(last, cubic);
        }

        // The key at or before the time: Times[key] <= seconds < Times[key + 1].
        var key = Array.BinarySearch(Times, seconds);
        key = key >= 0 ? key : ~key - 1;
        var start = Times[key];
        var span = Times[key + 1] - start;
        var t = span > 0 ? (seconds - start) / span : 0;
        switch (Interpolation)
        {
            case RigInterpolation.Step:
                return Values[key];
            case RigInterpolation.Linear:
                if (rotation)
                {
                    var from = Values[key];
                    var to = Values[key + 1];
                    var turned = Quaternion.Slerp(
                        new Quaternion(from.X, from.Y, from.Z, from.W),
                        new Quaternion(to.X, to.Y, to.Z, to.W),
                        t
                    );
                    return new Vector4(turned.X, turned.Y, turned.Z, turned.W);
                }

                return Vector4.Lerp(Values[key], Values[key + 1], t);
            default:
                // glTF: p(t) = (2t³ - 3t² + 1) v0 + d (t³ - 2t² + t) b0 + (-2t³ + 3t²) v1 + d (t³ - t²) a1, where b0 is the
                // first key's out-tangent, a1 the second's in-tangent, and d the time between them.
                var t2 = t * t;
                var t3 = t2 * t;
                var v0 = Values[key * 3 + 1];
                var b0 = Values[key * 3 + 2];
                var a1 = Values[(key + 1) * 3];
                var v1 = Values[(key + 1) * 3 + 1];
                return (2 * t3 - 3 * t2 + 1) * v0
                    + span * (t3 - 2 * t2 + t) * b0
                    + (-2 * t3 + 3 * t2) * v1
                    + span * (t3 - t2) * a1;
        }
    }

    private Vector4 ValueAt(int key, bool cubic) => Values[cubic ? key * 3 + 1 : key];
}
