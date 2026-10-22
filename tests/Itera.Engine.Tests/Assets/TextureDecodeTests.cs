using Itera.Engine.Assets;
using SkiaSharp;

namespace Itera.Engine.Tests.Assets;

public sealed class TextureDecodeTests
{
    [Fact]
    public void Straight_alpha_keeps_the_colour_of_a_clear_texel()
    {
        // Three texels wide, two high: the first is clear but coloured.
        var texels = new byte[]
        {
            200,
            10,
            30,
            0, /**/
            1,
            2,
            3,
            255, /**/
            40,
            50,
            60,
            128,
            7,
            8,
            9,
            255, /**/
            70,
            80,
            90,
            64, /**/
            255,
            254,
            253,
            255,
        };
        var png = Encode(texels, 3, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKEncodedImageFormat.Png);

        Assert.True(TextureDecode.TryDecode(png, out var width, out var height, out var rgba));

        Assert.Equal(3, width);
        Assert.Equal(2, height);
        Assert.Equal(texels, rgba);
    }

    [Fact]
    public void An_opaque_rgb_png_round_trips_exactly()
    {
        var texels = new byte[4 * 3 * 4];
        for (var i = 0; i < 12; i++)
        {
            texels[i * 4] = (byte)(i * 20);
            texels[i * 4 + 1] = (byte)(255 - i * 17);
            texels[i * 4 + 2] = (byte)(i * 7 + 3);
            texels[i * 4 + 3] = 255;
        }

        var png = Encode(texels, 4, 3, SKColorType.Rgba8888, SKAlphaType.Opaque, SKEncodedImageFormat.Png);

        Assert.True(TextureDecode.TryDecode(png, out var width, out var height, out var rgba));

        Assert.Equal(4, width);
        Assert.Equal(3, height);
        Assert.Equal(texels, rgba);
    }

    [Fact]
    public void A_greyscale_png_gets_equal_channels_and_full_alpha()
    {
        var grey = new byte[] { 0, 64, 128, 255 };
        var png = Encode(grey, 2, 2, SKColorType.Gray8, SKAlphaType.Opaque, SKEncodedImageFormat.Png);

        Assert.True(TextureDecode.TryDecode(png, out var width, out var height, out var rgba));

        Assert.Equal(2, width);
        Assert.Equal(2, height);
        Assert.Equal(16, rgba.Length);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(rgba[i * 4], rgba[i * 4 + 1]);
            Assert.Equal(rgba[i * 4], rgba[i * 4 + 2]);
            Assert.Equal(255, rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void A_flat_jpeg_decodes_close_to_its_colour()
    {
        var texels = new byte[8 * 8 * 4];
        for (var i = 0; i < 64; i++)
        {
            texels[i * 4] = 120;
            texels[i * 4 + 1] = 200;
            texels[i * 4 + 2] = 40;
            texels[i * 4 + 3] = 255;
        }

        var jpeg = Encode(texels, 8, 8, SKColorType.Rgba8888, SKAlphaType.Opaque, SKEncodedImageFormat.Jpeg);

        Assert.True(TextureDecode.TryDecode(jpeg, out var width, out var height, out var rgba));

        Assert.Equal(8, width);
        Assert.Equal(8, height);
        for (var i = 0; i < 64; i++)
        {
            Assert.InRange(rgba[i * 4], 120 - 4, 120 + 4);
            Assert.InRange(rgba[i * 4 + 1], 200 - 4, 200 + 4);
            Assert.InRange(rgba[i * 4 + 2], 40 - 4, 40 + 4);
            Assert.Equal(255, rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void Bytes_that_are_not_an_image_fail_without_throwing()
    {
        var garbage = new byte[256];
        new Random(5).NextBytes(garbage);

        Assert.False(TextureDecode.TryDecode(garbage, out var width, out var height, out var rgba));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
        Assert.Empty(rgba);

        Assert.False(TextureDecode.TryDecode([], out width, out height, out rgba));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
        Assert.Empty(rgba);
    }

    [Fact]
    public void A_truncated_png_fails_without_throwing()
    {
        var texels = new byte[16 * 16 * 4];
        new Random(7).NextBytes(texels);
        var png = Encode(texels, 16, 16, SKColorType.Rgba8888, SKAlphaType.Opaque, SKEncodedImageFormat.Png);

        Assert.False(
            TextureDecode.TryDecode(png.AsSpan(0, png.Length / 2), out var width, out var height, out var rgba)
        );
        Assert.Equal(0, width);
        Assert.Equal(0, height);
        Assert.Empty(rgba);
    }

    private static byte[] Encode(
        byte[] pixels,
        int width,
        int height,
        SKColorType colourType,
        SKAlphaType alphaType,
        SKEncodedImageFormat format
    )
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, colourType, alphaType));
        pixels.CopyTo(bitmap.GetPixelSpan());
        using var data = bitmap.Encode(format, 100);
        return data.ToArray();
    }
}
