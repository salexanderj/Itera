using Itera.Engine.Diagnostics;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Rendering;

/// <summary>
/// Dynamic render scaling (<c>Engine:Renderer:DynamicRenderScaling</c>, the settings'
/// Dynamic render scaling): while it is on, the render scale (<see cref="RenderView.RenderScale"/>, 0.5 to 1) follows
/// the GPU's time per frame so the frame rate holds the target: the lower of the threshold
/// (<see cref="RenderView.ScalingThreshold"/>, the settings' Dynamic scaling threshold) and the screen's refresh rate
/// (<see cref="WindowService.RefreshRate"/>: frames past it are not shown whole). It steers by the GPU's
/// time, not the whole frame's: the scale changes only the GPU's work, so a frame the CPU holds up would only lose
/// sharpness. It aims for <see cref="Aim"/> of the target's frame time, so frames' usual variation stays under the
/// target. Each frame's time is split in two: a fixed part, the passes whose work does not follow the drawn size
/// (<see cref="FixedPasses"/>: the shadow maps, skinning, uploads, the particles' simulation, image views, the UI, the
/// upscale, the loading screen), and the rest, which follows the pixels (the square of the scale). Each step takes
/// <see cref="StepFrames"/> frames measured after the last change reached the GPU (the <see cref="SettleFrames"/> before
/// are skipped), about 0.2 s at 60 frames a second. Their worst frame is predicted as their mean fixed part plus their
/// largest pixel part, so a frame whose fixed passes are slow, which a lower scale cannot help, does not move the scale.
/// When that worst frame is over the aim, it lowers the scale at once to where the worst frame would meet the aim, or to
/// the least when the mean fixed part alone is over. When their mean is under <see cref="RaiseBelow"/> of the aim, it raises the scale by at most <see cref="MostRaise"/>
/// a step, never past where the mean would meet the aim, so it takes about a second to rise 0.2 and does not swing.
/// Between the two it holds, and at 1 with time to spare it does nothing. Effects give way before the resolution
/// (<see cref="EffectsBudget"/>): over the aim, a step lowers the effects' share by
/// <see cref="EffectsBudget.LowerStep"/> and leaves the scale, until the share is at its least; under the raise line,
/// a step raises the scale first, and the share by <see cref="EffectsBudget.RaiseStep"/> only once the scale is at 1.
/// Off, or with the console's <c>scale</c> set (<see cref="RenderView.ScaleOverride"/>), or where the GPU cannot
/// measure, the scale is fixed and the share is its ceiling. Main thread, once a frame before the view is made;
/// allocates nothing.
/// </summary>
public sealed partial class DynamicRenderScale
{
    /// <summary>The share of the target's frame time it aims for.</summary>
    public const double Aim = 0.95;

    /// <summary>The share of the aim under which it raises the scale.</summary>
    public const double RaiseBelow = 0.9;

    /// <summary>The most it raises the scale in a step.</summary>
    public const float MostRaise = 0.02f;

    /// <summary>
    /// Frames measured that make a step: their largest pixel part lowers the scale, their mean raises it. Of 8 frames the
    /// largest is also the 90th percentile (the 8th by nearest rank).
    /// </summary>
    public const int StepFrames = 8;

    /// <summary>
    /// Frames measured after a change that are skipped: those already recorded at the old scale (one per frame in flight)
    /// and the frame being built.
    /// </summary>
    public const int SettleFrames = FrameSync.FramesInFlight + 1;

    /// <summary>The least threshold, frames a second.</summary>
    public const float LeastThreshold = 30;

    /// <summary>The most threshold, frames a second.</summary>
    public const float MostThreshold = 360;

    /// <summary>How often the Debug line gives the scale's range, in seconds.</summary>
    public const double LogSeconds = 10;

    // Scales are whole hundredths: a smaller change would redraw the picture at a few pixels' difference for nothing.
    private const int Hundredths = 100;

    // The most a step can multiply the scale by, when the part of the time that follows the pixels is too small to measure.
    private const double MostRatio = 2;

