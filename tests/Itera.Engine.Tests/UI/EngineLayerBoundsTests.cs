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

namespace Itera.Engine.Tests.UI;

/// <summary>
/// The engine's layers with bounds smaller than the screen, the console, the loading list, and the stats line, draw
/// nothing outside them (<see cref="UISurface"/> clips each layer to its bounds and
/// redraws only the area that changed, so ink past them would be cut off), at three screen sizes.
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
        using var layer = new ConsoleLayer(console, _fonts);

        AssertInside(layer);
    }

    [Fact]
    public void The_loading_list_draws_nothing_outside_its_bounds()
    {
        var loading = new LoadingScreen(_options, NullLogger<LoadingScreen>.Instance);
        loading.Initialize([new Task("Sounds"), new Task("A task with a rather long name to show"), new Task("World")]);
        loading.Begin(0);
        using var layer = new LoadingLayer(loading, _fonts);

        AssertInside(layer);
    }

    [Fact]
    public void The_stats_line_draws_nothing_outside_its_bounds()
    {
        var options = new EngineOptions();
        options.Ui.Stats = true;
        var window = new WindowService(Options.Create(options), NullLogger<WindowService>.Instance);
        var input = new InputService(window, NullLogger<InputService>.Instance);
        using var layer = new StatsLayer(
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

        Assert.NotEmpty(layer.Line);
        AssertInside(layer);
    }

    private static void AssertInside(IUiLayer layer)
    {
        foreach (var (width, height) in s_sizes)
        {
            layer.Update(new FrameTime(1 / 60f, 2, 120, 0));
            Assert.True(layer.Visible);
            var (outside, drawn) = LayerInk.Outside(layer, width, height);
            Assert.Equal(0, outside);
            Assert.True(drawn > 0);
        }
    }

    private sealed class Task(string name) : ILoadingTask
    {
        public string Name => name;

        public LoadingProgress Progress => LoadingProgress.Waiting;
    }
}
