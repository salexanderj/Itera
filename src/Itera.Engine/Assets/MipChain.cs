using Itera.Engine.Rendering.Materials;

namespace Itera.Engine.Assets;

/// <summary>
/// Builds the mip levels of a mesh texture on the CPU, all of them down to 1x1, packed level 0 first. Runs at load
/// time only.
/// </summary>
internal static class MipChain
{
    /// <summary>sRGB byte to linear light, the exact sRGB transfer function.</summary>
    private static readonly double[] s_srgbToLinear = BuildSrgbToLinear();

    /// <summary>The number of levels from <paramref name="width"/> by <paramref name="height"/> down to 1x1.</summary>
    public static int LevelCount(int width, int height) => int.Log2(Math.Max(width, height)) + 1;

    /// <summary>The size of level <paramref name="level"/>: each side halves, rounded down, and stops at 1.</summary>
    public static (int Width, int Height) LevelSize(int width, int height, int level) =>
        (Math.Max(1, width >> level), Math.Max(1, height >> level));

    /// <summary>The bytes of one texel in an uncompressed <paramref name="format"/>.</summary>
    public static int BytesPerTexel(MeshTextureFormat format) =>
        format switch
        {
            MeshTextureFormat.Rgba8Srgb => 4,
            MeshTextureFormat.Rgba8Unorm => 4,
            MeshTextureFormat.Rg8Unorm => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    /// <summary>The DDS format of a compressed <paramref name="format"/>; null for an uncompressed one.</summary>
    public static DdsFormat? CompressedFormat(MeshTextureFormat format) =>
        format switch
        {
            MeshTextureFormat.Bc7Srgb => DdsFormat.Bc7Srgb,
            MeshTextureFormat.Bc7Unorm => DdsFormat.Bc7Unorm,
            MeshTextureFormat.Bc5Unorm => DdsFormat.Bc5,
            _ => null,
        };

    /// <summary>
    /// The bytes of level <paramref name="level"/> in <paramref name="format"/>: its texels, or for a compressed format
    /// its whole 4x4 blocks, as its DDS file holds them (<see cref="DdsFile.MipBytes"/>).
    /// </summary>
    public static int LevelBytes(int width, int height, int level, MeshTextureFormat format)
    {
        if (CompressedFormat(format) is { } compressed)
        {
            return DdsFile.MipBytes(compressed, width, height, level);
        }

        var (w, h) = LevelSize(width, height, level);
        return w * h * BytesPerTexel(format);
    }

    /// <summary>
    /// The byte offset of level <paramref name="level"/> in the packed chain: the sum of the earlier levels' sizes.
    /// </summary>
    public static int LevelOffset(int width, int height, int level, MeshTextureFormat format)
    {
        var offset = 0;
        for (var i = 0; i < level; i++)
        {
            offset += LevelBytes(width, height, i, format);
        }

        return offset;
    }

    /// <summary>
    /// Makes every level of <paramref name="rgba"/> (<paramref name="width"/> by <paramref name="height"/> texels of
    /// RGBA8, top row first) from level 0 down to 1x1, tightly packed, in the format of <paramref name="role"/>:
    /// <see cref="MeshTextureRole.Colour"/> gives <see cref="MeshTextureFormat.Rgba8Srgb"/>,
    /// <see cref="MeshTextureRole.Linear"/> gives <see cref="MeshTextureFormat.Rgba8Unorm"/>, and
    /// <see cref="MeshTextureRole.Normal"/> gives <see cref="MeshTextureFormat.Rg8Unorm"/>.
    /// </summary>
    /// <remarks>
    /// Each level comes from the one above with a box filter that works for any size: destination texel x covers the
    /// source columns from floor(x * srcW / dstW) up to ceil((x + 1) * srcW / dstW), and rows likewise, at least one
    /// each, and takes the mean of the texels in that box. Colour is averaged in linear light, so mips do not darken
    /// (alpha is averaged as stored). A normal map is averaged as vectors and made unit length again, so mipped lighting
    /// keeps its strength; the chain keeps z beside x and y while it builds, though it stores only x and y.
    /// </remarks>
    public static byte[] Build(
        byte[] rgba,
        int width,
        int height,
        MeshTextureRole role,
        out MeshTextureFormat format,
        out int levels
    )
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("The texels are fewer than width * height.", nameof(rgba));
        }

        format = role switch
        {
            MeshTextureRole.Colour => MeshTextureFormat.Rgba8Srgb,
            MeshTextureRole.Linear => MeshTextureFormat.Rgba8Unorm,
            MeshTextureRole.Normal => MeshTextureFormat.Rg8Unorm,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        levels = LevelCount(width, height);
        var bytesPerTexel = BytesPerTexel(format);
        var output = new byte[LevelOffset(width, height, levels, format)];

        // The working copy of the level above, four bytes a texel (for a normal map: x, y, z, unused).
        var current = role == MeshTextureRole.Normal ? NormaliseLevel(rgba, width, height) : rgba;
        var currentWidth = width;
        var currentHeight = height;
        var offset = 0;
        for (var level = 0; level < levels; level++)
        {
            if (level > 0)
            {
                var (w, h) = LevelSize(width, height, level);
                current = role switch
                {
                    MeshTextureRole.Colour => Downsample(current, currentWidth, currentHeight, w, h, srgb: true),
                    MeshTextureRole.Linear => Downsample(current, currentWidth, currentHeight, w, h, srgb: false),
                    _ => DownsampleNormals(current, currentWidth, currentHeight, w, h),
                };
                currentWidth = w;
                currentHeight = h;
            }

            var texels = currentWidth * currentHeight;
            if (role == MeshTextureRole.Normal)
            {
                for (var i = 0; i < texels; i++)
                {
                    output[offset + i * 2] = current[i * 4];
                    output[offset + i * 2 + 1] = current[i * 4 + 1];
                }
            }
            else
            {
                Array.Copy(current, 0, output, offset, texels * 4);
            }

            offset += texels * bytesPerTexel;
        }

        return output;
    }

