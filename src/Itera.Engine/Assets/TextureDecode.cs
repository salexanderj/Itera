using System.Runtime.InteropServices;
using SkiaSharp;

namespace Itera.Engine.Assets;

/// <summary>Decodes the PNG, JPEG, and WebP images inside model files into raw texels.</summary>
internal static class TextureDecode
{
    /// <summary>
    /// Decodes <paramref name="encoded"/> into <paramref name="rgba"/>: <paramref name="width"/> by
    /// <paramref name="height"/> texels of RGBA8, tightly packed, top row first, with straight (unpremultiplied) alpha.
    /// A texel with alpha 0 keeps its colour. Greyscale, palette, and RGB images get alpha 255. Returns false, with a
    /// size of 0 and no texels, for bytes that do not decode (a truncated file included).
    /// </summary>
    /// <remarks>
    /// glTF 2.0 says the colour-space information inside PNG and JPEG files (ICC profiles, gAMA, sRGB chunks) must be
    /// ignored. The target has no colour space, so Skia converts nothing, and it is unpremultiplied so the colour of a
    /// transparent texel survives.
    /// </remarks>
    public static bool TryDecode(ReadOnlySpan<byte> encoded, out int width, out int height, out byte[] rgba)
    {
        width = 0;
        height = 0;
        rgba = [];
        if (encoded.IsEmpty)
        {
            return false;
        }

        try
        {
            using var data = SKData.CreateCopy(encoded);
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                return false;
            }

            var size = codec.Info.Size;
            if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height * 4 > Array.MaxLength)
            {
                return false;
            }

            var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var pixels = new byte[info.BytesSize];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            SKCodecResult result;
            try
            {
                result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            if (result != SKCodecResult.Success)
            {
                return false;
            }

            width = size.Width;
            height = size.Height;
            rgba = pixels;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            return false;
        }
    }
}
