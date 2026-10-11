namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// How a <see cref="Ui"/>'s elements look and how large they are when the code leaves it to the style: each kind's
/// default box (<see cref="DefaultBox"/>), each text role's size (<see cref="TextUnits"/>), and the drawing of each kind.
/// A game replaces the whole style or overrides any part of it; sizes are units (<see cref="UiSize"/>). The defaults here
/// are proportions of <see cref="BodyUnits"/>, the ordinary text's size.
/// </summary>
public class UiStyle
{
    /// <param name="bodyUnits">The ordinary text's size in units (<c>Engine:Ui:FontPixels</c> in the engine's style).</param>
    public UiStyle(float bodyUnits = 18)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bodyUnits);
        BodyUnits = bodyUnits;
    }

    /// <summary>The ordinary text's size in units.</summary>
    public float BodyUnits { get; }

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
}
