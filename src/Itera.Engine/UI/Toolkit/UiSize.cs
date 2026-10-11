namespace Itera.Engine.UI.Toolkit;

/// <summary>How an element's width or height is chosen (<see cref="UiSize"/>).</summary>
public enum UiSizeKind
{
    /// <summary>The style's choice for the element's kind (<see cref="UiStyle.DefaultBox"/>); fit to content when it has none.</summary>
    Auto,

    /// <summary>As large as its content and padding.</summary>
    Fit,

    /// <summary>A fixed size in units.</summary>
    Fixed,

    /// <summary>A share of the room its parent has left along the parent's direction, by weight; across it, all of it.</summary>
    Grow,

    /// <summary>A fraction of its parent's inner size.</summary>
    Share,
}

/// <summary>
/// An element's width or height: fit to content, fixed in units, grown into the room left, or a share of its parent,
/// kept within <see cref="Least"/> and <see cref="Most"/> (units). Units are pixels of a screen 1080 pixels high, scaled
/// with the screen's height and the UI scale (<see cref="Ui.UnitPixelsFor"/>).
/// </summary>
/// <param name="Kind">How the size is chosen.</param>
/// <param name="Value">Units for <see cref="UiSizeKind.Fixed"/>, the weight for <see cref="UiSizeKind.Grow"/>, the fraction for <see cref="UiSizeKind.Share"/>.</param>
/// <param name="Least">The least it may be, in units.</param>
/// <param name="Most">The most it may be, in units; 0 for no limit.</param>
public readonly record struct UiSize(UiSizeKind Kind, float Value = 0, float Least = 0, float Most = 0)
{
    /// <summary>The style's choice for the element's kind.</summary>
    public static UiSize Auto => default;

    /// <summary>As large as the content and padding.</summary>
    public static UiSize Fit => new(UiSizeKind.Fit);

    /// <summary><paramref name="units"/> exactly.</summary>
    public static UiSize Fixed(float units) => new(UiSizeKind.Fixed, units);

    /// <summary>The room left, shared with the other growing elements by <paramref name="weight"/>.</summary>
    public static UiSize Grow(float weight = 1) => new(UiSizeKind.Grow, weight);

    /// <summary><paramref name="fraction"/> of the parent's inner size (0.5: half).</summary>
    public static UiSize Share(float fraction) => new(UiSizeKind.Share, fraction);

    /// <summary>This size, kept at least <paramref name="least"/> and at most <paramref name="most"/> units (0: no limit).</summary>
    public UiSize Within(float least, float most = 0) => this with { Least = least, Most = most };
}
