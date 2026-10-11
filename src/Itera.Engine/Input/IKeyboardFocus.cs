using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>
/// Something that takes the keyboard from the game while it is open: the in-game console. <see cref="InputService"/>
/// opens it on <see cref="Key.GraveAccent"/> and then sends it every key and character (<see cref="ITextInput"/>), and
/// the wheel, instead of the game; ` or Escape closes it. Main thread.
/// </summary>
public interface IKeyboardFocus : ITextInput
{
    /// <summary>False: ` does nothing.</summary>
    bool Enabled { get; }

    /// <summary>True while it takes the keyboard.</summary>
    bool IsOpen { get; }

    /// <summary>Opens it; the game stops seeing keys until it closes.</summary>
    void Open();

    /// <summary>Closes it; the game sees keys again.</summary>
    void Close();

    /// <summary>The wheel turned by <paramref name="steps"/> while open; positive away from the user.</summary>
    void Scroll(float steps);

    /// <summary>The window lost focus: forget held keys.</summary>
    void ReleaseKeys();
}
