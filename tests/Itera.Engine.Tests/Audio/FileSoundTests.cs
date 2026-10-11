using System.Numerics;
using Itera.Engine.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// Sounds read from files (<see cref="FileOneShotRecipe"/>) through the mixer on the in-memory device, their WAV files
/// written to a folder of the test's own (<see cref="WavBuilder"/>): one kept stereo plays at the listener with its two
/// channels at its own rate; one not kept is mixed to mono and placed in the world; each file is a variation, never the
/// same twice running; a file that cannot be read is logged at Error and another plays in its place, and with none the
/// sound is silent but counted as ready; an Ogg file plays at its rate; playing them allocates nothing; a file one-shot
/// takes 1 to 16 files and counts among the prepared sounds; with no device a missing file is still named.
/// </summary>
public sealed class FileSoundTests : IDisposable
{
    private const int Rate = 48_000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "itera-sounds-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger<AudioService> _log = new();
    private readonly FakeAudioDevice _device = new();
    private readonly SoundRecipes _recipes = new();
    private readonly AudioStats _stats = new();
    private readonly AudioExchange _exchange = new();
    private readonly AudioMixer _mixer;
    private double _now;

    public FileSoundTests()
    {
        Directory.CreateDirectory(_folder);
        _mixer = new AudioMixer(_device, _recipes, _stats, _exchange, 8, Rate, _log, renderOnPump: true);
    }

