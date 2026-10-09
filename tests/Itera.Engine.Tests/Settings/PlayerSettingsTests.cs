using System.Text.Json;
using Itera.Engine.Audio;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
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
/// not know written back; a byte order mark read; a failed write keeping the old file and tried again; and the command
/// line's keys read from its arguments.
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
        Assert.Equal(120, written.RootElement.GetProperty("DynamicScalingThreshold").GetDouble());
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

    /// <summary>The settings with the file read, as the run's <c>settings file</c> step does.</summary>
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
