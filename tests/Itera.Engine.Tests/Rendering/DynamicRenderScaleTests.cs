using Itera.Engine.Diagnostics;
using Itera.Engine.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// Dynamic render scaling against a simulated GPU whose frame time is a fixed part plus a part that follows the pixels
/// (the square of the scale), with a little noise, measured <see cref="FrameSync.FramesInFlight"/> frames after each
/// frame is built, as the render thread reads its timestamps after the slot's fence. The fixed part is recorded as the
/// shadow maps' time before the whole frame's, as the render thread records a frame's passes. 120 frames a second; the
/// target is 120 fps, 8.33 ms a frame, which the scaler aims 5 % under. The simulated GPU's time does not follow the
/// effects' share, so the tests of the scale alone hold the share at its least (a ceiling of
/// <see cref="EffectsBudget.MinimumShare"/>), and those of the share raise the ceiling.
/// </summary>
public sealed class DynamicRenderScaleTests
{
    private const double Fps = 120;
    private const double BudgetMs = 1000 / Fps;

    private readonly RenderView _view;
    private readonly GpuTimings _timings = new() { Enabled = true };
    private readonly EffectsBudget _effects = new() { Ceiling = EffectsBudget.MinimumShare };
    private readonly DynamicRenderScale _scaler;
    private readonly float[] _inFlight = new float[FrameSync.FramesInFlight];
    private int _frame;
    private uint _noise = 12345;

    public DynamicRenderScaleTests()
    {
        _view = new RenderView(Options.Create(new EngineOptions()));
        _view.ScalingThreshold = (float)Fps;
        _scaler = new DynamicRenderScale(_view, _timings, null, NullLogger<DynamicRenderScale>.Instance, _effects);
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
        var frame = new GpuFrameTime(meanMs, 0);
        Assert.Equal(expected, DynamicRenderScale.Next(scale, frame, frame, Fps));
    }

    [Fact]
    public void A_large_fixed_part_lowers_the_scale_further_than_the_whole_time_would()
    {
        // 9.5 ms at 1, 4 of them fixed: 4 + 5.5 s² meets the aim (7.92 ms) at 0.84. Taken all as pixels, at 0.91.
        var frame = new GpuFrameTime(9.5, 4);
        var whole = DynamicRenderScale.Next(1f, new GpuFrameTime(9.5, 0), new GpuFrameTime(9.5, 0), Fps);

        Assert.Equal(0.84f, DynamicRenderScale.Next(1f, frame, frame, Fps));
        Assert.Equal(0.91f, whole);
    }

    [Fact]
    public void A_fixed_part_alone_over_the_aim_goes_to_the_least_scale()
    {
        var frame = new GpuFrameTime(12, 8.5);

        Assert.Equal(RenderScale.Min, DynamicRenderScale.Next(1f, frame, frame, Fps));
    }

    [Fact]
    public void The_worst_frame_lowers_the_scale_where_the_mean_would_hold_it()
    {
        // The mean (7.6 ms) is between the share under which it raises and the aim, but one frame drew for 9.5 ms.
        var mean = new GpuFrameTime(7.6, 0);
        var worst = new GpuFrameTime(9.5, 0);

        Assert.Equal(0.8f, DynamicRenderScale.Next(0.8f, mean, mean, Fps));
        Assert.True(DynamicRenderScale.Next(0.8f, worst, mean, Fps) < 0.8f);
    }

