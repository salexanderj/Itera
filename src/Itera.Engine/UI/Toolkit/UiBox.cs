namespace Itera.Engine.UI.Toolkit;

/// <summary>Where along an axis children sit in the room they have, or an element's text in its rectangle.</summary>
public enum UiAlign
{
    /// <summary>The left or top.</summary>
    Start,

    /// <summary>The middle.</summary>
    Centre,

    /// <summary>The right or bottom.</summary>
    End,
}

/// <summary>Space inside an element's edges, in units: left, top, right, bottom.</summary>
public readonly record struct UiEdges(float Left, float Top, float Right, float Bottom)
{
    /// <summary>The same on every side.</summary>
    public UiEdges(float all)
        : this(all, all, all, all) { }

    /// <summary><paramref name="across"/> on the left and right, <paramref name="down"/> on the top and bottom.</summary>
    public UiEdges(float across, float down)
        : this(across, down, across, down) { }

    /// <summary>Left and right together.</summary>
    public float Across => Left + Right;

    /// <summary>Top and bottom together.</summary>
    public float Down => Top + Bottom;
}

/// <summary>One of nine points of a rectangle: its corners, the middles of its sides, and its centre.</summary>
public enum UiPoint
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Centre,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

/// <summary>
/// An element taken out of its parent's flow and placed over the others (a tooltip, a popup, a corner readout): its
/// <see cref="Point"/> at <see cref="At"/> of the screen, or of the element <see cref="Element"/> laid out before it, moved
/// by <see cref="OffsetX"/> and <see cref="OffsetY"/> units. It does not move its siblings, is drawn after the rest, and is hit first.
/// </summary>
/// <param name="At">The point of the screen or of the element it is placed against.</param>
/// <param name="Point">The point of this element placed there.</param>
/// <param name="Element">The id of the element it is placed against; 0 for the screen.</param>
/// <param name="OffsetX">Moved right by this many units.</param>
/// <param name="OffsetY">Moved down by this many units.</param>
public readonly record struct UiAnchor(UiPoint At, UiPoint Point, int Element = 0, float OffsetX = 0, float OffsetY = 0)
{
    /// <summary>Against the screen: this element's <paramref name="point"/> at the screen's same point (a corner, a side, the centre).</summary>
    public static UiAnchor Screen(UiPoint point, float offsetX = 0, float offsetY = 0) =>
        new(point, point, 0, offsetX, offsetY);

    /// <summary>Against element <paramref name="element"/>: this element's <paramref name="point"/> at its <paramref name="at"/>.</summary>
    public static UiAnchor Beside(int element, UiPoint at, UiPoint point, float offsetX = 0, float offsetY = 0) =>
        new(at, point, element, offsetX, offsetY);
}

/// <summary>
/// How an element is sized and how a container places its children: its width and height, the padding inside its
/// edges, the gap between children, where children sit along each axis, whether it is anchored over the rest, and whether
/// the style draws a panel behind it. Every part left at its default is the style's choice for the kind
/// (<see cref="UiStyle.DefaultBox"/>). Distances are units (<see cref="UiSize"/>).
/// </summary>
public readonly record struct UiBox
{
    /// <summary>The width.</summary>
    public UiSize Width { get; init; }

    /// <summary>The height.</summary>
    public UiSize Height { get; init; }

    /// <summary>Space inside the edges; null for the style's.</summary>
    public UiEdges? Padding { get; init; }

    /// <summary>Space between children; null for the style's.</summary>
    public float? Gap { get; init; }

    /// <summary>Where children sit across: along a row, or across a column, a stack, or a grid's cells.</summary>
    public UiAlign AlignX { get; init; }

    /// <summary>Where children sit down: along a column, or across a row, a stack, or a grid's cells.</summary>
    public UiAlign AlignY { get; init; }

    /// <summary>Placed over the rest, anchored to the screen or an element, instead of in its parent's flow; null in the flow.</summary>
    public UiAnchor? Anchor { get; init; }

    /// <summary>The style draws a panel behind it.</summary>
    public bool Panel { get; init; }

    /// <summary>Both sizes at once.</summary>
    public static UiBox Sized(UiSize width, UiSize height) => new() { Width = width, Height = height };

    /// <summary>Both sizes fixed, in units.</summary>
    public static UiBox Fixed(float width, float height) => Sized(UiSize.Fixed(width), UiSize.Fixed(height));

    /// <summary>Growing into all the room both ways.</summary>
    public static UiBox Fill => Sized(UiSize.Grow(), UiSize.Grow());

    /// <summary>
    /// This box with every part left at its default taken from <paramref name="fallback"/> (the style's for the kind).
    /// </summary>
    public UiBox Over(in UiBox fallback) =>
        this with
        {
            Width = Width.Kind == UiSizeKind.Auto ? Limits(fallback.Width, Width) : Width,
            Height = Height.Kind == UiSizeKind.Auto ? Limits(fallback.Height, Height) : Height,
            Padding = Padding ?? fallback.Padding,
            Gap = Gap ?? fallback.Gap,
            Panel = Panel || fallback.Panel,
        };

    /// <summary><paramref name="size"/>, with the least and most the code set on an automatic size kept.</summary>
    private static UiSize Limits(UiSize size, UiSize own) =>
        own.Least > 0 || own.Most > 0 ? size with { Least = own.Least, Most = own.Most } : size;
}
