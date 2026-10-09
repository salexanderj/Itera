using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.Time;
using Itera.Engine.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Commands;

/// <summary>
/// The console's <c>sound</c> (<see cref="SoundCommands"/>) without a device: the
/// counts, on and off, the volume as the player's setting, the recipes, a test
/// refused with no sound running, an unknown recipe, WAV files written into the data folder's <c>Sounds</c>, and the
/// usage for anything else.
/// </summary>
public sealed class SoundCommandsTests : IDisposable
{
    private readonly EngineOptions _options = new();
    private readonly ConsoleCommands _commands = new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
    private readonly DevConsole _console;
    private readonly AudioService _audio;
    private readonly PlayerSettings _settings;

    public SoundCommandsTests()
    {
        // A data folder of the test's own, for the WAV files.
        _options.Application.DataFolder = "itera-sound-commands-" + Guid.NewGuid().ToString("N")[..8];
        _options.Audio.Cache = false;
        var options = Options.Create(_options);
        _console = new DevConsole(options, _commands, new ConsoleLogBuffer(), NullLogger<DevConsole>.Instance);
        _audio = new AudioService(options, NullLogger<AudioService>.Instance);
        _audio.Initialize(new NullAudioDevice());
        _settings = new PlayerSettings(
            _options,
            CommandLineKeys.None,
            null,
            NullLogger<PlayerSettings>.Instance,
            _audio
        );
        new SoundCommands(
            _commands,
            _audio,
            new RenderView(options),
            new MatchControl(),
            new GameClock(),
            options,
            _settings
        ).Register();
    }

    public void Dispose()
    {
        _audio.Dispose();
        var folder = _options.Application.DataDirectory();
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void With_nothing_it_prints_the_counts()
    {
        Run("sound");

        var first = _console.ScrollbackLine(_console.ScrollbackCount - 2).Text;
        Assert.StartsWith("sound silent (none (silent)), volume 0.1: 0 one-shots", first, StringComparison.Ordinal);
        Assert.StartsWith("since start: 0 played", Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void On_and_off_switch_every_sound()
    {
        Run("sound off");
        Assert.False(_audio.Enabled);
        Assert.Equal("sound off", Last());

        Run("sound on");
        Assert.True(_audio.Enabled);
    }

    [Fact]
    public void The_volume_is_the_players_setting()
    {
        Run("sound volume 0.4");

        Assert.Equal(0.4f, _audio.Volume);
        Assert.Equal(0.4f, _settings.Volume);
        Assert.Equal("volume 0.4", Last());

        // Another setting's change keeps it.
        _settings.SetShadows(false);
        Assert.Equal(0.4f, _audio.Volume);

        Run("sound volume");
        Assert.Equal("volume 0.4", Last());
        Run("sound volume 2");
        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
        Assert.Equal(0.4f, _audio.Volume);
    }

    [Fact]
    public void Recipes_lists_each_with_its_kind()
    {
        Run("sound recipes");

        Assert.Equal(_audio.Recipes.Count, _console.ScrollbackCount);
        Assert.Equal("bar (one-shot)", _console.ScrollbackLine(0).Text);
    }

    [Fact]
    public void A_test_needs_a_recipe_and_running_sound()
    {
        Run("sound test nosuch");
        Assert.Equal("nosuch: no such recipe; 'sound recipes' lists them.", Last());

        Run("sound test bar 2");
        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);

        Run("sound test bar");
        Assert.Equal("no sound: none (silent)", Last());
    }

    [Fact]
    public void Wav_writes_a_recipe_into_the_data_folders_sounds()
    {
        Run("sound wav bar");

        Assert.StartsWith("wrote ", Last(), StringComparison.Ordinal);
        var path = Last()["wrote ".Length..];
        Assert.Equal(Path.Combine(_options.Application.DataDirectory(), "Sounds"), Path.GetDirectoryName(path));
        Assert.True(new FileInfo(path).Length > 44);
    }

    [Theory]
    [InlineData("sound loud")]
    [InlineData("sound test")]
    [InlineData("sound wav")]
    public void Anything_else_prints_the_usage(string line)
    {
        Run(line);

        Assert.StartsWith("usage: ", Last(), StringComparison.Ordinal);
    }

    private void Run(string line)
    {
        _console.Submit(line);
        _console.RunPending();
    }

    private string Last() => _console.ScrollbackLine(_console.ScrollbackCount - 1).Text;
}
