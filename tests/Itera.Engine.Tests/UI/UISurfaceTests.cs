using Itera.Engine.UI;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

public sealed class UISurfaceTests : IDisposable
{
    private readonly BandLayer _layer = new();
    private readonly UISurface _surface;
    private ulong _frame;

    public UISurfaceTests()
    {
        _surface = new UISurface([_layer], NullLogger<UISurface>.Instance);
        _surface.Initialize(64, 48);
    }

    public void Dispose()
    {
        _surface.Dispose();
        _layer.Dispose();
    }

    [Fact]
    public void The_first_draw_sends_every_row()
    {
        var draw = Draw();

        Assert.Equal(new UiDraw(0, 48, true), draw);
    }

    [Fact]
    public void An_unchanged_ui_sends_no_rows_and_allocates_nothing()
    {
        Draw();
        Draw();

        var start = SystemAllocation.StartMeasurement();
        var draw = Draw();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(new UiDraw(0, 0, true), draw);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_changed_layer_sends_the_rows_it_covers_now_and_before()
    {
        Draw();
        _layer.Move(20, 30);

        var draw = Draw();

        // Rows 10 to 20 held the band before; 20 to 30 hold it now.
        Assert.Equal(new UiDraw(10, 20, true), draw);
    }

    [Fact]
    public void A_hidden_layer_clears_its_rows()
    {
        Draw();
        _layer.Shown = false;

        var draw = Draw();

        Assert.Equal(new UiDraw(10, 10, false), draw);
        Assert.Equal(0u, Pixel(5, 15));
    }

    [Fact]
    public void The_pixels_are_premultiplied_bgra_rows_from_the_top()
    {
        Draw();

        Assert.Equal(0u, Pixel(5, 9));
        // Half-transparent pure red, premultiplied: blue 0, green 0, red 128, alpha 128.
        var pixel = Pixel(5, 10);
        Assert.Equal(0u, pixel & 0xFFFFu);
        Assert.InRange((pixel >> 16) & 0xFF, 127u, 129u);
        Assert.InRange(pixel >> 24, 127u, 129u);
    }

    [Fact]
    public void A_new_size_sends_every_row()
    {
        Draw();

        var draw = _surface.Draw(new FrameTime(0.016f, 1, _frame++, 0), 80, 40);

        Assert.Equal(new UiDraw(0, 40, true), draw);
        Assert.Equal(80 * 40 * 4, _surface.Pixels.Length);
    }

    [Fact]
    public void A_matchs_layers_draw_over_the_run_layers_below_and_under_those_above()
    {
        using var below = new ColourLayer(new SKColor(255, 0, 0), 10, 20, above: false);
        using var above = new ColourLayer(new SKColor(0, 0, 255), 10, 12, above: true);
        using var match = new ColourLayer(new SKColor(0, 255, 0), 10, 16, above: false);
        using var surface = new UISurface([above, below], NullLogger<UISurface>.Instance);
        surface.Initialize(64, 48);
        Draw(surface);

        surface.SetMatchLayers([match]);
        var draw = Draw(surface);

        Assert.Equal(new UiDraw(0, 48, true), draw);
        Assert.Equal(Opaque(0, 0, 255), Pixel(surface, 5, 11));
        Assert.Equal(Opaque(0, 255, 0), Pixel(surface, 5, 14));
        Assert.Equal(Opaque(255, 0, 0), Pixel(surface, 5, 18));
    }

    [Fact]
    public void A_matchs_layers_go_when_the_match_ends()
    {
        using var match = new ColourLayer(new SKColor(0, 255, 0), 30, 40, above: false);
        _surface.SetMatchLayers([match]);
        Draw();
        Assert.Equal(Opaque(0, 255, 0), Pixel(_surface, 5, 35));

        _surface.SetMatchLayers([]);
        var draw = Draw();

        Assert.Equal(new UiDraw(0, 48, true), draw);
        Assert.Equal(0u, Pixel(_surface, 5, 35));
    }

    /// <summary>
    /// A changed layer redraws and sends only its area: another layer outside it is neither drawn nor cleared.
    /// </summary>
    [Fact]
    public void A_changed_layer_redraws_only_its_area_and_leaves_other_layers_alone()
    {
        using var left = new RectLayer(new SKRectI(0, 2, 20, 10), new SKColor(0, 0, 255));
        using var right = new RectLayer(new SKRectI(40, 30, 60, 40), new SKColor(0, 255, 0));
        using var surface = new UISurface([left, right], NullLogger<UISurface>.Instance);
        surface.Initialize(64, 48);
        Draw(surface);
        var drawnRight = right.Draws;

        left.Change();
        var draw = Draw(surface);

        Assert.Equal(new UiDraw(2, 8, true), draw);
        Assert.Equal(drawnRight, right.Draws);
        Assert.Equal(Opaque(0, 255, 0), Pixel(surface, 50, 35));
        Assert.Equal(Opaque(0, 0, 255), Pixel(surface, 10, 5));
    }

    /// <summary>A layer under a changed one's area is drawn again there, so the area shows it, not a hole.</summary>
    [Fact]
    public void A_layer_under_a_changed_ones_area_is_drawn_again_within_it()
    {
        using var under = new RectLayer(new SKRectI(0, 0, 64, 48), new SKColor(0, 0, 255));
        using var over = new RectLayer(new SKRectI(10, 10, 20, 20), new SKColor(255, 0, 0));
        using var surface = new UISurface([under, over], NullLogger<UISurface>.Instance);
        surface.Initialize(64, 48);
        Draw(surface);
        Assert.Equal(Opaque(255, 0, 0), Pixel(surface, 15, 15));

        over.Move(new SKRectI(30, 30, 40, 40));
        var draw = Draw(surface);

        Assert.Equal(new UiDraw(10, 30, true), draw);
        Assert.Equal(Opaque(0, 0, 255), Pixel(surface, 15, 15));
        Assert.Equal(Opaque(255, 0, 0), Pixel(surface, 35, 35));
    }

    /// <summary>A layer's ink past its bounds is cut off, so no redraw of another area can leave it behind.</summary>
    [Fact]
    public void A_layer_is_clipped_to_its_bounds()
    {
        using var layer = new RectLayer(
            new SKRectI(10, 10, 20, 20),
            new SKColor(255, 0, 0),
            ink: new SKRectI(0, 0, 64, 48)
        );
        using var surface = new UISurface([layer], NullLogger<UISurface>.Instance);
        surface.Initialize(64, 48);

        Draw(surface);

        Assert.Equal(Opaque(255, 0, 0), Pixel(surface, 15, 15));
        Assert.Equal(0u, Pixel(surface, 5, 5));
        Assert.Equal(0u, Pixel(surface, 25, 15));
    }

    /// <summary>A layer whose bounds change without being dirty is still drawn again, where it was and where it is.</summary>
    [Fact]
    public void New_bounds_alone_redraw_the_layer()
    {
        using var layer = new RectLayer(new SKRectI(10, 10, 20, 20), new SKColor(255, 0, 0));
        using var surface = new UISurface([layer], NullLogger<UISurface>.Instance);
        surface.Initialize(64, 48);
        Draw(surface);

        layer.Move(new SKRectI(10, 30, 20, 40), dirty: false);
        var draw = Draw(surface);

        Assert.Equal(new UiDraw(10, 30, true), draw);
        Assert.Equal(0u, Pixel(surface, 15, 15));
        Assert.Equal(Opaque(255, 0, 0), Pixel(surface, 15, 35));
    }

    /// <summary>
    /// New GPU buffers, even at the same size, get every row at the next draw; an unchanged generation sends nothing.
    /// </summary>
    [Fact]
    public void Buffers_made_again_get_every_row()
    {
        _surface.FollowBuffers(1);
        Draw();
        _surface.FollowBuffers(1);
        Assert.Equal(new UiDraw(0, 0, true), Draw());

        _surface.FollowBuffers(2);

        Assert.Equal(new UiDraw(0, 48, true), Draw());
        Assert.Equal(new UiDraw(0, 0, true), Draw());
    }

    private UiDraw Draw() => Draw(_surface);

    private UiDraw Draw(UISurface surface) => surface.Draw(new FrameTime(0.016f, _frame * 0.016, _frame++, 0), 64, 48);

    /// <summary>An opaque colour as the surface stores it: blue, green, red, alpha from the lowest byte.</summary>
    private static uint Opaque(byte red, byte green, byte blue) =>
        blue | ((uint)green << 8) | ((uint)red << 16) | 0xFF000000u;

    private static uint Pixel(UISurface surface, int x, int y)
    {
        var offset = (y * surface.Width + x) * 4;
        return BitConverter.ToUInt32(surface.Pixels.Slice(offset, 4));
    }

    private uint Pixel(int x, int y)
    {
        var offset = (y * _surface.Width + x) * 4;
        return BitConverter.ToUInt32(_surface.Pixels.Slice(offset, 4));
    }

    /// <summary>An opaque band of one colour across the surface; above the match's layers or under them.</summary>
    private sealed class ColourLayer(SKColor colour, int top, int bottom, bool above) : IUiLayer, IDisposable
    {
        private readonly SKPaint _paint = new() { Color = colour };

        public bool AboveMatch => above;

        public bool Visible => true;

        public bool Dirty => false;

        public void Update(in FrameTime time) { }

        public SKRectI Bounds(int width, int height) => new(0, top, width, bottom);

        public void Draw(SKCanvas canvas, int width, int height) =>
            canvas.DrawRect(new SKRect(0, top, width, bottom), _paint);

        public void Dispose() => _paint.Dispose();
    }

    /// <summary>
    /// An opaque rectangle of one colour within <c>bounds</c>, or over <c>ink</c> when given (past its bounds); counts its
    /// draws.
    /// </summary>
    private sealed class RectLayer(SKRectI bounds, SKColor colour, SKRectI? ink = null) : IUiLayer, IDisposable
    {
        private readonly SKPaint _paint = new() { Color = colour };
        private SKRectI _bounds = bounds;

        public int Draws { get; private set; }

        public bool Visible => true;

        public bool Dirty { get; private set; } = true;

        public void Change() => Dirty = true;

        public void Move(SKRectI moved, bool dirty = true)
        {
            _bounds = moved;
            Dirty |= dirty;
        }

        public void Update(in FrameTime time) { }

        public SKRectI Bounds(int width, int height) => _bounds;

        public void Draw(SKCanvas canvas, int width, int height)
        {
            Dirty = false;
            Draws++;
            canvas.DrawRect(ink ?? _bounds, _paint);
        }

        public void Dispose() => _paint.Dispose();
    }

    /// <summary>A band of half-transparent red across the surface, from row <c>top</c> to <c>bottom</c>.</summary>
    private sealed class BandLayer : IUiLayer, IDisposable
    {
        private readonly SKPaint _paint = new() { Color = new SKColor(255, 0, 0, 128) };
        private bool _shown = true;
        private int _top = 10;
        private int _bottom = 20;

        public bool Shown
        {
            get => _shown;
            set
            {
                _shown = value;
                Dirty = true;
            }
        }

        public bool Visible => _shown;

        public bool Dirty { get; private set; } = true;

        public void Move(int top, int bottom)
        {
            _top = top;
            _bottom = bottom;
            Dirty = true;
        }

        public void Update(in FrameTime time) { }

        public SKRectI Bounds(int width, int height) => new(0, _top, width, _bottom);

        public void Draw(SKCanvas canvas, int width, int height)
        {
            Dirty = false;
            canvas.DrawRect(new SKRect(0, _top, width, _bottom), _paint);
        }

        public void Dispose() => _paint.Dispose();
    }
}
