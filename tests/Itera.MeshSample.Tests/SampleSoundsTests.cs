using Itera.Engine;
using Itera.Engine.Audio;
using Itera.Engine.Audio.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The sample's sound files (<see cref="SampleSounds"/>, in <c>Sounds/</c> beside the build) read as declared: the clang's
/// three variations mono at 48 kHz, the hum with its loop, the chime stereo in 24 bits, the menu's music with its loop
/// tags and the yard's looping whole; and the run's start (with no device) names no file as missing or bad.
/// </summary>
public sealed class SampleSoundsTests
{
    [Fact]
    public void Every_sound_file_reads_as_declared_and_the_start_names_no_problem()
    {
        var log = new WarningLog();
        using var audio = new AudioService(Options.Create(new EngineOptions()), log);
        audio.Initialize(new NullAudioDevice());
        SampleSounds.Register(audio);

        audio.Start();

        Assert.Empty(log.Lines);
        var clang = Files(audio, SampleSounds.Clang);
        Assert.Equal(3, clang.Count);
        Assert.All(clang, path => AssertSound(path, 1, 48_000, 96_000, LoopPoints.None));
        AssertSound(Files(audio, SampleSounds.Hum)[0], 1, 48_000, 124_800, new LoopPoints(28_800, 124_800));
        var chime = Files(audio, SampleSounds.Chime)[0];
        AssertSound(chime, 2, 44_100, 88_200, LoopPoints.None);
        using (var wav = (WavDecoder)SoundDecoders.Open(chime))
        {
            Assert.Equal(24, wav.Format.BitsPerSample);
        }

        var tracks = audio.Music.Tracks;
        var menu = tracks.Single(track => track.Name == SampleSounds.MenuMusic);
        Assert.Equal((2, 44_100, 1_234_800L), (menu.Channels, menu.SampleRate, menu.Frames));
        Assert.Equal(new LoopPoints(705_600, 1_234_800), menu.LoopPoints);
        var yard = tracks.Single(track => track.Name == SampleSounds.YardMusic);
        Assert.Equal((2, 44_100, 705_600L), (yard.Channels, yard.SampleRate, yard.Frames));
        Assert.False(yard.LoopPoints.IsSet);
        Assert.All(tracks, track => Assert.True(track.Loop));
    }

    private static IReadOnlyList<string> Files(AudioService audio, string name)
    {
        Assert.True(audio.Recipes.TryFind(name, out var recipe), name);
        return ((FileRecipe)audio.Recipes[recipe]).Paths;
    }

    private static void AssertSound(string path, int channels, int rate, long frames, LoopPoints loop)
    {
        using var decoder = SoundDecoders.Open(path);
        Assert.Equal((channels, rate, frames), (decoder.Channels, decoder.SampleRate, decoder.Frames));
        Assert.Equal(loop, decoder.LoopPoints);
        Assert.Null(decoder.Warning);
    }

    /// <summary>The lines logged at Warning or above.</summary>
    private sealed class WarningLog : ILogger<AudioService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel >= LogLevel.Warning)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}
