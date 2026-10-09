using Itera.Modding;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// A message in the middle of the screen, drawn with a shadow over the picture: a countdown, an end-of-match notice.
/// Its texts come from a fixed list, made once as text blobs (<see cref="TextBlobs"/>), and a callback chooses the one
/// shown at each frame (its index, or -1 for none), so the layer allocates nothing after its first draw. It is drawn
/// again only when the choice changes. Main thread.
/// </summary>
public sealed class CentredMessageLayer : IMatchUiLayer, IDisposable
{
    /// <summary>The index <c>choose</c> returns while nothing is shown.</summary>
    public const int None = -1;

    private const float Padding = 16;

    private static readonly SKColor s_textColour = new(235, 225, 215);
    private static readonly SKColor s_shadowColour = new(0, 0, 0, 200);

    private readonly UiFonts _fonts;
    private readonly TextBlobs _texts;
    private readonly Func<int> _choose;
    private readonly float _textScale;
    private readonly float _heightShare;
    private SKFont? _font;
    private SKPaint? _text;
    private SKPaint? _shadow;
    private int _shown = None;
    private bool _dirty;
    private bool _disposed;

    /// <param name="fonts">The UI's typeface and text size.</param>
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
        _fonts = fonts;
        _texts = new TextBlobs(texts);
        _choose = choose;
        _textScale = textScale;
        _heightShare = heightShare;
    }

    /// <summary>The index shown, or <see cref="None"/> while nothing is.</summary>
    public int Shown => _shown;

    /// <summary>The text shown, or null while nothing is.</summary>
    public string? Text => _shown >= 0 ? _texts[_shown] : null;

    public bool Visible => _shown >= 0;

    public bool Dirty => _dirty;

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
        var lineHeight = Font.Size * 1.4f;
        var middle = height * _heightShare;
        return SKRectI.Ceiling(
            new SKRect(0, middle - lineHeight / 2 - Padding, width, middle + lineHeight / 2 + Padding)
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

        var font = Font;
        _text ??= new SKPaint { Color = s_textColour, IsAntialias = true };
        _shadow ??= new SKPaint { Color = s_shadowColour, IsAntialias = true };
        var baseline = height * _heightShare + font.Metrics.CapHeight / 2;
        _texts.Draw(canvas, _shown, width / 2f + 2, baseline + 2, SKTextAlign.Center, font, _shadow);
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
        _font?.Dispose();
        _text?.Dispose();
        _shadow?.Dispose();
    }

    private SKFont Font => _font ??= new SKFont(_fonts.Mono, _fonts.Pixels * _textScale) { Embolden = true };
}
