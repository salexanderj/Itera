using System.Numerics;
using System.Runtime.CompilerServices;
using Itera.Engine.Networking;

namespace Itera.Engine.Replication;

/// <summary>
/// A state on the wire, as its changes from a baseline the receiver holds (a state it acknowledged, or the zero state
/// for a whole one): a mask of the lanes that differ, then each such lane's difference as a zigzag variable-length
/// number, so a lane that moved a little takes a byte and one that did not takes none. The reader checks every lane
/// against its field's range, so a damaged or hostile message is refused (<see cref="InvalidDataException"/>), never
/// read as a value no field holds.
/// </summary>
internal static class ReplicatedStateCodec
{
    /// <summary>The zero state: the baseline of a whole state.</summary>
    public static readonly ReplicatedState Zero;

    /// <summary>The zero state's lanes, as many as a state holds: a whole state's baseline.</summary>
    public static ReadOnlySpan<long> ZeroLanes => s_zeroLanes;

    private static readonly long[] s_zeroLanes = new long[ReplicatedState.MostLanes];

    public static void Write(
        MessageWriter writer,
        ReplicatedSchema schema,
        in ReplicatedState state,
        in ReplicatedState baseline
    )
    {
        ReadOnlySpan<long> lanes = state.Lanes;
        ReadOnlySpan<long> from = baseline.Lanes;
        Measure(schema, lanes, from, out var mask);
        Write(writer, schema, lanes, from, mask);
    }

    /// <summary>
    /// The lanes of <paramref name="state"/> that differ from <paramref name="baseline"/> (its mask, as
    /// <see cref="Write(MessageWriter, ReplicatedSchema, ReadOnlySpan{long}, ReadOnlySpan{long}, long)"/> takes it), and
    /// the bytes that write takes: so a host knows a record's size before it writes it.
    /// </summary>
    public static int Measure(
        ReplicatedSchema schema,
        ReadOnlySpan<long> state,
        ReadOnlySpan<long> baseline,
        out long mask
    )
    {
        mask = 0;
        var bytes = 0;
        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if (state[lane] != baseline[lane])
            {
                mask |= 1L << lane;
                bytes += MessageWriter.VarLongBytes(state[lane] - baseline[lane]);
            }
        }

        return bytes + MessageWriter.VarMaskBytes(mask);
    }

    /// <summary>The changes of <paramref name="state"/> from <paramref name="baseline"/> in the lanes of <paramref name="mask"/> (<see cref="Measure"/>).</summary>
    public static void Write(
        MessageWriter writer,
        ReplicatedSchema schema,
        ReadOnlySpan<long> state,
        ReadOnlySpan<long> baseline,
        long mask
    )
    {
        writer.WriteVarMask(mask);
        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if ((mask & (1L << lane)) != 0)
            {
                writer.WriteVarLong(state[lane] - baseline[lane]);
            }
        }
    }

    public static void Read(
        ref MessageReader reader,
        ReplicatedSchema schema,
        in ReplicatedState baseline,
        out ReplicatedState state
    )
    {
        // The schema's lanes alone, so a narrow kind copies no more than its own.
        Unsafe.SkipInit(out state);
        ((ReadOnlySpan<long>)baseline.Lanes)[..schema.Lanes].CopyTo(state.Lanes);
        var mask = reader.ReadVarMask();
        if (mask >> schema.Lanes != 0)
        {
            throw new InvalidDataException("A state changes a lane its kind does not have.");
        }

        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if ((mask & (1L << lane)) == 0)
            {
                continue;
            }

            var change = reader.ReadVarLong();
            var value = unchecked(baseline.Lanes[lane] + change);
            // A change past a long's reach wraps; either way the range refuses what no field holds.
            if (
                (change > 0 && value < baseline.Lanes[lane])
                || (change < 0 && value > baseline.Lanes[lane])
                || !schema.Holds(lane, value)
            )
            {
                throw new InvalidDataException("A state's lane is out of its field's range.");
            }

            state.Lanes[lane] = value;
        }
    }

    /// <summary>
    /// Reads past a state the receiver cannot use (its baseline is gone, or the entity was removed since), checking only
    /// that its mask fits the schema.
    /// </summary>
    public static void Skip(ref MessageReader reader, ReplicatedSchema schema)
    {
        var mask = reader.ReadVarMask();
        if (mask >> schema.Lanes != 0)
        {
            throw new InvalidDataException("A state changes a lane its kind does not have.");
        }

        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if ((mask & (1L << lane)) != 0)
            {
                reader.ReadVarLong();
            }
        }
    }

    /// <summary>True when the two states hold the same value in every lane of the schema.</summary>
    public static bool Same(ReplicatedSchema schema, in ReplicatedState a, in ReplicatedState b) =>
        Same(schema, a.Lanes, b.Lanes);

    /// <summary>True when the two states' lanes hold the same value in every lane of the schema.</summary>
    public static bool Same(ReplicatedSchema schema, ReadOnlySpan<long> a, ReadOnlySpan<long> b) =>
        a[..schema.Lanes].SequenceEqual(b[..schema.Lanes]);

    /// <summary>
    /// The state <paramref name="share"/> of the way from <paramref name="from"/> to <paramref name="to"/> (0 to 1):
    /// positions and scalars in a straight line, angles the short way, rotations along the sphere, and whole numbers and
    /// flags at <paramref name="from"/>'s value until <paramref name="to"/> is reached. Only the schema's lanes of
    /// <paramref name="result"/> are set, so a narrow kind copies no more than its own.
    /// </summary>
    public static void Blend(
        ReplicatedSchema schema,
        in ReplicatedState from,
        in ReplicatedState to,
        float share,
        out ReplicatedState result
    )
    {
        Unsafe.SkipInit(out result);
        ReadOnlySpan<long> earlier = from.Lanes;
        ReadOnlySpan<long> later = to.Lanes;
        Span<long> lanes = result.Lanes;
        (share >= 1 ? later : earlier)[..schema.Lanes].CopyTo(lanes);
        if (share <= 0 || share >= 1)
        {
            return;
        }

        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            long a = earlier[lane],
                b = later[lane];
            if (a == b)
            {
                continue;
            }

            switch (schema.BlendOf(lane))
            {
                case LaneBlend.Linear:
                    lanes[lane] = a + (long)Math.Round((b - a) * (double)share);
                    break;
                case LaneBlend.Angle:
                    var turn = AngleField.StepsPerTurn;
                    var difference = ((b - a) % turn + turn + turn / 2) % turn - turn / 2;
                    lanes[lane] = ((a + (long)Math.Round(difference * (double)share)) % turn + turn) % turn;
                    break;
                case LaneBlend.Rotation:
                    var blended = Quaternion.Slerp(RotationField.Unpack(a), RotationField.Unpack(b), share);
                    lanes[lane] = RotationField.Pack(blended);
                    break;
                default:
                    break;
            }
        }
    }
}
