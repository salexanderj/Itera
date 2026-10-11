using System.Buffers;
using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// What a <see cref="UiStyle"/> (and a game's own drawing of an area) draws with: fills and outlines, and text drawn
/// from text blobs made once a text, size, and weight and kept while drawn (Skia makes a blob of a string at every
/// <c>DrawText</c>, 160 bytes), in the UI's typefaces with fallback ones for characters they lack. Text that changes
/// every frame still makes a blob a frame; blobs not drawn for a while are freed. Run-wide, used by one layer's drawing
/// at a time (<see cref="UiToolkit.Painter"/>). Main thread.
/// </summary>
public sealed class UiPainter : IDisposable
{
    /// <summary>A text's blob is freed after this many draws without it.</summary>
    private const int KeptDraws = 120;

    private const string EllipsisText = "…";

    private readonly UiTypefaces _faces;
    private readonly Dictionary<TextKey, CachedText> _cache = [];
    private readonly List<TextKey> _stale = [];
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private int _draws;
    private SKCanvas? _canvas;
    private Ui? _ui;
    private bool _disposed;

    /// <param name="faces">The typefaces text is drawn in.</param>
    public UiPainter(UiTypefaces faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        _faces = faces;
    }

    /// <summary>The canvas being drawn on.</summary>
    public SKCanvas Canvas => _canvas ?? throw new InvalidOperationException("UiPainter: nothing is being drawn.");

    /// <summary>The UI being drawn: its lines and units.</summary>
    public Ui Ui => _ui ?? throw new InvalidOperationException("UiPainter: nothing is being drawn.");

    /// <summary><paramref name="units"/> in pixels on the screen being drawn.</summary>
    public float Px(float units) => units * Ui.UnitPixels;

    /// <summary>How many texts' blobs are kept (tests).</summary>
    internal int CachedTexts => _cache.Count;

