using System.Text;
using Itera.Engine.Input;
using Silk.NET.Input;

namespace Itera.Engine.UI;

/// <summary>
/// A text box's typing (<see cref="ITextInput"/>) over a <see cref="TextLine"/>: characters and pastes go into the line,
/// the editing keys edit it (Backspace, Delete, Left, and Right repeat while held), Enter (either) calls
/// <see cref="Enter"/>, and Escape calls <see cref="Escape"/>; the game draws <see cref="Line"/> where it likes. A game
/// gives it the typing with <see cref="IFrameInput.SetTextFocus"/>, and its <see cref="Escape"/> (or
/// <see cref="Enter"/>) usually takes it back with <see cref="IFrameInput.ClearTextFocus"/>: without one, Escape does
/// nothing while it has the typing, and only the game can end it. The callbacks run inside the window's key event:
/// keep them short, do not throw, and change no ECS world there (note it, and act at the next update). Main thread.
/// </summary>
public sealed class TextEntry : ITextInput
{
    /// <param name="maxLength">The longest the line may grow, in UTF-16 characters.</param>
    /// <param name="enter">Called on Enter; none ignores it.</param>
    /// <param name="escape">Called on Escape; none ignores it.</param>
    public TextEntry(int maxLength, Action? enter = null, Action? escape = null)
    {
        Line = new TextLine(maxLength);
        Enter = enter;
        Escape = escape;
    }

    /// <summary>The text typed, with its cursor.</summary>
    public TextLine Line { get; }

    /// <summary>Called on Enter or the keypad's Enter.</summary>
    public Action? Enter { get; set; }

    /// <summary>Called on Escape.</summary>
    public Action? Escape { get; set; }

    public bool KeyDown(Key key, bool control)
    {
        switch (key)
        {
            case Key.Enter or Key.KeypadEnter:
                Enter?.Invoke();
                return false;
            case Key.Escape:
                Escape?.Invoke();
                return false;
            default:
                Line.Edit(key, control, out var repeats);
                return repeats;
        }
    }

    public void KeyUp(Key key) { }

    public void Character(Rune character) => Line.Insert(character);

    public void Paste(string text) => Line.Paste(text);
}