    public void Dispose()
    {
        _mixer.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_stereo_file_kept_stereo_plays_at_the_listener_with_its_channels_and_rate()
    {
        var chime = Wav(
            "chime.wav",
            new WavBuilder
            {
                Channels = 2,
                Rate = 44_100,
                Bits = 24,
            },
            4410
        );
        var id = _recipes.Register("chime", new FileOneShotRecipe(chime) { KeepStereo = true });
        Pump();

        Pump(new SoundEvent { Recipe = id, Position = new Vector3(5, 0, 0) });

        var source = PlayingSource();
        Assert.True(source.AtListener);
        Assert.Equal((2, 44_100, 0.1f), _device.BufferOf(source.Static));
        Assert.Null(_log.Entries.FirstOrDefault(entry => entry.Level >= LogLevel.Warning).Message);
    }

    [Fact]
    public void A_stereo_file_not_kept_is_mixed_to_mono_and_placed_in_the_world()
    {
        var clang = Wav("clang.wav", new WavBuilder { Channels = 2 }, 2205);
        var id = _recipes.Register("clang", new FileOneShotRecipe(clang));
        Pump();

        Pump(new SoundEvent { Recipe = id, Position = new Vector3(5, 0, 0) });

        var source = _device.PlayingAt(new Vector3(5, 0, 0));
        Assert.NotNull(source);
        Assert.False(source.AtListener);
        Assert.Equal((1, 22_050, 0.1f), _device.BufferOf(source.Static));
    }

    [Fact]
    public void Each_file_is_a_variation_never_played_twice_running()
    {
        var files = new[]
        {
            Wav("a.wav", new WavBuilder(), 1000),
            Wav("b.wav", new WavBuilder(), 2000),
            Wav("c.wav", new WavBuilder(), 3000),
        };
        var id = _recipes.Register("step", new FileOneShotRecipe(files));
        Pump();

        var lengths = new List<float>();
        for (var i = 0; i < 12; i++)
        {
            var at = new Vector3(i + 1, 0, 0);
            Pump(new SoundEvent { Recipe = id, Position = at });
            lengths.Add(_device.BufferOf(_device.PlayingAt(at)!.Static).Seconds);
            _device.Advance(1);
            Pump();
        }

        Assert.Equal(3, lengths.Distinct().Count());
        for (var i = 1; i < lengths.Count; i++)
        {
            Assert.NotEqual(lengths[i - 1], lengths[i]);
        }
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_logged_and_another_plays_in_its_place()
    {
        var good = Wav("good.wav", new WavBuilder(), 2205);
        var missing = Path.Combine(_folder, "missing.wav");
        var id = _recipes.Register("door", new FileOneShotRecipe(good, missing));
        Pump();

        var error = Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("missing.wav", error.Message, StringComparison.Ordinal);
        Assert.Contains("no such file", error.Message, StringComparison.Ordinal);
        var files = _mixer.Renderer.FilesOf(id)!;
        Assert.False(files[0].Failed);
        Assert.True(files[1].Failed);
        for (var i = 0; i < 4; i++)
        {
            var at = new Vector3(i + 1, 0, 0);
            Pump(new SoundEvent { Recipe = id, Position = at });
            Assert.Equal(0.1f, _device.BufferOf(_device.PlayingAt(at)!.Static).Seconds);
        }
    }

    [Fact]
    public void A_sound_whose_files_all_fail_is_silent_and_counted_as_ready()
    {
        var id = _recipes.Register("nothing", new FileOneShotRecipe(Path.Combine(_folder, "none.wav")));
        Pump();

        Pump(new SoundEvent { Recipe = id, Position = Vector3.UnitX });

        Assert.Equal(_recipes.PreparedCount, _stats.RenderedRecipes);
        Assert.Equal(0, _device.Playing());
        Assert.Equal(0, _stats.Late);
    }

    [Fact]
    public void An_ogg_file_plays_at_its_own_rate()
    {
        var id = _recipes.Register(
            "tone",
            new FileOneShotRecipe(VorbisDecoderTests.Fixture("tone-stereo-loop.ogg")) { KeepStereo = true }
        );
        Pump();

        Pump(new SoundEvent { Recipe = id });

        Assert.Equal((2, 22_050, 0.5f), _device.BufferOf(PlayingSource().Static));
    }

    [Fact]
    public void Playing_file_sounds_allocates_nothing()
    {
        var files = new[] { Wav("a.wav", new WavBuilder(), 500), Wav("b.wav", new WavBuilder { Channels = 2 }, 700) };
        var id = _recipes.Register("tick", new FileOneShotRecipe(files));
        var chime = _recipes.Register("chime", new FileOneShotRecipe(files[1]) { KeepStereo = true });
        Pump();
        Pump();
        SoundEvent[] events = [new() { Recipe = id, Position = new Vector3(2, 0, 0) }, new() { Recipe = chime }];
        for (var i = 0; i < 100; i++)
        {
            Pump(i % 5 == 0 ? events : []);
            _device.Advance(0.004f);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 500; i++)
        {
            Pump(i % 5 == 0 ? events : []);
            _device.Advance(0.004f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(_stats.Played > 100, $"{_stats.Played} played");
        Assert.Equal(0, _stats.Late);
    }

    [Fact]
    public void A_file_one_shot_takes_one_to_sixteen_files_and_counts_as_prepared()
    {
        var recipes = new SoundRecipes();

        Assert.Throws<ArgumentException>(() => recipes.Register("none", new FileOneShotRecipe()));
        Assert.Throws<ArgumentException>(() =>
            recipes.Register("many", new FileOneShotRecipe([.. Enumerable.Repeat("a.wav", 17)]))
        );
        var id = recipes.Register("door", new FileOneShotRecipe("Sounds/door.wav"));

        Assert.Equal(1, recipes.PreparedCount);
        Assert.Equal("file one-shot", recipes.KindOf(id));
        Assert.False(recipes.IsSustained(id));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Sounds/door.wav"), ((FileRecipe)recipes[id]).Paths[0]);
    }

    [Fact]
    public void With_no_device_a_missing_file_is_still_named()
    {
        var audio = new AudioService(Options.Create(new EngineOptions()), _log);
        audio.Initialize(new NullAudioDevice());
        audio.Recipes.Register("door", new FileOneShotRecipe(Path.Combine(_folder, "door.wav")));

        audio.Start();

        var error = Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("door.wav", error.Message, StringComparison.Ordinal);
        audio.Dispose();
    }

    private string Wav(string name, WavBuilder builder, int frames)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, builder.Build(WavBuilder.Ramp(frames, builder.Channels)));
        return path;
    }

    /// <summary>Posts <paramref name="events"/> and pumps once (the first pumps read the files, one sound a round).</summary>
    private void Pump(params SoundEvent[] events)
    {
        _exchange.Post(events, [], ListenerPose.Default, MixLevels.Full);
        _mixer.Pump(_now);
        _now += 0.004;
    }

    /// <summary>The one source playing.</summary>
    private FakeAudioDevice.Source PlayingSource()
    {
        Assert.Equal(1, _device.Playing());
        for (uint i = 1; i <= _device.Sources; i++)
        {
            if (_device.Get(i).Playing)
            {
                return _device.Get(i);
            }
        }

        throw new InvalidOperationException("none plays");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
