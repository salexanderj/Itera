using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Input;
using Itera.Engine.Loading;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.UI;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// The engine's layers with bounds smaller than the screen, the console, the loading list, and the stats line, draw
/// nothing outside them (<see cref="UISurface"/> clips each layer to its bounds and
/// redraws only the area that changed, so ink past them would be cut off), at three screen sizes; and each one's text
/// and box grow with the screen's height and the UI scale, one instance resized (the UI's sizing rule).
/// </summary>
public sealed class EngineLayerBoundsTests : IDisposable
{
    private static readonly (int Width, int Height)[] s_sizes = [(1280, 720), (2560, 1440), (819, 879)];

    private readonly IOptions<EngineOptions> _options = Options.Create(new EngineOptions());
    private readonly UiFonts _fonts;

    public EngineLayerBoundsTests()
    {
        _fonts = new UiFonts(_options, NullLogger<UiFonts>.Instance);
    }

    public void Dispose() => _fonts.Dispose();

    [Fact]
    public void The_console_draws_nothing_outside_its_bounds()
    {
        using var layer = Console();

        AssertInside(layer);
    }

    [Fact]
    public void The_console_text_grows_with_the_screen_and_the_ui_scale()
    {
        using var layer = Console();

        var small = InkRows(layer, 1280, 720);
        var large = InkRows(layer, 2560, 1440);
        _fonts.UiScale = 1.5f;
        var scaled = InkRows(layer, 2560, 1440);

        // The typed line, its prompt and cursor, twice as tall on a screen twice as high, and half as tall again at 150 %.
        Assert.InRange(large / (float)small, 1.8f, 2.2f);
        Assert.InRange(scaled / (float)large, 1.35f, 1.65f);
    }

    [Fact]
    public void The_stats_line_and_loading_list_grow_with_the_screen_and_the_ui_scale()
    {
        using var stats = Stats();
        using var loading = Loading();
        foreach (var layer in new IUiLayer[] { stats, loading })
        {
            var small = Size(layer, 1280, 720);
            var large = Size(layer, 2560, 1440);
            Assert.Equal(2, large.Height / (float)small.Height, 1);
            Assert.Equal(2, large.Width / (float)small.Width, 1);
        }

        _fonts.UiScale = 1.5f;
        var scaled = Size(stats, 2560, 1440);
        _fonts.UiScale = 1;
        var plain = Size(stats, 2560, 1440);
        Assert.Equal(1.5f, scaled.Height / (float)plain.Height, 1);
    }

    [Fact]
    public void A_centred_message_grows_with_the_screen()
    {
        using var layer = new CentredMessageLayer(_fonts, ["Round 3"], () => 0);

        var small = Size(layer, 1280, 720);
        var large = Size(layer, 2560, 1440);

        Assert.Equal(2, large.Height / (float)small.Height, 1);
        AssertInside(layer);
    }

    private ConsoleLayer Console()
    {
        var logs = new ConsoleLogBuffer();
        var console = new DevConsole(
            _options,
            new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance),
            logs,
            NullLogger<DevConsole>.Instance
        );
        for (var i = 0; i < 80; i++)
        {
            logs.Add(Microsoft.Extensions.Logging.LogLevel.Warning, $"Line {i} " + new string('w', i * 3));
        }

        console.Open();
        return new ConsoleLayer(console, _fonts);
    }

    [Fact]
    public void The_loading_list_draws_nothing_outside_its_bounds()
    {
        using var layer = Loading();

        AssertInside(layer);
    }

    [Fact]
    public void The_stats_line_draws_nothing_outside_its_bounds()
    {
        using var layer = Stats();

        Assert.NotEmpty(layer.Line);
        AssertInside(layer);
    }

    private LoadingLayer Loading()
    {
        var loading = new LoadingScreen(_options, NullLogger<LoadingScreen>.Instance);
        loading.Initialize([new Task("Sounds"), new Task("A task with a rather long name to show"), new Task("World")]);
        loading.Begin(0);
        return new LoadingLayer(loading, _fonts);
    }

    private StatsLayer Stats()
    {
        var options = new EngineOptions();
        options.Ui.Stats = true;
        var window = new WindowService(Options.Create(options), NullLogger<WindowService>.Instance);
        var input = new InputService(window, NullLogger<InputService>.Instance);
        var layer = new StatsLayer(
            Options.Create(options),
            _fonts,
            new GpuTimings(),
            new FrameStats(0, NullLogger<FrameStats>.Instance),
            new MatchControl(),
            input
        );
        for (var frame = 0UL; frame < 90; frame++)
        {
            layer.Update(new FrameTime(1 / 60f, frame / 60.0, frame, 0));
        }

        return layer;
    }

    /// <summary>The layer's bounds on a <paramref name="width"/> x <paramref name="height"/> screen.</summary>
    private static SKRectI Size(IUiLayer layer, int width, int height)
    {
        layer.Update(new FrameTime(1 / 60f, 2, 120, 0), width, height);
        return layer.Bounds(width, height);
    }

    /// <summary>The pixel rows with bright ink in the console on a <paramref name="width"/> x <paramref name="height"/> screen.</summary>
    private static int InkRows(ConsoleLayer layer, int width, int height)
    {
        layer.Update(new FrameTime(1 / 60f, 2, 120, 0), width, height);
        using var picture = LayerPicture.Render(layer, width, height);
        var rows = 0;
        var bounds = layer.Bounds(width, height);
        for (var y = bounds.Top; y < bounds.Bottom - 4; y++)
        {
            var ink = false;
            for (var x = 0; x < width && !ink; x++)
            {
                var pixel = picture.GetPixel(x, y);
                ink = pixel.Red > 150 && pixel.Green > 150;
            }

            rows += ink ? 1 : 0;
        }

        return rows;
    }

    private static void AssertInside(IUiLayer layer)
    {
        foreach (var (width, height) in s_sizes)
        {
            layer.Update(new FrameTime(1 / 60f, 2, 120, 0), width, height);
            Assert.True(layer.Visible);
            var (outside, drawn) = LayerInk.Outside(layer, width, height);
            Assert.True(outside == 0, $"{outside} pixels outside {layer.Bounds(width, height)} at {width}x{height}");
            Assert.True(drawn > 0);
        }
    }

    private sealed class Task(string name) : ILoadingTask
    {
        public string Name => name;

        public LoadingProgress Progress => LoadingProgress.Waiting;
    }
}
