using Itera.Engine;
using Itera.Engine.Loading;
using Itera.Engine.Matches;
using Itera.Engine.Rendering;
using Itera.Engine.UI;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Game.Tests;

/// <summary>
/// <see cref="MinimapLayer"/>: it draws within its bounds, hidden while the minimap is not shown or the loading screen is
/// drawn; it keeps below a shown layer it keeps clear of (the stats line); its arrow turns with the look, redrawn once
/// the look has turned about a degree; a frame after the first draw allocates nothing.
/// </summary>
public sealed class MinimapLayerTests : IDisposable
{
    private static readonly FrameTime Time = new(1f / 60, 1, 1, 0);

    private readonly ServiceProvider _provider = GameComposition.Build();
    private readonly Minimap _minimap;
    private readonly RenderViews _views;
    private readonly LoadingScreen _loading = new(
        Options.Create(new EngineOptions()),
        NullLogger<LoadingScreen>.Instance
    );

    public MinimapLayerTests()
    {
        _ = new HeadlessRun(_provider);
        _minimap = _provider.GetRequiredService<Minimap>();
        _views = _provider.GetRequiredService<RenderViews>();
        _minimap.Shown = true;
    }

    public void Dispose() => _provider.Dispose();

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1280, 720)]
    [InlineData(500, 900)]
    public void It_draws_within_its_bounds(int width, int height)
    {
        using var layer = new MinimapLayer(_minimap, _views, _loading);

        var (outside, drawn) = Itera.TestSupport.LayerInk.Outside(layer, width, height);

        Assert.Equal(0, outside);
        Assert.True(drawn > 0);
    }

    [Fact]
    public void It_hides_while_the_map_is_not_shown_or_the_loading_screen_is_drawn()
    {
        using var layer = new MinimapLayer(_minimap, _views, _loading);
        Assert.True(layer.Visible);

        _minimap.Shown = false;
        var notShown = layer.Visible;
        _minimap.Shown = true;
        _loading.Initialize([new Task()]);
        _loading.Begin(0);

        Assert.False(notShown);
        Assert.False(layer.Visible);
    }

    [Fact]
    public void It_keeps_below_a_shown_layer_it_keeps_clear_of()
    {
        var stats = new Box(new SKRectI(1800, 8, 2552, 52));
        using var layer = new MinimapLayer(_minimap, _views, _loading, stats);

        var beside = layer.Bounds(2560, 1440);
        stats.Shown = false;
        var alone = layer.Bounds(2560, 1440);

        Assert.Equal(52 + 24 - MinimapLayer.FramePixels, beside.Top);
        Assert.Equal(24 - MinimapLayer.FramePixels, alone.Top);
        Assert.Equal(
            MinimapLayer.Map(2560, 1440, below: 52),
            SKRectI.Inflate(beside, -MinimapLayer.FramePixels, -MinimapLayer.FramePixels)
        );
    }

    /// <summary>
    /// Looking west (yaw a quarter turn left), the arrow's tip, its farther end from the map's middle, points left: the
    /// white ink reaches farther left of the middle than right of it.
    /// </summary>
    [Fact]
    public void The_arrow_turns_with_the_look_and_redraws_past_a_degree()
    {
        using var layer = new MinimapLayer(_minimap, _views, _loading);
        _minimap.Yaw = MathF.PI / 2;
        using var bitmap = Draw(layer, 1280, 720);
        var map = MinimapLayer.Map(1280, 720);
        var (left, right) = (int.MaxValue, int.MinValue);
        for (var y = map.Top; y < map.Bottom; y++)
        {
            for (var x = map.Left; x < map.Right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200 && pixel.Alpha > 200)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }

        Assert.True(map.MidX - left > right - map.MidX, $"Left {left}, right {right}, middle {map.MidX}.");
        Assert.False(layer.Dirty);
        _minimap.Yaw += 0.01f;
        var smallTurn = layer.Dirty;
        _minimap.Yaw += 0.02f;
        Assert.False(smallTurn);
        Assert.True(layer.Dirty);
    }

    [Fact]
    public void A_frame_after_the_first_draw_allocates_nothing()
    {
        using var layer = new MinimapLayer(_minimap, _views, _loading);
        using var bitmap = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        Frame(layer, canvas);

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                _minimap.Yaw += 0.05f;
                Frame(layer, canvas);
            }
        });

        Assert.Equal(0, bytes);
    }

    private static void Frame(MinimapLayer layer, SKCanvas canvas)
    {
        layer.Update(in Time);
        layer.Bounds(1280, 720);
        layer.Draw(canvas, 1280, 720);
    }

    private static SKBitmap Draw(MinimapLayer layer, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        layer.Draw(canvas, width, height);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>A layer that shows a fixed box, as the stats line does in the top-right corner.</summary>
    private sealed class Box(SKRectI bounds) : IUiLayer
    {
        public bool Shown { get; set; } = true;

        public bool Visible => Shown;

        public bool Dirty => false;

        public void Update(in FrameTime time) { }

        public SKRectI Bounds(int width, int height) => bounds;

        public void Draw(SKCanvas canvas, int width, int height) { }
    }

    private sealed class Task : ILoadingTask
    {
        public string Name => "World";

        public LoadingProgress Progress => LoadingProgress.Waiting;
    }
}