    private static readonly float[] s_thresholds = [30, 60, 75, 90, 100, 120, 144, 165, 180, 240, 360];

    private static readonly GpuPass[] s_fixedPasses =
    [
        GpuPass.ShadowMaps,
        GpuPass.Skinning,
        GpuPass.Upload,
        GpuPass.ParticleSimulate,
        GpuPass.ImageViews,
        GpuPass.Ui,
        GpuPass.Upscale,
        GpuPass.Loading,
    ];

    private readonly RenderView _view;
    private readonly GpuTimings _timings;
    private readonly WindowService? _window;
    private readonly EffectsBudget _effects;
    private readonly ILogger<DynamicRenderScale> _logger;
    private readonly float[] _passes = new float[GpuTimings.PassCount];
    private bool _running;
    private long _seen;
    private int _settle;
    private double _sum;
    private double _fixedSum;
    private double _mostPixels;
    private int _count;
    private double _intervalStart = -1;
    private float _least;
    private float _most;
    private float _leastShare;
    private float _mostShare;
    private double _scaleSum;
    private double _gpuSum;
    private double _gpuFixedSum;
    private int _intervalFrames;
    private int _gpuFrames;

    /// <param name="view">The view whose render scale it sets.</param>
    /// <param name="timings">The GPU's times.</param>
    /// <param name="window">The window, for the screen's refresh rate; null for none.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="effects">The effects' share it lowers before the scale; one of its own when null (tests).</param>
    public DynamicRenderScale(
        RenderView view,
        GpuTimings timings,
        WindowService? window,
        ILogger<DynamicRenderScale> logger,
        EffectsBudget? effects = null
    )
    {
        _view = view;
        _timings = timings;
        _window = window;
        _logger = logger;
        _effects = effects ?? new EffectsBudget();
    }

    /// <summary>The thresholds the settings offer: common refresh rates, in frames a second, lowest first.</summary>
    public static ReadOnlySpan<float> Thresholds => s_thresholds;

    /// <summary>The passes whose GPU time does not follow the drawn size: a frame's fixed part.</summary>
    public static ReadOnlySpan<GpuPass> FixedPasses => s_fixedPasses;

    /// <summary>The frame rate held: the lower of the threshold and the screen's refresh rate when it is known.</summary>
    public float TargetFps => TargetFor(_view.ScalingThreshold, _window?.RefreshRate ?? 0);

    /// <summary>
    /// The frame rate held for <paramref name="threshold"/> on a screen of <paramref name="refreshRate"/> Hz (0 when
    /// unknown): the lower of the two.
    /// </summary>
    public static float TargetFor(float threshold, int refreshRate) =>
        refreshRate > 0 ? Math.Min(threshold, refreshRate) : threshold;

    /// <summary>True while the scale follows the GPU's time (on, not overridden, and the GPU measures).</summary>
    public bool Running => _running;

    /// <summary>The offered threshold nearest <paramref name="fps"/>.</summary>
    public static float NearestThreshold(float fps)
    {
        var best = s_thresholds[0];
        foreach (var threshold in s_thresholds)
        {
            if (Math.Abs(threshold - fps) < Math.Abs(best - fps))
            {
                best = threshold;
            }
        }

        return best;
    }

