using System.Numerics;
using System.Runtime.CompilerServices;
using Itera.Engine.Maths;
using Itera.Engine.Networking;

namespace Itera.Engine.Replication;

/// <summary>
/// One entity's replicated state: up to <see cref="MostLanes"/> whole numbers (lanes), each a field's quantised value
/// or one axis of a position. A kind's <see cref="ReplicatedSchema"/> says what each lane holds; its fields
/// (<see cref="PositionField"/>, <see cref="AngleField"/>, ...) set and read them. A value type of 248 bytes, passed by
/// reference.
/// </summary>
public struct ReplicatedState
{
    /// <summary>The most lanes a state holds: a changed-lane mask fits a positive int.</summary>
    public const int MostLanes = 31;

    internal LaneArray Lanes;

    /// <summary>The lanes' values, for a test or a game's own check.</summary>
    public readonly long Lane(int lane) => Lanes[lane];
}

[InlineArray(ReplicatedState.MostLanes)]
internal struct LaneArray
{
    private long _lane;
}

/// <summary>How a lane's value goes from one state to the next on a client, between the two it shows.</summary>
internal enum LaneBlend : byte
{
    /// <summary>In a straight line (a position's axis, a scalar).</summary>
    Linear,

    /// <summary>A turn of 65,536 steps, the short way round.</summary>
    Angle,

    /// <summary>A rotation's smallest three, blended along the sphere.</summary>
    Rotation,

    /// <summary>Not blended: the earlier state's value until the later one's (whole numbers, flags).</summary>
    Step,
}

/// <summary>
/// An absolute position in three lanes of 1/1,024 m (<see cref="StepsPerMetre"/>), whatever either peer's origin: a
/// game sets it from its local position through the match's origin (<see cref="ReplicationContext.Origin"/>).
/// </summary>
public readonly record struct PositionField(int Lane)
{
    /// <summary>Steps a metre: about a millimetre.</summary>
    public const int StepsPerMetre = 1024;

    /// <summary>The farthest a position may lie from the map's middle on any axis, in steps.</summary>
    internal const long MostSteps = (long)(SessionProtocol.MaxPositionMetres * StepsPerMetre);

    /// <summary>Sets the position; an axis that is not finite reads 0, and one past the reach of a message is held to it.</summary>
    public void Set(ref ReplicatedState state, in WorldPoint point)
    {
        state.Lanes[Lane] = Steps(point.X);
        state.Lanes[Lane + 1] = Steps(point.Y);
        state.Lanes[Lane + 2] = Steps(point.Z);
    }

    public WorldPoint Get(in ReplicatedState state) =>
        new(
            (double)state.Lanes[Lane] / StepsPerMetre,
            (double)state.Lanes[Lane + 1] / StepsPerMetre,
            (double)state.Lanes[Lane + 2] / StepsPerMetre
        );

    private static long Steps(double metres) =>
        double.IsFinite(metres) ? Math.Clamp((long)Math.Round(metres * StepsPerMetre), -MostSteps, MostSteps) : 0;
}

/// <summary>A turn about one axis in a lane of 65,536 steps a turn (about 0.0055 degrees), blended the short way.</summary>
public readonly record struct AngleField(int Lane)
{
    /// <summary>Steps a turn.</summary>
    public const int StepsPerTurn = 65536;

    /// <summary>Sets the angle in radians; one that is not finite reads 0.</summary>
    public void Set(ref ReplicatedState state, float radians)
    {
        var steps = float.IsFinite(radians) ? (long)Math.Round(radians / (2 * Math.PI) * StepsPerTurn) : 0;
        state.Lanes[Lane] = ((steps % StepsPerTurn) + StepsPerTurn) % StepsPerTurn;
    }

    /// <summary>The angle in radians, from -pi to pi.</summary>
    public float Get(in ReplicatedState state)
    {
        var steps = state.Lanes[Lane];
        var signed = steps >= StepsPerTurn / 2 ? steps - StepsPerTurn : steps;
        return (float)(signed * (2 * Math.PI) / StepsPerTurn);
    }
}

