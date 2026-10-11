using System.Diagnostics;
using Itera.Engine.Audio.Files;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Audio;

/// <summary>
/// Music: tracks a game declares by name once a run (<see cref="Declare"/>, in its <c>IRunStartup</c>, before the audio
/// thread starts), streamed from their files while they play. One track is heard at a time; <see cref="Play(int, float, bool)"/> cross-fades
/// to a new one from what plays (equal power: halfway, each at 0.71 of full) and <see cref="Stop"/> fades it out. The
/// commands go to the audio thread with the sounds (<see cref="AudioService.Publish"/>); a thread of its own, "Music"
/// (<see cref="MusicStreamer"/>), decodes ahead, and the audio thread plays two decks (<see cref="MusicDecks"/>) at the
/// listener under the music volume. Each file's header is read as the audio thread starts, also with no device, and one
/// that cannot be read is logged at Error and plays nothing. Without an audio thread, <see cref="Current"/> still follows
/// the commands, so a game's logic and its menu's do not depend on a device. Main thread only; allocates nothing after the
/// start.
/// </summary>
public sealed partial class MusicPlayer
{
    /// <summary>The most tracks a run declares.</summary>
    public const int MaxTracks = 64;

    /// <summary>The fade <see cref="Play(int, float, bool)"/> and <see cref="Stop"/> take unless told, seconds.</summary>
    public const float DefaultFadeSeconds = 2;

    /// <summary>The longest fade, seconds.</summary>
    public const float MaxFadeSeconds = 60;

    private readonly List<MusicTrack> _tracks = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly MusicCommand[] _pending = new MusicCommand[MusicExchange.MaxCommands];
    private readonly string? _menuMusic;
    private readonly ILogger _logger;
    private int _pendingCount;
    private int _plays;
    private int _currentPlay;
    private int _current = -1;
    private int? _menuTrack;
    private bool _started;

    /// <param name="logger">Where a file that cannot be read is named.</param>
    /// <param name="menuMusic">The track the engine's menu plays (<c>Engine:Audio:MenuMusic</c>); null for none.</param>
    public MusicPlayer(ILogger logger, string? menuMusic = null)
        : this(logger, menuMusic, SoundDecoders.Open) { }

    internal MusicPlayer(ILogger logger, string? menuMusic, Func<string, ISoundDecoder> open)
    {
        _logger = logger;
        _menuMusic = string.IsNullOrWhiteSpace(menuMusic) ? null : menuMusic;
        Open = open;
    }

    /// <summary>The tracks declared, in order (a track's number is its place here).</summary>
    public IReadOnlyList<MusicTrack> Tracks => _tracks;

    /// <summary>
    /// The track asked to play, -1 for none: set by <see cref="Play(int, float, bool)"/>, -1 after <see cref="Stop"/>, and -1 again once a
    /// track that does not loop has ended, or its file failed while it played (as soon as the audio thread says so).
    /// </summary>
    public int Current
    {
        get
        {
            FollowEnd();
            return _current;
        }
    }

    /// <summary>The name of <see cref="Current"/>, or null.</summary>
    public string? CurrentName => Current >= 0 ? _tracks[_current].Name : null;

    /// <summary>
    /// The track <c>Engine:Audio:MenuMusic</c> names, -1 when it names none, or one no game declared (logged at Error
    /// once). Read once the tracks are declared.
    /// </summary>
    public int MenuTrack => _menuTrack ??= FindMenuTrack();

    /// <summary>The commands' way to the audio thread.</summary>
    internal MusicExchange Exchange { get; } = new();

    /// <summary>Each deck's decoded music, from the music thread to the audio thread.</summary>
    internal MusicFeed[] Feeds { get; } = [new(), new()];

    /// <summary>Opens a track's file (the decoders; a test's own).</summary>
    internal Func<string, ISoundDecoder> Open { get; }

