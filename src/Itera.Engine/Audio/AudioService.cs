using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Audio.Synthesis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Audio;

/// <summary>
/// Where game code asks for sound. Fixed and frame systems queue one-shots with <see cref="Play"/> and hold streams
/// with <see cref="Sustain"/>; <see cref="Publish"/>, once a main-loop iteration, hands them and the listener (the
/// camera) to the audio thread through <see cref="AudioExchange"/>. The audio thread (<see cref="AudioMixer"/>, started
/// by <see cref="Start"/>) makes every OpenAL call; a render thread renders the one-shot recipes for it
/// (<see cref="RecipeRenderer"/>). Nothing is sent over a session: each game makes the sounds of what it
/// sees. With no device (or <c>Engine:Audio:Enabled</c> false) no thread starts and every call does nothing. Main thread
/// only; allocates nothing after start.
/// </summary>
public sealed partial class AudioService : IDisposable
{
    /// <summary>The most one-shots and loops one iteration can queue.</summary>
    public const int MaxEventsPerIteration = AudioExchange.MaxEvents;

    /// <summary>
    /// How long a loop keeps playing after it was last sustained, in seconds: frame systems sustain only in iterations
    /// that build a frame, and fixed systems only in iterations that run a step, so a loop outlasts the gaps between.
    /// </summary>
    public const double HoldSeconds = 0.1;

    /// <summary>How often the audio thread logs its counts at Debug while sounds play, in seconds.</summary>
    private const double StatsSeconds = 10;

    /// <summary>How long the audio thread waits between rounds, in milliseconds.</summary>
    private const int PumpMilliseconds = 4;

    private readonly AudioSettings _settings;
    private readonly string _cacheDirectory;
    private readonly ILogger<AudioService> _logger;
    private readonly AudioExchange _exchange = new();
    private readonly SoundEvent[] _events = new SoundEvent[MaxEventsPerIteration];
    private readonly SoundLoop[] _loops = new SoundLoop[AudioExchange.MaxLoops];
    private readonly SoundLoop[] _held = new SoundLoop[AudioExchange.MaxLoops];
    private readonly double[] _heldUntil = new double[AudioExchange.MaxLoops];
    private int _heldCount;
    private IAudioDevice _device = new NullAudioDevice();
    private AudioMixer? _mixer;
    private Thread? _thread;
    private Thread? _renderThread;
    private volatile bool _stopping;
    private volatile bool _faulted;
    private int _eventCount;
    private int _loopCount;
    private long _droppedHere;
    private bool _warnedFull;

    public AudioService(IOptions<EngineOptions> options, ILogger<AudioService> logger)
    {
        _settings = options.Value.Audio;
        _cacheDirectory = Path.Combine(options.Value.Application.DataDirectory(), "SoundCache");
        _logger = logger;
        Enabled = _settings.Enabled;
        Volume = _settings.Volume;
        MusicVolume = _settings.MusicVolume;
        EffectsVolume = _settings.EffectsVolume;
    }

    /// <summary>The recipes sounds name.</summary>
    public SoundRecipes Recipes { get; } = new();

    /// <summary>What the audio thread has done.</summary>
    public AudioStats Stats { get; } = new();

    /// <summary>The rate recipes render at.</summary>
    public int SampleRate => _settings.SampleRate;

    /// <summary>The most sounds playing at once.</summary>
    public int MaxVoices => _settings.MaxVoices;

    /// <summary>The device's name.</summary>
    public string DeviceName => _device.Name;

    /// <summary>
    /// True once every one-shot recipe and sound from files registered so far has its variations in buffers (a file that
    /// cannot be read counts as done, silent), or when no audio thread runs (nothing will prepare them). A recipe
    /// registered later makes it false until that one is ready.
    /// </summary>
    public bool AllRendered => !IsRunning || Stats.RenderedRecipes >= Recipes.PreparedCount;

    /// <summary>True while the audio thread runs: a device opened and nothing failed.</summary>
    public bool IsRunning => _thread is not null && !_faulted;

    /// <summary>False silences every sound (the console's <c>sound off</c>).</summary>
    public bool Enabled { get; set; }

    /// <summary>The master volume, 0 to 1.</summary>
    public float Volume { get; set; }

    /// <summary>Music's volume under the master, 0 to 1.</summary>
    public float MusicVolume { get; set; }

    /// <summary>The effects' volume under the master (every sound but music), 0 to 1.</summary>
    public float EffectsVolume { get; set; }

