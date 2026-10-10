using Itera.Engine.Diagnostics;

namespace Itera.Engine.Tests.Diagnostics;

public sealed class GpuTimingsTests
{
    [Fact]
    public void Take_returns_the_average_and_the_longest_time_then_starts_again()
    {
        var timings = new GpuTimings();
        timings.Record(GpuPass.Voxels, 1_000_000);
        timings.Record(GpuPass.Voxels, 3_000_000);
        timings.Record(GpuPass.Frame, 5_000_000);

        var sky = timings.Take(GpuPass.Voxels);
        var frame = timings.Take(GpuPass.Frame);

        Assert.Equal(new GpuPassTimes(2, 2.0, 3.0), sky);
        Assert.Equal(new GpuPassTimes(1, 5.0, 5.0), frame);
        Assert.Equal(default, timings.Take(GpuPass.Voxels));
    }

    [Fact]
    public void A_game_pass_is_timed_by_its_index_apart_from_the_engines_and_none_past_the_most()
    {
        var timings = new GpuTimings();
        timings.RecordGame(0, 1_000_000);
        timings.RecordGame(GpuTimings.MostGamePasses - 1, 2_000_000);
        timings.RecordGame(GpuTimings.MostGamePasses, 3_000_000);
        timings.RecordGame(-1, 3_000_000);
        timings.Record(GpuPass.Game, 3_000_000);

        Assert.Equal(new GpuPassTimes(1, 1.0, 1.0), timings.GamePass(0));
        Assert.Equal(new GpuPassTimes(1, 1.0, 1.0), timings.TakeGame(0));
        Assert.Equal(new GpuPassTimes(1, 2.0, 2.0), timings.TakeGame(GpuTimings.MostGamePasses - 1));
        Assert.Equal(default, timings.TakeGame(GpuTimings.MostGamePasses));
        Assert.Equal(default, timings.TakeGame(0));
        Assert.Equal(new GpuPassTimes(1, 3.0, 3.0), timings.Take(GpuPass.Game));
        Assert.Equal(default, timings.Take(GpuPass.Voxels));
    }

    [Fact]
    public void Recording_from_another_thread_loses_nothing()
    {
        var timings = new GpuTimings();
        var writer = new Thread(() =>
        {
            for (var i = 1; i <= 100_000; i++)
            {
                timings.Record(GpuPass.Frame, i);
            }
        });
        writer.Start();
        long count = 0;
        double sumMs = 0,
            maxMs = 0;
        while (writer.IsAlive)
        {
            var taken = timings.Take(GpuPass.Frame);
            count += taken.Count;
            sumMs += taken.AverageMs * taken.Count;
            maxMs = Math.Max(maxMs, taken.MaxMs);
        }

        writer.Join();
        var last = timings.Take(GpuPass.Frame);
        count += last.Count;
        sumMs += last.AverageMs * last.Count;
        maxMs = Math.Max(maxMs, last.MaxMs);

        Assert.Equal(100_000, count);
        Assert.Equal(100_000L * 100_001 / 2 / 1e6, sumMs, 3);
        Assert.Equal(0.1, maxMs, 6);
    }

    [Fact]
    public void Recording_and_taking_allocate_nothing()
    {
        var timings = new GpuTimings();
        timings.Record(GpuPass.Voxels, 1);
        timings.Take(GpuPass.Voxels);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            timings.Record(GpuPass.Voxels, i);
            timings.Record(GpuPass.Frame, i);
            timings.Take(GpuPass.Voxels);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
