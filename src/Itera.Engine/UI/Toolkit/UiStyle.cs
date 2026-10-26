using Itera.Engine.UI.Menu;
using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// How a <see cref="Ui"/>'s elements look and how large they are when the code leaves it to the style: each kind's
/// default box (<see cref="DefaultBox"/>), each text role's size and weight (<see cref="TextUnits"/>,
/// <see cref="Bold"/>), the colours of the text tones (<see cref="Tone"/>), and the drawing of each kind, every part of
/// it a virtual method a game's style overrides (or a whole style of its own). This one draws the engine's plain look in
/// <see cref="Colours"/> (the menu's <see cref="MenuStyle"/>): rounded faces with an outline, a switch as a face reading
/// On or Off, a slider's bar filled to its value. Sizes are units (<see cref="UiSize"/>), proportions of
/// <see cref="BodyUnits"/>, the ordinary text's size.
/// </summary>
public class UiStyle
{
    private const string OnText = "On";
    private const string OffText = "Off";

    /// <param name="bodyUnits">The ordinary text's size in units (<c>Engine:Ui:FontPixels</c> in the engine's style).</param>
    /// <param name="colours">The colours; the engine's plain ones when null.</param>
    public UiStyle(float bodyUnits = 18, MenuStyle? colours = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bodyUnits);
        BodyUnits = bodyUnits;
        Colours = colours ?? MenuStyle.Plain;
    }

    /// <summary>The ordinary text's size in units.</summary>
    public float BodyUnits { get; }

    /// <summary>The colours this style draws in.</summary>
    public MenuStyle Colours { get; }

    /// <summary>Corners' rounding, in units.</summary>
    public virtual float CornerUnits => 0.7f * BodyUnits;

    /// <summary>Outlines' width, in units.</summary>
    public virtual float OutlineUnits => 0.18f * BodyUnits;

    /// <summary>How far past an element's rectangle this style may draw (the focus's outline, a glow), in units.</summary>
    public virtual float OverdrawUnits => 3 * OutlineUnits;

    /// <summary>
    /// The box an element of <paramref name="kind"/> takes where the code leaves a part of it at its default: containers
    /// and texts fit their content with no padding or gap; a button pads its label; a switch, a slider, and a text box
    /// have sizes of their own; an area fits nothing (the game gives it a size).
    /// </summary>
    public virtual UiBox DefaultBox(UiKind kind)
    {
        var body = BodyUnits;
        return kind switch
        {
            UiKind.Button => new UiBox
            {
                Width = UiSize.Fit,
                Height = UiSize.Fit,
                Padding = new UiEdges(body, body / 2),
            },
            UiKind.Switch => UiBox.Fixed(2.5f * body, 1.5f * body),
            UiKind.Slider => UiBox.Fixed(12 * body, 1.5f * body),
            UiKind.TextBox => new UiBox
            {
                Width = UiSize.Fixed(16 * body),
                Height = UiSize.Fit,
                Padding = new UiEdges(body / 2, body / 3),
            },
            _ => new UiBox { Width = UiSize.Fit, Height = UiSize.Fit },
        };
    }

    /// <summary>The size in units of a text set as <paramref name="text"/>: its own, else its role's.</summary>
    public virtual float TextUnits(in UiText text) =>
        text.Units > 0
            ? text.Units
            : text.Role switch
            {
                UiTextRole.Title => 2.5f * BodyUnits,
                UiTextRole.Heading => 1.4f * BodyUnits,
                UiTextRole.Small => 0.85f * BodyUnits,
                _ => BodyUnits,
            };

    /// <summary>True for a text drawn heavier: titles.</summary>
    public virtual bool Bold(in UiText text) => text.Role == UiTextRole.Title;

    /// <summary>A text tone's colour.</summary>
    public virtual SKColor Tone(UiTone tone) =>
        tone switch
        {
            UiTone.Dim => Colours.TextDim,
            UiTone.Strong => Colours.ButtonTextHover,
            UiTone.Warning => Colours.Warning,
            _ => Colours.Text,
        };

    /// <summary>Draws <paramref name="element"/> by its kind; containers draw their panel when asked for one.</summary>
    public virtual void Draw(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        if (element.Panel)
        {
            DrawPanel(painter, in element);
        }

        switch (element.Kind)
        {
            case UiKind.Text:
                DrawText(painter, in element);
                break;
            case UiKind.Button:
                DrawButton(painter, in element);
                break;
            case UiKind.Switch:
                DrawSwitch(painter, in element);
                break;
            case UiKind.Slider:
                DrawSlider(painter, in element);
                break;
            case UiKind.TextBox:
                DrawTextBox(painter, in element);
                break;
            case UiKind.Pressable:
                DrawPressable(painter, in element);
                break;
        }

        if (element.Focused)
        {
            DrawFocus(painter, in element);
        }
    }

    /// <summary>A panel behind a container or element: the shade, rounded.</summary>
    public virtual void DrawPanel(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        painter.Fill(element.Rect, Colours.Shade, painter.Px(CornerUnits));
    }

    /// <summary>A text in its tone.</summary>
    public virtual void DrawText(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        painter.Lines(in element, Tone(element.Setting.Tone), Bold(element.Setting));
    }

    /// <summary>A button: a rounded face (lighter under the pointer, sunk when selected, dim when disabled), outlined, its label centred.</summary>
    public virtual void DrawButton(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        Face(painter, in element);
        painter.Lines(in element, LabelColour(in element), Bold(element.Setting), UiAlign.Centre, UiAlign.Centre);
    }

    /// <summary>A pressable row: a button's face behind its children.</summary>
    public virtual void DrawPressable(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        Face(painter, in element);
    }

    /// <summary>A switch: a button's face reading On or Off.</summary>
    public virtual void DrawSwitch(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        Face(painter, in element);
        painter.Label(
            element.On ? OnText : OffText,
            element.Rect,
            Math.Max(Ui.LeastTextPixels, painter.Px(BodyUnits)),
            bold: false,
            LabelColour(in element)
        );
    }

    /// <summary>A slider: a sunk bar filled to its value, lighter under the pointer or while dragged, outlined.</summary>
    public virtual void DrawSlider(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        var corner = painter.Px(CornerUnits);
        var rect = element.Rect;
        painter.Fill(rect, Colours.Field, corner);
        var filled = new SKRect(rect.Left, rect.Top, rect.Left + element.Share * rect.Width, rect.Bottom);
        if (filled.Width > 0)
        {
            var colour =
                element.Disabled ? Colours.ButtonDisabled
                : element.Hovered || element.Pressed ? Colours.ButtonHover
                : Colours.Button;
            painter.Fill(filled, colour, corner);
        }

        painter.Outline(rect, Colours.Ink, painter.Px(OutlineUnits), corner);
    }

    /// <summary>
    /// A text box: a sunk face, its outline lit while it has the typing, its text moved left as far as the cursor needs,
    /// the cursor while typing, or its placeholder dim while empty.
    /// </summary>
    public virtual void DrawTextBox(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        var corner = painter.Px(CornerUnits);
        var rect = element.Rect;
        painter.Fill(rect, Colours.Field, corner);
        painter.Outline(
            rect,
            !element.Disabled && (element.Typing || element.Hovered) ? Colours.ButtonHover : Colours.Ink,
            painter.Px(OutlineUnits),
            corner
        );
        var inner = element.Inner;
        var pixels = element.TextPixels;
        var baseline = painter.Baseline(inner.MidY - element.LineHeight / 2, element.LineHeight, pixels);
        var line = element.Entry?.Line;
        if (line is null || (line.Length == 0 && !element.Typing))
        {
            if (element.Text is { Length: > 0 } placeholder)
            {
                painter.Text(placeholder, 0, placeholder.Length, inner.Left, baseline, pixels, false, Colours.TextDim);
            }

            return;
        }

        var text = line.Text;
        var cursor = painter.Measure(text.AsSpan(0, line.Cursor), pixels);
        var shift = Math.Max(0, cursor - inner.Width);
        var canvas = painter.Canvas;
        canvas.Save();
        canvas.ClipRect(new SKRect(inner.Left, rect.Top, inner.Right + 1, rect.Bottom));
        painter.Text(
            text,
            0,
            text.Length,
            inner.Left - shift,
            baseline,
            pixels,
            false,
            element.Disabled ? Colours.TextDim : Colours.Text
        );
        if (element.Typing)
        {
            var x = inner.Left + cursor - shift;
            painter.Line(x, inner.Top, x, inner.Bottom, Colours.Text, Math.Max(1, painter.Px(OutlineUnits) / 2));
        }

        canvas.Restore();
    }

    /// <summary>A scroll area's bar along its right side, while its content is taller than it.</summary>
    public virtual void DrawScrollBar(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        var inner = element.Inner;
        // Half a pixel's slack: a scroll area as tall as its content may differ from it by the layout's rounding.
        if (element.Content <= inner.Height + 0.5f || inner.Height <= 0)
        {
            return;
        }

        var width = painter.Px(0.35f * BodyUnits);
        var track = new SKRect(inner.Right - width, inner.Top, inner.Right, inner.Bottom);
        var thumbHeight = Math.Max(width, inner.Height * inner.Height / element.Content);
        var top = inner.Top + (inner.Height - thumbHeight) * element.Scroll / (element.Content - inner.Height);
        painter.Fill(track, Colours.Field, width / 2);
        painter.Fill(new SKRect(track.Left, top, track.Right, top + thumbHeight), Colours.TextDim, width / 2);
    }

    /// <summary>The keyboard's focus: an outline around the element, in the text's colour.</summary>
    public virtual void DrawFocus(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        var width = painter.Px(OutlineUnits);
        var rect = element.Rect;
        rect.Inflate(width * 1.5f, width * 1.5f);
        painter.Outline(rect, Colours.Text, width, painter.Px(CornerUnits) + width);
    }

    /// <summary>A button's or switch's face and outline by its state.</summary>
    private void Face(UiPainter painter, in UiElement element)
    {
        var corner = painter.Px(CornerUnits);
        var face =
            element.Disabled ? Colours.ButtonDisabled
            : element.Hovered ? Colours.ButtonHover
            : element.Selected ? Colours.Field
            : Colours.Button;
        painter.Fill(element.Rect, face, corner);
        painter.Outline(element.Rect, Colours.Ink, painter.Px(OutlineUnits), corner);
    }

    private SKColor LabelColour(in UiElement element) =>
        element.Disabled ? Colours.TextDim
        : element.Hovered ? Colours.ButtonTextHover
        : element.Selected ? Colours.Text
        : Colours.ButtonText;
}
