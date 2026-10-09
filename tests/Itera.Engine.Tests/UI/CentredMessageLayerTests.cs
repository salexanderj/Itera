using Itera.Engine.UI;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="CentredMessageLayer"/>: it draws the chosen text inside its bounds, hides when the callback says none, is
/// dirty only when the choice changes, and a redraw allocates nothing.
/// </summary>
public sealed class CentredMessageLayerTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;

    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly string[] _texts = ["Get ready", "Round over", "Next in 3"];
    private int _choice = CentredMessageLayer.None;

    public void Dispose() => _fonts.Dispose();

    [Fact]
    public void It_draws_the_chosen_text_inside_its_bounds()
    {
        using var layer = Layer();
        _choice = 1;
        Update(layer);

        Assert.True(layer.Visible);
        Assert.Equal(1, layer.Shown);
        Assert.Equal("Round over", layer.Text);
        var (outside, drawn) = LayerInk.Outside(layer, Width, Height);
        Assert.Equal(0, outside);
        Assert.True(drawn > 0);
    }

    [Fact]
    public void It_hides_and_draws_nothing_while_the_callback_says_none()
    {
        using var layer = Layer();
        Update(layer);

        Assert.False(layer.Visible);
        Assert.Null(layer.Text);
        Assert.False(layer.Dirty);
        var (_, drawn) = LayerInk.Outside(layer, Width, Height);
        Assert.Equal(0, drawn);
    }

    [Fact]
    public void It_is_dirty_only_when_the_choice_changes()
    {
        using var layer = Layer();
        _choice = 0;
        Update(layer);
        Assert.True(layer.Dirty);
        Draw(layer);
        Assert.False(layer.Dirty);

        Update(layer);
        Assert.False(layer.Dirty);

        _choice = 2;
        Update(layer);
        Assert.True(layer.Dirty);
        Draw(layer);

        _choice = CentredMessageLayer.None;
        Update(layer);
        Assert.True(layer.Dirty);
        Assert.False(layer.Visible);
    }

    [Fact]
    public void An_index_past_the_list_shows_the_last_text()
    {
        using var layer = Layer();
        _choice = 99;
        Update(layer);

        Assert.Equal(2, layer.Shown);
    }

    [Fact]
    public void A_redraw_allocates_nothing()
    {
        using var layer = Layer();
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        for (var i = 0; i < 20; i++)
        {
            Redraw(layer, canvas, i);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            Redraw(layer, canvas, i);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private CentredMessageLayer Layer() => new(_fonts, _texts, () => _choice);

    private void Redraw(CentredMessageLayer layer, SKCanvas canvas, int round)
    {
        _choice = round % 4 - 1;
        Update(layer);
        canvas.Clear(SKColors.Transparent);
        layer.Draw(canvas, Width, Height);
    }

    private static void Update(CentredMessageLayer layer) => layer.Update(new FrameTime(1 / 60f, 0, 0, 0));

    private static void Draw(CentredMessageLayer layer)
    {
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        layer.Draw(canvas, Width, Height);
    }
}
