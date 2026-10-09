using System.Text;
using Silk.NET.Input;

namespace Itera.Engine.UI;

/// <summary>
/// One line of text being typed, with its cursor: characters inserted at the cursor, a pasted line, and the editing
/// keys (Backspace, Delete, Left and Right, by word with Ctrl, Home, End, Ctrl+U to empty it). The console's line and
/// the menu's text boxes edit text with it. <see cref="Version"/> rises whenever the text or the cursor changes, so
/// what draws it draws again only then. Main thread.
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

    /// <summary>Inserts <paramref name="character"/> at the cursor; false for a control character or a full line.</summary>
    public bool Insert(char character)
    {
        if (char.IsControl(character) || _text.Length >= MaxLength)
        {
            return false;
        }

        _text.Insert(Cursor, character);
        Cursor++;
        Version++;
        return true;
    }

    /// <summary>Inserts the first line of <paramref name="text"/> at the cursor, as far as it fits.</summary>
    public void Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var firstLine = text.AsSpan();
        var end = firstLine.IndexOfAny('\r', '\n');
        if (end >= 0)
        {
            firstLine = firstLine[..end];
        }

        foreach (var character in firstLine)
        {
            Insert(character);
        }
    }

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
                    _text.Remove(Cursor - 1, 1);
                    Cursor--;
                    Version++;
                }

                repeats = true;
                return true;
            case Key.Delete:
                if (Cursor < _text.Length)
                {
                    _text.Remove(Cursor, 1);
                    Version++;
                }

                repeats = true;
                return true;
            case Key.Left:
                MoveCursor(control ? WordStart() : Cursor - 1);
                repeats = true;
                return true;
            case Key.Right:
                MoveCursor(control ? WordEnd() : Cursor + 1);
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

    /// <summary>Replaces the text (cut to <see cref="MaxLength"/>), with the cursor at <paramref name="cursor"/> or the end.</summary>
    public void Set(string text, int cursor = -1)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text.Clear();
        _text.Append(text.Length > MaxLength ? text[..MaxLength] : text);
        Cursor = cursor < 0 ? _text.Length : Math.Min(cursor, _text.Length);
        Version++;
    }

    /// <summary>Moves the cursor, within the line.</summary>
    public void MoveCursor(int cursor)
    {
        var moved = Math.Clamp(cursor, 0, _text.Length);
        if (moved != Cursor)
        {
            Cursor = moved;
            Version++;
        }
    }

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