    /// <summary>
    /// For tests: queue sounds with no audio thread, to read them back with <see cref="QueuedEvents"/> and
    /// <see cref="QueuedLoops"/> until the next <see cref="Publish"/> or <see cref="ClearQueued"/>.
    /// </summary>
    internal bool QueueWithoutThread { get; set; }

    /// <summary>The events queued this iteration. For tests.</summary>
    internal ReadOnlySpan<SoundEvent> Queued => _events.AsSpan(0, _eventCount);

    /// <summary>The loops sustained this iteration. For tests.</summary>
    internal ReadOnlySpan<SoundLoop> Sustained => _loops.AsSpan(0, _loopCount);

    /// <summary>The loops held. For tests.</summary>
    internal ReadOnlySpan<SoundLoop> Held => _held.AsSpan(0, _heldCount);

    /// <summary>What waits for the audio thread. For tests.</summary>
    internal AudioExchange Exchange => _exchange;

    /// <summary>The one-shots queued since the last <see cref="Publish"/>.</summary>
    internal ReadOnlySpan<SoundEvent> QueuedEvents => _events.AsSpan(0, _eventCount);

    /// <summary>The loops sustained since the last <see cref="Publish"/>.</summary>
    internal ReadOnlySpan<SoundLoop> QueuedLoops => _loops.AsSpan(0, _loopCount);

    /// <summary>Forgets what was queued (tests).</summary>
    internal void ClearQueued()
    {
        _eventCount = 0;
        _loopCount = 0;
    }

    /// <summary>The loops held for the audio thread as of the last <see cref="Publish"/> (tests).</summary>
    internal int HeldLoops => _heldCount;

    /// <summary>
    /// The files of the sound from files <paramref name="recipe"/> as the render thread read them (channels, rate, length,
    /// loop, or why one could not be read); null before they are read, or with no audio thread.
    /// </summary>
    public IReadOnlyList<SoundFileInfo>? FilesOf(int recipe) => _mixer?.Renderer.FilesOf(recipe);

    /// <summary>Events dropped because an iteration or the exchange was full.</summary>
    public long DroppedEvents => _droppedHere + _exchange.DroppedEvents;

    /// <summary>
    /// Takes the opened <paramref name="device"/> and registers the engine's recipes. The game registers its own after
    /// this, before <see cref="Start"/> or after (the audio thread renders new one-shot recipes as they appear).
    /// </summary>
    public void Initialize(IAudioDevice device)
    {
        _device = device;
        EngineSounds.Register(Recipes);
    }

    /// <summary>
    /// Starts the audio thread, when a device is open. With none, the sounds from files are still looked for, so a run
    /// without sound names a file missing (logged at Error) as one with sound would.
    /// </summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        if (!_device.IsAvailable)
        {
            CheckFilesExist();
            return;
        }

        var cache = _settings.Cache ? new SoundCache(_cacheDirectory, _logger) : null;
        _mixer = new AudioMixer(
            _device,
            Recipes,
            Stats,
            _exchange,
            _settings.MaxVoices,
            _settings.SampleRate,
            _logger,
            cache,
            renderOnPump: false
        );
        // The one-shots render on a thread of their own, so the audio thread plays what is ready meanwhile.
        _renderThread = new Thread(_mixer.Renderer.Run)
        {
            Name = "Sound render",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread = new Thread(Run) { Name = "Audio", IsBackground = true };
        _renderThread.Start();
        _thread.Start();
        LogStarted(_settings.MaxVoices, _settings.SampleRate);
    }

    /// <summary>Queues a one-shot for the audio thread. Dropped while off, and past <see cref="MaxEventsPerIteration"/>.</summary>
    public void Play(in SoundEvent sound)
    {
        if (!Enabled || (_thread is null && !QueueWithoutThread))
        {
            return;
        }

        CheckRecipe(sound.Recipe);
        if (_eventCount == _events.Length)
        {
            _droppedHere++;
            if (!_warnedFull)
            {
                _warnedFull = true;
                LogFull(_events.Length);
            }

            return;
        }

        _events[_eventCount++] = sound;
    }

    /// <summary>Holds a stream for this iteration (<see cref="SoundLoop"/>).</summary>
    public void Sustain(in SoundLoop loop)
    {
        if (!Enabled || (_thread is null && !QueueWithoutThread))
        {
            return;
        }

        CheckRecipe(loop.Recipe);
        if (_loopCount < _loops.Length)
        {
            _loops[_loopCount++] = loop;
        }
    }

