using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.TestSupport;

/// <summary>
/// Values a modified peer may put in a message's fields: floats far past the map (3e7 m),
/// past any sum (3e38, <see cref="float.MaxValue"/>), and not numbers; integers just past a float's exact range
/// (2^24 + 1) and at an int's ends; ticks at the start, at the latest a state may carry, and past it. The handlers'
/// fuzz once refused none of the values that then broke the systems using them.
/// </summary>
internal static class HostileValues
{
    public static readonly float[] Floats =
    [
        0,
        float.Epsilon,
        3e7f,
        -3e7f,
        3e38f,
        -3e38f,
        float.MaxValue,
        float.MinValue,
        float.NaN,
        float.PositiveInfinity,
        float.NegativeInfinity,
    ];

    public static readonly int[] Ints = [0, -1, (1 << 24) + 1, -((1 << 24) + 1), int.MaxValue, int.MinValue];

    public static readonly uint[] Ticks = [0, 15, int.MaxValue, uint.MaxValue];

    /// <summary>Every int, tick, and float above as the 4 bytes a message carries, each once.</summary>
    public static uint[] Words { get; } =
    [
        .. Ints.Select(value => (uint)value)
            .Concat(Ticks)
            .Concat(Floats.Select(BitConverter.SingleToUInt32Bits))
            .Distinct(),
    ];

    /// <summary><paramref name="basis"/> with each axis in turn, then all three, set to each of <see cref="Floats"/>.</summary>
    public static IEnumerable<(string Label, Vector3 Value)> Vectors(Vector3 basis)
    {
        foreach (var value in Floats)
        {
            var text = Text(value);
            yield return ($"x {text}", basis with { X = value });
            yield return ($"y {text}", basis with { Y = value });
            yield return ($"z {text}", basis with { Z = value });
            yield return ($"all {text}", new Vector3(value));
        }
    }

    /// <summary><paramref name="basis"/> with each axis in turn, then all three, set to each of <see cref="Ints"/>.</summary>
    public static IEnumerable<(string Label, VoxelCoord Value)> Coords(VoxelCoord basis)
    {
        foreach (var value in Ints)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            yield return ($"x {text}", basis with { X = value });
            yield return ($"y {text}", basis with { Y = value });
            yield return ($"z {text}", basis with { Z = value });
            yield return ($"all {text}", new VoxelCoord(value, value, value));
        }
    }

    /// <summary>
    /// A message body <paramref name="sample"/> as made and damaged as a modified peer may send it: each 4-byte window
    /// from <paramref name="from"/> on set to each of <see cref="Words"/>, each byte from there to 0, 128, and 255, and the
    /// whole cut short to each length. <paramref name="name"/> starts each label.
    /// </summary>
    public static IEnumerable<(string Label, byte[] Body)> Damaged(string name, byte[] sample, int from = 0)
    {
        yield return ($"{name} as made", sample);
        for (var offset = from; offset + 4 <= sample.Length; offset++)
        {
            foreach (var word in Words)
            {
                var body = (byte[])sample.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), word);
                yield return ($"{name} with {word:X8} at {offset}", body);
            }
        }

        for (var offset = from; offset < sample.Length; offset++)
        {
            foreach (var value in (byte[])[0, 128, 255])
            {
                var body = (byte[])sample.Clone();
                body[offset] = value;
                yield return ($"{name} with byte {value} at {offset}", body);
            }
        }

        for (var length = 0; length < sample.Length; length++)
        {
            yield return ($"{name} cut to {length} bytes", sample[..length]);
        }
    }

    public static string Text(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
