using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// A fixed list of texts drawn as Skia text blobs, made once for a font. SkiaSharp 4's
/// <see cref="SKCanvas.DrawText(string, float, float, SKTextAlign, SKFont, SKPaint)"/> makes a blob of its string on every
/// call (160 bytes, measured 2026-10-02), so a layer that draws the same texts at each redraw keeps them here instead:
/// <see cref="Draw"/> places a text as that call would and allocates nothing once the blobs exist. They are made on the
/// first draw with a font, and made again when another font (or the same one at another size) is given, as after the
/// screen's height changes. An empty text draws nothing. Main thread.
/// </summary>
public sealed class TextBlobs : IDisposable
{
    private readonly string[] _texts;
    private readonly SKTextBlob?[] _blobs;
    private readonly float[] _widths;
    private SKFont? _font;
    private float _size;
    private bool _disposed;

    /// <param name="texts">The texts, by index; the list is kept, not copied.</param>
    public TextBlobs(string[] texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        _texts = texts;
        _blobs = new SKTextBlob?[texts.Length];
        _widths = new float[texts.Length];
    }

    /// <summary>How many texts there are.</summary>
    public int Count => _texts.Length;

    /// <summary>The text at <paramref name="index"/>.</summary>
    public string this[int index] => _texts[index];

    /// <summary>
    /// Draws the text at <paramref name="index"/> in <paramref name="font"/> with <paramref name="paint"/>, its baseline at
    /// <paramref name="y"/> and its left, middle, or right at <paramref name="x"/> by <paramref name="align"/>.
    /// </summary>
    public void Draw(SKCanvas canvas, int index, float x, float y, SKTextAlign align, SKFont font, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(paint);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Prepare(font);
        var blob = _blobs[index];
        if (blob is null)
        {
            return;
        }

        var shift =
            align == SKTextAlign.Center ? _widths[index] / 2
            : align == SKTextAlign.Right ? _widths[index]
            : 0;
        canvas.DrawText(blob, x - shift, y, paint);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Free();
    }

    /// <summary>Makes the blobs for <paramref name="font"/> unless they were made for it at its size.</summary>
    private void Prepare(SKFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (ReferenceEquals(font, _font) && font.Size == _size)
        {
            return;
        }

        Free();
        _font = font;
        _size = font.Size;
        for (var i = 0; i < _texts.Length; i++)
        {
            // Null for an empty text, which draws nothing.
            _blobs[i] = SKTextBlob.Create(_texts[i], font);
            _widths[i] = font.MeasureText(_texts[i]);
        }
    }

    private void Free()
    {
        for (var i = 0; i < _blobs.Length; i++)
        {
            _blobs[i]?.Dispose();
            _blobs[i] = null;
        }

        _font = null;
    }
}