    /// <summary>Starts drawing <paramref name="ui"/> on <paramref name="canvas"/>.</summary>
    public void Begin(SKCanvas canvas, Ui ui)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(ui);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _canvas = canvas;
        _ui = ui;
        _draws++;
    }

    /// <summary>Ends a drawing, now and then freeing the blobs of texts long not drawn.</summary>
    public void End()
    {
        _canvas = null;
        _ui = null;
        if (_draws % 32 != 0)
        {
            return;
        }

        _stale.Clear();
        foreach (var (key, cached) in _cache)
        {
            if (_draws - cached.Drawn > KeptDraws)
            {
                _stale.Add(key);
            }
        }

        foreach (var key in _stale)
        {
            _cache.Remove(key, out var cached);
            cached.Blob?.Dispose();
        }

        _stale.Clear();
    }

    /// <summary>Fills <paramref name="rect"/>, its corners rounded by <paramref name="corner"/> pixels.</summary>
    public void Fill(SKRect rect, SKColor colour, float corner = 0)
    {
        _fill.Color = colour;
        if (corner > 0)
        {
            Canvas.DrawRoundRect(rect, corner, corner, _fill);
        }
        else
        {
            Canvas.DrawRect(rect, _fill);
        }
    }

    /// <summary>Outlines <paramref name="rect"/> <paramref name="width"/> pixels wide, its corners rounded by <paramref name="corner"/>.</summary>
    public void Outline(SKRect rect, SKColor colour, float width, float corner = 0)
    {
        _stroke.Color = colour;
        _stroke.StrokeWidth = width;
        if (corner > 0)
        {
            Canvas.DrawRoundRect(rect, corner, corner, _stroke);
        }
        else
        {
            Canvas.DrawRect(rect, _stroke);
        }
    }

    /// <summary>A line from one point to another, <paramref name="width"/> pixels wide.</summary>
    public void Line(float x0, float y0, float x1, float y1, SKColor colour, float width)
    {
        _stroke.Color = colour;
        _stroke.StrokeWidth = width;
        Canvas.DrawLine(x0, y0, x1, y1, _stroke);
    }

    /// <summary>How wide <paramref name="text"/> is at <paramref name="pixels"/>.</summary>
    public float Measure(ReadOnlySpan<char> text, float pixels, bool bold = false) => _faces.Width(text, pixels, bold);

    /// <summary>Where the baseline of a line <paramref name="lineHeight"/> tall starting at <paramref name="top"/> lies, its text centred in it.</summary>
    public float Baseline(float top, float lineHeight, float pixels)
    {
        var metrics = _faces.Font(_faces.Primary, pixels, bold: false).Metrics;
        return top + (lineHeight - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
    }

    /// <summary>
    /// Draws <paramref name="length"/> characters of <paramref name="text"/> from <paramref name="start"/>, their left at
    /// <paramref name="x"/> and baseline at <paramref name="baseline"/>, followed by an ellipsis when
    /// <paramref name="ellipsis"/>; returns the width drawn.
    /// </summary>
    public float Text(
        string text,
        int start,
        int length,
        float x,
        float baseline,
        float pixels,
        bool bold,
        SKColor colour,
        bool ellipsis = false
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        var key = new TextKey(text, start, length, pixels, bold, ellipsis);
        if (!_cache.TryGetValue(key, out var cached))
        {
            cached = Make(text.AsSpan(start, length), pixels, bold, ellipsis);
        }

        cached.Drawn = _draws;
        _cache[key] = cached;
        if (cached.Blob is not null)
        {
            _text.Color = colour;
            Canvas.DrawText(cached.Blob, x, baseline, _text);
        }

        return cached.Width;
    }

    /// <summary>One line of <paramref name="text"/> placed in <paramref name="rect"/> by the alignments (not cut where it does not fit).</summary>
    public void Label(
        string text,
        SKRect rect,
        float pixels,
        bool bold,
        SKColor colour,
        UiAlign alignX = UiAlign.Centre,
        UiAlign alignY = UiAlign.Centre
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        var width = Measure(text, pixels, bold);
        var lineHeight = Ui.LineHeight(pixels);
        var baseline = Baseline(rect.Top + Offset(alignY, rect.Height - lineHeight), lineHeight, pixels);
        Text(text, 0, text.Length, rect.Left + Offset(alignX, rect.Width - width), baseline, pixels, bold, colour);
    }

    /// <summary>
    /// An element's laid-out lines in its inner rectangle: the block of lines placed down by <paramref name="alignY"/>, each
    /// line across by <paramref name="alignX"/> (the text's own alignment when null), with an ellipsis where it was cut.
    /// </summary>
    public void Lines(
        in UiElement element,
        SKColor colour,
        bool bold,
        UiAlign? alignX = null,
        UiAlign alignY = UiAlign.Start
    )
    {
        var text = element.Kind == UiKind.TextBox ? element.Entry?.Line.Text : element.Text;
        if (text is null || element.LineCount == 0)
        {
            return;
        }

        var inner = element.Inner;
        var across = alignX ?? element.Setting.Align;
        var top = inner.Top + Offset(alignY, inner.Height - element.LineCount * element.LineHeight);
        for (var i = 0; i < element.LineCount; i++)
        {
            var line = Ui.Line(element.FirstLine + i);
            var baseline = Baseline(top + i * element.LineHeight, element.LineHeight, element.TextPixels);
            var x = inner.Left + Offset(across, inner.Width - line.Width);
            Text(text, line.Start, line.Length, x, baseline, element.TextPixels, bold, colour, line.Ellipsis);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var cached in _cache.Values)
        {
            cached.Blob?.Dispose();
        }

        _cache.Clear();
        _fill.Dispose();
        _stroke.Dispose();
        _text.Dispose();
    }

    private static float Offset(UiAlign align, float free) =>
        align switch
        {
            UiAlign.Centre => free / 2,
            UiAlign.End => free,
            _ => 0,
        };

    /// <summary>A text's blob, run by run of typeface, the ellipsis after it.</summary>
    private CachedText Make(ReadOnlySpan<char> text, float pixels, bool bold, bool ellipsis)
    {
        using var builder = new SKTextBlobBuilder();
        var x = Runs(builder, text, 0, pixels, bold);
        if (ellipsis)
        {
            x = Runs(builder, EllipsisText, x, pixels, bold);
        }

        return new CachedText { Blob = x > 0 ? builder.Build() : null, Width = x };
    }

    /// <summary>Adds <paramref name="text"/>'s runs to <paramref name="builder"/> from <paramref name="x"/>; returns where they end.</summary>
    private float Runs(SKTextBlobBuilder builder, ReadOnlySpan<char> text, float x, float pixels, bool bold)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = _faces.Run(text, start, out var face);
            var font = _faces.Font(face, pixels, bold);
            var run = text[start..end];
            var count = font.CountGlyphs(run);
            var glyphs = ArrayPool<ushort>.Shared.Rent(Math.Max(1, count));
            try
            {
                font.GetGlyphs(run, glyphs.AsSpan(0, count));
                builder.AddRun(glyphs.AsSpan(0, count), font, new SKPoint(x, 0));
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(glyphs);
            }

            x += font.MeasureText(run);
            start = end;
        }

        return x;
    }

    /// <summary>What a drawn text is kept by: its characters (wherever in a string they are), size, weight, and ellipsis.</summary>
    private readonly record struct TextKey(string Text, int Start, int Length, float Pixels, bool Bold, bool Ellipsis)
    {
        public bool Equals(TextKey other) =>
            Length == other.Length
            && Pixels == other.Pixels
            && Bold == other.Bold
            && Ellipsis == other.Ellipsis
            && Text.AsSpan(Start, Length).SequenceEqual(other.Text.AsSpan(other.Start, other.Length));

        public override int GetHashCode() =>
            HashCode.Combine(string.GetHashCode(Text.AsSpan(Start, Length)), Pixels, Bold, Ellipsis);
    }

    private struct CachedText
    {
        public SKTextBlob? Blob;
        public float Width;
        public int Drawn;
    }
}
