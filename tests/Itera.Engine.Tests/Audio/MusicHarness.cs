using Itera.Engine.Audio;
using Itera.Engine.Audio.Files;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// A sound file in memory: frame f reads (f mod 20,000 + 1) / 32,768 on its left (or only) channel and the negative on
/// its right, so its 16-bit samples say which frame they came from (<see cref="Sample"/>); or what
/// <see cref="Wave"/> gives. Reading past <see cref="FailAt"/> throws, as a damaged file does.
/// </summary>
internal sealed class FakeDecoder(int channels, int rate, long frames, LoopPoints loop) : ISoundDecoder
{
    private long _at;

    public int Channels => channels;

    public int SampleRate => rate;

    public long Frames => frames;

    public LoopPoints LoopPoints => loop;

    public string? Warning => null;

    /// <summary>A frame past which <see cref="Read"/> throws; -1 for none.</summary>
    public long FailAt { get; init; } = -1;

    /// <summary>Each frame's sample by channel, in place of the frame's number.</summary>
    public Func<long, int, float>? Wave { get; init; }

    /// <summary>The 16-bit left sample of frame <paramref name="frame"/> (the right is its negative).</summary>
    public static short Sample(long frame) => (short)(frame % 20_000 + 1);

    public int Read(Span<float> output)
    {
        var count = (int)Math.Min(output.Length / channels, frames - _at);
        if (FailAt >= 0 && _at + count > FailAt)
        {
            throw new SoundFileException("damaged (fake)");
        }

        for (var i = 0; i < count; i++)
        {
            var frame = _at + i;
            for (var c = 0; c < channels; c++)
            {
                output[i * channels + c] =
                    Wave?.Invoke(frame, c) ?? (c == 0 ? Sample(frame) / 32768f : -Sample(frame) / 32768f);
            }
        }

        _at += count;
        return count;
    }

    public void Seek(long frame) => _at = Math.Clamp(frame, 0, frames);

    public void Dispose() { }
}

/// <summary>
/// The music's parts without threads: a <see cref="MusicPlayer"/> over files in memory (<see cref="FakeDecoder"/>, by
/// file name) or real files, its <see cref="MusicStreamer"/> and <see cref="MusicDecks"/> pumped in turn each 4 ms
/// round, on a <see cref="FakeAudioDevice"/>. With no other sources, deck d plays on source d + 1.
/// </summary>
internal sealed class MusicHarness : IDisposable
{
    /// <summary>A round's length, seconds: the audio thread's pump.</summary>
    public const float Round = 0.004f;

    private readonly Dictionary<string, Func<ISoundDecoder>> _files = new(StringComparer.Ordinal);
    private MusicStreamer? _streamer;
    private MusicDecks? _decks;
    private double _now;

    public MusicHarness(bool record = true, string? menuMusic = null)
    {
        Device = new FakeAudioDevice { RecordSamples = record };
        Music = new MusicPlayer(Log, menuMusic, Open);
    }

    public FakeAudioDevice Device { get; }

    public AudioStats Stats { get; } = new();

    public RecordingLogger Log { get; } = new();

    public MusicPlayer Music { get; }

    /// <summary>Files opened, by full path.</summary>
    private Dictionary<string, int> Opens { get; } = new(StringComparer.Ordinal);

    /// <summary>The music volume and sound on or off the decks are pumped with.</summary>
    public float Volume { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Declares a track in memory of <paramref name="frames"/> frames, at <paramref name="rate"/>.</summary>
    public int Track(
        string name,
        long frames,
        bool loop = true,
        LoopPoints? points = null,
        int channels = 2,
        int rate = 48_000,
        long failAt = -1,
        Func<long, int, float>? wave = null
    )
    {
        File(
            name + ".fake",
            () => new FakeDecoder(channels, rate, frames, points ?? LoopPoints.None) { FailAt = failAt, Wave = wave }
        );
        return Music.Declare(name, name + ".fake", loop);
    }

    /// <summary>Opening the file <paramref name="path"/> (from the game's folder) gives what <paramref name="open"/> makes.</summary>
    public void File(string path, Func<ISoundDecoder> open) => _files[FileRecipe.Resolve(path)] = open;

    /// <summary>How many times the file <paramref name="path"/> (from the game's folder) was opened.</summary>
    public int OpensOf(string path) => Opens.GetValueOrDefault(FileRecipe.Resolve(path));

    /// <summary>Reads the tracks' headers and makes the streamer and decks, as the audio thread's step does.</summary>
    public void Start()
    {
        Music.ReadTracks();
        _streamer = new MusicStreamer(Music.Tracks, Music.Feeds, Open, Log);
        _decks = new MusicDecks(Device, Music.Exchange, Music.Feeds, Stats);
    }

    /// <summary>One round: the commands published, the decks pumped, the streamer pumped, the device played on.</summary>
    public void Step()
    {
        Music.Publish(running: true);
        _decks!.Pump(_now, Volume, Enabled);
        _streamer!.Pump();
        Device.Advance(Round);
        _now += Round;
    }

    /// <summary>Rounds for <paramref name="seconds"/>.</summary>
    public void Run(float seconds)
    {
        var rounds = (int)MathF.Round(seconds / Round);
        for (var i = 0; i < rounds; i++)
        {
            Step();
        }
    }

    /// <summary>Rounds until a deck's source plays <paramref name="track"/>: its fade began in the last round.</summary>
    public void StepUntilHeard(int track)
    {
        for (var i = 0; i < 100; i++)
        {
            Step();
            for (var deck = 0; deck < MusicDecks.Count; deck++)
            {
                if (Stats.MusicDeckTrack(deck) == track && Device.Get((uint)deck + 1).Playing)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException($"track {track} was never heard");
    }

    /// <summary>The deck playing <paramref name="track"/>, or -1.</summary>
    public int DeckOf(int track)
    {
        for (var deck = 0; deck < MusicDecks.Count; deck++)
        {
            if (Stats.MusicDeckTrack(deck) == track)
            {
                return deck;
            }
        }

        return -1;
    }

    /// <summary>The gain <paramref name="track"/> plays at; 0 when no deck plays it.</summary>
    public float GainOf(int track)
    {
        var deck = DeckOf(track);
        return deck < 0 ? 0 : Device.Get((uint)deck + 1).Gain;
    }

    /// <summary>The source of the deck playing <paramref name="track"/>.</summary>
    public FakeAudioDevice.Source SourceOf(int track) => Device.Get((uint)DeckOf(track) + 1);

    /// <summary>Closes the streamer's files, as the music thread does when it ends, and the decks.</summary>
    public void Dispose()
    {
        _streamer?.CloseFiles();
        _decks?.Dispose();
    }

    /// <summary>A file in memory, or else on disk. Allocates nothing but the decoder.</summary>
    private ISoundDecoder Open(string path)
    {
        Opens[path] = Opens.GetValueOrDefault(path) + 1;
        return _files.TryGetValue(path, out var open) ? open() : SoundDecoders.Open(path);
    }
}

/// <summary>A logger that keeps each line with its level.</summary>
internal sealed class RecordingLogger : ILogger
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
    )
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
