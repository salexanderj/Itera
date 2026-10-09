using System.Buffers.Binary;

namespace Itera.Engine.Rendering.Materials;

/// <summary>The block-compressed formats a material texture is stored in, by their DXGI numbers.</summary>
public enum DdsFormat
{
    /// <summary>One channel, 8 bytes a 4x4 block: a height map.</summary>
    Bc4 = 80,

    /// <summary>Two channels, 16 bytes a block: a normal map's x and y.</summary>
    Bc5 = 83,

    /// <summary>Four channels with colour in sRGB, 16 bytes a block: colour with occlusion in alpha.</summary>
    Bc7Srgb = 99,
}

/// <summary>
/// A square 2D texture with every mip level, block-compressed, in a DDS file with the DX10 header (148 bytes of header,
/// then each level from the largest, packed): what <c>tools/Textures</c> writes for each layer of a material's texture
/// set and <see cref="MaterialTextureSets"/> reads. Levels smaller than a block take one padded block.
/// </summary>
public sealed class DdsFile
{
    private const uint Magic = 0x20534444; // "DDS "
    private const uint FourCcDx10 = 0x30315844; // "DX10"
    private const int HeaderBytes = 4 + 124 + 20;

    private DdsFile(DdsFormat format, int texels, int[] offsets, int[] lengths)
    {
        Format = format;
        Texels = texels;
        Offsets = offsets;
        Lengths = lengths;
    }

    /// <summary>The texture's format.</summary>
    public DdsFormat Format { get; }

    /// <summary>Texels along each side of the largest level.</summary>
    public int Texels { get; }

    /// <summary>Each level's byte offset in the file, from the largest.</summary>
    public IReadOnlyList<int> Offsets { get; }

    /// <summary>Each level's byte count.</summary>
    public IReadOnlyList<int> Lengths { get; }

    /// <summary>Bytes in one 4x4 block of <paramref name="format"/>.</summary>
    public static int BlockBytes(DdsFormat format) => format == DdsFormat.Bc4 ? 8 : 16;

    /// <summary>Levels of a texture <paramref name="texels"/> square, down to one texel.</summary>
    public static int MipCount(int texels) => System.Numerics.BitOperations.Log2((uint)texels) + 1;

    /// <summary>Bytes of level <paramref name="mip"/> of a texture <paramref name="texels"/> square.</summary>
    public static int MipBytes(DdsFormat format, int texels, int mip)
    {
        var blocks = Math.Max(1, ((texels >> mip) + 3) / 4);
        return blocks * blocks * BlockBytes(format);
    }

    /// <summary>
    /// Writes a texture <paramref name="texels"/> square (a power of two) with its <paramref name="mips"/>, every level
    /// from the largest down to one texel, each <see cref="MipBytes"/> long.
    /// </summary>
    public static void Write(Stream stream, DdsFormat format, int texels, IReadOnlyList<byte[]> mips)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(mips);
        if (texels < 1 || (texels & (texels - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(texels), texels, "A texture's side must be a power of two.");
        }

        if (mips.Count != MipCount(texels))
        {
            throw new ArgumentException($"A texture {texels} square has {MipCount(texels)} levels, not {mips.Count}.");
        }

        for (var i = 0; i < mips.Count; i++)
        {
            if (mips[i].Length != MipBytes(format, texels, i))
            {
                throw new ArgumentException(
                    $"Level {i} holds {mips[i].Length} bytes, not {MipBytes(format, texels, i)}."
                );
            }
        }

        Span<byte> header = stackalloc byte[HeaderBytes];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        var main = header[4..];
        BinaryPrimitives.WriteUInt32LittleEndian(main, 124);
        // Caps, height, width, pixel format, mip count, linear size.
        BinaryPrimitives.WriteUInt32LittleEndian(main[4..], 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        BinaryPrimitives.WriteUInt32LittleEndian(main[8..], (uint)texels);
        BinaryPrimitives.WriteUInt32LittleEndian(main[12..], (uint)texels);
        BinaryPrimitives.WriteUInt32LittleEndian(main[16..], (uint)mips[0].Length);
        BinaryPrimitives.WriteUInt32LittleEndian(main[24..], (uint)mips.Count);
        // The pixel format at 72: its size, "four CC" with DX10.
        BinaryPrimitives.WriteUInt32LittleEndian(main[72..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(main[76..], 0x4);
        BinaryPrimitives.WriteUInt32LittleEndian(main[80..], FourCcDx10);
        // Caps at 104: texture, mip map, complex.
        BinaryPrimitives.WriteUInt32LittleEndian(main[104..], 0x1000 | 0x400000 | 0x8);
        var dx10 = header[128..];
        BinaryPrimitives.WriteUInt32LittleEndian(dx10, (uint)format);
        BinaryPrimitives.WriteUInt32LittleEndian(dx10[4..], 3); // 2D
        BinaryPrimitives.WriteUInt32LittleEndian(dx10[12..], 1); // one array element
        stream.Write(header);
        foreach (var mip in mips)
        {
            stream.Write(mip);
        }
    }

    /// <summary>
    /// Reads the header of a file <see cref="Write"/> made: its format, size, and where each level lies. Throws
    /// <see cref="InvalidDataException"/> on anything else (another format, not square, levels missing or cut short).
    /// </summary>
    public static DdsFile Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic)
        {
            throw new InvalidDataException("Not a DDS file.");
        }

        var main = file[4..];
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(main[8..]);
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(main[12..]);
        var mipCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(main[24..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(main[80..]) != FourCcDx10)
        {
            throw new InvalidDataException("The DDS file has no DX10 header.");
        }

        var format = (DdsFormat)BinaryPrimitives.ReadUInt32LittleEndian(file[128..]);
        if (format is not (DdsFormat.Bc4 or DdsFormat.Bc5 or DdsFormat.Bc7Srgb))
        {
            throw new InvalidDataException($"The DDS file's format {(int)format} is not BC4, BC5, or BC7 sRGB.");
        }

        if (width != height || width < 1 || (width & (width - 1)) != 0 || mipCount != MipCount(width))
        {
            throw new InvalidDataException(
                $"The DDS file is {width}x{height} with {mipCount} levels, not square, a power of two, and complete."
            );
        }

        var offsets = new int[mipCount];
        var lengths = new int[mipCount];
        var offset = HeaderBytes;
        for (var i = 0; i < mipCount; i++)
        {
            offsets[i] = offset;
            lengths[i] = MipBytes(format, width, i);
            offset += lengths[i];
        }

        if (offset > file.Length)
        {
            throw new InvalidDataException($"The DDS file holds {file.Length} bytes; its levels need {offset}.");
        }

        return new DdsFile(format, width, offsets, lengths);
    }
}
