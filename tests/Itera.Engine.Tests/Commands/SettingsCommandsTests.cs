using System.Text.Json;
using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Networking;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Commands;

/// <summary>
/// The console's <c>settings</c> (<see cref="SettingsCommands"/>): with nothing it
/// prints every setting; a name and a value changes it as the menu does, clamped to the menu's range, and writes the
/// file; anything else prints the usage and changes and writes nothing.
/// </summary>
public sealed class SettingsCommandsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "itera-settings-commands",
        Guid.NewGuid().ToString("N")
    );
    private readonly ConsoleCommands _commands = new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
    private readonly DevConsole _console;
    private readonly RenderView _view;
    private readonly PlayerSettings _settings;
    private int _presentModes;

    public SettingsCommandsTests()
    {
        var options = Options.Create(new EngineOptions());
        _console = new DevConsole(options, _commands, new ConsoleLogBuffer(), NullLogger<DevConsole>.Instance);
        _view = new RenderView(options);
        _settings = new PlayerSettings(
            options.Value,
            CommandLineKeys.None,
            FilePath,
            NullLogger<PlayerSettings>.Instance,
            view: _view
        );
        _settings.Initialize();
        _settings.PresentModeChanged += () => _presentModes++;
        new SettingsCommands(_commands, _settings).Register();
    }

    private string FilePath => Path.Combine(_folder, PlayerSettings.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void With_nothing_it_prints_every_setting()
    {
        Run("settings");

        Assert.Equal(
            "volume 0.10, sensitivity 0.10, fov 90, scaling on, threshold 120, shadows on, aa 4x, vsync on, fullscreen off",
            Last()
        );
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_switch_changes_the_setting_and_its_service_and_writes_the_file()
    {
        Run("settings shadows off; settings vsync off");

        Assert.False(_settings.Shadows);
        Assert.False(_view.Shadows);
        Assert.Equal(1, _presentModes);
        Assert.Contains("shadows off", Last(), StringComparison.Ordinal);
        using var file = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        Assert.False(file.RootElement.GetProperty("Shadows").GetBoolean());
        Assert.False(file.RootElement.GetProperty("VSync").GetBoolean());
    }

    [Fact]
    public void Anti_aliasing_takes_each_mode_into_the_view_the_samples_and_the_file()
    {
        var samplesChanged = 0;
        _settings.SamplesChanged += () => samplesChanged++;

        Run("settings aa off");
        Assert.False(_settings.AntiAliasing);
        Assert.False(_view.AntiAliasing);
        Assert.Equal(1, _settings.MsaaSamples);
        Assert.Equal(1, samplesChanged);
        Assert.Contains("aa off", Last(), StringComparison.Ordinal);
        using (var file = JsonDocument.Parse(File.ReadAllBytes(FilePath)))
        {
            Assert.False(file.RootElement.GetProperty("AntiAliasing").GetBoolean());
            Assert.Equal(1, file.RootElement.GetProperty("MsaaSamples").GetInt32());
        }

        Run("settings aa smaa");
        Assert.True(_view.AntiAliasing);
        Assert.Equal(1, samplesChanged);
        Assert.Contains("aa smaa", Last(), StringComparison.Ordinal);

        Run("settings aa 8x");
        Assert.Equal(8, _settings.MsaaSamples);
        Assert.Equal(2, samplesChanged);
        Assert.Contains("aa 8x", Last(), StringComparison.Ordinal);
        using (var file = JsonDocument.Parse(File.ReadAllBytes(FilePath)))
        {
            Assert.Equal(8, file.RootElement.GetProperty("MsaaSamples").GetInt32());
        }

        Run("settings aa on");
        Assert.Equal(AntiAliasMode.Msaa8, _settings.AntiAliasMode);
    }

    [Fact]
    public void Scaling_turns_off_and_the_threshold_takes_the_nearest_offered_rate()
    {
        Run("settings scaling off; settings threshold 130; settings threshold 150");

        Assert.False(_settings.DynamicScaling);
        Assert.False(_view.DynamicScaling);
        Assert.Equal(144, _settings.ScalingThreshold);
        Assert.Equal(144, _view.ScalingThreshold);
        using var file = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        Assert.False(file.RootElement.GetProperty("DynamicRenderScaling").GetBoolean());
        Assert.Equal(144, file.RootElement.GetProperty("DynamicScalingThreshold").GetDouble());
    }

    /// <summary>Frame blending, smooth normals, and texture filtering were the raymarcher's, and left with it.</summary>
    [Theory]
    [InlineData("settings blend off")]
    [InlineData("settings smoothing off")]
    [InlineData("settings filtering off")]
    public void The_raymarchers_settings_are_no_settings(string line)
    {
        Run(line);

        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void A_number_is_clamped_to_the_menus_range()
    {
        Run("settings volume 0.4; settings fov 200; settings sensitivity 0; settings threshold 1000");

        Assert.Equal(0.4f, _settings.Volume);
        Assert.Equal(PlayerSettings.MostFieldOfView, _settings.FieldOfViewDegrees);
        Assert.Equal(PlayerSettings.LeastSensitivity, _settings.MouseSensitivity);
        Assert.Equal(DynamicRenderScale.MostThreshold, _view.ScalingThreshold);
    }

    [Theory]
    [InlineData("settings volume")]
    [InlineData("settings shadows maybe")]
    [InlineData("settings colour 3")]
    [InlineData("settings volume loud")]
    [InlineData("settings volume 0.5 more")]
    public void Anything_else_prints_the_usage_and_changes_nothing(string line)
    {
        Run(line);

        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        Assert.Equal(0.1f, _settings.Volume);
        Assert.True(_settings.Shadows);
        Assert.False(File.Exists(FilePath));
    }

    private void Run(string line)
    {
        _console.Submit(line);
        _console.RunPending();
    }

    private string Last() => _console.ScrollbackLine(_console.ScrollbackCount - 1).Text;
}
