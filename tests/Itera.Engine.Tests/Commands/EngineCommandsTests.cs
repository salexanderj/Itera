using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Input;
using Itera.Engine.Networking;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Settings;
using Itera.Engine.UI;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Commands;

public sealed class EngineCommandsTests : IDisposable
{
    private readonly ConsoleCommands _commands = new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
    private readonly DevConsole _console;
    private readonly DebugLines _lines = new(NullLogger<DebugLines>.Instance);
    private readonly RenderView _view;
    private readonly DynamicRenderScale _dynamicScale;
    private readonly PlayerSettings _settings;
    private readonly UiFonts _fonts;
    private readonly StatsLayer _stats;
    private readonly WindowService _window;

    // Not initialised: no device, so it draws no wireframe.
    private readonly VulkanContext _vulkan = new(
        Options.Create(new EngineOptions()),
        NullLogger<VulkanContext>.Instance
    );
    private readonly InputService _input;
    private readonly Screenshots _screenshots = new(Path.Combine(Path.GetTempPath(), "itera-test-screenshots"));

    public EngineCommandsTests()
    {
        var options = Options.Create(new EngineOptions());
        _console = new DevConsole(options, _commands, new ConsoleLogBuffer(), NullLogger<DevConsole>.Instance);
        _view = new RenderView(options);
        _dynamicScale = new DynamicRenderScale(_view, new GpuTimings(), null, NullLogger<DynamicRenderScale>.Instance);
        _settings = new PlayerSettings(
            options.Value,
            CommandLineKeys.None,
            null,
            NullLogger<PlayerSettings>.Instance,
            view: _view
        );
        _fonts = new UiFonts(options, NullLogger<UiFonts>.Instance);
        _window = new WindowService(options, NullLogger<WindowService>.Instance);
        _input = new InputService(_window, NullLogger<InputService>.Instance);
        // The stats line reads the session only when its numbers change, which these tests never wait for.
        _stats = new StatsLayer(
            options,
            _fonts,
            new GpuTimings(),
            new FrameStats(0, NullLogger<FrameStats>.Instance),
            null!,
            _input
        );
        new EngineCommands(
            _commands,
            _console,
            _lines,
            _view,
            _stats,
            _window,
            _screenshots,
            new ShaderReloads(new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, Path.GetTempPath())),
            _settings,
            _dynamicScale,
            _vulkan
        ).Register();
    }

    public void Dispose()
    {
        _vulkan.Dispose();
        _stats.Dispose();
        _fonts.Dispose();
        _input.Dispose();
        _window.Dispose();
    }

    [Fact]
    public void The_toggles_set_and_print_their_settings()
    {
        Run("lines on; shadows off; stats on");

        Assert.True(_lines.Enabled);
        Assert.False(_view.Shadows);
        Assert.True(_stats.Shown);
        Assert.Equal("stats on", Last());
    }

    /// <summary>
    /// The console's render settings are the player's, so another setting's change keeps them
    /// (it put the file's back). The scale is the run's alone, and stays too.
    /// </summary>
    [Fact]
    public void Render_settings_from_the_console_are_the_players_and_stay_after_another_change()
    {
        Run("shadows off; scale 0.75");

        _settings.SetVolume(0.3f);
        _dynamicScale.Update(1);

        Assert.False(_view.Shadows);
        Assert.Equal(0.75f, _view.RenderScale);
        Assert.False(_settings.Shadows);
    }

    /// <summary>
    /// <c>view</c> sets the run's debug views, which the next frame's view constants carry; without a device that draws
    /// lines, a wire is refused and the levels kept.
    /// </summary>
    [Fact]
    public void View_sets_the_runs_debug_views_and_refuses_a_wire_a_device_cannot_draw()
    {
        Run("view levels");
        Assert.Equal(ViewSwitches.Levels, _view.DebugViews);
        Assert.Equal("view levels", Last());
        var camera = CameraView.Default;
        var atmosphere = _view.Atmosphere;
        var constants = ViewConstants.Create(
            in camera,
            640,
            480,
            0,
            ViewConstants.DefaultSunDirection,
            in atmosphere,
            100,
            shadows: false,
            debugViews: _view.DebugViews
        );
        Assert.True(constants.Has(ViewSwitches.Levels));

        Run("view all");
        Assert.Equal(ViewSwitches.Levels, _view.DebugViews);
        Assert.Equal("view levels", Last());

        Run("view plain");
        Assert.Equal(ViewSwitches.Plain, _view.DebugViews);
        Assert.Equal("view plain", Last());
        Assert.True(
            ViewConstants
                .Create(
                    in camera,
                    640,
                    480,
                    0,
                    ViewConstants.DefaultSunDirection,
                    in atmosphere,
                    100,
                    shadows: false,
                    debugViews: _view.DebugViews
                )
                .Has(ViewSwitches.Plain)
        );

        Run("view off");
        Assert.Equal(ViewSwitches.None, _view.DebugViews);
        Run("view sideways");
        Assert.Equal(ViewSwitches.None, _view.DebugViews);
    }

    /// <summary>
    /// <c>materials</c> sets how the world's materials are drawn for the run, which the view constants carry and
    /// <c>view</c> keeps; a method it does not know changes nothing.
    /// </summary>
    [Fact]
    public void Materials_sets_how_materials_are_drawn_for_the_run_and_view_keeps_it()
    {
        Run("materials flat");
        Assert.Equal(ViewSwitches.MaterialsFlat, _view.DebugViews);
        Assert.Equal("materials flat where a set is named", Last());

        Run("materials textures all");
        Assert.Equal(ViewSwitches.MaterialsAll, _view.DebugViews);
        Assert.Equal("materials textures on every material", Last());
        Run("view levels");
        Assert.Equal(ViewSwitches.MaterialsAll | ViewSwitches.Levels, _view.DebugViews);
        var camera = CameraView.Default;
        var atmosphere = _view.Atmosphere;
        var constants = ViewConstants.Create(
            in camera,
            640,
            480,
            0,
            ViewConstants.DefaultSunDirection,
            in atmosphere,
            100,
            shadows: false,
            debugViews: _view.DebugViews
        );
        Assert.True(constants.Has(ViewSwitches.MaterialsAll | ViewSwitches.Levels));

        Run("materials glossy");
        Run("materials recipes");
        Run("materials bake 4 sideways");
        Run("materials flat some");
        Assert.Equal(ViewSwitches.MaterialsAll | ViewSwitches.Levels, _view.DebugViews);
        Run("view off");
        Run("materials textures");
        Assert.Equal(ViewSwitches.None, _view.DebugViews);
        Assert.Equal("materials textures where a set is named", Last());

        // Without the world's meshes, sets, set, and bake report it and change nothing.
        Run("materials set 1 grass");
        Assert.Equal(ViewSwitches.None, _view.DebugViews);
        Assert.Contains("not running", Last(), StringComparison.Ordinal);
        Run("materials bake 4 side");
        Assert.Contains("not running", Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Aa_sets_anti_aliasing_for_the_run_shows_its_steps_and_auto_gives_it_back()
    {
        var version = _settings.Version;
        Assert.True(_view.AntiAliasDrawn);

        Run("aa off");
        Assert.False(_view.AntiAliasDrawn);
        Assert.True(_view.AntiAliasing);
        Assert.Equal("aa off (set for this run)", Last());

        Run("aa edges");
        Assert.True(_view.AntiAliasDrawn);
        Assert.Equal(SmaaPicture.Edges, _view.AntiAliasPicture);
        Assert.Equal("aa on, showing its edges (set for this run)", Last());

        Run("aa weights");
        Assert.Equal(SmaaPicture.Weights, _view.AntiAliasPicture);

        // A change of the setting meanwhile does not end the run's choice.
        _settings.SetAntiAliasing(false);
        Run("aa on");
        Assert.True(_view.AntiAliasDrawn);
        Assert.Equal(SmaaPicture.Blended, _view.AntiAliasPicture);

        Run("aa auto");
        Assert.Null(_view.AntiAliasOverride);
        Assert.False(_view.AntiAliasDrawn);
        Assert.Equal("aa off (the settings')", Last());
        Assert.Equal(version + 1, _settings.Version);

        Run("aa sideways");
        Assert.Null(_view.AntiAliasOverride);
    }

    [Fact]
    public void Scale_sets_a_scale_for_the_run_and_auto_gives_it_back()
    {
        var version = _settings.Version;
        Run("scale 0.6");
        Assert.Equal(0.6f, _view.RenderScale);
        Assert.Equal(0.6f, _view.ScaleOverride);
        Assert.Equal("scale 0.6 (set for this run)", Last());

        Run("scale auto");
        _dynamicScale.Update(1);
        Assert.Null(_view.ScaleOverride);
        // The GPU measures nothing here, so the scale is the options' fixed one.
        Assert.Equal(1f, _view.RenderScale);
        Assert.Equal(version, _settings.Version);

        Run("scale 2");
        Assert.Null(_view.ScaleOverride);
    }

    [Fact]
    public void F1_shows_and_hides_the_stats_line_and_F6_no_longer_does()
    {
        Press(Key.F6);
        var afterF6 = _stats.Shown;
        Press(Key.F1);
        var shown = _stats.Shown;
        Press(Key.F1);

        Assert.False(afterF6);
        Assert.True(shown);
        Assert.False(_stats.Shown);
    }

    [Fact]
    public void A_setting_without_a_value_prints_it()
    {
        Run("shadows");

        Assert.Equal("shadows on", Last());
    }

    [Fact]
    public void Scale_sets_the_render_scale()
    {
        Run("scale 0.75");
        Assert.Equal(0.75f, _view.RenderScale);
        Assert.Equal("scale 0.75 (set for this run)", Last());

        Run("scale 0.4");
        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        Assert.Equal(0.75f, _view.RenderScale);
    }

    [Theory]
    [InlineData("shadows maybe")]
    [InlineData("scale 1.5")]
    [InlineData("scale nan")]
    [InlineData("lines maybe")]
    public void A_bad_value_prints_the_usage_and_changes_nothing(string line)
    {
        Run(line);

        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        Assert.True(_view.Shadows);
        Assert.False(_lines.Enabled);
    }

    [Fact]
    public void Help_lists_every_command_and_describes_one()
    {
        _commands.Register(
            "long",
            "[one|two|three|four|five|six|seven|eight]",
            "A usage wider than help's first column.",
            CommandScope.Local,
            _ => { }
        );

        Run("help");
        Assert.Equal(_commands.All.Count(), _console.ScrollbackCount);

        // Two columns: the name and usage, at most MostUsageWidth wide (a longer usage wraps within it, under its first
        // argument), and the help.
        var widest = _commands.All.Max(c => (c.Name + " " + c.Usage).Length);
        Assert.True(widest > EngineCommands.MostUsageWidth);
        var row = 0;
        foreach (var command in _commands.All)
        {
            var line = _console.ScrollbackLine(row++);
            Assert.Equal((command.Name + " " + command.Usage).TrimEnd(), line.Text);
            Assert.Equal(command.Help, line.Right);
            Assert.Equal(EngineCommands.MostUsageWidth + 2, line.Column);
            Assert.Equal(command.Name.Length + 1, line.Indent);
        }

        Run("help lines");
        var described = _console.ScrollbackLine(_console.ScrollbackCount - 1);
        Assert.Equal("", described.Text);
        Assert.StartsWith("Debug lines", described.Right, StringComparison.Ordinal);
        Assert.Equal(2, described.Column);
    }

    [Fact]
    public void Quit_asks_the_window_to_close()
    {
        Run("quit");

        Assert.True(_window.ExitRequested);
    }

    private void Run(string line)
    {
        _console.Submit(line);
        _console.RunPending();
    }

    private string Last() => _console.ScrollbackLine(_console.ScrollbackCount - 1).Text;

    /// <summary>Presses and lets go of <paramref name="key"/>, then runs a frame of the stats line.</summary>
    private void Press(Key key)
    {
        _input.InjectKey(key, true);
        _input.InjectKey(key, false);
        _input.BeginFrame();
        _stats.Update(new FrameTime(1f / 60, 0, 0, 0));
    }
}
