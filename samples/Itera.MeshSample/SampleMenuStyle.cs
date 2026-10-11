using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using SkiaSharp;

namespace Itera.MeshSample;

/// <summary>
/// The sample menu's look, to show a game's own over the engine's plain one: deep blue-grey panels, amber under the
/// pointer (the yard's amber cubes), small corners and thin outlines, a bold title and headings. A bar entry (a button
/// whose label is a heading, <see cref="SampleMenu"/>'s) is drawn flat, its label at its left, filled amber under the
/// pointer and marked by an amber bar while its page shows; every other kind is the engine's drawing in these colours.
/// </summary>
public sealed class SampleMenuStyle : UiStyle
{
    /// <summary>The sample's colours.</summary>
    public static MenuStyle Palette { get; } =
        new(
            Text: new SKColor(236, 232, 222),
            TextDim: new SKColor(146, 156, 170),
            Shade: new SKColor(12, 18, 27, 242),
            Button: new SKColor(40, 54, 72),
            ButtonHover: new SKColor(226, 160, 72),
            ButtonDisabled: new SKColor(32, 40, 52, 170),
            Ink: new SKColor(14, 20, 28),
            ButtonText: new SKColor(230, 232, 236),
            ButtonTextHover: new SKColor(24, 20, 16),
            Field: new SKColor(22, 30, 42),
            Warning: new SKColor(240, 128, 108)
        );

    /// <summary>Body text of 30 units (2.8 % of the reference screen's height).</summary>
    public SampleMenuStyle()
        : base(bodyUnits: 30, Palette) { }

    public override float CornerUnits => 6;

    public override float OutlineUnits => 2;

    public override float TextUnits(in UiText text) =>
        text.Units <= 0 && text.Role == UiTextRole.Title ? 1.9f * BodyUnits : base.TextUnits(in text);

    public override bool Bold(in UiText text) => text.Role is UiTextRole.Title or UiTextRole.Heading;

    public override void DrawButton(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        if (element.Setting.Role != UiTextRole.Heading)
        {
            base.DrawButton(painter, in element);
            return;
        }

        var rect = element.Rect;
        var lit = element.Hovered && !element.Disabled;
        if (lit)
        {
            painter.Fill(rect, Colours.ButtonHover, painter.Px(CornerUnits));
        }

        if (element.Selected)
        {
            var inset = rect.Height * 0.2f;
            var bar = new SKRect(rect.Left, rect.Top + inset, rect.Left + painter.Px(6), rect.Bottom - inset);
            painter.Fill(bar, lit ? Colours.ButtonTextHover : Colours.ButtonHover);
        }

        var colour =
            element.Disabled ? Colours.TextDim
            : lit ? Colours.ButtonTextHover
            : Colours.Text;
        painter.Lines(in element, colour, Bold(element.Setting), UiAlign.Start, UiAlign.Centre);
    }
}