    /// <summary>
    /// Declares the track <paramref name="name"/> in the file at <paramref name="path"/> (from the game's folder when not
    /// rooted; a WAV or Ogg Vorbis file, mono or stereo), repeating when <paramref name="loop"/>, and returns its number.
    /// Once a run, before the audio thread starts (an <c>IRunStartup</c>). Throws for a name declared already, past
    /// <see cref="MaxTracks"/>, and once the audio thread has started.
    /// </summary>
    public int Declare(string name, string path, bool loop = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_started)
        {
            throw new InvalidOperationException(
                $"Music track {name}: tracks are declared before the audio thread starts (in an IRunStartup)."
            );
        }

        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"A music track named {name} is declared already.", nameof(name));
        }

        if (_tracks.Count == MaxTracks)
        {
            throw new InvalidOperationException($"At most {MaxTracks} music tracks are declared.");
        }

        _tracks.Add(new MusicTrack(name, FileRecipe.Resolve(path), loop));
        _byName.Add(name, _tracks.Count - 1);
        return _tracks.Count - 1;
    }

    /// <summary>The track named <paramref name="name"/>, if one is declared.</summary>
    public bool TryFind(string name, out int track) => _byName.TryGetValue(name, out track);

    /// <summary>Plays the track named <paramref name="name"/> (<see cref="Play(int, float, bool)"/>). Throws for a name no track has.</summary>
    public void Play(string name, float fadeSeconds = DefaultFadeSeconds, bool restart = false)
    {
        if (!TryFind(name, out var track))
        {
            throw new ArgumentException($"No music track is named {name}.", nameof(name));
        }

        Play(track, fadeSeconds, restart);
    }

    /// <summary>
    /// Plays <paramref name="track"/> from its start, cross-fading over <paramref name="fadeSeconds"/> (0 to
    /// <see cref="MaxFadeSeconds"/>) from what plays once its first sound is ready; when it plays already, nothing changes
    /// unless <paramref name="restart"/>. A track whose file could not be read plays nothing: what plays fades out.
    /// </summary>
    public void Play(int track, float fadeSeconds = DefaultFadeSeconds, bool restart = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(track);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(track, _tracks.Count);
        if (_tracks[track].Error is not null)
        {
            Stop(fadeSeconds);
            return;
        }

        if (track == Current && !restart)
        {
            return;
        }

        _current = track;
        _currentPlay = ++_plays;
        MusicExchange.Append(
            _pending,
            ref _pendingCount,
            new MusicCommand(MusicCommandKind.Play, track, Fade(fadeSeconds), restart, _currentPlay)
        );
    }

    /// <summary>Fades out what plays over <paramref name="fadeSeconds"/> (0 to <see cref="MaxFadeSeconds"/>).</summary>
    public void Stop(float fadeSeconds = DefaultFadeSeconds)
    {
        _current = -1;
        MusicExchange.Append(
            _pending,
            ref _pendingCount,
            new MusicCommand(MusicCommandKind.Stop, -1, Fade(fadeSeconds), false, 0)
        );
    }

    /// <summary>
    /// Reads each track's header (its channels, rate, length, and loop points), logging one that cannot be read at Error,
    /// and the menu's track; from then on no track is declared. At the audio thread's step, with a device or none.
    /// </summary>
    internal void ReadTracks()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var clock = Stopwatch.StartNew();
        var seconds = 0.0;
        foreach (var track in _tracks)
        {
            try
            {
                using var decoder = Open(track.Path);
                track.Channels = decoder.Channels;
                track.SampleRate = decoder.SampleRate;
                track.Frames = decoder.Frames;
                track.LoopPoints = decoder.LoopPoints;
                track.Warning = decoder.Warning;
                seconds += track.Seconds;
                if (track.Warning is { } warning)
                {
                    LogTrackWarning(track.Name, track.Path, warning);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                track.Error = exception is SoundFileException ? exception.Message : exception.ToString();
                LogTrackFailed(track.Name, track.Path, track.Error);
            }
        }

        if (_tracks.Count > 0)
        {
            LogTracks(_tracks.Count, seconds, clock.Elapsed.TotalMilliseconds);
        }

        _ = MenuTrack;
    }

    /// <summary>
    /// Hands the commands given since the last call to the audio thread when it runs (<paramref name="running"/>; else
    /// they are dropped). Main thread, once a main-loop iteration.
    /// </summary>
    internal void Publish(bool running)
    {
        if (running)
        {
            Exchange.Post(_pending.AsSpan(0, _pendingCount));
        }

        _pendingCount = 0;
    }

    /// <summary>The track asked for last ended by itself (the audio thread says so): none is current.</summary>
    private void FollowEnd()
    {
        if (_current >= 0 && Exchange.Ended >= _currentPlay)
        {
            _current = -1;
        }
    }

    private static float Fade(float seconds) => float.IsNaN(seconds) ? 0 : Math.Clamp(seconds, 0, MaxFadeSeconds);

    private int FindMenuTrack()
    {
        if (_menuMusic is null)
        {
            return -1;
        }

        if (TryFind(_menuMusic, out var track))
        {
            return track;
        }

        LogNoMenuTrack(_menuMusic);
        return -1;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Music: {Tracks} track(s), {Seconds:F0} s in all, their headers read in {Ms:F0} ms"
    )]
    private partial void LogTracks(int tracks, double seconds, double ms);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Audio: music track {Name}: the file {Path} cannot be read ({Reason}); it plays nothing"
    )]
    private partial void LogTrackFailed(string name, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio: music track {Name}: the file {Path}: {Problem}")]
    private partial void LogTrackWarning(string name, string path, string problem);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Audio: Engine:Audio:MenuMusic names the music track {Name}, which no game declared; the menu plays no music"
    )]
    private partial void LogNoMenuTrack(string name);
}
