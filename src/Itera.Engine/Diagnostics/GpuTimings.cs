namespace Itera.Engine.Diagnostics;

/// <summary>The parts of a frame whose GPU time is measured.</summary>
public enum GpuPass
{
    /// <summary>The whole command buffer, from its first command to its last.</summary>
    Frame = 0,

    /// <summary>The mesh copies from staging, when the frame has any.</summary>
    Upload = 1,

    /// <summary>The world: its shadow maps, its meshes and the models', and the sky (<c>MeshPass</c>).</summary>
    Voxels = 2,

    /// <summary>The debug lines, when <c>lines</c> shows any.</summary>
    DebugLines = 3,

    /// <summary>Part of <see cref="Voxels"/>: the models drawn in the world's pass, when the frame has any.</summary>
    Models = 4,

    /// <summary>The UI: its copy when it changed, and its draw over the picture when anything is shown.</summary>
    Ui = 5,

    /// <summary>FSR 1's upscale and sharpening, when the render scale is below 1.</summary>
    Upscale = 6,

    /// <summary>The particles made and moved (<c>particle-spawn.comp</c>, <c>particle-simulate.comp</c>), when any live.</summary>
    ParticleSimulate = 7,

    /// <summary>The particle cubes and sprites drawn, when any live.</summary>
    Particles = 8,

    /// <summary>The loading screen (<c>loading.frag</c>), while it covers the picture or fades out.</summary>
    Loading = 9,

    /// <summary>SMAA's three passes (<c>SmaaPass</c>), when the picture is anti-aliased.</summary>
    AntiAlias = 10,

    /// <summary>Part of <see cref="Voxels"/>: the sun's shadow cascades drawn (<c>MeshPass</c>).</summary>
    ShadowMaps = 11,

    /// <summary>Part of <see cref="Voxels"/>: the mesh assets drawn in the world's pass (<c>MeshAssetPass</c>), when any are.</summary>
    Meshes = 12,

    /// <summary>Part of <see cref="Voxels"/>: the posed mesh assets skinned before the cascades (<c>MeshAssetPass.RecordSkinning</c>), when any are.</summary>
    Skinning = 13,

    /// <summary>
    /// A game's passes (<c>IRenderPass</c>), all of them, when any ran: the sum of their own times, each of which
    /// <see cref="GpuTimings.GamePass"/> gives by name. Those at the Compute, Opaque, and Transparent stages run inside the
    /// mesh pass, so their time is also part of <see cref="Voxels"/>; those at the Scene and Screen stages are not.
    /// </summary>
    Game = 14,

    /// <summary>
    /// The image views drawn this frame (<c>RenderViewPass</c>: each due view's light clusters, its world, models, mesh
    /// assets, and sky, and its copy into the image the HUD reads), when any were; after the world's pass, not part of it.
    /// </summary>
    ImageViews = 15,
}

/// <summary>Average and longest GPU time of one pass over an interval.</summary>
/// <param name="Count">Frames measured.</param>
/// <param name="AverageMs">Mean time in milliseconds, or 0 without measurements.</param>
/// <param name="MaxMs">Longest time in milliseconds.</param>
public readonly record struct GpuPassTimes(long Count, double AverageMs, double MaxMs);

/// <summary>
/// GPU pass times, measured with timestamp queries on the render thread (<c>GpuTimer</c>) and read once per stats
/// interval by <see cref="FrameStats"/> on the main thread. A lock keeps each pass's count, sum, and maximum together,
/// and the whole frame's times one by one (<see cref="TakeFrameTimes"/>, the first <see cref="MostFrameTimes"/> of an
/// interval) for their percentiles; it is taken once per pass per frame and once per pass per interval, so it is never
/// contended for long. Recording and taking never allocate. A second set of sums, the window, is started and read by the
/// control port's <c>gpu</c> request (<see cref="StartWindow"/>, <see cref="Window"/>) without touching the interval's.
/// A game's passes, up to <see cref="MostGamePasses"/>, are measured one by one after the engine's
/// (<see cref="GamePassNames"/>, <see cref="RecordGame"/>, <see cref="GamePass"/>), and their sum as <see cref="GpuPass.Game"/>.
/// </summary>
public sealed class GpuTimings
{
    /// <summary>Number of <see cref="GpuPass"/> values.</summary>
    public const int PassCount = 16;

    /// <summary>The game's passes measured one by one, at most.</summary>
    public const int MostGamePasses = 8;

    /// <summary>The engine's passes and the game's, each one entry.</summary>
    private const int Entries = PassCount + MostGamePasses;

    /// <summary>How many whole-frame times an interval keeps one by one: 10 s at over 1,600 frames a second.</summary>
    public const int MostFrameTimes = 16_384;

    private readonly object _lock = new();
    private readonly long[] _count = new long[Entries];
    private readonly long[] _sumNanoseconds = new long[Entries];
    private readonly long[] _maxNanoseconds = new long[Entries];
    private readonly float[] _frameTimes = new float[MostFrameTimes];
    private readonly long[] _windowCount = new long[Entries];
    private readonly long[] _windowSumNanoseconds = new long[Entries];
    private readonly long[] _windowMaxNanoseconds = new long[Entries];
    private int _frameTimeCount;
    private long _lastFrameNanoseconds;
    private long _framesMeasured;
    private volatile bool _enabled;

