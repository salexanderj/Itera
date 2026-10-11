namespace Itera.Engine.Input;

/// <summary>
/// A game's panel that takes the free pointer while it shows (such as a map screen), set with
/// <see cref="IFrameInput.SetPanel"/>: the pointer is freed as it opens, its left and right clicks and the wheel go to
/// <see cref="IFrameInput.MenuPointer"/> and never capture the pointer, other buttons reach the game, and Escape goes to the
/// panel rather than the menu. Keys still reach the game, so walking goes on. Clearing the panel captures the pointer
/// again when it was captured as the panel opened. The menu and the console, when open, come first. Main thread.
/// </summary>
public interface IPanelInput
{
    /// <summary>
    /// Escape went down while the panel is set. Called from inside an input event: the panel notes it and is cleared at
    /// its owner's next update, not from here.
    /// </summary>
    void Escape();
}