    [Fact]
    public void A_large_fixed_part_still_raises_by_a_small_step()
    {
        // 4 + 2 s² at 0.6 is 4.72 ms, far under the aim: up by MostRaise, however much room the model finds.
        var frame = new GpuFrameTime(4.72, 4);

        Assert.Equal(0.62f, DynamicRenderScale.Next(0.6f, frame, frame, Fps));
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
    public void A_large_fixed_cost_settles_in_one_step()
    {
        // 12 ms at 1, 4 of them fixed: the first step lands where the worst frame meets the aim. The square root of
        // the whole time's excess would stop at 0.81.
        Run(DynamicRenderScale.SettleFrames + DynamicRenderScale.StepFrames + 1, 4, 8);
        Assert.InRange(_view.RenderScale, 0.62f, 0.71f);

        Run(360, 4, 8);
        var (least, most, meanMs) = Run(600, 4, 8);

        // 4 + 8 s² within 7.13 to 7.92 ms: 0.63 to 0.70.
        Assert.InRange(least, 0.62f, 0.71f);
        Assert.True(most - least <= 0.03f, $"{least} to {most}");
        Assert.True(meanMs < BudgetMs, $"{meanMs:F2} ms");
    }

    [Fact]
    public void One_frame_slow_in_its_pixels_in_each_step_lowers_the_scale()
    {
        // 5 ms at 1: held at 1.
        Run(240, 0, 5);
        Assert.Equal(1f, _view.RenderScale);

        // One frame in 8 draws 4 ms more: the mean (5.5 ms) is well within the aim, that frame (9 ms) is not.
        Run(3 * DynamicRenderScale.StepFrames, 0, 5, slowEvery: DynamicRenderScale.StepFrames, slowMs: 4);
        Assert.True(_view.RenderScale < 1f, $"{_view.RenderScale}");
    }

    [Fact]
    public void One_frame_slow_in_its_fixed_passes_in_each_step_holds_the_scale()
    {
        Run(240, 0, 5);

        // One frame in 8 spends 10 ms more in its fixed passes, alone over the aim: a lower scale would not help it. The mean fixed
        // part (1.25 ms) and the largest pixel part (5.15 ms) stay within the aim.
        Run(
            10 * DynamicRenderScale.StepFrames,
            0,
            5,
            slowEvery: DynamicRenderScale.StepFrames,
            slowMs: 10,
            slowFixed: true
        );
        Assert.Equal(1f, _view.RenderScale);
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

    [Theory]
    // Over the aim with effects to spare: the share falls by a quarter, and the scale holds.
    [InlineData(1f, 1f, 9.5, 1f, 0.75f)]
    [InlineData(0.8f, 0.4f, 9.5, 0.8f, 0.25f)]
    // Over the aim at the least share: the scale falls as it would alone.
    [InlineData(1f, 0.25f, 9.5, 0.91f, 0.25f)]
    // Under the raise line below 1: the scale rises first, and the share holds.
    [InlineData(0.6f, 0.25f, 2.0, 0.62f, 0.25f)]
    // Under the raise line at 1: the share rises by a tenth, up to the ceiling.
    [InlineData(1f, 0.25f, 3.0, 1f, 0.35f)]
    [InlineData(1f, 0.95f, 3.0, 1f, 1f)]
    [InlineData(1f, 1f, 3.0, 1f, 1f)]
    // Between the two: both hold.
    [InlineData(1f, 0.5f, 7.5, 1f, 0.5f)]
    public void A_step_lowers_the_effects_share_before_the_scale_and_raises_it_after(
        float scale,
        float share,
        double meanMs,
        float expectedScale,
        float expectedShare
    )
    {
        var frame = new GpuFrameTime(meanMs, 0);

        var (nextScale, nextShare) = DynamicRenderScale.Step(scale, share, 1, frame, frame, Fps);

        Assert.Equal((expectedScale, expectedShare), (nextScale, nextShare));
    }

    [Fact]
    public void The_effects_share_falls_before_the_scale_and_rises_after_it()
    {
        _effects.Ceiling = 1;
        _effects.Share = 1;
        Run(120, 1.5, 4);
        Assert.Equal((1f, 1f), (_view.RenderScale, _effects.Share));

        // A heavier GPU: the share falls to its least before the scale leaves 1.
        for (var i = 0; i < 240; i++)
        {
            Run(1, 0, 12);
            if (_view.RenderScale < 1 && _effects.Share > EffectsBudget.MinimumShare)
            {
                Assert.Fail($"The scale fell to {_view.RenderScale} with the share at {_effects.Share}.");
            }
        }

        Assert.Equal(EffectsBudget.MinimumShare, _effects.Share);
        Assert.True(_view.RenderScale < 1f, $"{_view.RenderScale}");

        // A lighter one: the scale is back at 1 before the share rises, and then the share climbs to the ceiling.
        for (var i = 0; i < 600; i++)
        {
            Run(1, 0, 3);
            if (_effects.Share > EffectsBudget.MinimumShare && _view.RenderScale < 1)
            {
                Assert.Fail($"The share rose to {_effects.Share} with the scale at {_view.RenderScale}.");
            }
        }

        Assert.Equal((1f, 1f), (_view.RenderScale, _effects.Share));
    }

    [Fact]
    public void The_share_never_passes_the_ceiling_and_is_the_ceiling_while_off()
    {
        _effects.Ceiling = 0.5f;
        Run(600, 0, 3);
        Assert.Equal(0.5f, _effects.Share);

        _effects.Ceiling = 1;
        Run(240, 0, 30);
        Assert.Equal(EffectsBudget.MinimumShare, _effects.Share);

        _view.DynamicScaling = false;
        _scaler.Update(10);
        Assert.Equal(1f, _effects.Share);

        // A lower ceiling lowers the share with it.
        _effects.Ceiling = 0.75f;
        Assert.Equal(0.75f, _effects.Share);
    }

    [Fact]
    public void Updates_allocate_nothing()
    {
        // The share moves too.
        _effects.Ceiling = 1;
        Run(600, 0, 10);
        var start = SystemAllocation.StartMeasurement();
        Run(1200, 0, 10);
        Run(1200, 1, 4);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>
    /// Builds <paramref name="frames"/> frames on a GPU taking <paramref name="fixedMs"/> plus
    /// <paramref name="pixelMs"/> times the scale squared, ±3 %, and <paramref name="slowMs"/> more in one frame of each
    /// <paramref name="slowEvery"/> (none at 0), in its fixed part when <paramref name="slowFixed"/>. Returns the least and
    /// most scale drawn, and the mean GPU time measured.
    /// </summary>
    private (float Least, float Most, double MeanMs) Run(
        int frames,
        double fixedMs,
        double pixelMs,
        int slowEvery = 0,
        double slowMs = 0,
        bool slowFixed = false
    )
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
            var slow = slowEvery > 0 && i % slowEvery == 0 ? slowMs : 0;
            var milliseconds = (fixedMs + pixelMs * drawn * drawn) * noise + slow;
            sum += milliseconds;
            _timings.Record(GpuPass.ShadowMaps, (long)((fixedMs * noise + (slowFixed ? slow : 0)) * 1e6));
            _timings.Record(GpuPass.Frame, (long)(milliseconds * 1e6));
            _frame++;
        }

        return (least, most, sum / frames);
    }
}
