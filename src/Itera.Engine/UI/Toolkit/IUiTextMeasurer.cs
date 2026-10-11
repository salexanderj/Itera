namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// How wide text is and how tall its lines are, for the layout: the engine's from its fonts, or
/// <see cref="FixedTextMeasurer"/> in tests, so their rectangles do not depend on a machine's fonts. Main thread.
/// </summary>
public interface IUiTextMeasurer
{
    /// <summary>The width of <paramref name="text"/> in pixels at a text size of <paramref name="pixels"/>.</summary>
    float Width(ReadOnlySpan<char> text, float pixels);

    /// <summary>The height of one line in pixels at a text size of <paramref name="pixels"/>.</summary>
    float LineHeight(float pixels);
}

/// <summary>
/// Text measured by a fixed advance: every character (a pair of UTF-16 halves as one) <see cref="Advance"/> times the
/// size wide, lines <see cref="Leading"/> times the size tall. For tests: their rectangles are the same on every machine.
/// </summary>
/// <param name="Advance">A character's width as a share of the text size.</param>
/// <param name="Leading">A line's height as a share of the text size.</param>
public sealed record FixedTextMeasurer(float Advance = 0.5f, float Leading = 1.25f) : IUiTextMeasurer
{
    public float Width(ReadOnlySpan<char> text, float pixels)
    {
        var characters = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLowSurrogate(text[i]))
            {
                characters++;
            }
        }

        return characters * Advance * pixels;
    }

    public float LineHeight(float pixels) => Leading * pixels;
}
