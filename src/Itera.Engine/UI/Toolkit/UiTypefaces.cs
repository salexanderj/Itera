using System.Text;
using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// The UI's typefaces: its own (<see cref="UiFonts.Mono"/>), and for a character it lacks, the system's choice for that
/// character (<see cref="SKFontManager.MatchCharacter(int)"/>, found once a character and kept); fonts made once per
/// typeface, size, and weight. Text is measured and drawn in runs of one typeface. Main thread.
/// </summary>
public sealed class UiTypefaces : IDisposable
{
    /// <summary>The most fonts kept; past it they are all freed and made again as asked for.</summary>
    private const int MostFonts = 64;

    private readonly Func<SKTypeface> _primary;
    private readonly Dictionary<int, SKTypeface?> _fallbacks = [];
    private readonly Dictionary<(SKTypeface Face, float Pixels, bool Bold), SKFont> _fonts = [];
    private bool _disposed;

    /// <param name="primary">The UI's own typeface, asked for at first use.</param>
    public UiTypefaces(Func<SKTypeface> primary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        _primary = primary;
    }

    /// <summary>The UI's own typeface.</summary>
    public SKTypeface Primary => _primary();

    /// <summary>The typeface that draws <paramref name="character"/>: the UI's own when it has it, else the system's choice, else the UI's own (its box).</summary>
    public SKTypeface For(Rune character)
    {
        var primary = Primary;
        if (character.Value < 0x80)
        {
            return primary;
        }

        if (!_fallbacks.TryGetValue(character.Value, out var face))
        {
            // Found once a character: the UI's own typeface (null here) when it has the glyph, else the system's choice.
            face = Font(primary, 12, bold: false).ContainsGlyph(character.Value)
                ? null
                : SKFontManager.Default.MatchCharacter(character.Value);
            _fallbacks[character.Value] = face;
        }

        return face ?? primary;
    }

    /// <summary>The font of <paramref name="face"/> at <paramref name="pixels"/>, bold drawn heavier; made once.</summary>
    public SKFont Font(SKTypeface face, float pixels, bool bold)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_fonts.TryGetValue((face, pixels, bold), out var font))
        {
            if (_fonts.Count >= MostFonts)
            {
                // Sizes come and go with the screen's: the old fonts go, made again when asked for.
                foreach (var old in _fonts.Values)
                {
                    old.Dispose();
                }

                _fonts.Clear();
            }

            font = new SKFont(face, pixels) { Embolden = bold, Subpixel = true };
            _fonts[(face, pixels, bold)] = font;
        }

        return font;
    }

    /// <summary>
    /// The end of the run of characters drawn with one typeface that starts at <paramref name="start"/>, and that
    /// typeface.
    /// </summary>
    public int Run(ReadOnlySpan<char> text, int start, out SKTypeface face)
    {
        Rune.DecodeFromUtf16(text[start..], out var first, out var length);
        face = For(first);
        var end = start + Math.Max(1, length);
        while (end < text.Length)
        {
            Rune.DecodeFromUtf16(text[end..], out var next, out length);
            if (!ReferenceEquals(For(next), face))
            {
                break;
            }

            end += Math.Max(1, length);
        }

        return end;
    }

    /// <summary>The width of <paramref name="text"/> at <paramref name="pixels"/>, run by run.</summary>
    public float Width(ReadOnlySpan<char> text, float pixels, bool bold)
    {
        var width = 0f;
        var start = 0;
        while (start < text.Length)
        {
            var end = Run(text, start, out var face);
            width += Font(face, pixels, bold).MeasureText(text[start..end]);
            start = end;
        }

        return width;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var font in _fonts.Values)
        {
            font.Dispose();
        }

        _fonts.Clear();
        foreach (var face in _fallbacks.Values)
        {
            face?.Dispose();
        }

        _fallbacks.Clear();
    }
}

/// <summary>Text measured with the UI's typefaces (<see cref="UiTypefaces"/>), a line as tall as the font's spacing.</summary>
public sealed class UiTextMeasurer(UiTypefaces typefaces) : IUiTextMeasurer
{
    public float Width(ReadOnlySpan<char> text, float pixels) => typefaces.Width(text, pixels, bold: false);

    public float LineHeight(float pixels) => typefaces.Font(typefaces.Primary, pixels, bold: false).Spacing;
}
