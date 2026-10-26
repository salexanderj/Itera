using System.Diagnostics;

namespace Itera.Engine.Diagnostics;

/// <summary>The parts of a frame's CPU work that are timed, on the main thread and on the render thread.</summary>
public enum FrameCpuInterval
{
    /// <summary>From the render thread's request for a frame until the main thread takes it (<c>FrameExchange</c>).</summary>
    RequestWait = 0,

    /// <summary>The main thread's frame build, from taking the request until it publishes the frame.</summary>
    Build = 1,

    /// <summary>The render thread's recording, from receiving the frame until its submit returns (<c>FrameRenderer</c>).</summary>
    Record = 2,

    /// <summary>The render thread's present, from just before <c>vkQueuePresentKHR</c> until it returns.</summary>
    Present = 3,

    /// <summary>The render thread's wait on the frame slot's fence and its acquire of a swapchain image.</summary>
    FenceAndAcquire = 4,
}

/// <summary>Average and longest time of one CPU interval over a stats interval.</summary>
/// <param name="Count">Frames measured.</param>
/// <param name="AverageMs">Mean time in milliseconds, or 0 without measurements.</param>
/// <param name="MaxMs">Longest time in milliseconds.</param>
public readonly record struct FrameCpuTimes(long Count, double AverageMs, double MaxMs);

/// <summary>
/// The frame's CPU times (<see cref="FrameCpuInterval"/>), in <see cref="Stopwatch.GetTimestamp"/> ticks, recorded on the
/// thread where each interval ends and read once per stats interval by <see cref="FrameStats"/> on the main thread. A
/// lock keeps each interval's count, sum, and maximum together; it is taken once per interval per frame and once per
/// interval per stats interval, so it is never contended for long. Recording and taking never allocate.
/// </summary>
public sealed class FrameCpuTimings
{
    /// <summary>Number of <see cref="FrameCpuInterval"/> values.</summary>
    public const int IntervalCount = 5;

    private readonly object _lock = new();
    private readonly long[] _count = new long[IntervalCount];
    private readonly long[] _sumTicks = new long[IntervalCount];
    private readonly long[] _maxTicks = new long[IntervalCount];

    /// <summary>Adds one frame's time for an interval, in <see cref="Stopwatch"/> ticks. Any thread.</summary>
    public void Record(FrameCpuInterval interval, long ticks)
    {
        var i = (int)interval;
        lock (_lock)
        {
            _count[i]++;
            _sumTicks[i] += ticks;
            _maxTicks[i] = Math.Max(_maxTicks[i], ticks);
        }
    }

    /// <summary>Returns an interval's times since the last call and starts again. Main thread.</summary>
    public FrameCpuTimes Take(FrameCpuInterval interval)
    {
        var i = (int)interval;
        long count,
            sum,
            max;
        lock (_lock)
        {
            count = _count[i];
            sum = _sumTicks[i];
            max = _maxTicks[i];
            _count[i] = 0;
            _sumTicks[i] = 0;
            _maxTicks[i] = 0;
        }

        return count == 0 ? default : new FrameCpuTimes(count, Ms(sum) / count, Ms(max));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