/// <summary>
/// A rotation in one lane: its three smallest components at 12 bits each and which component is the largest (within
/// about 0.1 degrees), blended along the sphere.
/// </summary>
public readonly record struct RotationField(int Lane)
{
    private const int Bits = 12;
    private const int Most = (1 << Bits) - 1;
    private const float Reach = 0.70710678f;

    /// <summary>Sets the rotation; one that is not finite or has no length reads as none.</summary>
    public void Set(ref ReplicatedState state, Quaternion rotation) => state.Lanes[Lane] = Pack(rotation);

    public Quaternion Get(in ReplicatedState state) => Unpack(state.Lanes[Lane]);

    internal static long Pack(Quaternion rotation)
    {
        var length = rotation.Length();
        if (!float.IsFinite(length) || length < 1e-6f)
        {
            rotation = Quaternion.Identity;
        }
        else
        {
            rotation = Quaternion.Multiply(rotation, 1 / length);
        }

        Span<float> c = stackalloc float[4];
        (c[0], c[1], c[2], c[3]) = (rotation.X, rotation.Y, rotation.Z, rotation.W);
        var largest = 0;
        for (var i = 1; i < 4; i++)
        {
            if (MathF.Abs(c[i]) > MathF.Abs(c[largest]))
            {
                largest = i;
            }
        }

        // q and -q are one rotation: the largest is made positive and left out.
        var sign = c[largest] < 0 ? -1f : 1f;
        long packed = largest;
        for (var i = 0; i < 4; i++)
        {
            if (i != largest)
            {
                var share = Math.Clamp(c[i] * sign / Reach * 0.5f + 0.5f, 0, 1);
                packed = (packed << Bits) | (long)MathF.Round(share * Most);
            }
        }

        return packed;
    }

    internal static Quaternion Unpack(long packed)
    {
        Span<float> c = stackalloc float[4];
        var largest = (int)(packed >> (3 * Bits)) & 3;
        var sum = 0f;
        for (var i = 3; i >= 0; i--)
        {
            if (i == largest)
            {
                continue;
            }

            var value = ((packed & Most) / (float)Most - 0.5f) * 2 * Reach;
            packed >>= Bits;
            c[i] = value;
            sum += value * value;
        }

        c[largest] = MathF.Sqrt(MathF.Max(0, 1 - sum));
        return Quaternion.Normalize(new Quaternion(c[0], c[1], c[2], c[3]));
    }

    /// <summary>The largest value a lane may hold: two bits for the largest component and three of 12.</summary>
    internal const long MostPacked = (1L << (2 + 3 * Bits)) - 1;
}

/// <summary>A number from <see cref="Min"/> to <see cref="Max"/> in <see cref="Steps"/> + 1 even steps, blended in a straight line.</summary>
public readonly record struct ScalarField(int Lane, float Min, float Max, int Steps)
{
    /// <summary>Sets the value, held to the range; one that is not finite reads <see cref="Min"/>.</summary>
    public void Set(ref ReplicatedState state, float value)
    {
        var share = float.IsFinite(value) ? Math.Clamp((value - Min) / (Max - Min), 0, 1) : 0;
        state.Lanes[Lane] = (long)Math.Round(share * Steps);
    }

    public float Get(in ReplicatedState state) => Min + (Max - Min) * state.Lanes[Lane] / Steps;
}

/// <summary>A whole number from <see cref="Min"/> to <see cref="Max"/>, not blended: a clip, a state, a count.</summary>
public readonly record struct IntegerField(int Lane, int Min, int Max)
{
    /// <summary>Sets the value, held to the range.</summary>
    public void Set(ref ReplicatedState state, int value) =>
        state.Lanes[Lane] = (long)Math.Clamp(value, Min, Max) - Min;

    public int Get(in ReplicatedState state) => (int)(state.Lanes[Lane] + Min);
}

/// <summary>Up to 32 bits of flags, not blended.</summary>
public readonly record struct FlagsField(int Lane, int Bits)
{
    /// <summary>Sets the flags; bits past <see cref="Bits"/> are dropped.</summary>
    public void Set(ref ReplicatedState state, uint flags) =>
        state.Lanes[Lane] = Bits == 32 ? flags : flags & ((1u << Bits) - 1);

    public uint Get(in ReplicatedState state) => (uint)state.Lanes[Lane];
}
