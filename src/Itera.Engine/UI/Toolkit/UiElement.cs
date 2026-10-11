using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// One element of a <see cref="Ui"/> as last laid out, for drawing it (<see cref="UiStyle"/>, a game's own drawing of
/// an area) and for tests and tools: its kind and id, its rectangle and the area it may be seen in (screen pixels), its
/// text and how it was set and broken into lines (<see cref="Ui.Line"/>), and a switch's or slider's value.
/// </summary>
/// <param name="Index">Its place in the order declared.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Id">Its id; 0 for none.</param>
/// <param name="Rect">Where it was laid out.</param>
/// <param name="Clip">The area its scroll areas leave it to be seen in.</param>
/// <param name="Text">A text's text, a button's label, or a text box's placeholder.</param>
/// <param name="Entry">A text box's typing.</param>
/// <param name="Setting">How its text is set.</param>
/// <param name="TextPixels">Its text's size in pixels.</param>
/// <param name="LineHeight">One line's height in pixels.</param>
/// <param name="FirstLine">Its first line (<see cref="Ui.Line"/>).</param>
/// <param name="LineCount">How many lines its text was broken into.</param>
/// <param name="On">A switch's value.</param>
/// <param name="Value">A slider's value.</param>
/// <param name="Least">A slider's least value.</param>
/// <param name="Most">A slider's most value.</param>
/// <param name="Panel">The style draws a panel behind it.</param>
/// <param name="Depth">How many containers it is inside.</param>
/// <param name="Floating">Placed over the rest (<see cref="UiBox.Anchor"/>), or inside such an element.</param>
/// <param name="Marks">Disabled or selected, as declared.</param>
/// <param name="Hovered">The pointer is over it.</param>
/// <param name="Pressed">The pointer's button went down on it and is held over it.</param>
/// <param name="Focused">It has the keyboard's focus, and a key moved it there (<see cref="Ui.FocusShown"/>).</param>
/// <param name="Typing">A text box that has the typing.</param>
/// <param name="Padding">Its padding in pixels: its text sits inside it.</param>
/// <param name="Scroll">A scroll area's offset, in pixels.</param>
/// <param name="Content">A scroll area's content height, in pixels (more than its own when it scrolls).</param>
/// <param name="Version">An area's version, as declared (<see cref="Ui.Area"/>).</param>
/// <param name="End">The index after the last element inside it: those between are its children and theirs.</param>
public readonly record struct UiElement(
    int Index,
    UiKind Kind,
    int Id,
    SKRect Rect,
    SKRect Clip,
    string? Text,
    TextEntry? Entry,
    UiText Setting,
    float TextPixels,
    float LineHeight,
    int FirstLine,
    int LineCount,
    bool On,
    float Value,
    float Least,
    float Most,
    bool Panel,
    int Depth,
    bool Floating,
    UiMarks Marks,
    bool Hovered,
    bool Pressed,
    bool Focused,
    bool Typing,
    UiEdges Padding,
    float Scroll,
    float Content,
    int Version,
    int End
)
{
    /// <summary>Declared disabled: it does nothing now.</summary>
    public bool Disabled => (Marks & UiMarks.Disabled) != 0;

    /// <summary>Declared the one chosen.</summary>
    public bool Selected => (Marks & UiMarks.Selected) != 0;

    /// <summary>The rectangle inside its padding, where its text or content sits.</summary>
    public SKRect Inner =>
        new(Rect.Left + Padding.Left, Rect.Top + Padding.Top, Rect.Right - Padding.Right, Rect.Bottom - Padding.Bottom);

    /// <summary>A slider's value as a share of its range, 0 to 1.</summary>
    public float Share => Most > Least ? (Value - Least) / (Most - Least) : 0;
}
