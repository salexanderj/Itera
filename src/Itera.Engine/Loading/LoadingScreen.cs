using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Loading;

/// <summary>Where the loading screen is: not begun, drawn over nothing, fading out over the world, or gone.</summary>
public enum LoadingPhase
{
    NotBegun,
    Loading,
    Fading,
    Done,
}

/// <summary>
/// The loading screen's state. From the first iteration of the main loop until every
/// <see cref="ILoadingTask"/> is done, or <c>Engine:Loading:MaxSeconds</c> have passed, the render thread draws the
/// loading shader instead of the world and the game gets no input; everything else runs as usual, since most of what
/// loading waits for (streaming, uploads, navigation's fields, character kinds) moves on only while the loop runs. As it
/// ends, while it still covers the picture, it collects the garbage loading left (<see cref="Collections"/>). It then
/// fades out over <see cref="FadeSeconds"/> over the world. Main thread; <see cref="Update"/> allocates nothing except
/// the log lines at the end.
/// </summary>
public sealed partial class LoadingScreen
{
    /// <summary>How long the loading screen takes to fade out over the world, in seconds.</summary>
    public const double FadeSeconds = 0.3;

    private readonly LoadingSettings _settings;
    private readonly ILogger<LoadingScreen> _logger;
    private ILoadingTask[] _tasks = [];
    private LoadingProgress[] _progress = [];
    private double[] _finishedAt = [];
    private int[] _percent = [];
    private double _start;
    private double _now;
    private double _fadeStart;
    private int _version;
    private long _droppedSteps;

    public LoadingScreen(IOptions<EngineOptions> options, ILogger<LoadingScreen> logger)
    {
        _settings = options.Value.Loading;
        _logger = logger;
    }

    /// <summary>Where the loading screen is.</summary>
    public LoadingPhase Phase { get; private set; }

    /// <summary>True while the loading screen covers the picture and the game waits: the world is not drawn.</summary>
    public bool Active => Phase == LoadingPhase.Loading;

    /// <summary>True while the loading screen is drawn: covering the picture, or fading out over it.</summary>
    public bool Drawn => Phase is LoadingPhase.Loading or LoadingPhase.Fading;

    /// <summary>How opaque the loading screen is: 1 while loading, falling to 0 as it fades.</summary>
    public float Alpha =>
        Phase switch
        {
            LoadingPhase.Loading => 1,
            LoadingPhase.Fading => (float)Math.Clamp(1 - (_now - _fadeStart) / FadeSeconds, 0, 1),
            _ => 0,
        };

    /// <summary>Seconds since loading began, for the shader's motion.</summary>
    public float Seconds => (float)(_now - _start);

    /// <summary>How many tasks it waits for.</summary>
    public int TaskCount => _tasks.Length;

    /// <summary>The full collections run as loading ended (<see cref="Collect"/>), since the screen was made.</summary>
    public int Collections { get; private set; }

    /// <summary>
    /// Changes whenever a task finishes or its whole percentage changes, so the text is drawn again only then.
    /// </summary>
    public int Version => _version;

    /// <summary>Task <paramref name="index"/>'s name.</summary>
    public string TaskName(int index) => _tasks[index].Name;

    /// <summary>Task <paramref name="index"/>'s progress when last read.</summary>
    public LoadingProgress TaskProgress(int index) => _progress[index];

    /// <summary>Fixed steps the loop dropped while loading, reported in the line that ends it rather than as a Warning.</summary>
    public void CountDroppedSteps(int steps) => _droppedSteps += steps;

    /// <summary>Takes the tasks to wait for, in the order they are shown.</summary>
    public void Initialize(IEnumerable<ILoadingTask> tasks)
    {
        _tasks = [.. tasks];
        _progress = new LoadingProgress[_tasks.Length];
        _finishedAt = new double[_tasks.Length];
        _percent = new int[_tasks.Length];
        Array.Fill(_progress, LoadingProgress.Waiting);
        Array.Fill(_percent, -1);
    }

    /// <summary>Starts loading at <paramref name="nowSeconds"/>; with <c>Engine:Loading:Enabled</c> false it is done at once.</summary>
    public void Begin(double nowSeconds)
    {
        _start = nowSeconds;
        _now = nowSeconds;
        if (!_settings.Enabled)
        {
            Phase = LoadingPhase.Done;
            return;
        }

        Phase = LoadingPhase.Loading;
        LogBegun(_tasks.Length);
    }

    /// <summary>The match ended: no tasks, not begun. Its next match calls <see cref="Initialize"/> and <see cref="Begin"/>.</summary>
    public void End()
    {
        Phase = LoadingPhase.NotBegun;
        _tasks = [];
        _progress = [];
        _finishedAt = [];
        _percent = [];
        _droppedSteps = 0;
        _version++;
    }

