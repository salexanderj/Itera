using System.Diagnostics;
using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Diagnostics;

public sealed class FrameStatsTests
{
    [Fact]
    public void No_summary_before_the_interval_elapses()
    {
        var stats = Started();

        stats.RecordSteps(1, 0);
        stats.RecordFrame(0.016);
        stats.Update(0.5, 0);

        Assert.Null(stats.LastSummary);
    }

    [Fact]
    public void Summary_reports_frames_steps_and_frame_times()
    {
        var stats = Started();

        stats.RecordSteps(1, 0);
        stats.RecordFrame(0.010);
        stats.Update(0.25, 0);
        stats.RecordSteps(2, 0);
        stats.RecordFrame(0.030);
        stats.Update(0.50, 0);
        stats.RecordSteps(1, 3);
        stats.RecordFrame(0.020);
        stats.Update(1.00, 0);

        var summary = Assert.NotNull(stats.LastSummary);
        Assert.Equal(3, summary.Frames);
        Assert.Equal(4, summary.FixedSteps);
        Assert.Equal(3, summary.DroppedSteps);
        Assert.Equal(20.0, summary.AverageMs, precision: 3);
        Assert.Equal(30.0, summary.MaxMs, precision: 3);
        Assert.Equal(1.0, summary.Seconds, precision: 6);
        Assert.True(summary.HeapMb > 0);
    }

    [Fact]
    public void Summary_reports_gpu_frame_time_and_takes_it()
    {
        var gpu = new GpuTimings();
        var stats = new FrameStats(1.0, NullLogger<FrameStats>.Instance, gpu);
        stats.Start(0, 0);
        gpu.Record(GpuPass.Frame, 2_000_000);
        gpu.Record(GpuPass.Frame, 4_000_000);
        gpu.Record(GpuPass.Voxels, 1_000_000);

        stats.RecordFrame(0.016);
        stats.Update(1.0, 0);

        var summary = Assert.NotNull(stats.LastSummary);
        Assert.Equal(3.0, summary.GpuAverageMs, precision: 6);
        Assert.Equal(4.0, summary.GpuMaxMs, precision: 6);
        Assert.Equal(default, gpu.Take(GpuPass.Frame));
        Assert.Equal(default, gpu.Take(GpuPass.Voxels));
    }

    /// <summary>
    /// A frame rate that swings shows as a spread: of 100 frames, 90 of 4 ms and 10 of 8 ms (a 240 Hz screen dropping
    /// to 120 one frame in ten), the median is 4 ms and the 95th and 99th percentiles 8 ms; the GPU's likewise.
    /// </summary>
    [Fact]
    public void Summary_gives_the_median_95th_and_99th_percentile_of_frame_and_gpu_times()
    {
        var gpu = new GpuTimings();
        var stats = new FrameStats(1.0, NullLogger<FrameStats>.Instance, gpu);
        stats.Start(0, 0);
        for (var i = 0; i < 100; i++)
        {
            var slow = i % 10 == 9;
            stats.RecordFrame(slow ? 0.008 : 0.004);
            gpu.Record(GpuPass.Frame, slow ? 7_000_000 : 3_000_000);
        }

        stats.Update(1.0, 0);

        var summary = Assert.NotNull(stats.LastSummary);
        Assert.Equal(4.0, summary.MedianMs, precision: 4);
        Assert.Equal(8.0, summary.P95Ms, precision: 4);
        Assert.Equal(8.0, summary.P99Ms, precision: 4);
        Assert.Equal(3.0, summary.GpuMedianMs, precision: 4);
        Assert.Equal(7.0, summary.GpuP95Ms, precision: 4);
        Assert.Equal(0, gpu.TakeFrameTimes(new float[4]));

        // The next interval starts empty.
        stats.RecordFrame(0.002);
        stats.Update(2.0, 0);
        Assert.Equal(2.0, Assert.NotNull(stats.LastSummary).P99Ms, precision: 4);
    }