    /// <summary>
    /// Sets the render scale of the frame being built. <paramref name="now"/> is the clock in seconds.
    /// </summary>
    public void Update(double now)
    {
        var view = _view;
        if (view.ScaleOverride is { } fixedScale)
        {
            Stop(fixedScale);
            return;
        }

        if (!view.DynamicScaling || !_timings.Enabled)
        {
            Stop(view.FixedRenderScale);
            return;
        }

        if (!_running)
        {
            _running = true;
            _seen = _timings.FramesMeasured;
            _settle = SettleFrames;
            StartStep();
            StartInterval(now);
        }

        var measured = _timings.FramesMeasured;
        if (measured != _seen)
        {
            var fresh = measured - _seen;
            _seen = measured;
            if (_settle > 0)
            {
                _settle -= (int)Math.Min(fresh, _settle);
            }
            else
            {
                var frame = LastFrame();
                _sum += frame.Milliseconds;
                _fixedSum += frame.FixedMilliseconds;
                _mostPixels = Math.Max(_mostPixels, frame.Milliseconds - frame.FixedMilliseconds);

                _count++;
                _gpuSum += frame.Milliseconds;
                _gpuFixedSum += frame.FixedMilliseconds;
                _gpuFrames++;
            }
        }

        if (_count >= StepFrames)
        {
            var meanFixed = _fixedSum / _count;
            var mean = new GpuFrameTime(_sum / _count, meanFixed);
            var worst = new GpuFrameTime(meanFixed + _mostPixels, meanFixed);
            var effects = _effects;
            var (next, share) = Step(view.RenderScale, effects.Share, effects.Ceiling, worst, mean, TargetFps);
            StartStep();
            if (next != view.RenderScale || share != effects.Share)
            {
                view.RenderScale = next;
                effects.Share = share;
                _settle = SettleFrames;
            }
        }

        Track(now, view.RenderScale, _effects.Share);
    }

    /// <summary>
    /// The scale and the effects' share after a step's frames drawn at <paramref name="scale"/> with
    /// <paramref name="share"/> of the effects, for <paramref name="targetFps"/> frames a second. Over the aim, the share
    /// falls by <see cref="EffectsBudget.LowerStep"/> to <see cref="EffectsBudget.MinimumShare"/> at least, and the scale
    /// holds; at the least share, the scale falls (<see cref="Next"/>). Under <see cref="RaiseBelow"/> of the aim, the
    /// scale rises first; at <see cref="RenderScale.Max"/>, the share rises by <see cref="EffectsBudget.RaiseStep"/> to
    /// <paramref name="ceiling"/> at most. Shares are whole hundredths. Pure.
    /// </summary>
    public static (float Scale, float Share) Step(
        float scale,
        float share,
        float ceiling,
        GpuFrameTime worst,
        GpuFrameTime mean,
        double targetFps
    )
    {
        var aim = Aim * 1000 / targetFps;
        if (worst.Milliseconds > aim && share > EffectsBudget.MinimumShare)
        {
            return (scale, ShareHundredths(Math.Max(EffectsBudget.MinimumShare, share - EffectsBudget.LowerStep)));
        }

        var next = Next(scale, worst, mean, targetFps);
        if (
            next == scale
            && next >= RenderScale.Max
            && worst.Milliseconds <= aim
            && mean.Milliseconds < RaiseBelow * aim
            && share < ceiling
        )
        {
            return (next, ShareHundredths(Math.Min(ceiling, share + EffectsBudget.RaiseStep)));
        }

        return (next, share);
    }

    private static float ShareHundredths(float share) => MathF.Round(share * 100) / 100;

    /// <summary>
    /// The scale after a step's frames drawn at <paramref name="scale"/>, for <paramref name="targetFps"/> frames a
    /// second: in whole hundredths, from <see cref="RenderScale.Min"/> to <see cref="RenderScale.Max"/>.
    /// <paramref name="worst"/> is their worst frame predicted (their mean fixed part plus their largest pixel part), and
    /// <paramref name="mean"/> their mean. Pure.
    /// </summary>
    public static float Next(float scale, GpuFrameTime worst, GpuFrameTime mean, double targetFps)
    {
        var aim = Aim * 1000 / targetFps;
        var current = (int)Math.Round(scale * Hundredths);
        var least = (int)Math.Round(RenderScale.Min * Hundredths);
        var most = (int)Math.Round(RenderScale.Max * Hundredths);
        int next;
        if (worst.Milliseconds > aim)
        {
            // Down at once, by at least a hundredth; to the least when the fixed part alone is over the aim.
            next = Math.Min(current - 1, (int)Math.Floor(current * Ratio(worst, aim)));
        }
        else if (mean.Milliseconds < RaiseBelow * aim)
        {
            // Up by at most MostRaise, never past the time the aim allows.
            var raise = (int)Math.Round(MostRaise * Hundredths);
            next = Math.Min(current + raise, (int)Math.Floor(current * Ratio(mean, aim)));
            next = Math.Max(next, current);
        }
        else
        {
            next = current;
        }

        return Math.Clamp(next, least, most) / (float)Hundredths;
    }