    /// <summary>
    /// Covers the picture again and waits for every task afresh, from <paramref name="nowSeconds"/>: after the world was
    /// reset (<see cref="Voxels.VoxelWorld.Reset"/>), whose columns are all generated again. Tasks that stay done report
    /// done at the next <see cref="Update"/>. Nothing with <c>Engine:Loading:Enabled</c> false.
    /// </summary>
    public void BeginAgain(double nowSeconds)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        Array.Fill(_progress, LoadingProgress.Waiting);
        Array.Fill(_percent, -1);
        _droppedSteps = 0;
        _version++;
        Begin(nowSeconds);
    }

    /// <summary>
    /// Reads every unfinished task, ends loading when all are done or the time is up, and ends the fade. Once per
    /// main-loop iteration, after the fixed steps.
    /// </summary>
    public void Update(double nowSeconds)
    {
        _now = nowSeconds;
        if (Phase == LoadingPhase.Fading && nowSeconds - _fadeStart >= FadeSeconds)
        {
            Phase = LoadingPhase.Done;
        }

        if (Phase != LoadingPhase.Loading)
        {
            return;
        }

        var allDone = true;
        for (var i = 0; i < _tasks.Length; i++)
        {
            if (_progress[i].Done)
            {
                continue;
            }

            var progress = _tasks[i].Progress;
            _progress[i] = progress;
            var percent = progress.Fraction < 0 ? -1 : (int)(progress.Fraction * 100);
            if (progress.Done)
            {
                _finishedAt[i] = nowSeconds - _start;
                _version++;
            }
            else
            {
                allDone = false;
                if (percent != _percent[i])
                {
                    _version++;
                }
            }

            _percent[i] = percent;
        }

        var elapsed = nowSeconds - _start;
        if (allDone)
        {
            var finished = Summary();
            LogDone(elapsed, finished, _droppedSteps);
            Collect();
            StartFade(nowSeconds);
        }
        else if (elapsed >= _settings.MaxSeconds)
        {
            var unfinished = Unfinished();
            var finished = Summary();
            LogTimedOut(elapsed, unfinished, finished, _droppedSteps);
            Collect();
            StartFade(nowSeconds);
        }
    }

    /// <summary>
    /// Collects every generation, compacting the large objects too, while the screen still covers the picture. What
    /// loading made and dropped (a world's generation, navigation's grid and fields, a match's first bodies) outlived the
    /// young collections that ran during it, into the oldest generation, which a full collection alone frees: left to
    /// play, that came a minute or so in as one step of 10 to 12 ms, freeing about 210 MB of a 760 MB heap.
    /// </summary>
    private void Collect()
    {
        var before = GC.GetTotalMemory(forceFullCollection: false);
        var start = Stopwatch.GetTimestamp();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var after = GC.GetTotalMemory(forceFullCollection: false);
        Collections++;
        const double Megabyte = 1024.0 * 1024.0;
        LogCollected((before - after) / Megabyte, after / Megabyte, milliseconds);
    }

    private void StartFade(double nowSeconds)
    {
        Phase = LoadingPhase.Fading;
        _fadeStart = nowSeconds;
        _version++;
    }

    /// <summary>Each finished task and when it finished, for the log: "Sounds 0.8 s, World 2.4 s".</summary>
    private string Summary()
    {
        var parts = new List<string>(_tasks.Length);
        for (var i = 0; i < _tasks.Length; i++)
        {
            if (_progress[i].Done)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_tasks[i].Name} {_finishedAt[i]:F1} s"));
            }
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private string Unfinished()
    {
        var names = new List<string>(_tasks.Length);
        for (var i = 0; i < _tasks.Length; i++)
        {
            if (!_progress[i].Done)
            {
                names.Add(_tasks[i].Name);
            }
        }

        return string.Join(", ", names);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loading: waiting for {Tasks} task(s)")]
    private partial void LogBegun(int tasks);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Loading: done after {Seconds:F1} s ({Finished}); {Dropped} fixed step(s) dropped while loading"
    )]
    private partial void LogDone(double seconds, string finished, long dropped);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Loading: stopped waiting after {Seconds:F1} s (Engine:Loading:MaxSeconds); not finished: {Unfinished}; finished: {Finished}; {Dropped} fixed step(s) dropped while loading"
    )]
    private partial void LogTimedOut(double seconds, string unfinished, string finished, long dropped);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Loading: collected {FreedMb:F0} MB that loading left in {Milliseconds:F0} ms; {HeapMb:F0} MB in use"
    )]
    private partial void LogCollected(double freedMb, double heapMb, double milliseconds);
}