    [Fact]
    public void Recording_frames_allocates_nothing()
    {
        var gpu = new GpuTimings();
        var stats = new FrameStats(1.0, NullLogger<FrameStats>.Instance, gpu);
        stats.Start(0, 0);
        stats.RecordFrame(0.004);
        gpu.Record(GpuPass.Frame, 3_000_000);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 20_000; i++)
        {
            stats.RecordFrame(0.004);
            gpu.Record(GpuPass.Frame, 3_000_000);
            stats.Update(0.5, 0);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Steps_without_frames_still_produce_a_summary()
    {
        // While presentation blocks, the loop keeps stepping but builds no frames.
        var stats = Started();

        for (var i = 1; i <= 60; i++)
        {
            stats.RecordSteps(1, 0);
            stats.Update(i / 60.0, 0);
        }

        var summary = Assert.NotNull(stats.LastSummary);
        Assert.Equal(0, summary.Frames);
        Assert.Equal(60, summary.FixedSteps);
        Assert.Equal(0, summary.AverageMs);
        Assert.Equal(0, summary.BytesPerFrame);
    }

    [Fact]
    public void Counters_reset_after_each_summary()
    {
        var stats = Started();
        stats.RecordSteps(3, 1);
        stats.RecordFrame(0.050);
        stats.Update(1.0, 0);

        stats.RecordSteps(1, 0);
        stats.RecordFrame(0.010);
        stats.Update(2.0, 0);

        var summary = Assert.NotNull(stats.LastSummary);
        Assert.Equal(1, summary.Frames);
        Assert.Equal(1, summary.FixedSteps);
        Assert.Equal(0, summary.DroppedSteps);
        Assert.Equal(10.0, summary.MaxMs, precision: 3);
    }

    [Fact]
    public void Long_frames_are_reported_at_their_measured_length()
    {
        var stats = Started();

        // The simulation clamps a delta to 0.25 s; stats receive the measured time, so a 2 s gap shows as 2 s.
        stats.RecordSteps(8, 112);
        stats.RecordFrame(2.0);
        stats.Update(2.0, 0);

        var summary = Assert.NotNull(stats.LastSummary);
        Assert.Equal(2000.0, summary.MaxMs, precision: 3);
    }

    [Fact]
    public void Summary_counts_bytes_allocated_on_the_calling_thread_and_on_the_render_thread()
    {
        var stats = Started(renderAllocatedBytes: 1000);

        var garbage = new byte[64 * 1024];
        stats.RecordFrame(0.5);
        stats.Update(0.5, 1000);
        stats.RecordFrame(0.5);
        stats.Update(1.0, 1000 + 4096);
        // Kept alive past the summary: in Release the JIT otherwise drops an array read only for its length.
        GC.KeepAlive(garbage);

        var summary = Assert.NotNull(stats.LastSummary);
        Assert.InRange(summary.AllocatedBytes, garbage.Length, garbage.Length + 16 * 1024);
        Assert.Equal(summary.AllocatedBytes / 2.0, summary.BytesPerFrame, precision: 6);
        Assert.Equal(4096, summary.RenderAllocatedBytes);
        Assert.Equal(2048.0, summary.RenderBytesPerFrame, precision: 6);
    }

    [Fact]
    public void Zero_interval_disables_summaries()
    {
        var stats = new FrameStats(0, NullLogger<FrameStats>.Instance);
        stats.Start(0, 0);

        stats.RecordSteps(1, 0);
        stats.RecordFrame(0.016);
        stats.Update(100, 0);

        Assert.False(stats.Enabled);
        Assert.Null(stats.LastSummary);
    }

    [Fact]
    public void The_step_line_gives_each_parts_average_and_longest_and_the_most_steps_in_one_iteration()
    {
        var lines = new List<string>();
        var stats = new FrameStats(1.0, new ListLogger(lines));
        stats.Start(0, 0);

        // Two steps in one iteration: systems 1 and 3 ms, colliders 0.5 and 0, physics 2 and 6, bodies as voxels 0.
        Step(stats, 1, 0.5, 2, 0);
        Step(stats, 3, 0, 6, 0);
        stats.RecordSteps(2, 0);
        stats.Update(1.0, 0);

        var line = Assert.Single(lines, l => l.StartsWith("Fixed steps", StringComparison.Ordinal));
        Assert.Contains("(2 steps): longest 9.00 ms, at most 2 in one loop iteration", line, StringComparison.Ordinal);
        Assert.Contains("systems avg 2.000 ms max 3.00 ms", line, StringComparison.Ordinal);
        Assert.Contains("voxel colliders avg 0.250 ms max 0.50 ms", line, StringComparison.Ordinal);
        Assert.Contains("physics avg 4.000 ms max 6.00 ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_line_gives_the_large_edits_and_a_line_names_the_slowest_systems()
    {
        static long Ticks(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
        var lines = new List<string>();
        var systems = new SystemTimes();
        var movement = systems.Add("PlayerMovementSystem");
        var streaming = systems.Add("StreamingCentreSystem");
        var camera = systems.Add("CameraExtractSystem");
        var stats = new FrameStats(1.0, new ListLogger(lines), systems: systems);
        stats.Start(0, 0);

        // Two steps: the movement 3 and 5 ms, the streaming 1 and 1, the camera 0.5 and 0; large edits 8 ms in the
        // first.
        foreach (
            var (movementMs, streamingMs, cameraMs, editsMs) in (ReadOnlySpan<(double, double, double, double)>)
                [(3, 1, 0.5, 8), (5, 1, 0, 0)]
        )
        {
            systems.Record(movement, Ticks(movementMs));
            systems.Record(streaming, Ticks(streamingMs));
            systems.Record(camera, Ticks(cameraMs));
            var start = 1_000_000L;
            var edits = start + Ticks(movementMs + streamingMs + cameraMs);
            var colliders = edits + Ticks(editsMs);
            stats.RecordStepParts(start, edits, colliders, colliders, colliders, colliders, colliders + Ticks(1));
        }

        stats.RecordSteps(2, 0);
        stats.Update(1.0, 0);

        var steps = Assert.Single(lines, l => l.StartsWith("Fixed steps", StringComparison.Ordinal));
        Assert.Contains("large edits avg 4.000 ms max 8.00 ms", steps, StringComparison.Ordinal);
        // The step's tail (mods, navigation, the session, and the saves) is timed and counts towards the longest step.
        Assert.Contains("mods, navigation, session, and saves avg 1.000 ms max 1.00 ms", steps, StringComparison.Ordinal);
        Assert.Contains("longest 13.50 ms", steps, StringComparison.Ordinal);
        var slowest = Assert.Single(lines, l => l.StartsWith("Fixed systems", StringComparison.Ordinal));
        Assert.EndsWith(
            ": PlayerMovementSystem 4.000 max 5.00, StreamingCentreSystem 1.000 max 1.00, CameraExtractSystem 0.250 max 0.50",
            slowest,
            StringComparison.Ordinal
        );
    }

    /// <summary>Records one step whose parts took the given milliseconds.</summary>
    private static void Step(FrameStats stats, double systems, double colliders, double physics, double bodyVoxels)
    {
        static long Ticks(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
        var start = 1_000_000L;
        var a = start + Ticks(systems);
        var b = a + Ticks(colliders);
        var c = b + Ticks(physics);
        stats.RecordStepParts(start, a, a, b, c, c + Ticks(bodyVoxels), c + Ticks(bodyVoxels));
    }

    private static FrameStats Started(long renderAllocatedBytes = 0)
    {
        var stats = new FrameStats(1.0, NullLogger<FrameStats>.Instance);
        stats.Start(0, renderAllocatedBytes);
        return stats;
    }

    private sealed class ListLogger(List<string> lines) : ILogger<FrameStats>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => lines.Add(formatter(state, exception));
    }
}
