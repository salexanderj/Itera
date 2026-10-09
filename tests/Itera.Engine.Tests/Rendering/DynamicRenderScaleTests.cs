using Itera.Engine.Diagnostics;
using Itera.Engine.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// Dynamic render scaling against a simulated GPU whose frame time is a fixed part plus a part that follows the pixels
/// (the square of the scale), with a little noise, measured <see cref="FrameSync.FramesInFlight"/> frames after each
/// frame is built, as the render thread reads its timestamps after the slot's fence. 120 frames a second; the target is
/// 120 fps, 8.33 ms a frame, which the scaler aims 5 % under.
/// </summary>
public sealed class DynamicRenderScaleTests
{
    private const double Fps = 120;
    private const double BudgetMs = 1000 / Fps;

    private readonly RenderView _view;
    private readonly GpuTimings _timings = new() { Enabled = true };
    private readonly DynamicRenderScale _scaler;
    private readonly float[] _inFlight = new float[FrameSync.FramesInFlight];
    private int _frame;
    private uint _noise = 12345;

    public DynamicRenderScaleTests()
    {
        _view = new RenderView(Options.Create(new EngineOptions()));
        _scaler = new DynamicRenderScale(_view, _timings, null, NullLogger<DynamicRenderScale>.Instance);
        Array.Fill(_inFlight, 1f);
    }

    [Theory]
    // Within the aim (7.92 ms) and above the share under which it raises (7.13 ms): held.
    [InlineData(0.8f, 7.5, 0.8f)]
    // At 1 with time to spare: still 1.
    [InlineData(1f, 3.0, 1f)]
    // Over the aim: down by the square root of the excess, to whole hundredths.
    [InlineData(1f, 9.5, 0.91f)]
    // Only just over: down by a hundredth at least.
    [InlineData(0.8f, 7.95, 0.79f)]
    // Far under: up by MostRaise at most.
    [InlineData(0.6f, 2.0, 0.62f)]
    // Never under the least scale.
    [InlineData(0.5f, 30.0, 0.5f)]
    public void A_step_lowers_holds_or_raises_by_the_time_against_the_aim(float scale, double meanMs, float expected)
    {
        Assert.Equal(expected, DynamicRenderScale.Next(scale, meanMs, Fps));
    }

    [Theory]
    [InlineData(120f, 240, 120f)]
    [InlineData(120f, 100, 100f)]
    [InlineData(120f, 0, 120f)]
    [InlineData(360f, 60, 60f)]
    public void The_target_is_the_lower_of_the_threshold_and_the_refresh_rate(
        float threshold,
        int refresh,
        float target
    )
    {
        Assert.Equal(target, DynamicRenderScale.TargetFor(threshold, refresh));
    }

    [Theory]
    [InlineData(130f, 120f)]
    [InlineData(150f, 144f)]
    [InlineData(1000f, 360f)]
    [InlineData(0f, 30f)]
    [InlineData(75f, 75f)]
    public void A_threshold_snaps_to_the_nearest_offered_rate(float fps, float nearest)
    {
        Assert.Equal(nearest, DynamicRenderScale.NearestThreshold(fps));
    }

    [Fact]
    public void A_gpu_with_time_to_spare_keeps_the_full_size()
    {
        Run(1200, 1.5, 4);

        Assert.Equal(1f, _view.RenderScale);
    }

    [Fact]
    public void A_slow_gpu_settles_under_the_target_without_swinging()
    {
        Run(240, 0, 10);
        var (least, most, meanMs) = Run(600, 0, 10);

        // 10 s² within 7.13 to 7.92 ms: 0.84 to 0.89.
        Assert.InRange(least, 0.83f, 0.9f);
        Assert.True(most - least <= 0.03f, $"{least} to {most}");
        Assert.True(meanMs < BudgetMs, $"{meanMs:F2} ms");
    }

