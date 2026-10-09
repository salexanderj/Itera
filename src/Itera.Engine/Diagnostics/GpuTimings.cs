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
/// </summary>
public sealed class GpuTimings
{
    /// <summary>Number of <see cref="GpuPass"/> values.</summary>
    public const int PassCount = 12;

    /// <summary>How many whole-frame times an interval keeps one by one: 10 s at over 1,600 frames a second.</summary>
    public const int MostFrameTimes = 16_384;

    private readonly object _lock = new();
    private readonly long[] _count = new long[PassCount];
    private readonly long[] _sumNanoseconds = new long[PassCount];
    private readonly long[] _maxNanoseconds = new long[PassCount];
    private readonly float[] _frameTimes = new float[MostFrameTimes];
    private readonly long[] _windowCount = new long[PassCount];
    private readonly long[] _windowSumNanoseconds = new long[PassCount];
    private readonly long[] _windowMaxNanoseconds = new long[PassCount];
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

    /// <summary>Adds one frame's time for a pass. Render thread.</summary>
    public void Record(GpuPass pass, long nanoseconds)
    {
        var i = (int)pass;
        if (pass == GpuPass.Frame)
        {
            Interlocked.Exchange(ref _lastFrameNanoseconds, nanoseconds);
            Interlocked.Increment(ref _framesMeasured);
        }

        lock (_lock)
        {
            _count[i]++;
            _sumNanoseconds[i] += nanoseconds;
            _maxNanoseconds[i] = Math.Max(_maxNanoseconds[i], nanoseconds);
            _windowCount[i]++;
            _windowSumNanoseconds[i] += nanoseconds;
            _windowMaxNanoseconds[i] = Math.Max(_windowMaxNanoseconds[i], nanoseconds);
            if (pass == GpuPass.Frame && _frameTimeCount < _frameTimes.Length)
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
    public GpuPassTimes Take(GpuPass pass)
    {
        var i = (int)pass;
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
    public GpuPassTimes Window(GpuPass pass)
    {
        var i = (int)pass;
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
