using System.Text.Json;
using Itera.Engine.Audio;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Settings;

/// <summary>
/// <see cref="PlayerSettings"/> in a temporary folder: the options' values without a file; the file's over them, with
/// anything else in it, values of the wrong kind, and values out of range left out or clamped; a key the command line
/// gave winning for the run and keeping the file's value when it is written; each change reaching its service (the
/// volume, the render scale, shadows, smoothing, anti-aliasing, texture filtering, VSync's swapchain); a changed
/// keybinding written alone and read back; the file read at <see cref="PlayerSettings.Initialize"/>; keys this build does
/// not know written back; a byte order mark read; a failed write keeping the old file and tried again; the screen full
/// screen covers, read, written, and kept to a name; and the command line's keys read from its arguments.
/// </summary>
public sealed class PlayerSettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("itera-settings-").FullName;

    private string FilePath => Path.Combine(_folder, PlayerSettings.FileName);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Without_a_file_the_options_values_hold()
    {
        var options = new EngineOptions();
        options.Audio.Volume = 0.25f;
        options.Window.VSync = false;

        var settings = Create(options);

        Assert.Equal(0.25f, settings.Volume);
        Assert.False(settings.VSync);
        Assert.Equal(90, settings.FieldOfViewDegrees);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void The_files_values_replace_the_options_and_anything_else_in_it_is_left_out()
    {
        File.WriteAllText(
            FilePath,
            """{ "Volume": 0.6, "MouseSensitivity": 9, "FieldOfViewDegrees": "wide", "DynamicRenderScaling": false, "DynamicScalingThreshold": 1000, "Shadows": false, "SmoothNormals": 1, "AntiAliasing": false, "TextureFiltering": false, "VSync": false, "Fullscreen": true, "Colour": "red" }"""
        );

        var settings = Create(new EngineOptions());

        Assert.Equal(0.6f, settings.Volume, 3);
        // Past the menu's range: the most it offers.
        Assert.Equal(PlayerSettings.MostSensitivity, settings.MouseSensitivity);
        // Not a number: the options' value.
        Assert.Equal(90, settings.FieldOfViewDegrees);
        Assert.False(settings.DynamicScaling);
        Assert.Equal(DynamicRenderScale.MostThreshold, settings.ScalingThreshold);
        Assert.False(settings.Shadows);
        Assert.False(settings.AntiAliasing);
        Assert.False(settings.VSync);
        Assert.True(settings.Fullscreen);
    }

    [Fact]
    public void A_file_that_is_not_json_leaves_the_options_values()
    {
        File.WriteAllText(FilePath, "volume = loud");

        var settings = Create(new EngineOptions());

        Assert.Equal(0.1f, settings.Volume, 3);
    }

    [Fact]
    public void A_key_the_command_line_gave_wins_for_the_run_and_the_file_keeps_its_value()
    {
        File.WriteAllText(FilePath, """{ "Volume": 0.6, "DynamicScalingThreshold": 144 }""");
        var options = new EngineOptions();
        options.Audio.Volume = 0.3f;

        var settings = Create(options, new CommandLineKeys(["--Engine:Audio:Volume=0.3", "--Engine:Voxels:Seed", "7"]));
        Assert.Equal(0.3f, settings.Volume, 3);
        Assert.Equal(144, settings.ScalingThreshold);

        settings.SetFieldOfView(100);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(0.6, written.RootElement.GetProperty("Volume").GetDouble(), 3);
        Assert.Equal(100, written.RootElement.GetProperty("FieldOfViewDegrees").GetDouble());
        Assert.Equal(144, written.RootElement.GetProperty("DynamicScalingThreshold").GetDouble());

        // Changed in the menu, it is the player's again, and written.
        settings.SetVolume(0.4f);
        settings.Save();
        using var again = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(0.4, again.RootElement.GetProperty("Volume").GetDouble(), 3);
    }

    [Fact]
    public void Anti_aliasing_from_the_command_line_wins_for_the_run_and_the_file_keeps_its_value()
    {
        File.WriteAllText(FilePath, """{ "AntiAliasing": false }""");

        var settings = Create(new EngineOptions(), new CommandLineKeys(["--Engine:Renderer:AntiAliasing=true"]));
        Assert.True(settings.AntiAliasing);

        settings.SetShadows(false);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.False(written.RootElement.GetProperty("AntiAliasing").GetBoolean());
    }

    [Fact]
    public void The_screen_is_the_options_then_the_files_and_a_change_is_written_and_read_back()
    {
        var options = new EngineOptions();
        options.Window.Screen = "HDMI-A-1";
        Assert.Equal("HDMI-A-1", Create(options).Screen);

        File.WriteAllText(FilePath, """{ "Screen": "DP-1" }""");
        var settings = Create(options);
        Assert.Equal("DP-1", settings.Screen);

        settings.SetScreen("");
        settings.Save();

        var next = Create(options);
        Assert.Equal("", next.Screen);
        Assert.Equal(PlayerSettings.MainScreenLabel, next.ScreenLabel);
    }

    [Fact]
    public void A_screen_name_that_is_not_text_or_is_too_long_changes_nothing()
    {
        var tooLong = new string('x', WindowSettings.MostScreenNameLength + 1);
        File.WriteAllText(FilePath, """{ "Screen": 3 }""");
        Assert.Equal("", Create(new EngineOptions()).Screen);

        File.WriteAllText(FilePath, $$"""{ "Screen": "{{tooLong}}" }""");
        var settings = Create(new EngineOptions());
        Assert.Equal("", settings.Screen);

        settings.SetScreen(tooLong);
        Assert.Equal("", settings.Screen);
    }

    [Fact]
    public void The_screen_from_the_command_line_wins_for_the_run_and_the_file_keeps_its_value()
    {
        File.WriteAllText(FilePath, """{ "Screen": "DP-1" }""");
        var options = new EngineOptions();
        options.Window.Screen = "HDMI-A-1";

        var settings = Create(options, new CommandLineKeys(["--Engine:Window:Screen=HDMI-A-1"]));
        Assert.Equal("HDMI-A-1", settings.Screen);

        settings.SetShadows(false);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal("DP-1", written.RootElement.GetProperty("Screen").GetString());
    }

    [Fact]
    public void Nothing_is_written_until_a_setting_changes_and_then_a_new_start_reads_it()
    {
        var settings = Create(new EngineOptions());
        settings.Save();
        Assert.False(File.Exists(FilePath));

        settings.SetMouseSensitivity(0.2f);
        settings.SetAntiAliasing(false);
        settings.SetVSync(false);
        settings.Save();

        var next = Create(new EngineOptions());
        Assert.Equal(0.2f, next.MouseSensitivity, 3);
        Assert.True(next.Shadows);
        Assert.False(next.AntiAliasing);
        Assert.False(next.VSync);
    }

    /// <summary>
    /// The settings' Anti-aliasing: the world's samples and SMAA's switch, read from the file (snapped to 1, 2, 4, or
    /// 8), written back, changed with an event for the render thread only when the samples change, and the command
    /// line's samples winning for the run.
    /// </summary>
    [Fact]
    public void The_anti_aliasing_mode_keeps_the_samples_and_the_smaa_switch()
    {
        File.WriteAllText(FilePath, """{ "MsaaSamples": 3, "AntiAliasing": true }""");
        var settings = Create(new EngineOptions());
        Assert.Equal(2, settings.MsaaSamples);
        Assert.Equal(AntiAliasMode.Msaa2, settings.AntiAliasMode);
        var changes = 0;
        settings.SamplesChanged += () => changes++;

        settings.SetAntiAliasMode(AntiAliasMode.Smaa);
        Assert.Equal((1, true, 1), (settings.MsaaSamples, settings.AntiAliasing, changes));
        settings.SetAntiAliasMode(AntiAliasMode.Off);
        Assert.Equal((1, false, 1), (settings.MsaaSamples, settings.AntiAliasing, changes));
        settings.SetAntiAliasMode(AntiAliasMode.Msaa8);
        Assert.Equal((8, false, 2), (settings.MsaaSamples, settings.AntiAliasing, changes));
        Assert.Equal(AntiAliasMode.Msaa8, settings.AntiAliasMode);
        settings.Save();

        Assert.Equal(AntiAliasMode.Msaa8, Create(new EngineOptions()).AntiAliasMode);
        File.WriteAllText(FilePath, """{ "MsaaSamples": 100 }""");
        Assert.Equal(8, Create(new EngineOptions()).MsaaSamples);
        File.WriteAllText(FilePath, """{ "MsaaSamples": "many" }""");
        Assert.Equal(4, Create(new EngineOptions()).MsaaSamples);
        File.WriteAllText(FilePath, """{ "MsaaSamples": 8 }""");
        var commandLine = new CommandLineKeys(["--Engine:Renderer:MsaaSamples=1"]);
        Assert.Equal(
            1,
            Create(new EngineOptions { Renderer = new RendererSettings { MsaaSamples = 1 } }, commandLine).MsaaSamples
        );
    }

    [Fact]
    public void Each_change_reaches_its_service_at_once()
    {
        var options = Options.Create(new EngineOptions());
        var audio = new AudioService(options, NullLogger<AudioService>.Instance);
        var view = new RenderView(options);
        var settings = new PlayerSettings(
            options.Value,
            CommandLineKeys.None,
            FilePath,
            NullLogger<PlayerSettings>.Instance,
            audio,
            view
        );
        var presentModes = 0;
        settings.PresentModeChanged += () => presentModes++;

        settings.SetVolume(0.7f);
        settings.SetDynamicScaling(false);
        settings.SetScalingThreshold(60);
        settings.SetShadows(false);
        settings.SetAntiAliasing(false);
        settings.SetVSync(false);
        settings.SetVSync(false);
        settings.SetFieldOfView(1000);

        Assert.Equal(0.7f, audio.Volume, 3);
        Assert.False(view.DynamicScaling);
        Assert.Equal(60, view.ScalingThreshold);
        Assert.False(view.Shadows);
        Assert.False(view.AntiAliasing);
        Assert.Equal(1, presentModes);
        Assert.Equal(PlayerSettings.MostFieldOfView, settings.FieldOfViewDegrees);
        Assert.Equal(settings.MouseSensitivity * MathF.PI / 180, settings.RadiansPerPixel, 6);
    }

    [Fact]
    public void A_files_retired_settings_are_ignored_and_dropped_at_the_next_save()
    {
        var options = Options.Create(new EngineOptions());
        var view = new RenderView(options);
        File.WriteAllText(
            FilePath,
            """{ "FrameBlend": false, "RenderScale": 0.6, "SmoothNormals": false, "TextureFiltering": false, "Colour": "red" }"""
        );
        var settings = new PlayerSettings(
            options.Value,
            CommandLineKeys.None,
            FilePath,
            NullLogger<PlayerSettings>.Instance,
            view: view
        );
        settings.Initialize();
        settings.Apply();

        // Frame blending has no setting since 2026-10-05, and dynamic render scaling took the render scale's place the
        // same day; smooth normals and texture filtering left with the raymarcher on 2026-10-06.
        Assert.Equal(1f, view.FixedRenderScale);
        Assert.True(view.DynamicScaling);

        settings.SetShadows(false);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.False(written.RootElement.TryGetProperty("FrameBlend", out _));
        Assert.False(written.RootElement.TryGetProperty("RenderScale", out _));
        Assert.False(written.RootElement.TryGetProperty("SmoothNormals", out _));
        Assert.False(written.RootElement.TryGetProperty("TextureFiltering", out _));
        Assert.True(written.RootElement.GetProperty("DynamicRenderScaling").GetBoolean());
        Assert.Equal(60, written.RootElement.GetProperty("DynamicScalingThreshold").GetDouble());
        Assert.Equal("red", written.RootElement.GetProperty("Colour").GetString());
    }

    [Fact]
    public void A_changed_keybinding_is_written_alone_and_read_back_by_a_new_start()
    {
        var settings = Create(new EngineOptions());
        var jump = settings.Keys.Register("jump", "Jump", "", InputBinding.Of(Key.Space));
        settings.Keys.Register("crouch", "Crouch", "", InputBinding.Of(Key.C));
        settings.Save();
        Assert.False(File.Exists(FilePath));

        settings.Keys.Set(jump, InputBinding.Of(MouseButton.Right));
        settings.Save();

        using (var written = JsonDocument.Parse(File.ReadAllText(FilePath)))
        {
            var keys = written.RootElement.GetProperty("Keys");
            Assert.Equal("MouseRight", keys.GetProperty("jump").GetString());
            Assert.False(keys.TryGetProperty("crouch", out _));
        }

        var next = Create(new EngineOptions());
        var again = next.Keys.Register("jump", "Jump", "", InputBinding.Of(Key.Space));
        Assert.Equal(InputBinding.Of(MouseButton.Right), next.Keys[again]);
    }

    /// <summary>The file is read in the run's step before the window, not when the settings are made.</summary>
    [Fact]
    public void The_file_is_read_at_initialize_not_when_made()
    {
        File.WriteAllText(FilePath, """{ "Volume": 0.6 }""");
        var settings = new PlayerSettings(
            new EngineOptions(),
            CommandLineKeys.None,
            FilePath,
            NullLogger<PlayerSettings>.Instance
        );
        Assert.Equal(0.1f, settings.Volume, 3);

        settings.Initialize();

        Assert.Equal(0.6f, settings.Volume, 3);
    }

    /// <summary>What the file holds that this build does not know is written back.</summary>
    [Fact]
    public void Keys_this_build_does_not_know_are_written_back()
    {
        File.WriteAllText(FilePath, """{ "Colour": "red", "Future": { "Depth": [1, 2] }, "Volume": 0.6 }""");
        var settings = Create(new EngineOptions());

        settings.SetVolume(0.3f);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal("red", written.RootElement.GetProperty("Colour").GetString());
        Assert.Equal(2, written.RootElement.GetProperty("Future").GetProperty("Depth")[1].GetInt32());
        Assert.Equal(0.3, written.RootElement.GetProperty("Volume").GetDouble(), 3);
    }

    /// <summary>A file an editor wrote with a UTF-8 byte order mark is read.</summary>
    [Fact]
    public void A_file_with_a_byte_order_mark_is_read()
    {
        File.WriteAllText(
            FilePath,
            """{ "Volume": 0.6 }""",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
        );
        Assert.Equal(0xEF, File.ReadAllBytes(FilePath)[0]);

        var settings = Create(new EngineOptions());

        Assert.Equal(0.6f, settings.Volume, 3);
    }

    /// <summary>
    /// A write that fails leaves the old file whole, and the next save writes the change (the
    /// file was written in place, and a failed write was never tried again).
    /// </summary>
    [Fact]
    public void A_failed_write_keeps_the_old_file_and_the_next_save_writes_the_change()
    {
        File.WriteAllText(FilePath, """{ "Volume": 0.6 }""");
        var settings = Create(new EngineOptions());
        settings.SetVolume(0.3f);
        // The file written beside it cannot be made: a folder has its name.
        Directory.CreateDirectory(FilePath + ".tmp");

        settings.Save();
        Assert.Equal("""{ "Volume": 0.6 }""", File.ReadAllText(FilePath));

        Directory.Delete(FilePath + ".tmp");
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(0.3, written.RootElement.GetProperty("Volume").GetDouble(), 3);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void The_command_lines_keys_are_read_from_its_arguments()
    {
        var keys = new CommandLineKeys([
            "--Engine:Window:VSync=false",
            "/Engine:Audio:Volume",
            "0.2",
            "+connect_lobby",
            // A bare key and value, as .NET's configuration reads it.
            "Engine:Renderer:Shadows=false",
            // A value after its key is not a key, whatever it holds.
            "--Engine:Console:Run",
            "Engine:Camera:FieldOfViewDegrees=100",
        ]);

        Assert.True(keys.Contains("engine:window:vsync"));
        Assert.True(keys.Contains("Engine:Audio:Volume"));
        Assert.True(keys.Contains("Engine:Renderer:Shadows"));
        Assert.True(keys.Contains("Engine:Console:Run"));
        Assert.False(keys.Contains("Engine:Camera:FieldOfViewDegrees"));
        Assert.False(keys.Contains("0.2"));
        Assert.False(CommandLineKeys.None.Contains("Engine:Audio:Volume"));
    }

    [Fact]
    public void A_game_switch_is_its_default_when_the_file_has_none()
    {
        var settings = Create(new EngineOptions());

        var on = settings.RegisterSwitch("Minimap", defaultValue: true);
        var off = settings.RegisterSwitch("Compass", defaultValue: false);

        Assert.True(settings.IsOn(on));
        Assert.False(settings.IsOn(off));
        Assert.Equal("Minimap", settings.SwitchName(on));
        Assert.Equal("Compass", settings.SwitchName(off));
        settings.Save();
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_game_switch_is_read_from_the_file_and_a_value_that_is_not_a_boolean_is_ignored()
    {
        File.WriteAllText(FilePath, """{ "Game": { "Minimap": false, "Compass": "yes", "Grid": 1 } }""");
        var settings = Create(new EngineOptions());

        var minimap = settings.RegisterSwitch("Minimap", defaultValue: true);
        var compass = settings.RegisterSwitch("Compass", defaultValue: true);
        var grid = settings.RegisterSwitch("Grid", defaultValue: false);
        var other = settings.RegisterSwitch("Other", defaultValue: true);

        Assert.False(settings.IsOn(minimap));
        Assert.True(settings.IsOn(compass));
        Assert.False(settings.IsOn(grid));
        Assert.True(settings.IsOn(other));
    }

    [Fact]
    public void A_game_section_that_is_not_an_object_is_ignored_and_kept_until_a_switch_replaces_it()
    {
        File.WriteAllText(FilePath, """{ "Game": 5 }""");
        var settings = Create(new EngineOptions());
        settings.SetVolume(0.3f);
        settings.Save();
        using (var kept = JsonDocument.Parse(File.ReadAllText(FilePath)))
        {
            Assert.Equal(5, kept.RootElement.GetProperty("Game").GetInt32());
        }

        var minimap = settings.RegisterSwitch("Minimap", defaultValue: true);
        Assert.True(settings.IsOn(minimap));
        settings.SetSwitch(minimap, false);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.False(written.RootElement.GetProperty("Game").GetProperty("Minimap").GetBoolean());
    }

    [Fact]
    public void A_changed_game_switch_is_written_and_read_back_by_a_new_start()
    {
        var settings = Create(new EngineOptions());
        var minimap = settings.RegisterSwitch("Minimap", defaultValue: true);
        var version = settings.Version;

        settings.SetSwitch(minimap, true);
        settings.Save();
        Assert.False(File.Exists(FilePath));
        Assert.Equal(version, settings.Version);

        settings.SetSwitch(minimap, false);
        settings.Save();
        Assert.NotEqual(version, settings.Version);

        using (var written = JsonDocument.Parse(File.ReadAllText(FilePath)))
        {
            Assert.False(written.RootElement.GetProperty("Game").GetProperty("Minimap").GetBoolean());
        }

        var next = Create(new EngineOptions());
        var again = next.RegisterSwitch("Minimap", defaultValue: true);
        Assert.False(next.IsOn(again));
    }

    [Fact]
    public void Game_entries_this_run_did_not_register_are_written_back_after_the_registered_ones()
    {
        File.WriteAllText(
            FilePath,
            """{ "Game": { "Old": 7, "Minimap": true, "Names": ["a"] }, "Colour": "red", "Volume": 0.6 }"""
        );
        var settings = Create(new EngineOptions());
        var grid = settings.RegisterSwitch("Grid", defaultValue: false);
        var minimap = settings.RegisterSwitch("Minimap", defaultValue: true);

        settings.SetSwitch(grid, true);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        var section = written.RootElement.GetProperty("Game");
        Assert.Equal(["Grid", "Minimap", "Old", "Names"], section.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(section.GetProperty("Grid").GetBoolean());
        Assert.True(section.GetProperty("Minimap").GetBoolean());
        Assert.Equal(7, section.GetProperty("Old").GetInt32());
        Assert.Equal("a", section.GetProperty("Names")[0].GetString());
        Assert.Equal(1, written.RootElement.EnumerateObject().Count(p => p.Name == "Game"));
        Assert.Equal("red", written.RootElement.GetProperty("Colour").GetString());
        Assert.True(settings.IsOn(minimap));
    }

    [Fact]
    public void A_game_section_with_no_switch_registered_is_written_back_as_it_was()
    {
        File.WriteAllText(FilePath, """{ "Game": { "Minimap": false, "Old": 7 }, "Volume": 0.6 }""");
        var settings = Create(new EngineOptions());

        settings.SetVolume(0.3f);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.False(written.RootElement.GetProperty("Game").GetProperty("Minimap").GetBoolean());
        Assert.Equal(7, written.RootElement.GetProperty("Game").GetProperty("Old").GetInt32());
    }

    [Fact]
    public void A_game_switch_registered_twice_or_with_an_empty_name_is_refused()
    {
        var settings = Create(new EngineOptions());
        settings.RegisterSwitch("Minimap", defaultValue: true);

        Assert.Throws<ArgumentException>(() => settings.RegisterSwitch("Minimap", defaultValue: false));
        Assert.Throws<ArgumentException>(() => settings.RegisterSwitch("", defaultValue: true));
        Assert.Throws<ArgumentException>(() => settings.RegisterSwitch("  ", defaultValue: true));
        // Names compare ordinally.
        settings.RegisterSwitch("minimap", defaultValue: true);
    }

    [Fact]
    public void Reading_and_setting_a_game_switch_allocate_nothing()
    {
        var settings = Create(new EngineOptions());
        var minimap = settings.RegisterSwitch("Minimap", defaultValue: true);
        for (var i = 0; i < 100; i++)
        {
            settings.SetSwitch(minimap, i % 2 == 0);
            _ = settings.IsOn(minimap);
        }

        var before = SystemAllocation.StartMeasurement();
        var on = 0;
        for (var i = 0; i < 1000; i++)
        {
            settings.SetSwitch(minimap, i % 2 == 0);
            if (settings.IsOn(minimap))
            {
                on++;
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(500, on);
    }

    [Fact]
    public void A_game_value_is_its_default_when_the_file_has_none_and_keeps_its_bounds_and_name()
    {
        var settings = Create(new EngineOptions());

        var grass = settings.RegisterValue("Grass", 0.5f, 0, 1);
        var clamped = settings.RegisterValue("Reach", 90, 10, 40);

        Assert.Equal(0.5f, settings.ValueOf(grass));
        Assert.Equal(40f, settings.ValueOf(clamped));
        Assert.Equal(0f, settings.LeastOf(grass));
        Assert.Equal(1f, settings.MostOf(grass));
        Assert.Equal(10f, settings.LeastOf(clamped));
        Assert.Equal(40f, settings.MostOf(clamped));
        Assert.Equal("Grass", settings.ValueName(grass));
        Assert.Equal("Reach", settings.ValueName(clamped));
        settings.Save();
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_game_value_is_read_from_the_file_clamped_and_a_value_that_is_not_a_number_is_ignored()
    {
        File.WriteAllText(
            FilePath,
            """{ "Game": { "Grass": 0.25, "High": 7, "Low": -3, "Text": "5", "Flag": true, "Nothing": null } }"""
        );
        var settings = Create(new EngineOptions());

        var grass = settings.RegisterValue("Grass", 0.5f, 0, 1);
        var high = settings.RegisterValue("High", 0.5f, 0, 1);
        var low = settings.RegisterValue("Low", 0.5f, 0, 1);
        var text = settings.RegisterValue("Text", 0.5f, 0, 1);
        var flag = settings.RegisterValue("Flag", 0.5f, 0, 1);
        var nothing = settings.RegisterValue("Nothing", 0.5f, 0, 1);
        var absent = settings.RegisterValue("Absent", 0.75f, 0, 1);

        Assert.Equal(0.25f, settings.ValueOf(grass));
        Assert.Equal(1f, settings.ValueOf(high));
        Assert.Equal(0f, settings.ValueOf(low));
        Assert.Equal(0.5f, settings.ValueOf(text));
        Assert.Equal(0.5f, settings.ValueOf(flag));
        Assert.Equal(0.5f, settings.ValueOf(nothing));
        Assert.Equal(0.75f, settings.ValueOf(absent));
    }

    [Fact]
    public void Setting_a_game_value_keeps_it_within_its_bounds_and_changes_the_version_only_when_it_moves()
    {
        var settings = Create(new EngineOptions());
        var grass = settings.RegisterValue("Grass", 0.5f, 0, 1);
        var version = settings.Version;

        settings.SetValue(grass, 0.5f);
        settings.SetValue(grass, float.NaN);
        settings.SetValue(grass, float.PositiveInfinity);
        Assert.Equal(version, settings.Version);
        Assert.Equal(0.5f, settings.ValueOf(grass));

        settings.SetValue(grass, 3);
        Assert.Equal(1f, settings.ValueOf(grass));
        Assert.Equal(version + 1, settings.Version);

        // Past the bound again: the same value once kept within it.
        settings.SetValue(grass, 7);
        Assert.Equal(version + 1, settings.Version);

        settings.SetValue(grass, -2);
        Assert.Equal(0f, settings.ValueOf(grass));
        Assert.Equal(version + 2, settings.Version);

        settings.SetValue(grass, 0.25f);
        Assert.Equal(0.25f, settings.ValueOf(grass));
        Assert.Equal(version + 3, settings.Version);
    }

    [Fact]
    public void A_changed_game_value_is_written_rounded_after_the_switches_with_the_files_other_entries_and_read_back()
    {
        File.WriteAllText(FilePath, """{ "Game": { "Old": 7, "Names": ["a"] }, "Colour": "red" }""");
        var settings = Create(new EngineOptions());
        var grass = settings.RegisterValue("Grass", 0.5f, 0, 1);
        var minimap = settings.RegisterSwitch("Minimap", defaultValue: true);

        settings.SetValue(grass, 0.123456f);
        settings.Save();

        using (var written = JsonDocument.Parse(File.ReadAllText(FilePath)))
        {
            var section = written.RootElement.GetProperty("Game");
            Assert.Equal(["Minimap", "Grass", "Old", "Names"], section.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(0.1235f, section.GetProperty("Grass").GetSingle());
            Assert.True(section.GetProperty("Minimap").GetBoolean());
            Assert.Equal(7, section.GetProperty("Old").GetInt32());
            Assert.Equal("a", section.GetProperty("Names")[0].GetString());
            Assert.Equal("red", written.RootElement.GetProperty("Colour").GetString());
        }

        var next = Create(new EngineOptions());
        var again = next.RegisterValue("Grass", 0.5f, 0, 1);
        Assert.Equal(0.1235f, next.ValueOf(again));
        Assert.True(settings.IsOn(minimap));
    }

    [Fact]
    public void A_game_section_with_only_a_value_registered_is_rewritten_and_one_with_none_is_kept()
    {
        File.WriteAllText(FilePath, """{ "Game": { "Minimap": false, "Old": 7 } }""");
        var settings = Create(new EngineOptions());
        var grass = settings.RegisterValue("Grass", 0.5f, 0, 1);

        settings.SetValue(grass, 0.8f);
        settings.Save();

        using var written = JsonDocument.Parse(File.ReadAllText(FilePath));
        var section = written.RootElement.GetProperty("Game");
        Assert.Equal(["Grass", "Minimap", "Old"], section.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(0.8f, section.GetProperty("Grass").GetSingle());
        Assert.False(section.GetProperty("Minimap").GetBoolean());
    }

    [Fact]
    public void A_name_a_switch_or_a_value_took_is_refused_for_both_and_bad_bounds_are_refused()
    {
        var settings = Create(new EngineOptions());
        settings.RegisterSwitch("Minimap", defaultValue: true);
        settings.RegisterValue("Grass", 0.5f, 0, 1);

        Assert.Throws<ArgumentException>(() => settings.RegisterValue("Minimap", 0.5f, 0, 1));
        Assert.Throws<ArgumentException>(() => settings.RegisterSwitch("Grass", defaultValue: true));
        Assert.Throws<ArgumentException>(() => settings.RegisterValue("Grass", 0.5f, 0, 1));
        Assert.Throws<ArgumentException>(() => settings.RegisterValue("", 0.5f, 0, 1));
        Assert.Throws<ArgumentException>(() => settings.RegisterValue("A", 0.5f, 1, 0));
        Assert.Throws<ArgumentException>(() => settings.RegisterValue("B", 0.5f, float.NaN, 1));
        Assert.Throws<ArgumentException>(() => settings.RegisterValue("C", 0.5f, 0, float.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => settings.RegisterValue("D", float.NaN, 0, 1));
        Assert.Throws<ArgumentException>(() => settings.RegisterValue("E", float.NegativeInfinity, 0, 1));
        // A refused name stays free.
        Assert.Equal(1f, settings.ValueOf(settings.RegisterValue("A", 2, 0, 1)));
        // Equal bounds are allowed.
        Assert.Equal(3f, settings.ValueOf(settings.RegisterValue("F", 0, 3, 3)));
    }

    [Fact]
    public void Reading_and_setting_a_game_value_allocate_nothing()
    {
        var settings = Create(new EngineOptions());
        var grass = settings.RegisterValue("Grass", 0.5f, 0, 1);
        for (var i = 0; i < 100; i++)
        {
            settings.SetValue(grass, i % 2);
            _ = settings.ValueOf(grass);
        }

        var before = SystemAllocation.StartMeasurement();
        var sum = 0f;
        for (var i = 0; i < 1000; i++)
        {
            settings.SetValue(grass, i % 2);
            sum += settings.ValueOf(grass);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(500f, sum);
    }

    /// <summary>The settings with the file read, as the run's <c>settings file</c> step does.</summary>
    [Fact]
    public void The_ui_scale_comes_from_the_options_then_the_file_within_its_range_and_reaches_the_toolkit()
    {
        var options = new EngineOptions();
        options.Ui.Scale = 1.25f;
        Assert.Equal(1.25f, Create(options).UiScale);

        File.WriteAllText(FilePath, """{ "UiScale": 9 }""");
        using var fonts = new UiFonts(Options.Create(options), NullLogger<UiFonts>.Instance);
        using var toolkit = new UiToolkit(
            fonts,
            MenuStyle.Plain,
            Options.Create(options),
            NullLogger<UiToolkit>.Instance
        );
        var settings = new PlayerSettings(
            options,
            CommandLineKeys.None,
            FilePath,
            NullLogger<PlayerSettings>.Instance,
            toolkit: toolkit
        );
        settings.Initialize();
        settings.Apply();
        Assert.Equal(PlayerSettings.MostUiScale, settings.UiScale);
        Assert.Equal(PlayerSettings.MostUiScale, toolkit.Scale);

        settings.SetUiScale(0.8f);
        settings.Save();
        Assert.Equal(0.8f, toolkit.Scale);
        Assert.Equal(0.8f, Create(new EngineOptions()).UiScale, 3);
    }

    [Fact]
    public void Music_and_effects_volumes_come_from_the_options_then_the_file_within_range_and_the_command_line_wins()
    {
        var options = new EngineOptions();
        options.Audio.MusicVolume = 0.6f;
        options.Audio.EffectsVolume = 0.4f;
        var plain = Create(options);
        Assert.Equal(0.6f, plain.MusicVolume);
        Assert.Equal(0.4f, plain.EffectsVolume);

        File.WriteAllText(FilePath, """{ "MusicVolume": 3, "EffectsVolume": 0.25 }""");
        var read = Create(options);
        Assert.Equal(1f, read.MusicVolume);
        Assert.Equal(0.25f, read.EffectsVolume);

        // The command line gave the effects' volume: the file's is not read for this run.
        var given = Create(options, new CommandLineKeys(["--Engine:Audio:EffectsVolume=0.4"]));
        Assert.Equal(0.4f, given.EffectsVolume);

        read.SetMusicVolume(0.5f);
        read.SetEffectsVolume(-1);
        read.Save();
        var again = Create(new EngineOptions());
        Assert.Equal(0.5f, again.MusicVolume, 3);
        Assert.Equal(0f, again.EffectsVolume);
        // An old file without the keys reads the options' defaults.
        File.WriteAllText(FilePath, """{ "Volume": 0.3 }""");
        Assert.Equal(1f, Create(new EngineOptions()).MusicVolume);
    }

    private PlayerSettings Create(EngineOptions options, CommandLineKeys? keys = null, KeyBindings? bindings = null)
    {
        var settings = new PlayerSettings(
            options,
            keys ?? CommandLineKeys.None,
            FilePath,
            NullLogger<PlayerSettings>.Instance,
            keys: bindings
        );
        settings.Initialize();
        return settings;
    }
}
