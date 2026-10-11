using System.Numerics;
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
            default,
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
                    default,
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
            default,
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

    /// <summary>
    /// <c>look</c> lists the look's values, sets a number or a colour over every frame's look for the run (starred), and
    /// gives the game's back with reset; a wrong name or count of numbers changes nothing.
    /// </summary>
    [Fact]
    public void Look_sets_values_over_every_frames_look_and_reset_gives_the_games_back()
    {
        Run("look");
        Assert.Equal("LightsAbove 0.6", Last());

        Run("look exposure 0.9; look SunWarmth 1 0.5 0.25");
        Assert.Equal("SunWarmth 1 0.5 0.25 *", Last());
        _view.SetLook(RenderLook.Default with { Exposure = 0.5f, GlowStrength = 2 });
        Assert.Equal(0.9f, _view.DrawnLook.Exposure);
        Assert.Equal(new System.Numerics.Vector3(1, 0.5f, 0.25f), _view.DrawnLook.SunWarmth);
        Assert.Equal(2f, _view.DrawnLook.GlowStrength);

        Run("look Fog 1");
        Assert.Equal("look: no value Fog (look lists them).", Last());
        Run("look Exposure 1 2");
        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        Run("look SunWarmth 1");
        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        Run("look exposure nan");
        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        Assert.Equal(0.9f, _view.DrawnLook.Exposure);

        Run("look reset");
        Assert.Equal(0.5f, _view.DrawnLook.Exposure);
        Run("look exposure");
        Assert.Equal("Exposure 0.5", Last());
    }

    /// <summary>
    /// <c>sun</c> and <c>time</c> set the run's sun in place of the game's (at night, said so), and off gives the game's
    /// back; angles or a time out of range change nothing.
    /// </summary>
    [Fact]
    public void Sun_and_time_set_the_runs_sun_and_off_gives_the_games_back()
    {
        Run("sun");
        Assert.Equal("sun azimuth 126.9 elevation 58 (the game's)", Last());

        Run("sun 90 30");
        Assert.Equal("sun azimuth 90 elevation 30 (set for this run)", Last());
        Assert.Equal(SunPath.FromAngles(90, 30), _view.DrawnSun);

        Run("time 22:30");
        Assert.EndsWith("(set for this run, night)", Last(), StringComparison.Ordinal);
        Assert.Equal(SunPath.AtHour(22.5f), _view.DrawnSun);

        foreach (
            var line in (string[])["sun 1 100", "sun nan 4", "sun 1", "time 25:00", "time 24:30", "time 7", "time 7:60"]
        )
        {
            Run(line);
            Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        }

        Assert.Equal(SunPath.AtHour(22.5f), _view.DrawnSun);
        Run("time off");
        Assert.Null(_view.SunOverride);
        Assert.Equal(ViewConstants.DefaultSunDirection, _view.DrawnSun);
    }

    /// <summary>
    /// <c>lens ortho</c> draws the run's camera through an orthographic lens in place of its own (the view distance each way
    /// unless given), and off gives the camera's own back; a size or depths that cannot be drawn change nothing.
    /// </summary>
    [Fact]
    public void Lens_sets_an_orthographic_lens_for_the_run_and_off_gives_the_cameras_own_back()
    {
        var camera = CameraView.Default with
        {
            Position = new Vector3(3, 40, -7),
            Orientation = Quaternion.CreateFromYawPitchRoll(0.3f, -1.2f, 0),
        };
        _view.SetCamera(in camera);
        Run("lens");
        Assert.Equal("lens perspective, 90 degrees high, near 0.05 m (the camera's)", Last());

        Run("lens ortho 64");
        Assert.Equal(
            "lens orthographic, 64 m high, from the view distance behind to the view distance ahead along the view (set for this run)",
            Last()
        );
        Assert.True(_view.DrawnCamera.Orthographic);
        Assert.Equal(64, _view.DrawnCamera.HeightMetres);
        Assert.Equal(camera.Position, _view.DrawnCamera.Position);
        Assert.Equal(camera.Orientation, _view.DrawnCamera.Orientation);

        Run("lens ortho 120 -50 75.5");
        Assert.Equal("lens orthographic, 120 m high, from -50 m to 75.5 m along the view (set for this run)", Last());
        Assert.Equal((-50f, 75.5f), (_view.DrawnCamera.NearMetres, _view.DrawnCamera.FarMetres));

        foreach (
            var line in (string[])
                [
                    "lens ortho",
                    "lens ortho 0",
                    "lens ortho -3",
                    "lens ortho nan",
                    "lens ortho 10 5 5",
                    "lens ortho 10 1",
                    "lens wide",
                ]
        )
        {
            Run(line);
            Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        }

        Assert.Equal(120, _view.DrawnCamera.HeightMetres);
        Run("lens off");
        Assert.Equal("lens perspective, 90 degrees high, near 0.05 m (the camera's)", Last());
        Assert.Null(_view.LensOverride);
        Assert.Equal(camera, _view.DrawnCamera);
        Assert.False(_view.DrawnCamera.Orthographic);
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
        Assert.Equal(0, _window.ExitCode);
    }

    [Fact]
    public void Quit_takes_an_exit_code_from_0_to_255()
    {
        Run("quit 256");
        Assert.False(_window.ExitRequested);

        Run("quit 3");

        Assert.True(_window.ExitRequested);
        Assert.Equal(3, _window.ExitCode);
    }

    [Fact]
    public void Wait_ticks_holds_the_rest_of_the_line_until_the_steps_have_run()
    {
        Run("wait ticks 2; lines on", new ConsoleMoment(10, 0, 0, false));
        Assert.False(_lines.Enabled);

        Step(new ConsoleMoment(11, 5, 1, false));
        Assert.False(_lines.Enabled);
        Step(new ConsoleMoment(12, 5, 1, false));

        Assert.True(_lines.Enabled);
    }

    [Fact]
    public void Wait_frames_seconds_and_loaded_hold_until_theirs_has_come()
    {
        Run("wait frames 3; lines on; wait seconds 1.5; stats on; wait loaded; shadows off", default);
        Step(new ConsoleMoment(0, 2, 0, false));
        Assert.False(_lines.Enabled);
        Step(new ConsoleMoment(0, 3, 0, false));
        Assert.True(_lines.Enabled);
        Assert.False(_stats.Visible);

        Step(new ConsoleMoment(0, 3, 1.5, false));
        Assert.True(_stats.Visible);
        Assert.True(_view.Shadows);

        Step(new ConsoleMoment(0, 3, 1.6, true));
        Assert.False(_view.Shadows);
    }

    [Fact]
    public void A_wait_past_its_timeout_fails_and_drops_the_rest_of_the_line()
    {
        Run("wait loaded 5; lines on", new ConsoleMoment(0, 0, 10, false));
        Step(new ConsoleMoment(0, 0, 14, false));
        Assert.True(_console.ScriptsBusy);

        Step(new ConsoleMoment(0, 0, 15, false));
        Step(new ConsoleMoment(0, 0, 16, true));

        Assert.False(_lines.Enabled);
        Assert.False(_console.ScriptsBusy);
        Assert.StartsWith("wait loaded: not done after 5 s", Last(), StringComparison.Ordinal);
        // A typed line's failure is not the startup script's.
        Assert.False(_console.StartupScriptFailed);
    }

    [Theory]
    [InlineData("wait")]
    [InlineData("wait ticks")]
    [InlineData("wait loaded abc")]
    [InlineData("wait loaded 0")]
    [InlineData("wait loaded 4000")]
    [InlineData("wait ticks -1")]
    [InlineData("wait ticks 2000000")]
    [InlineData("wait hours 2")]
    [InlineData("wait seconds 1 2 3")]
    public void Wait_with_wrong_words_prints_its_usage_and_holds_nothing(string line)
    {
        Run(line + "; lines on", default);

        Assert.True(_lines.Enabled);
        Assert.Contains(
            Enumerable.Range(0, _console.ScrollbackCount),
            i => _console.ScrollbackLine(i).Text.StartsWith("usage: wait", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Screenshot_takes_a_path_only_when_it_ends_in_png()
    {
        Run("screenshot noiu");

        Assert.Null(_screenshots.TakeForFrame());
        Assert.StartsWith("usage: screenshot", Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Wait_through_the_control_port_holds_nothing_and_says_so()
    {
        var output = new RecordingOutput();

        _console.RunNow("wait ticks 5; lines on", output);

        Assert.True(_lines.Enabled);
        Assert.Single(output.Failures);
        Assert.StartsWith("wait: holds the commands after it", output.Failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_screenshot_to_a_path_holds_the_rest_of_the_line_until_its_png_is_written()
    {
        var path = Path.Combine(Path.GetTempPath(), "itera-test-screenshots", "shot.png");

        Run("screenshot 0.5 noui " + path + "; lines on", default);
        var request = _screenshots.TakeForFrame();
        Step(new ConsoleMoment(0, 1, 0.1, true));

        Assert.NotNull(request);
        Assert.Equal(Path.GetFullPath(path), request.Path);
        Assert.Equal(0.5f, request.Scale);
        Assert.False(request.WithUi);
        Assert.False(_lines.Enabled);
        request.Succeed(64, 36, 128, 72);
        Step(new ConsoleMoment(0, 2, 0.2, true));
        Assert.True(_lines.Enabled);
    }

    [Fact]
    public void A_screenshot_that_no_frame_takes_is_withdrawn_when_its_hold_runs_out()
    {
        Run("screenshot; lines on", default);
        Step(new ConsoleMoment(0, 0, EngineCommands.ScreenshotHoldSeconds + 1, true));

        Assert.False(_lines.Enabled);
        Assert.Null(_screenshots.TakeForFrame());
    }

    private void Run(string line)
    {
        _console.Submit(line);
        _console.RunPending();
    }

    private void Run(string line, ConsoleMoment now)
    {
        _console.Submit(line);
        _console.RunPending(in now);
    }

    private void Step(ConsoleMoment now) => _console.RunPending(in now);

    /// <summary>The control port's side of a reply: answers and failures.</summary>
    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Failures { get; } = [];

        public void Print(string text) { }

        public void PrintColumns(string left, string right, int column, int indent) { }

        public void PrintError(string text) => Failures.Add(text);
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
