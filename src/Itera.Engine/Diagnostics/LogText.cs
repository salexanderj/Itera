using System.Buffers;

namespace Itera.Engine.Diagnostics;

/// <summary>Text from outside the game made safe to log.</summary>
public static class LogText
{
    /// <summary>
    /// <paramref name="text"/> with every character that could forge or hide a line of the log (control characters, line
    /// and paragraph separators, and the marks that turn the text's direction) made '?': for a string another game sent,
    /// before it is logged or shown. The same string when it has none.
    /// </summary>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var first = text.AsSpan().IndexOfAny(s_unprintable);
        if (first < 0)
        {
            return text;
        }

        return string.Create(
            text.Length,
            (text, first),
            static (span, state) =>
            {
                state.text.AsSpan().CopyTo(span);
                for (var i = state.first; i < span.Length; i++)
                {
                    if (s_unprintable.Contains(span[i]))
                    {
                        span[i] = '?';
                    }
                }
            }
        );
    }

    // C0 and C1 controls and DEL, the line and paragraph separators, and the direction marks, embeddings, overrides,
    // and isolates.
    private static readonly SearchValues<char> s_unprintable = SearchValues.Create([
        .. Enumerable.Range(0, 0x20).Select(c => (char)c),
        .. Enumerable.Range(0x7F, 0x21).Select(c => (char)c),
        '\u061C',
        '\u200E',
        '\u200F',
        '\u2028',
        '\u2029',
        .. Enumerable.Range(0x202A, 5).Select(c => (char)c),
        .. Enumerable.Range(0x2066, 4).Select(c => (char)c),
    ]);
}