    /// <summary>
    /// The ratio r of the scale at which <paramref name="frame"/> would take <paramref name="aim"/>: its fixed part plus
    /// the rest times r squared. 0 when the fixed part alone reaches the aim.
    /// </summary>
    private static double Ratio(GpuFrameTime frame, double aim)
    {
        var fixedPart = Math.Clamp(frame.FixedMilliseconds, 0, frame.Milliseconds);
        if (fixedPart >= aim)
        {
            return 0;
        }

        var scaled = frame.Milliseconds - fixedPart;
        var squared = (aim - fixedPart) / scaled;
        return scaled > 0 && squared < MostRatio * MostRatio ? Math.Sqrt(squared) : MostRatio;
    }

    /// <summary>The last frame measured: its whole time and the sum of its <see cref="FixedPasses"/>.</summary>
    private GpuFrameTime LastFrame()
    {
        var milliseconds = _timings.LastFrame(_passes);
        var fixedPart = 0.0;
        foreach (var pass in s_fixedPasses)
        {
            fixedPart += _passes[(int)pass];
        }

        return new GpuFrameTime(milliseconds, Math.Min(fixedPart, milliseconds));
    }

    private void StartStep()
    {
        _sum = 0;
        _fixedSum = 0;
        _mostPixels = 0;
        _count = 0;
    }

    private void Stop(float scale)
    {
        _running = false;
        _view.RenderScale = scale;
        _effects.Share = _effects.Ceiling;
    }

    private void StartInterval(double now)
    {
        _intervalStart = now;
        _least = _view.RenderScale;
        _most = _view.RenderScale;
        _leastShare = _effects.Share;
        _mostShare = _effects.Share;
        _scaleSum = 0;
        _gpuSum = 0;
        _gpuFixedSum = 0;
        _intervalFrames = 0;
        _gpuFrames = 0;
    }

    /// <summary>
    /// Keeps the interval's range of scales and of effects' shares, and gives them in a Debug line every
    /// <see cref="LogSeconds"/>.
    /// </summary>
    private void Track(double now, float scale, float share)
    {
        _least = Math.Min(_least, scale);
        _most = Math.Max(_most, scale);
        _leastShare = Math.Min(_leastShare, share);
        _mostShare = Math.Max(_mostShare, share);
        _scaleSum += scale;
        _intervalFrames++;
        if (now - _intervalStart < LogSeconds)
        {
            return;
        }

        var mean = _scaleSum / _intervalFrames;
        var gpu = _gpuFrames > 0 ? _gpuSum / _gpuFrames : 0;
        var gpuFixed = _gpuFrames > 0 ? _gpuFixedSum / _gpuFrames : 0;
        LogInterval(_least, _most, mean, _leastShare, _mostShare, TargetFps, gpu, gpuFixed);
        StartInterval(now);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Render scale: {Least:F2} to {Most:F2}, mean {Mean:F2}, effects {LeastShare:F2} to {MostShare:F2}, holding {Fps} fps (GPU {Gpu:F2} ms a frame, {GpuFixed:F2} ms of it fixed)"
    )]
    private partial void LogInterval(
        float least,
        float most,
        double mean,
        float leastShare,
        float mostShare,
        float fps,
        double gpu,
        double gpuFixed
    );
}

/// <summary>A frame's GPU time and its fixed part (<see cref="DynamicRenderScale.FixedPasses"/>), in milliseconds.</summary>
/// <param name="Milliseconds">The whole frame's time.</param>
/// <param name="FixedMilliseconds">The time of its passes whose work does not follow the drawn size.</param>
public readonly record struct GpuFrameTime(double Milliseconds, double FixedMilliseconds);
