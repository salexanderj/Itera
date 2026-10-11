using System.Globalization;
using Itera.Engine.Diagnostics;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Particles;
using Itera.Engine.Rendering;
using Itera.Modding;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// The stats line in the top-right corner: frames per second, the frame time (mean and longest), the GPU time of the
/// last frame, fixed steps per second, and the session (the role, the players on a host, the round trip on a client).
/// It changes every <see cref="IntervalSeconds"/>. Off unless <c>Engine:Ui:Stats</c> is set; its key
/// (<see cref="KeyBindings.Stats"/>, F1 by default) and the <c>stats</c> command turn it on and off. Its text and margins
/// follow the UI's sizing rule (<see cref="LayerFont"/>). It allocates only when its text changes.
/// </summary>
public sealed class StatsLayer : IUiLayer, IDisposable
{
    /// <summary>How often the numbers change, in seconds.</summary>
    public const double IntervalSeconds = 0.5;

    /// <summary>The space between the line's box and the screen's corner, in units.</summary>
    private const float MarginUnits = 8f;

    /// <summary>The space round the text in its box, in units.</summary>
    private const float PaddingUnits = 6f;

    private readonly LayerFont _font;
    private readonly GpuTimings _gpu;
    private readonly FrameStats _stats;
    private readonly MatchControl _matches;
    private readonly ParticleStats? _particles;
    private readonly IFrameInput _input;
    private readonly KeyBindings _keys;
    private readonly RenderView? _view;
    private SKPaint? _text;
    private SKPaint? _backing;
    private string _line = "";
    private float _lineWidth;
    private bool _shown;
    private bool _dirty;
    private double _intervalStart = -1;
    private int _frames;
    private double _frameSeconds;
    private float _longestFrame;
    private long _stepsAtStart;
    private bool _disposed;

    public StatsLayer(
        IOptions<EngineOptions> options,
        UiFonts fonts,
        GpuTimings gpu,
        FrameStats stats,
        MatchControl matches,
        IFrameInput input,
        ParticleStats? particles = null,
        KeyBindings? keys = null,
        RenderView? view = null
    )
    {
        _keys = keys ?? new KeyBindings();
        _view = view;
        _particles = particles;
        _font = new LayerFont(fonts);
        _gpu = gpu;
        _stats = stats;
        _matches = matches;
        _input = input;
        _shown = options.Value.Ui.Stats;
    }

    /// <summary>True while the stats line is shown.</summary>
    public bool Shown
    {
        get => _shown;
        set
        {
            if (value != _shown)
            {
                _shown = value;
                _dirty = true;
            }
        }
    }

    public bool Visible => _shown;

    public bool Dirty => _dirty;

    /// <summary>The line as last drawn, for tests.</summary>
    public string Line => _line;

    public void Update(in FrameTime time, int width, int height)
    {
        Fit(height);
        Update(in time);
    }

    public void Update(in FrameTime time)
    {
        if (_keys.WasPressed(in _input.Frame, _keys.Stats))
        {
            Shown = !Shown;
        }

        if (_intervalStart < 0)
        {
            _intervalStart = time.TotalSeconds;
            _stepsAtStart = _stats.TotalFixedSteps;
        }

        _frames++;
        _frameSeconds += time.DeltaSeconds;
        _longestFrame = Math.Max(_longestFrame, time.DeltaSeconds);
        var elapsed = time.TotalSeconds - _intervalStart;
        if (elapsed < IntervalSeconds)
        {
            return;
        }

        if (_shown)
        {
            var steps = _stats.TotalFixedSteps - _stepsAtStart;
            _line = Compose(
                _frames / elapsed,
                _frameSeconds / _frames * 1000.0,
                _longestFrame * 1000.0,
                _gpu.Enabled ? _gpu.LastFrameMilliseconds : -1,
                steps / elapsed
            );
            _lineWidth = _font.Font.MeasureText(_line);
            _dirty = true;
        }

        _intervalStart = time.TotalSeconds;
        _stepsAtStart = _stats.TotalFixedSteps;
        _frames = 0;
        _frameSeconds = 0;
        _longestFrame = 0;
    }

    public SKRectI Bounds(int width, int height)
    {
        Fit(height);
        return SKRectI.Ceiling(Box(width), outwards: true);
    }

    public void Draw(SKCanvas canvas, int width, int height)
    {
        _dirty = false;
        Fit(height);
        var box = Box(width);
        _backing ??= new SKPaint { Color = new SKColor(0, 0, 0, 160) };
        _text ??= new SKPaint { Color = new SKColor(230, 230, 230), IsAntialias = true };
        canvas.DrawRect(box, _backing);
        var font = _font.Font;
        var padding = _font.Whole(PaddingUnits);
        canvas.DrawText(
            _line,
            box.Left + padding,
            box.Top + padding - font.Metrics.Ascent,
            SKTextAlign.Left,
            font,
            _text
        );
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _font.Dispose();
        _text?.Dispose();
        _backing?.Dispose();
    }

    /// <summary>The line's box in the top-right corner of a screen <paramref name="width"/> pixels wide.</summary>
    private SKRect Box(int width)
    {
        var margin = _font.Whole(MarginUnits);
        var padding = _font.Whole(PaddingUnits);
        var metrics = _font.Font.Metrics;
        var right = width - margin;
        return new SKRect(
            right - _lineWidth - 2 * padding,
            margin,
            right,
            margin + metrics.Descent - metrics.Ascent + 2 * padding
        );
    }

    /// <summary>The font sized for the screen; the line measured again when its size changed.</summary>
    private void Fit(int height)
    {
        if (_font.Fit(height))
        {
            _lineWidth = _font.Font.MeasureText(_line);
            _dirty = true;
        }
    }

    private string Compose(double fps, double meanMs, double longestMs, double gpuMs, double stepsPerSecond)
    {
        var culture = CultureInfo.InvariantCulture;
        var gpu = gpuMs < 0 ? "GPU -" : string.Create(culture, $"GPU {gpuMs:F1} ms");
        // The render scale the frames are drawn at (dynamic render scaling moves it).
        var scale = _view is null ? "" : string.Create(culture, $"  scale {_view.RenderScale * 100:F0} %");
        var current = _matches.Current?.Session;
        var session = current?.Role switch
        {
            null => "menu",
            SessionRole.Host => string.Create(culture, $"host, {PlayerCount(current)} players"),
            SessionRole.Client => current.HostRoundTripMilliseconds is >= 0 and var ms
                ? string.Create(culture, $"client, {ms} ms")
                : "client",
            _ => "offline",
        };
        var living = _particles?.Living ?? 0;
        var particles = living > 0 ? string.Create(culture, $"  {living} particles") : "";
        return string.Create(
            culture,
            $"{fps:F0} fps  {meanMs:F1} ms (max {longestMs:F1})  {gpu}{scale}  {stepsPerSecond:F0} steps/s  {session}{particles}"
        );
    }

    private static int PlayerCount(SessionService session)
    {
        var count = 0;
        foreach (var player in session.Players)
        {
            if (player.Present)
            {
                count++;
            }
        }

        return count;
    }
}