    /// <summary>
    /// One level at the size <paramref name="dstW"/> by <paramref name="dstH"/> from <paramref name="src"/>, the mean
    /// of the box each texel covers. With <paramref name="srgb"/>, RGB is averaged in linear light.
    /// </summary>
    private static byte[] Downsample(byte[] src, int srcW, int srcH, int dstW, int dstH, bool srgb)
    {
        var dst = new byte[dstW * dstH * 4];
        for (var y = 0; y < dstH; y++)
        {
            var (y0, y1) = Span(y, srcH, dstH);
            for (var x = 0; x < dstW; x++)
            {
                var (x0, x1) = Span(x, srcW, dstW);
                double r = 0;
                double g = 0;
                double b = 0;
                double a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = (sy * srcW + sx) * 4;
                        if (srgb)
                        {
                            r += s_srgbToLinear[src[p]];
                            g += s_srgbToLinear[src[p + 1]];
                            b += s_srgbToLinear[src[p + 2]];
                        }
                        else
                        {
                            r += src[p];
                            g += src[p + 1];
                            b += src[p + 2];
                        }

                        a += src[p + 3];
                    }
                }

                var inverse = 1.0 / ((y1 - y0) * (x1 - x0));
                var d = (y * dstW + x) * 4;
                if (srgb)
                {
                    dst[d] = LinearToSrgbByte(r * inverse);
                    dst[d + 1] = LinearToSrgbByte(g * inverse);
                    dst[d + 2] = LinearToSrgbByte(b * inverse);
                }
                else
                {
                    dst[d] = ToByte(r * inverse);
                    dst[d + 1] = ToByte(g * inverse);
                    dst[d + 2] = ToByte(b * inverse);
                }

                dst[d + 3] = ToByte(a * inverse);
            }
        }

        return dst;
    }

    /// <summary>
    /// One normal-map level at the size <paramref name="dstW"/> by <paramref name="dstH"/>: the box's vectors averaged
    /// and made unit length (a zero sum becomes straight up).
    /// </summary>
    private static byte[] DownsampleNormals(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (var y = 0; y < dstH; y++)
        {
            var (y0, y1) = Span(y, srcH, dstH);
            for (var x = 0; x < dstW; x++)
            {
                var (x0, x1) = Span(x, srcW, dstW);
                double vx = 0;
                double vy = 0;
                double vz = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = (sy * srcW + sx) * 4;
                        vx += src[p] / 255.0 * 2 - 1;
                        vy += src[p + 1] / 255.0 * 2 - 1;
                        vz += src[p + 2] / 255.0 * 2 - 1;
                    }
                }

                StoreNormal(dst, (y * dstW + x) * 4, vx, vy, vz);
            }
        }

        return dst;
    }

    /// <summary>Level 0 of a normal map: each texel's vector made unit length, stored as x, y, z bytes.</summary>
    private static byte[] NormaliseLevel(byte[] rgba, int width, int height)
    {
        var dst = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var p = i * 4;
            StoreNormal(dst, p, rgba[p] / 255.0 * 2 - 1, rgba[p + 1] / 255.0 * 2 - 1, rgba[p + 2] / 255.0 * 2 - 1);
        }

        return dst;
    }

    private static void StoreNormal(byte[] dst, int index, double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length > 1e-9)
        {
            x /= length;
            y /= length;
            z /= length;
        }
        else
        {
            x = 0;
            y = 0;
            z = 1;
        }

        dst[index] = ToByte((x * 0.5 + 0.5) * 255);
        dst[index + 1] = ToByte((y * 0.5 + 0.5) * 255);
        dst[index + 2] = ToByte((z * 0.5 + 0.5) * 255);
    }

    /// <summary>The source columns (or rows) of destination index <paramref name="index"/>, at least one.</summary>
    private static (int Start, int End) Span(int index, int srcSize, int dstSize)
    {
        var start = (int)((long)index * srcSize / dstSize);
        var end = (int)(((long)(index + 1) * srcSize + dstSize - 1) / dstSize);
        return (start, Math.Min(srcSize, Math.Max(end, start + 1)));
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>Linear light 0 to 1 as an sRGB byte, rounded to the nearest.</summary>
    private static byte LinearToSrgbByte(double linear)
    {
        var encoded = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return ToByte(encoded * 255);
    }

    private static double[] BuildSrgbToLinear()
    {
        var table = new double[256];
        for (var i = 0; i < table.Length; i++)
        {
            var c = i / 255.0;
            table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return table;
    }
}
