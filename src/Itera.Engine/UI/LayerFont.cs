using Itera.Engine.UI.Toolkit;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// A layer's font at the UI's text size for its screen (<see cref="UiFonts.Pixels"/>), resized when the screen's height
/// or the UI scale changes (<see cref="Fit"/>), and the pixels of a unit there (<see cref="Unit"/>) for the layer's
/// margins. For layers that draw text with Skia themselves (the console, the stats line, the loading list, a centred
/// message); a toolkit layer is sized by its <see cref="Ui"/>. Main thread.
/// </summary>
public sealed class LayerFont : IDisposable
{
    private readonly UiFonts _fonts;
    private readonly float _share;
    private readonly bool _embolden;
    private SKFont? _font;
    private float _height = Ui.ReferenceHeight;
    private float _unit;

    /// <param name="fonts">The UI's typeface and sizing rule.</param>
    /// <param name="share">The text's size as a multiple of the UI's (<c>Engine:Ui:FontPixels</c>).</param>
    /// <param name="embolden">Drawn bolder than the typeface.</param>
    public LayerFont(UiFonts fonts, float share = 1, bool embolden = false)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        _fonts = fonts;
        _share = share;
        _embolden = embolden;
    }

    /// <summary>The font at the size of the last <see cref="Fit"/> (a 1080-pixel-high screen's before the first).</summary>
    public SKFont Font
    {
        get
        {
            if (_font is null)
            {
                _font = new SKFont(_fonts.Mono, _fonts.Pixels(_height, _share)) { Embolden = _embolden };
                _unit = _fonts.UnitPixels(_height);
            }

            return _font;
        }
    }

    /// <summary>Screen pixels a unit at the size of the last <see cref="Fit"/>.</summary>
    public float Unit
    {
        get
        {
            _ = Font;
            return _unit;
        }
    }

    /// <summary>
    /// Sizes the font for a screen <paramref name="screenHeight"/> pixels high at the UI scale now; true when its size
    /// changed, so the layer measures its text again and draws again.
    /// </summary>
    public bool Fit(int screenHeight)
    {
        _height = Math.Max(1, screenHeight);
        var font = Font;
        var pixels = _fonts.Pixels(_height, _share);
        _unit = _fonts.UnitPixels(_height);
        if (font.Size == pixels)
        {
            return false;
        }

        font.Size = pixels;
        return true;
    }

    /// <summary><paramref name="units"/> in whole screen pixels at the size of the last <see cref="Fit"/>, at least 1 for more than none: margins and padding that keep a box on whole pixels.</summary>
    public float Whole(float units) => units <= 0 ? 0 : MathF.Max(1, MathF.Round(units * Unit));

    public void Dispose()
    {
        _font?.Dispose();
        _font = null;
    }
}
