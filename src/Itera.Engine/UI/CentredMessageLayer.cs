using Itera.Modding;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// A message in the middle of the screen, drawn with a shadow over the picture: a countdown, an end-of-match notice.
/// Its texts come from a fixed list, made once as text blobs (<see cref="TextBlobs"/>), and a callback chooses the one
/// shown at each frame (its index, or -1 for none), so the layer allocates nothing after its first draw. It is drawn
/// again only when the choice changes, or when its size does (the UI's sizing rule, <see cref="LayerFont"/>). Main
/// thread.
/// </summary>
public sealed class CentredMessageLayer : IMatchUiLayer, IDisposable
{
    /// <summary>The index <c>choose</c> returns while nothing is shown.</summary>
    public const int None = -1;

    /// <summary>The space over and under the text, in units.</summary>
    private const float PaddingUnits = 16;

    private static readonly SKColor s_textColour = new(235, 225, 215);
    private static readonly SKColor s_shadowColour = new(0, 0, 0, 200);

    private readonly LayerFont _font;
    private readonly TextBlobs _texts;
    private readonly Func<int> _choose;
    private readonly float _heightShare;
    private SKPaint? _text;
    private SKPaint? _shadow;
    private int _shown = None;
    private bool _dirty;
    private bool _disposed;

    /// <param name="fonts">The UI's typeface and sizing rule.</param>
    /// <param name="texts">The texts, by index; the list is kept, not copied.</param>
    /// <param name="choose">
    /// The index of the text to show now, or <see cref="None"/>. Called once a frame; an index outside the list is
    /// clamped to it.
    /// </param>
    /// <param name="textScale">The text's size as a multiple of the UI's (<c>Engine:Ui:FontPixels</c>).</param>
    /// <param name="heightShare">How far down the screen the text's middle is, as a share of its height.</param>
    public CentredMessageLayer(
        UiFonts fonts,
        string[] texts,
        Func<int> choose,
        float textScale = 2,
        float heightShare = 0.4f
    )
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(choose);
        _font = new LayerFont(fonts, textScale, embolden: true);
        _texts = new TextBlobs(texts);
        _choose = choose;
        _heightShare = heightShare;
    }

    /// <summary>The index shown, or <see cref="None"/> while nothing is.</summary>
    public int Shown => _shown;

    /// <summary>The text shown, or null while nothing is.</summary>
    public string? Text => _shown >= 0 ? _texts[_shown] : null;

    public bool Visible => _shown >= 0;

    public bool Dirty => _dirty;

    public void Update(in FrameTime time, int width, int height)
    {
        _dirty |= _font.Fit(height);
        Update(in time);
    }

    public void Update(in FrameTime time)
    {
        var chosen = _choose();
        var shown = chosen < 0 ? None : Math.Min(chosen, _texts.Count - 1);
        if (shown != _shown)
        {
            _shown = shown;
            _dirty = true;
        }
    }

    public SKRectI Bounds(int width, int height)
    {
        _font.Fit(height);
        var lineHeight = _font.Font.Size * 1.4f;
        var padding = _font.Whole(PaddingUnits);
        var middle = height * _heightShare;
        return SKRectI.Ceiling(
            new SKRect(0, middle - lineHeight / 2 - padding, width, middle + lineHeight / 2 + padding)
        );
    }

    public void Draw(SKCanvas canvas, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _dirty = false;
        if (_shown < 0)
        {
            return;
        }

        _font.Fit(height);
        var font = _font.Font;
        _text ??= new SKPaint { Color = s_textColour, IsAntialias = true };
        _shadow ??= new SKPaint { Color = s_shadowColour, IsAntialias = true };
        var baseline = height * _heightShare + font.Metrics.CapHeight / 2;
        var shadow = _font.Whole(2);
        _texts.Draw(canvas, _shown, width / 2f + shadow, baseline + shadow, SKTextAlign.Center, font, _shadow);
        _texts.Draw(canvas, _shown, width / 2f, baseline, SKTextAlign.Center, font, _text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _texts.Dispose();
        _font.Dispose();
        _text?.Dispose();
        _shadow?.Dispose();
    }
}
