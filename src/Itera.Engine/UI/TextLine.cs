using System.Text;
using Silk.NET.Input;

namespace Itera.Engine.UI;

/// <summary>
/// One line of text being typed, with its cursor: characters inserted at the cursor, a pasted line, and the editing
/// keys (Backspace, Delete, Left and Right, by word with Ctrl, Home, End, Ctrl+U to empty it). The console's line and
/// the text boxes edit text with it (<see cref="TextEntry"/>). A character past U+FFFF is two of the line's UTF-16
/// characters, kept whole: inserted only where both fit, and the cursor, Backspace, and Delete step over both. They step
/// by character (code point), not by what shows as one: a flag or a family emoji, several characters, takes several
/// Backspaces.
/// <see cref="Version"/> rises whenever the text or the cursor changes, so what draws it draws again only then. Main
/// thread.
/// </summary>
public sealed class TextLine
{
    private readonly StringBuilder _text = new();

    /// <param name="maxLength">The longest the line may grow; characters past it are not taken.</param>
    public TextLine(int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        MaxLength = maxLength;
    }

    /// <summary>The longest the line may grow.</summary>
    public int MaxLength { get; }

    /// <summary>The text (allocates).</summary>
    public string Text => _text.ToString();

    /// <summary>Characters in the line.</summary>
    public int Length => _text.Length;

    /// <summary>Where the cursor is, from 0 (before the first character) to <see cref="Length"/>.</summary>
    public int Cursor { get; private set; }

    /// <summary>Rises whenever the text or the cursor changes.</summary>
    public int Version { get; private set; }

    /// <summary>Character <paramref name="index"/>.</summary>
    public char this[int index] => _text[index];

    /// <summary>
    /// Inserts <paramref name="character"/> at the cursor; false for a control character, half of a pair (use
    /// <see cref="Insert(Rune)"/>), or a full line.
    /// </summary>
    public bool Insert(char character) => !char.IsSurrogate(character) && Insert(new Rune(character));

    /// <summary>Inserts <paramref name="character"/> at the cursor; false for a control character or no room for it.</summary>
    public bool Insert(Rune character)
    {
        if (Rune.IsControl(character) || !Fits(character))
        {
            return false;
        }

        Span<char> units = stackalloc char[2];
        var length = character.EncodeToUtf16(units);
        _text.Insert(Cursor, units[..length]);
        Cursor += length;
        Version++;
        return true;
    }

    /// <summary>
    /// Inserts the first line of <paramref name="text"/> at the cursor, as far as it fits; control characters are left
    /// out, and a broken pair becomes U+FFFD.
    /// </summary>
    public void Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var firstLine = text.AsSpan();
        var end = firstLine.IndexOfAny('\r', '\n');
        if (end >= 0)
        {
            firstLine = firstLine[..end];
        }

        foreach (var character in firstLine.EnumerateRunes())
        {
            if (!Rune.IsControl(character) && !Insert(character))
            {
                return;
            }
        }
    }

    /// <summary>True when the line has room for <paramref name="character"/>'s one or two UTF-16 characters.</summary>
    private bool Fits(Rune character) => _text.Length + character.Utf16SequenceLength <= MaxLength;

    /// <summary>
    /// Does what an editing key does. Returns false for a key that is not one (the caller's own keys: Enter, Tab, the
    /// arrows up and down); <paramref name="repeats"/> is true for a key that repeats while held (Backspace, Delete,
    /// Left, Right).
    /// </summary>
    public bool Edit(Key key, bool control, out bool repeats)
    {
        repeats = false;
        switch (key)
        {
            case Key.Backspace:
                if (Cursor > 0)
                {
                    var before = LengthBefore(Cursor);
                    _text.Remove(Cursor - before, before);
                    Cursor -= before;
                    Version++;
                }

                repeats = true;
                return true;
            case Key.Delete:
                if (Cursor < _text.Length)
                {
                    _text.Remove(Cursor, LengthAt(Cursor));
                    Version++;
                }

                repeats = true;
                return true;
            case Key.Left:
                MoveCursor(control ? WordStart() : Cursor - LengthBefore(Cursor));
                repeats = true;
                return true;
            case Key.Right:
                MoveCursor(control ? WordEnd() : Cursor + LengthAt(Cursor));
                repeats = true;
                return true;
            case Key.Home:
                MoveCursor(0);
                return true;
            case Key.End:
                MoveCursor(_text.Length);
                return true;
            case Key.U when control:
                Set("");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Replaces the text (cut to <see cref="MaxLength"/>, never inside a pair), with the cursor at
    /// <paramref name="cursor"/> or the end.
    /// </summary>
    public void Set(string text, int cursor = -1)
    {
        ArgumentNullException.ThrowIfNull(text);
        var length = text.Length;
        if (length > MaxLength)
        {
            length =
                char.IsLowSurrogate(text[MaxLength]) && char.IsHighSurrogate(text[MaxLength - 1])
                    ? MaxLength - 1
                    : MaxLength;
        }

        _text.Clear();
        _text.Append(text.AsSpan(0, length));
        Cursor = OffPair(cursor < 0 ? _text.Length : Math.Min(cursor, _text.Length));
        Version++;
    }

    /// <summary>Moves the cursor, within the line; a place inside a pair moves to the pair's start.</summary>
    public void MoveCursor(int cursor)
    {
        var moved = OffPair(Math.Clamp(cursor, 0, _text.Length));
        if (moved != Cursor)
        {
            Cursor = moved;
            Version++;
        }
    }

    /// <summary>The UTF-16 characters of the character just before <paramref name="at"/>: 2 for a pair, else 1.</summary>
    private int LengthBefore(int at) => at >= 2 && char.IsSurrogatePair(_text[at - 2], _text[at - 1]) ? 2 : 1;

    /// <summary>The UTF-16 characters of the character at <paramref name="at"/>: 2 for a pair, else 1.</summary>
    private int LengthAt(int at) => at + 1 < _text.Length && char.IsSurrogatePair(_text[at], _text[at + 1]) ? 2 : 1;

    /// <summary><paramref name="at"/>, or the start of the pair it lies inside.</summary>
    private int OffPair(int at) =>
        at > 0 && at < _text.Length && char.IsSurrogatePair(_text[at - 1], _text[at]) ? at - 1 : at;

    private int WordStart()
    {
        var i = Cursor;
        while (i > 0 && _text[i - 1] == ' ')
        {
            i--;
        }

        while (i > 0 && _text[i - 1] != ' ')
        {
            i--;
        }

        return i;
    }

    private int WordEnd()
    {
        var i = Cursor;
        while (i < _text.Length && _text[i] == ' ')
        {
            i++;
        }

        while (i < _text.Length && _text[i] != ' ')
        {
            i++;
        }

        return i;
    }
}
