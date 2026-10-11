namespace Itera.Engine.UI.Toolkit;

/// <summary>What a text is for, which the style turns into a size and weight (<see cref="UiStyle.TextUnits"/>).</summary>
public enum UiTextRole
{
    /// <summary>Ordinary text; <c>Engine:Ui:FontPixels</c> units in the engine's style.</summary>
    Body,

    /// <summary>A page's title.</summary>
    Title,

    /// <summary>A section's heading, or a control's label.</summary>
    Heading,

    /// <summary>Lesser text: hints, counts.</summary>
    Small,
}

/// <summary>Which of the style's colours a text takes (<see cref="UiStyle"/>).</summary>
public enum UiTone
{
    /// <summary>The ordinary text colour.</summary>
    Normal,

    /// <summary>Lesser text.</summary>
    Dim,

    /// <summary>Text that stands out.</summary>
    Strong,

    /// <summary>Something went wrong, or needs care.</summary>
    Warning,
}

/// <summary>
/// How a text is set: its role (the style's size and weight for it) or a size of its own in units, its tone, whether it
/// wraps at spaces within its width, at most how many lines it keeps (the last cut with an ellipsis), and where its lines
/// sit across its rectangle. A text that neither wraps nor fits is cut with an ellipsis.
/// </summary>
public readonly record struct UiText
{
    /// <summary>What the text is for.</summary>
    public UiTextRole Role { get; init; }

    /// <summary>Its size in units; 0 for the role's.</summary>
    public float Units { get; init; }

    /// <summary>Its colour, by the style's tones.</summary>
    public UiTone Tone { get; init; }

    /// <summary>Wraps at spaces (a word longer than the width breaks where it must) within the width it is given.</summary>
    public bool Wrap { get; init; }

    /// <summary>The most lines kept; 0 for no limit. The last kept line ends with an ellipsis where text was cut.</summary>
    public int MostLines { get; init; }

    /// <summary>Where each line sits across the text's rectangle.</summary>
    public UiAlign Align { get; init; }

    /// <summary>Ordinary text.</summary>
    public static UiText Body => default;

    /// <summary>A page's title.</summary>
    public static UiText Title => new() { Role = UiTextRole.Title };

    /// <summary>A section's heading.</summary>
    public static UiText Heading => new() { Role = UiTextRole.Heading };

    /// <summary>Lesser text in the dim tone.</summary>
    public static UiText Small => new() { Role = UiTextRole.Small, Tone = UiTone.Dim };

    /// <summary>Text that wraps within its width.</summary>
    public static UiText Wrapped => new() { Wrap = true };
}
