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
/// target. Each step takes the mean of <see cref="StepFrames"/> frames measured after the last change reached the GPU
/// (the <see cref="SettleFrames"/> before are skipped), about 0.1 s at 120 frames a second. Over the aim it lowers the
/// scale at once by the square root of the excess (the time follows the pixels, the square of the scale); under
/// <see cref="RaiseBelow"/> of the aim it raises it by at most <see cref="MostRaise"/> a step, so it takes about a
/// second to rise 0.2 and does not swing. Between the two it holds, and at 1 with time to spare it does nothing. Off, or
/// with the console's <c>scale</c> set (<see cref="RenderView.ScaleOverride"/>), or where the GPU cannot measure, the
/// scale is fixed. Main thread, once a frame before the view is made; allocates nothing.
/// </summary>
public sealed partial class DynamicRenderScale
{
    /// <summary>The share of the target's frame time it aims for.</summary>
    public const double Aim = 0.95;

    /// <summary>The share of the aim under which it raises the scale.</summary>
    public const double RaiseBelow = 0.9;

    /// <summary>The most it raises the scale in a step.</summary>
    public const float MostRaise = 0.02f;

    /// <summary>Frames measured whose mean makes a step.</summary>
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

    private static readonly float[] s_thresholds = [30, 60, 75, 90, 100, 120, 144, 165, 180, 240, 360];

    private readonly RenderView _view;
    private readonly GpuTimings _timings;
    private readonly WindowService? _window;
    private readonly ILogger<DynamicRenderScale> _logger;
    private bool _running;
    private long _seen;
    private int _settle;
    private double _sum;
    private int _count;
    private double _intervalStart = -1;
    private float _least;
    private float _most;
    private double _scaleSum;
    private double _gpuSum;
    private int _intervalFrames;
    private int _gpuFrames;

    public DynamicRenderScale(
        RenderView view,
        GpuTimings timings,
        WindowService? window,
        ILogger<DynamicRenderScale> logger
    )
    {
        _view = view;
        _timings = timings;
        _window = window;
        _logger = logger;
    }

    /// <summary>The thresholds the settings offer: common refresh rates, in frames a second, lowest first.</summary>
    public static ReadOnlySpan<float> Thresholds => s_thresholds;

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
            _sum = 0;
            _count = 0;
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
                var milliseconds = _timings.LastFrameMilliseconds;
                _sum += milliseconds;
                _count++;
                _gpuSum += milliseconds;
                _gpuFrames++;
            }
        }

        if (_count >= StepFrames)
        {
            var next = Next(view.RenderScale, _sum / _count, TargetFps);
            _sum = 0;
            _count = 0;
            if (next != view.RenderScale)
            {
                view.RenderScale = next;
                _settle = SettleFrames;
            }
        }

        Track(now, view.RenderScale);
    }

    /// <summary>
    /// The scale after frames drawn at <paramref name="scale"/> took <paramref name="meanMilliseconds"/> of GPU time on
    /// average, for <paramref name="targetFps"/> frames a second: in whole hundredths, from <see cref="RenderScale.Min"/>
    /// to <see cref="RenderScale.Max"/>. Pure.
    /// </summary>
    public static float Next(float scale, double meanMilliseconds, double targetFps)
    {
        var aim = Aim * 1000 / targetFps;
        var current = (int)Math.Round(scale * Hundredths);
        var least = (int)Math.Round(RenderScale.Min * Hundredths);
        var most = (int)Math.Round(RenderScale.Max * Hundredths);
        int next;
        if (meanMilliseconds > aim)
        {
            // Down at once, by at least a hundredth.
            next = Math.Min(current - 1, (int)Math.Floor(current * Math.Sqrt(aim / meanMilliseconds)));
        }
        else if (meanMilliseconds < RaiseBelow * aim)
        {
            // Up by at most MostRaise, never past the time the aim allows.
            var raise = (int)Math.Round(MostRaise * Hundredths);
            next = Math.Min(current + raise, (int)Math.Floor(current * Math.Sqrt(aim / meanMilliseconds)));
            next = Math.Max(next, current);
        }
        else
        {
            next = current;
        }

        return Math.Clamp(next, least, most) / (float)Hundredths;
    }

    private void Stop(float scale)
    {
        _running = false;
        _view.RenderScale = scale;
    }

    private void StartInterval(double now)
    {
        _intervalStart = now;
        _least = _view.RenderScale;
        _most = _view.RenderScale;
        _scaleSum = 0;
        _gpuSum = 0;
        _intervalFrames = 0;
        _gpuFrames = 0;
    }

    /// <summary>Keeps the interval's range of scales, and gives it in a Debug line every <see cref="LogSeconds"/>.</summary>
    private void Track(double now, float scale)
    {
        _least = Math.Min(_least, scale);
        _most = Math.Max(_most, scale);
        _scaleSum += scale;
        _intervalFrames++;
        if (now - _intervalStart < LogSeconds)
        {
            return;
        }

        var mean = _scaleSum / _intervalFrames;
        var gpu = _gpuFrames > 0 ? _gpuSum / _gpuFrames : 0;
        LogInterval(_least, _most, mean, TargetFps, gpu);
        StartInterval(now);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Render scale: {Least:F2} to {Most:F2}, mean {Mean:F2}, holding {Fps} fps (GPU {Gpu:F2} ms a frame)"
    )]
    private partial void LogInterval(float least, float most, double mean, float fps, double gpu);
}
