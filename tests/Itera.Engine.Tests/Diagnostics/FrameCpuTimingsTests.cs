using System.Diagnostics;
using Itera.Engine.Diagnostics;

namespace Itera.Engine.Tests.Diagnostics;

public sealed class FrameCpuTimingsTests
{
    [Fact]
    public void Take_returns_the_count_the_average_and_the_longest_time_then_starts_again()
    {
        var timings = new FrameCpuTimings();
        timings.Record(FrameCpuInterval.Build, Ticks(1));
        timings.Record(FrameCpuInterval.Build, Ticks(3));
        timings.Record(FrameCpuInterval.Present, Ticks(5));

        var build = timings.Take(FrameCpuInterval.Build);
        var present = timings.Take(FrameCpuInterval.Present);

        Assert.Equal(2, build.Count);
        Assert.Equal(2.0, build.AverageMs, 6);
        Assert.Equal(3.0, build.MaxMs, 6);
        Assert.Equal(1, present.Count);
        Assert.Equal(5.0, present.AverageMs, 6);
        Assert.Equal(default, timings.Take(FrameCpuInterval.Build));
        Assert.Equal(default, timings.Take(FrameCpuInterval.Record));
    }

    [Fact]
    public void Recording_from_another_thread_loses_nothing()
    {
        var timings = new FrameCpuTimings();
        var writer = new Thread(() =>
        {
            for (var i = 1; i <= 100_000; i++)
            {
                timings.Record(FrameCpuInterval.Record, i);
            }
        });
        writer.Start();
        long count = 0;
        double sumMs = 0,
            maxMs = 0;
        while (writer.IsAlive)
        {
            Add(timings.Take(FrameCpuInterval.Record));
        }

        writer.Join();
        Add(timings.Take(FrameCpuInterval.Record));

        Assert.Equal(100_000, count);
        Assert.Equal(100_000L * 100_001 / 2 * 1000.0 / Stopwatch.Frequency, sumMs, 3);
        Assert.Equal(100_000 * 1000.0 / Stopwatch.Frequency, maxMs, 6);

        void Add(FrameCpuTimes taken)
        {
            count += taken.Count;
            sumMs += taken.AverageMs * taken.Count;
            maxMs = Math.Max(maxMs, taken.MaxMs);
        }
    }

    [Fact]
    public void Recording_and_taking_allocate_nothing()
    {
        var timings = new FrameCpuTimings();
        timings.Record(FrameCpuInterval.FenceAndAcquire, 1);
        timings.Take(FrameCpuInterval.FenceAndAcquire);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            timings.Record(FrameCpuInterval.FenceAndAcquire, i);
            timings.Record(FrameCpuInterval.RequestWait, i);
            timings.Take(FrameCpuInterval.FenceAndAcquire);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static long Ticks(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
}
