using Itera.Engine.Loading;
using Itera.Engine.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// The aim dot (<see cref="AimDotLayer"/>): the pixels the world's shaders drew it on before it moved to the HUD (white
/// where a pixel's centre lies less than 1.5 pixels from the screen's centre along each axis, black less than 2.5),
/// nothing outside its bounds, and hidden while the loading screen is drawn.
/// </summary>
public sealed class AimDotLayerTests
{
    private readonly IOptions<EngineOptions> _options = Options.Create(new EngineOptions());

    [Theory]
    [InlineData(64, 64)]
    [InlineData(63, 63)]
    [InlineData(65, 64)]
    public void It_draws_the_shaders_squares_at_the_centre(int width, int height)
    {
        using var layer = new AimDotLayer(new LoadingScreen(_options, NullLogger<LoadingScreen>.Instance));
        using var bitmap = Draw(layer, width, height);

        var white = 0;
        var black = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var reach = MathF.Max(MathF.Abs(x + 0.5f - width * 0.5f), MathF.Abs(y + 0.5f - height * 0.5f));
                var pixel = bitmap.GetPixel(x, y);
                if (reach < AimDotLayer.WhiteReach)
                {
                    Assert.Equal(SKColors.White, pixel);
                }
                else if (reach < AimDotLayer.BlackReach)
                {
                    Assert.Equal(SKColors.Black, pixel);
                }
                else
                {
                    Assert.Equal(0, pixel.Alpha);
                }

                white += pixel == SKColors.White ? 1 : 0;
                black += pixel == SKColors.Black ? 1 : 0;
            }
        }

        // Two and four pixels across an even side, three and five across an odd one.
        int White(int size) => size % 2 == 0 ? 2 : 3;
        int Black(int size) => size % 2 == 0 ? 4 : 5;
        Assert.Equal(White(width) * White(height), white);
        Assert.Equal(Black(width) * Black(height) - white, black);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(256, 144)]
    [InlineData(819, 879)]
    public void It_draws_nothing_outside_its_bounds(int width, int height)
    {
        using var layer = new AimDotLayer(new LoadingScreen(_options, NullLogger<LoadingScreen>.Instance));

        var (outside, drawn) = LayerInk.Outside(layer, width, height);

        Assert.Equal(0, outside);
        Assert.True(drawn > 0);
    }

    [Fact]
    public void It_hides_while_the_loading_screen_is_drawn_and_is_dirty_when_that_changes()
    {
        var loading = new LoadingScreen(_options, NullLogger<LoadingScreen>.Instance);
        using var layer = new AimDotLayer(loading);
        Assert.True(layer.Dirty);
        using (Draw(layer, 64, 64))
        {
            Assert.False(layer.Dirty);
        }

        loading.Initialize([new Task()]);
        loading.Begin(0);

        Assert.False(layer.Visible);
        Assert.True(layer.Dirty);
        using var hidden = Draw(layer, 64, 64);
        Assert.False(layer.Dirty);
        Assert.Equal(0, hidden.GetPixel(32, 32).Alpha);
    }

    private static SKBitmap Draw(AimDotLayer layer, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        layer.Draw(canvas, width, height);
        canvas.Flush();
        return bitmap;
    }

    private sealed class Task : ILoadingTask
    {
        public string Name => "World";

        public LoadingProgress Progress => LoadingProgress.Waiting;
    }
}
