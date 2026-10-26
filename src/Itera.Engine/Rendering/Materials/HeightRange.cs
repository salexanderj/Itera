using System.Buffers.Binary;

namespace Itera.Engine.Rendering.Materials;

/// <summary>
/// The lowest and highest height a texture set's height layer can produce, from 0 to 1. The shader can skip reading
/// the heights where the materials' weights alone decide which shows.
/// </summary>
public readonly record struct HeightRange(float Low, float High)
{
    // Palette values are kept as integers over this denominator: 7 for the mode with eight interpolated values, 5 for
    // the mode with six, times 5 or 7, so both modes compare exactly.
    private const int Denominator = 35;

    private const int BlockBytes = 8;

    /// <summary>The whole range: nothing can be skipped. A set with no height layer has it.</summary>
    public static HeightRange Full { get; } = new(0f, 1f);

    /// <summary>
    /// The exact lowest and highest value the BC4 <paramref name="blocks"/> decode to, over the palette entries each
    /// block's indices use, as 0 to 1 (value over 255). Pass the largest mip level: the smaller levels are filtered from
    /// it, so they lie within its range. Trailing bytes short of a whole block are ignored; no blocks give
    /// <see cref="Full"/>.
    /// </summary>
    public static HeightRange FromBc4(ReadOnlySpan<byte> blocks)
    {
        var count = blocks.Length / BlockBytes;
        if (count == 0)
        {
            return Full;
        }

        Span<int> palette = stackalloc int[8];
        var low = int.MaxValue;
        var high = int.MinValue;
        for (var b = 0; b < count; b++)
        {
            var block = blocks.Slice(b * BlockBytes, BlockBytes);
            int red0 = block[0];
            int red1 = block[1];
            palette[0] = red0 * Denominator;
            palette[1] = red1 * Denominator;
            if (red0 > red1)
            {
                for (var i = 2; i < 8; i++)
                {
                    palette[i] = ((8 - i) * red0 + (i - 1) * red1) * 5;
                }
            }
            else
            {
                for (var i = 2; i < 6; i++)
                {
                    palette[i] = ((6 - i) * red0 + (i - 1) * red1) * 7;
                }

                palette[6] = 0;
                palette[7] = 255 * Denominator;
            }

            // Sixteen 3-bit indices in the 48 bits after the endpoints, texel 0 in the lowest bits.
            var indices = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(block[2..]);
            indices |= (ulong)BinaryPrimitives.ReadUInt16LittleEndian(block[6..]) << 32;
            for (var t = 0; t < 16; t++)
            {
                var value = palette[(int)(indices & 7)];
                indices >>= 3;
                if (value < low)
                {
                    low = value;
                }

                if (value > high)
                {
                    high = value;
                }
            }
        }

        const float Scale = Denominator * 255f;
        return new HeightRange(low / Scale, high / Scale);
    }
}