    [Fact]
    public void A_large_fixed_cost_takes_more_steps_but_settles()
    {
        Run(360, 4, 8);
        var (least, most, meanMs) = Run(600, 4, 8);

        // 4 + 8 s² within 7.13 to 7.92 ms: 0.63 to 0.70.
        Assert.InRange(least, 0.62f, 0.71f);
        Assert.True(most - least <= 0.03f, $"{least} to {most}");
        Assert.True(meanMs < BudgetMs, $"{meanMs:F2} ms");
    }

    [Fact]
    public void A_gpu_too_slow_at_any_scale_holds_the_least()
    {
        Run(600, 12, 2);

        Assert.Equal(RenderScale.Min, _view.RenderScale);
    }

    [Fact]
    public void It_lowers_within_a_quarter_second_and_rises_by_small_steps()
    {
        Run(120, 1.5, 4);
        Assert.Equal(1f, _view.RenderScale);

        // The GPU's work grows by half: within 30 frames the scale is down to where it fits.
        Run(30, 0, 12);
        Assert.True(_view.RenderScale <= 0.82f, $"{_view.RenderScale}");

        // The work drops again: the scale climbs by MostRaise at most each step, and is back at 1 within 2 s.
        var last = _view.RenderScale;
        for (var i = 0; i < 240; i++)
        {
            Run(1, 0, 3);
            Assert.True(_view.RenderScale - last <= DynamicRenderScale.MostRaise + 1e-4f);
            last = _view.RenderScale;
        }

        Assert.Equal(1f, _view.RenderScale);
    }

    [Fact]
    public void Off_overridden_or_unmeasured_the_scale_is_fixed()
    {
        Run(240, 0, 12);
        Assert.True(_view.RenderScale < 1f);

        _view.DynamicScaling = false;
        _view.FixedRenderScale = 0.75f;
        _scaler.Update(10);
        Assert.Equal(0.75f, _view.RenderScale);
        Assert.False(_scaler.Running);

        _view.DynamicScaling = true;
        _view.ScaleOverride = 0.6f;
        _scaler.Update(10);
        Assert.Equal(0.6f, _view.RenderScale);

        _view.ScaleOverride = null;
        _timings.Enabled = false;
        _scaler.Update(10);
        Assert.Equal(0.75f, _view.RenderScale);

        // Measured again, it starts from there and follows the GPU: 12 s² within 7.13 to 7.92 ms is 0.77 to 0.81.
        _timings.Enabled = true;
        Run(240, 0, 12);
        Assert.InRange(_view.RenderScale, 0.76f, 0.82f);
        Assert.True(_scaler.Running);
    }

    [Fact]
    public void Updates_allocate_nothing()
    {
        Run(600, 0, 10);
        var start = SystemAllocation.StartMeasurement();
        Run(1200, 0, 10);
        Run(1200, 1, 4);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>
    /// Builds <paramref name="frames"/> frames on a GPU taking <paramref name="fixedMs"/> plus
    /// <paramref name="pixelMs"/> times the scale squared, ±3 %. Returns the least and most scale drawn, and the mean GPU
    /// time measured.
    /// </summary>
    private (float Least, float Most, double MeanMs) Run(int frames, double fixedMs, double pixelMs)
    {
        var least = float.MaxValue;
        var most = float.MinValue;
        var sum = 0.0;
        for (var i = 0; i < frames; i++)
        {
            _scaler.Update(_frame / Fps);
            var scale = _view.RenderScale;
            least = Math.Min(least, scale);
            most = Math.Max(most, scale);

            // The frame built FramesInFlight frames ago is the one measured now.
            var slot = _frame % FrameSync.FramesInFlight;
            var drawn = _inFlight[slot];
            _inFlight[slot] = scale;
            _noise = _noise * 1664525 + 1013904223;
            var noise = 1 + 0.03 * ((_noise >> 8) / (double)(1 << 24) * 2 - 1);
            var milliseconds = (fixedMs + pixelMs * drawn * drawn) * noise;
            sum += milliseconds;
            _timings.Record(GpuPass.Frame, (long)(milliseconds * 1e6));
            _frame++;
        }

        return (least, most, sum / frames);
    }
}
