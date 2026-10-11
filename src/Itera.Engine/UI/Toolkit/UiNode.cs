using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>One element of a <see cref="Ui"/> as declared and laid out: kept in a pooled array, rebuilt each layout.</summary>
internal struct UiNode
{
    public UiKind Kind;
    public int Id;
    public int Parent;
    public int FirstChild;
    public int LastChild;
    public int Next;
    public int Children;
    public int Depth;

    /// <summary>The box, with the style's defaults filled in.</summary>
    public UiBox Box;

    /// <summary>The text's setting (texts, buttons' labels, text boxes).</summary>
    public UiText TextSetting;
    public int Columns;
    public float Value;
    public float Least;
    public float Most;
    public bool On;

    /// <summary>Laid out of its parent's flow (<see cref="UiBox.Anchor"/>), or inside such an element.</summary>
    public bool Floating;

    public float FitWidth;
    public float FitHeight;
    public float X;
    public float Y;
    public float W;
    public float H;

    /// <summary>The text's size in pixels and one line's height.</summary>
    public float TextPixels;
    public float LineHeight;
    public int FirstLine;
    public int LineCount;

    /// <summary>
    /// It may be narrower than its content: a fit text (wrapped or cut), or a fit container holding one. Along a row such
    /// children share what the rest leave.
    /// </summary>
    public bool Shrinks;

    /// <summary>Its size along its container's direction is settled in the pass sharing out the room.</summary>
    public bool Settled;

    /// <summary>Closed (<see cref="UiScope.Dispose"/>), so closing it again does nothing.</summary>
    public bool Closed;

    /// <summary>The area its scroll areas leave it to be seen in (the screen without one).</summary>
    public SKRect Clip;
}

/// <summary>One line of a text as laid out: which characters, how wide, and whether an ellipsis follows them.</summary>
/// <param name="Start">The first character, in the element's text.</param>
/// <param name="Length">How many characters.</param>
/// <param name="Width">Its width in pixels, the ellipsis included.</param>
/// <param name="Ellipsis">The text was cut here; an ellipsis is drawn after it.</param>
public readonly record struct UiLine(int Start, int Length, float Width, bool Ellipsis);
