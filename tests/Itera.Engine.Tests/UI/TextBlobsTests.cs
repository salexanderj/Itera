using Itera.Engine.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="TextBlobs"/>: each text drawn at the same pixels as Skia's <c>DrawText</c> of its string, left, centred,
/// and right aligned; made again for another font or size; an empty text drawing nothing; and nothing allocated after
/// the first draw.
/// </summary>
public sealed class TextBlobsTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 64;

    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly SKPaint _paint = new() { Color = new SKColor(235, 225, 215), IsAntialias = true };
    private readonly TextBlobs _texts = new(["Name", "72/100", "Connection lost. Retrying in 7 s...", ""]);

    public void Dispose()
    {
        _texts.Dispose();
        _paint.Dispose();
        _fonts.Dispose();
    }

    [Theory]
    [InlineData(SKTextAlign.Left)]
    [InlineData(SKTextAlign.Center)]
    [InlineData(SKTextAlign.Right)]
    public void Each_text_draws_where_drawing_its_string_does(SKTextAlign align)
    {
        using var font = new SKFont(_fonts.Mono, 22) { Embolden = true };
        var x =
            align == SKTextAlign.Left ? 4f
            : align == SKTextAlign.Center ? Width / 2f
            : Width - 4f;
        for (var i = 0; i < 3; i++)
        {
            var text = _texts[i];
            var expected = Drawn(canvas => canvas.DrawText(text, x, 40.5f, align, font, _paint));
            var drawn = Drawn(canvas => _texts.Draw(canvas, i, x, 40.5f, align, font, _paint));
            Assert.True(expected.AsSpan().ContainsAnyExcept((byte)0), "the string drew nothing");
            Assert.Equal(expected, drawn);
        }
    }

    [Fact]
    public void Another_font_or_size_makes_the_blobs_again()
    {
        using var small = new SKFont(_fonts.Mono, 14) { Embolden = true };
        using var large = new SKFont(_fonts.Mono, 30) { Embolden = true };
        Drawn(canvas => _texts.Draw(canvas, 1, 10, 40, SKTextAlign.Left, small, _paint));

        var expected = Drawn(canvas => canvas.DrawText("72/100", 10, 40, SKTextAlign.Left, large, _paint));
        Assert.Equal(expected, Drawn(canvas => _texts.Draw(canvas, 1, 10, 40, SKTextAlign.Left, large, _paint)));

        // The same font at another size.
        large.Size = 20;
        expected = Drawn(canvas => canvas.DrawText("72/100", 10, 40, SKTextAlign.Left, large, _paint));
        Assert.Equal(expected, Drawn(canvas => _texts.Draw(canvas, 1, 10, 40, SKTextAlign.Left, large, _paint)));
    }

    [Fact]
    public void An_empty_text_draws_nothing()
    {
        using var font = new SKFont(_fonts.Mono, 22);
        var drawn = Drawn(canvas => _texts.Draw(canvas, 3, 10, 40, SKTextAlign.Center, font, _paint));
        Assert.False(drawn.AsSpan().ContainsAnyExcept((byte)0));
        Assert.Equal(4, _texts.Count);
        Assert.Equal("", _texts[3]);
    }

    [Fact]
    public void Drawing_allocates_nothing_after_the_first_draw()
    {
        using var font = new SKFont(_fonts.Mono, 22) { Embolden = true };
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        for (var i = 0; i < 50; i++)
        {
            DrawEach(canvas, font, i);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            DrawEach(canvas, font, i);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private void DrawEach(SKCanvas canvas, SKFont font, int round)
    {
        canvas.Clear(SKColors.Transparent);
        var align = (SKTextAlign)(round % 3);
        for (var index = 0; index < _texts.Count; index++)
        {
            _texts.Draw(canvas, index, Width / 2f, 40, align, font, _paint);
        }
    }

    /// <summary>The bytes of a transparent bitmap after <paramref name="draw"/>.</summary>
    private static byte[] Drawn(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            draw(canvas);
        }

        return bitmap.Bytes;
    }
}