    /// <summary>True once the device can measure; false when the graphics queue has no timestamps.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// GPU time of the last frame measured, in milliseconds, or 0 before the first. Any thread; unlike <see cref="Take"/>
    /// it changes nothing, so the stats line can read it without taking the numbers from <see cref="FrameStats"/>.
    /// </summary>
    public double LastFrameMilliseconds => Interlocked.Read(ref _lastFrameNanoseconds) / 1e6;

    /// <summary>
    /// Whole frames measured so far: a new <see cref="LastFrameMilliseconds"/> each time it rises (dynamic render
    /// scaling reads each once). Any thread.
    /// </summary>
    public long FramesMeasured => Interlocked.Read(ref _framesMeasured);

    /// <summary>The game's passes measured one by one, by their index (at most <see cref="MostGamePasses"/>); set once before the render thread starts.</summary>
    public IReadOnlyList<string> GamePassNames { get; set; } = [];

    /// <summary>Adds one frame's time for a pass. Render thread.</summary>
    public void Record(GpuPass pass, long nanoseconds)
    {
        if (pass == GpuPass.Frame)
        {
            Interlocked.Exchange(ref _lastFrameNanoseconds, nanoseconds);
            Interlocked.Increment(ref _framesMeasured);
        }

        Add((int)pass, nanoseconds, pass == GpuPass.Frame);
    }

    /// <summary>Adds one frame's time for the game's pass <paramref name="index"/> (<see cref="GamePassNames"/>). Render thread.</summary>
    public void RecordGame(int index, long nanoseconds)
    {
        if ((uint)index < MostGamePasses)
        {
            Add(PassCount + index, nanoseconds, frame: false);
        }
    }

    private void Add(int i, long nanoseconds, bool frame)
    {
        lock (_lock)
        {
            _count[i]++;
            _sumNanoseconds[i] += nanoseconds;
            _maxNanoseconds[i] = Math.Max(_maxNanoseconds[i], nanoseconds);
            _windowCount[i]++;
            _windowSumNanoseconds[i] += nanoseconds;
            _windowMaxNanoseconds[i] = Math.Max(_windowMaxNanoseconds[i], nanoseconds);
            if (frame && _frameTimeCount < _frameTimes.Length)
            {
                _frameTimes[_frameTimeCount++] = nanoseconds / 1e6f;
            }
        }
    }

    /// <summary>
    /// Copies the whole frame's times since the last call, in milliseconds, into <paramref name="times"/> (as many as fit)
    /// and starts again; returns how many it copied. Main thread.
    /// </summary>
    public int TakeFrameTimes(Span<float> times)
    {
        lock (_lock)
        {
            var count = Math.Min(_frameTimeCount, times.Length);
            _frameTimes.AsSpan(0, count).CopyTo(times);
            _frameTimeCount = 0;
            return count;
        }
    }

    /// <summary>Returns a pass's times since the last call and starts a new interval. Main thread.</summary>
    public GpuPassTimes Take(GpuPass pass) => TakeEntry((int)pass);

    /// <summary>The game's pass <paramref name="index"/>'s times since the last call, as <see cref="Take(GpuPass)"/>. Main thread.</summary>
    public GpuPassTimes TakeGame(int index) => (uint)index < MostGamePasses ? TakeEntry(PassCount + index) : default;

    private GpuPassTimes TakeEntry(int i)
    {
        long count,
            sum,
            max;
        lock (_lock)
        {
            count = _count[i];
            sum = _sumNanoseconds[i];
            max = _maxNanoseconds[i];
            _count[i] = 0;
            _sumNanoseconds[i] = 0;
            _maxNanoseconds[i] = 0;
        }

        return count == 0 ? default : new GpuPassTimes(count, sum / 1e6 / count, max / 1e6);
    }

    /// <summary>Starts the window again: its sums count from the next frame measured. Any thread.</summary>
    public void StartWindow()
    {
        lock (_lock)
        {
            Array.Clear(_windowCount);
            Array.Clear(_windowSumNanoseconds);
            Array.Clear(_windowMaxNanoseconds);
        }
    }

    /// <summary>A pass's times since <see cref="StartWindow"/>, leaving them in place. Any thread.</summary>
    public GpuPassTimes Window(GpuPass pass) => WindowEntry((int)pass);

    /// <summary>The game's pass <paramref name="index"/>'s times since <see cref="StartWindow"/>. Any thread.</summary>
    public GpuPassTimes GamePass(int index) => (uint)index < MostGamePasses ? WindowEntry(PassCount + index) : default;

    private GpuPassTimes WindowEntry(int i)
    {
        long count,
            sum,
            max;
        lock (_lock)
        {
            count = _windowCount[i];
            sum = _windowSumNanoseconds[i];
            max = _windowMaxNanoseconds[i];
        }

        return count == 0 ? default : new GpuPassTimes(count, sum / 1e6 / count, max / 1e6);
    }
}
