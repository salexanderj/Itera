using System.Text;
using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>
/// Where typing goes: a text box's keys and characters. <see cref="InputService"/> sends them to the console while it
/// is open (<see cref="IKeyboardFocus"/>), to the menu's text box while the menu shows one
/// (<see cref="IMenuInput.TextFocus"/>), and to a game's text box while the game has set one
/// (<see cref="IFrameInput.SetTextFocus"/>); <see cref="UI.TextEntry"/> is a ready-made one over a
/// <see cref="UI.TextLine"/>. Main thread.
/// </summary>
public interface ITextInput
{
    /// <summary>
    /// A key went down. <paramref name="control"/> is true while Ctrl (on macOS also Command) is held. Returns true for a
    /// key that repeats while held (Backspace, Delete, the arrows): <see cref="InputService"/> sends it again after
    /// <see cref="InputService.RepeatDelaySeconds"/>, then <see cref="InputService.RepeatsPerSecond"/> times a second,
    /// until it comes up or the typing goes elsewhere (Silk.NET passes on no repeats of its own). Runs inside the
    /// window's key event: keep it short, do not throw, and change no ECS world there.
    /// </summary>
    bool KeyDown(Key key, bool control);

    /// <summary>A key went up: also one that went down before this had the typing.</summary>
    void KeyUp(Key key);

    /// <summary>A character was typed (repeats included), whole: one past U+FFFF too.</summary>
    void Character(Rune character);

    /// <summary>Text pasted with Ctrl+V (on macOS also Command+V).</summary>
    void Paste(string text);
}