    /// <summary>
    /// Holds <paramref name="loop"/> for <paramref name="seconds"/> from <paramref name="now"/> without the game
    /// sustaining it: the console's <c>sound test</c> of a stream.
    /// </summary>
    public void SustainFor(in SoundLoop loop, double now, double seconds)
    {
        CheckRecipe(loop.Recipe);
        Hold(in loop, now + seconds);
    }

    /// <summary>
    /// The match's origin moved by <paramref name="metres"/> (<c>OriginShifts</c>): the events queued this iteration, the
    /// loops sustained and held, and what waits for the audio thread move the other way, and the audio thread moves the
    /// one-shots playing. Main thread.
    /// </summary>
    public void Shift(Vector3 metres)
    {
        for (var i = 0; i < _eventCount; i++)
        {
            _events[i].Position -= metres;
        }

        for (var i = 0; i < _loopCount; i++)
        {
            _loops[i].Position -= metres;
        }

        for (var i = 0; i < _heldCount; i++)
        {
            _held[i].Position -= metres;
        }

        _exchange.Shift(metres);
    }

    /// <summary>
    /// Hands this iteration's events and loops, the listener, and the volumes to the audio thread, and starts the next
    /// iteration's lists. Main thread, once a main-loop iteration, after the frame systems placed the camera.
    /// </summary>
    public void Publish(in ListenerPose listener, double now)
    {
        if (_thread is null)
        {
            ClearQueued();
            return;
        }

        for (var i = 0; i < _loopCount; i++)
        {
            Hold(in _loops[i], now + HoldSeconds);
        }

        // Loops no longer held stop; the rest keep their order.
        var kept = 0;
        for (var i = 0; i < _heldCount; i++)
        {
            if (_heldUntil[i] >= now && Enabled)
            {
                _held[kept] = _held[i];
                _heldUntil[kept] = _heldUntil[i];
                kept++;
            }
        }

        _heldCount = kept;
        _exchange.Post(
            _events.AsSpan(0, _eventCount),
            _held.AsSpan(0, _heldCount),
            in listener,
            new MixLevels(
                Math.Clamp(Volume, 0, 1),
                Math.Clamp(EffectsVolume, 0, 1),
                Math.Clamp(MusicVolume, 0, 1),
                Enabled
            )
        );
        ClearQueued();
    }

    /// <summary>Holds <paramref name="loop"/> until <paramref name="until"/>, in place of the same recipe's and key's.</summary>
    private void Hold(in SoundLoop loop, double until)
    {
        for (var i = 0; i < _heldCount; i++)
        {
            if (_held[i].Recipe == loop.Recipe && _held[i].Key == loop.Key)
            {
                _held[i] = loop;
                _heldUntil[i] = Math.Max(_heldUntil[i], until);
                return;
            }
        }

        if (_heldCount < _held.Length)
        {
            _held[_heldCount] = loop;
            _heldUntil[_heldCount] = until;
            _heldCount++;
        }
    }

    /// <summary>
    /// Renders recipe <paramref name="recipe"/> to a WAV file in <paramref name="folder"/> named after it, and returns
    /// its path: a one-shot's variations in a row, 0.3 s apart, from the lowest level to the highest; a stream for 8 s,
    /// its level rising from 0 to 1 and falling back. Main thread (the console's <c>sound wav</c>); allocates.
    /// </summary>
    public string WriteWav(int recipe, string folder)
    {
        CheckRecipe(recipe);
        Directory.CreateDirectory(folder);
        var rate = _settings.SampleRate;
        var samples = Recipes[recipe] switch
        {
            OneShotRecipe oneShot => RenderOneShots(oneShot, rate),
            StreamRecipe stream => RenderStream(stream, rate),
            _ => throw new InvalidOperationException(
                $"{Recipes.NameOf(recipe)} is read from files: they are its sound already."
            ),
        };
        var pcm = new short[samples.Count];
        Dsp.ToPcm16(samples.ToArray(), pcm);
        var path = Path.Combine(folder, Recipes.NameOf(recipe) + ".wav");
        WavWriter.Write(path, pcm, rate);
        return path;
    }

    private static List<float> RenderOneShots(OneShotRecipe recipe, int rate)
    {
        var all = new List<float>();
        var scratch = new float[recipe.SampleCount(rate)];
        var gap = (int)(rate * 0.3f);
        for (var level = 0; level < recipe.Levels; level++)
        {
            for (var variation = 0; variation < recipe.VariationsPerLevel; variation++)
            {
                var length = recipe.RenderVariation(scratch, rate, level, variation);
                all.AddRange(scratch.AsSpan(0, length));
                all.AddRange(new float[gap]);
            }
        }

        return all;
    }

