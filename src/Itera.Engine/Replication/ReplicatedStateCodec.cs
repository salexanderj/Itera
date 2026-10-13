using System.Numerics;
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

    public static void Write(
        MessageWriter writer,
        ReplicatedSchema schema,
        in ReplicatedState state,
        in ReplicatedState baseline
    )
    {
        var mask = 0;
        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if (state.Lanes[lane] != baseline.Lanes[lane])
            {
                mask |= 1 << lane;
            }
        }

        writer.WriteVarInt(mask);
        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if ((mask & (1 << lane)) != 0)
            {
                writer.WriteVarLong(state.Lanes[lane] - baseline.Lanes[lane]);
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
        state = baseline;
        var mask = reader.ReadVarInt();
        if (mask >> schema.Lanes != 0)
        {
            throw new InvalidDataException("A state changes a lane its kind does not have.");
        }

        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if ((mask & (1 << lane)) == 0)
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
        var mask = reader.ReadVarInt();
        if (mask >> schema.Lanes != 0)
        {
            throw new InvalidDataException("A state changes a lane its kind does not have.");
        }

        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if ((mask & (1 << lane)) != 0)
            {
                reader.ReadVarLong();
            }
        }
    }

    /// <summary>True when the two states hold the same value in every lane of the schema.</summary>
    public static bool Same(ReplicatedSchema schema, in ReplicatedState a, in ReplicatedState b)
    {
        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            if (a.Lanes[lane] != b.Lanes[lane])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The state <paramref name="share"/> of the way from <paramref name="from"/> to <paramref name="to"/> (0 to 1):
    /// positions and scalars in a straight line, angles the short way, rotations along the sphere, and whole numbers and
    /// flags at <paramref name="from"/>'s value until <paramref name="to"/> is reached.
    /// </summary>
    public static void Blend(
        ReplicatedSchema schema,
        in ReplicatedState from,
        in ReplicatedState to,
        float share,
        out ReplicatedState result
    )
    {
        result = from;
        if (share <= 0)
        {
            return;
        }

        if (share >= 1)
        {
            result = to;
            return;
        }

        for (var lane = 0; lane < schema.Lanes; lane++)
        {
            long a = from.Lanes[lane],
                b = to.Lanes[lane];
            if (a == b)
            {
                continue;
            }

            switch (schema.BlendOf(lane))
            {
                case LaneBlend.Linear:
                    result.Lanes[lane] = a + (long)Math.Round((b - a) * (double)share);
                    break;
                case LaneBlend.Angle:
                    var turn = AngleField.StepsPerTurn;
                    var difference = ((b - a) % turn + turn + turn / 2) % turn - turn / 2;
                    result.Lanes[lane] = ((a + (long)Math.Round(difference * (double)share)) % turn + turn) % turn;
                    break;
                case LaneBlend.Rotation:
                    var blended = Quaternion.Slerp(RotationField.Unpack(a), RotationField.Unpack(b), share);
                    result.Lanes[lane] = RotationField.Pack(blended);
                    break;
                default:
                    break;
            }
        }
    }
}
