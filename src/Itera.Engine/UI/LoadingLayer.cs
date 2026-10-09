using System.Globalization;
using Itera.Engine.Loading;
using Itera.Modding;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// The loading screen's text: one line per loading task, in the ring's empty middle, with its
/// percentage while it runs and a tick once done. Shown only while the loading screen covers the picture. It composes
/// its lines again only when <see cref="LoadingScreen.Version"/> changes, and allocates only then.
/// </summary>
public sealed class LoadingLayer : IUiLayer, IDisposable
{
    private const float Padding = 6f;
    private const char Tick = '✓';

    private readonly LoadingScreen _loading;
    private readonly UiFonts _fonts;
    private string[] _lines = [];
    private int _version = -1;
    private float _width;
    private bool _dirty;
    private string? _tick;
    private SKFont? _font;
    private SKPaint? _text;
    private SKPaint? _done;
    private bool _disposed;

    public LoadingLayer(LoadingScreen loading, UiFonts fonts)
    {
        _loading = loading;
        _fonts = fonts;
    }

    public bool Visible => _loading.Active;

    public bool Dirty => _dirty;

    /// <summary>The lines as last composed, for tests.</summary>
    public IReadOnlyList<string> Lines => _lines;

    public void Update(in FrameTime time)
    {
        if (!_loading.Active || _loading.Version == _version)
        {
            return;
        }

        _version = _loading.Version;

        var count = _loading.TaskCount;
        if (_lines.Length != count)
        {
            _lines = new string[count];
        }

        _width = 0;
        for (var i = 0; i < count; i++)
        {
            _lines[i] = Compose(_loading.TaskName(i), _loading.TaskProgress(i));
            _width = Math.Max(_width, Font.MeasureText(_lines[i]));
        }

        _dirty = true;
    }

    public SKRectI Bounds(int width, int height)
    {
        var box = Box(width, height);
        return SKRectI.Ceiling(box);
    }

    public void Draw(SKCanvas canvas, int width, int height)
    {
        _dirty = false;
        _text ??= new SKPaint { Color = new SKColor(200, 196, 186), IsAntialias = true };
        _done ??= new SKPaint { Color = new SKColor(131, 101, 57), IsAntialias = true };
        var box = Box(width, height);
        var font = Font;
        var lineHeight = LineHeight;
        var baseline = box.Top + Padding - font.Metrics.Ascent;
        for (var i = 0; i < _lines.Length; i++)
        {
            var paint = _loading.TaskProgress(i).Done ? _done : _text;
            canvas.DrawText(_lines[i], box.Left + Padding, baseline + i * lineHeight, SKTextAlign.Left, font, paint);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _font?.Dispose();
        _text?.Dispose();
        _done?.Dispose();
    }

    private SKFont Font => _font ??= new SKFont(_fonts.Mono, _fonts.Pixels);

    private float LineHeight => Font.Metrics.Descent - Font.Metrics.Ascent;

    /// <summary>The lines' box, centred on the screen: the ring's empty middle.</summary>
    private SKRect Box(int width, int height)
    {
        var boxWidth = _width + 2 * Padding;
        var boxHeight = _lines.Length * LineHeight + 2 * Padding;
        var left = (width - boxWidth) / 2;
        var top = (height - boxHeight) / 2;
        return new SKRect(left, top, left + boxWidth, top + boxHeight);
    }

    /// <summary>"✓ Sounds" once done, "  World 42 %" while it runs, "  Navigation" when it cannot tell how far.</summary>
    private string Compose(string name, LoadingProgress progress)
    {
        if (progress.Done)
        {
            // The font may lack the tick; a plus sign stands in.
            _tick ??= Font.ContainsGlyph(Tick) ? Tick.ToString() : "+";
            return _tick + " " + name;
        }

        return progress.Fraction < 0
            ? "  " + name
            : string.Create(CultureInfo.InvariantCulture, $"  {name} {(int)(progress.Fraction * 100)} %");
    }
}