    private static List<float> RenderStream(StreamRecipe recipe, int rate)
    {
        var generator = recipe.Create(rate);
        generator.Reset(1);
        var all = new List<float>();
        var block = new float[AudioMixer.BlockSamples];
        var blocks = (int)(8f * rate / block.Length);
        for (var b = 0; b < blocks; b++)
        {
            var t = b / (float)(blocks - 1);
            var level = t < 0.5f ? t * 2 : (1 - t) * 2;
            generator.Render(block, level);
            all.AddRange(block);
        }

        return all;
    }

    private void CheckRecipe(int recipe)
    {
        if ((uint)recipe >= (uint)Recipes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(recipe), recipe, "No such sound recipe.");
        }
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        var mixer = _mixer!;
        var nextStats = StatsSeconds;
        var played = 0L;
        try
        {
            while (!_stopping)
            {
                var now = clock.Elapsed.TotalSeconds;
                mixer.Pump(now);
                if (now >= nextStats)
                {
                    nextStats = now + StatsSeconds;
                    var stats = Stats;
                    if (stats.Played != played)
                    {
                        played = stats.Played;
                        LogStats(
                            stats.OneShots,
                            stats.Streams,
                            stats.Played,
                            stats.Culled,
                            stats.Stolen,
                            stats.Refused,
                            stats.Underruns
                        );
                    }
                }

                Thread.Sleep(PumpMilliseconds);
            }
        }
        catch (Exception ex)
        {
            // Sound failing is no reason to end the game: it goes on silent.
            _faulted = true;
            LogFaulted(ex);
        }
        finally
        {
            try
            {
                mixer.Dispose();
            }
            catch (Exception ex)
            {
                LogFaulted(ex);
            }
        }
    }

    /// <summary>
    /// Stops the audio thread (it frees its sources and buffers). Returns false when it did not stop within 2 s: it may
    /// still be using the device (freeing its sources; the sound render thread touches no device), so the device is left open
    /// (<see cref="IAudioDevice.LeaveOpen"/>) and neither the loop's shutdown nor the container closes it under the
    /// thread. Idempotent.
    /// </summary>
    public bool Stop()
    {
        if (_thread is null)
        {
            return true;
        }

        // The render thread first, so no recipe finishes for a mixer being freed. It stops after the recipe it renders
        // now; one still rendering past the wait is a background thread that touches no device.
        _mixer?.Renderer.Stop();
        _renderThread?.Join(TimeSpan.FromSeconds(2));
        _renderThread = null;
        _stopping = true;
        var stopped = _thread.Join(TimeSpan.FromSeconds(2));
        if (!stopped)
        {
            LogStuck();
            _device.LeaveOpen();
        }

        _thread = null;
        return stopped;
    }

    /// <summary>Stops the thread. The device belongs to the container, which closes it.</summary>
    public void Dispose() => Stop();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Audio: thread started, {Voices} voices, recipes rendered at {Rate} Hz"
    )]
    private partial void LogStarted(int voices, int rate);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Audio: more than {Most} sounds waited in one iteration; the rest are dropped (reported once)"
    )]
    private partial void LogFull(int most);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Audio: {OneShots} one-shot(s) and {Streams} stream(s) playing; since start {Played} played, {Culled} too quiet, {Stolen} cut short, {Refused} refused, {Underruns} stream underrun(s)"
    )]
    private partial void LogStats(
        int oneShots,
        int streams,
        long played,
        long culled,
        long stolen,
        long refused,
        long underruns
    );

    /// <summary>With no audio thread to read them: each file of each sound from files is looked for, and a missing one logged.</summary>
    private void CheckFilesExist()
    {
        for (var id = 0; id < Recipes.Count; id++)
        {
            if (Recipes[id] is not FileRecipe files)
            {
                continue;
            }

            foreach (var path in files.Paths)
            {
                if (!File.Exists(path))
                {
                    LogFileMissing(Recipes.NameOf(id), path);
                }
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Audio: sound {Name}: the file {Path} cannot be read (no such file); it plays nothing"
    )]
    private partial void LogFileMissing(string name, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audio: the audio thread failed; the game goes on without sound")]
    private partial void LogFaulted(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Audio: the audio thread did not stop within 2 s; the audio device is left open"
    )]
    private partial void LogStuck();
}
