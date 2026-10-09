using SkiaSharp;

namespace Itera.Engine.UI.Menu;

/// <summary>
/// The menu's look: its colours. The engine's is plain; a game registers its own.
/// </summary>
/// <param name="Text">Titles and lines of text.</param>
/// <param name="TextDim">Hints and lesser lines.</param>
/// <param name="Shade">Laid over the picture under a menu shown during a match, and behind the hint band.</param>
/// <param name="Button">A button's face.</param>
/// <param name="ButtonHover">A button's face under the pointer.</param>
/// <param name="ButtonDisabled">A button's face when it does nothing now.</param>
/// <param name="Ink">Buttons' and boxes' outlines.</param>
/// <param name="ButtonText">A button's label.</param>
/// <param name="ButtonTextHover">A button's label under the pointer.</param>
/// <param name="Field">A text box's face.</param>
/// <param name="Warning">Why a match ended or could not start.</param>
public sealed record MenuStyle(
    SKColor Text,
    SKColor TextDim,
    SKColor Shade,
    SKColor Button,
    SKColor ButtonHover,
    SKColor ButtonDisabled,
    SKColor Ink,
    SKColor ButtonText,
    SKColor ButtonTextHover,
    SKColor Field,
    SKColor Warning
)
{
    /// <summary>The engine's plain look: light grey on dark.</summary>
    public static MenuStyle Plain { get; } =
        new(
            Text: new SKColor(230, 230, 230),
            TextDim: new SKColor(170, 170, 170),
            Shade: new SKColor(0, 0, 0, 210),
            Button: new SKColor(60, 60, 66, 235),
            ButtonHover: new SKColor(90, 90, 100, 245),
            ButtonDisabled: new SKColor(45, 45, 48, 160),
            Ink: new SKColor(20, 20, 22, 255),
            ButtonText: new SKColor(235, 235, 235),
            ButtonTextHover: new SKColor(255, 255, 255),
            Field: new SKColor(25, 25, 28, 235),
            Warning: new SKColor(235, 120, 110)
        );
}
