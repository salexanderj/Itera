using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>
/// Something that takes the keyboard from the game while it is open: the in-game console. <see cref="InputService"/>
/// opens it on <see cref="Key.GraveAccent"/> and then sends it every key and character, and the wheel, instead of the
/// game; ` or Escape closes it. Main thread.
/// </summary>
public interface IKeyboardFocus
{
    /// <summary>False: ` does nothing.</summary>
    bool Enabled { get; }

    /// <summary>True while it takes the keyboard.</summary>
    bool IsOpen { get; }

    /// <summary>Opens it; the game stops seeing keys until it closes.</summary>
    void Open();

    /// <summary>Closes it; the game sees keys again.</summary>
    void Close();

    /// <summary>A key went down while open. <paramref name="control"/> is true while Ctrl is held.</summary>
    void KeyDown(Key key, bool control);

    /// <summary>A key went up while open.</summary>
    void KeyUp(Key key);

    /// <summary>A character was typed while open (repeats included).</summary>
    void Character(char character);

    /// <summary>Text pasted with Ctrl+V while open.</summary>
    void Paste(string text);

    /// <summary>The wheel turned by <paramref name="steps"/> while open; positive away from the user.</summary>
    void Scroll(float steps);

    /// <summary>The window lost focus: forget held keys.</summary>
    void ReleaseKeys();
}
