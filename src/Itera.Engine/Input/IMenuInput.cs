namespace Itera.Engine.Input;

/// <summary>
/// The menu, as <see cref="InputService"/> sees it: while it is open it takes the pointer (no capture, left clicks and
/// the wheel go to it as <see cref="UiPointer"/>, read through <see cref="IFrameInput.MenuPointer"/>) and the game gets no input; a text box in it takes the keyboard; while
/// it waits for a key to bind (<see cref="CapturingBinding"/>) the next key or mouse button goes to it; and Escape goes
/// to it at once, also with the pointer captured (which it frees), and never quits. Main thread.
/// </summary>
public interface IMenuInput
{
    /// <summary>True while a menu screen shows.</summary>
    bool IsOpen { get; }

    /// <summary>The text box typed into, or null.</summary>
    ITextInput? TextFocus { get; }

    /// <summary>Escape went down with the console closed: back, resume, or open the in-game menu.</summary>
    void Escape();

    /// <summary>True while the menu waits for a key or mouse button to bind to an action.</summary>
    bool CapturingBinding { get; }

    /// <summary>
    /// The key or mouse button pressed while <see cref="CapturingBinding"/>; <see cref="InputBinding.None"/> stops waiting
    /// (the console opened). Escape goes to <see cref="Escape"/>, which stops waiting too.
    /// </summary>
    void BindingPressed(InputBinding binding);
}

/// <summary>
/// The pointer as the menu reads it in a frame: where it is in the frame's pixels (the framebuffer's, which the UI is
/// drawn in), whether the left button is down, went down, or came up since the last frame, and the wheel's steps.
/// </summary>
/// <param name="Position">Where the pointer is, in framebuffer pixels from the top left.</param>
/// <param name="Down">The left button is held.</param>
/// <param name="Pressed">The left button went down since the last frame.</param>
/// <param name="Released">The left button came up since the last frame.</param>
/// <param name="Scroll">Wheel steps since the last frame, positive away from the user.</param>
public readonly record struct UiPointer(
    System.Numerics.Vector2 Position,
    bool Down,
    bool Pressed,
    bool Released,
    float Scroll
);
