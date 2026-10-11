namespace Itera.Engine.Input;

/// <summary>
/// One of a game's screens that Escape closes before the menu opens (an inventory, a map, a dialogue), added with
/// <see cref="IFrameInput.AddEscapeHandler"/>. Escape goes, in order, to the console when open, the menu when open, a
/// game's text box (<see cref="IFrameInput.SetTextFocus"/>), these handlers from the newest added, a game's panel
/// (<see cref="IPanelInput"/>), and otherwise opens the menu. A handler takes nothing else: the pointer, the keys, and the
/// clicks stay as they were. Main thread.
/// </summary>
public interface IEscapeHandler
{
    /// <summary>
    /// Escape went down while the game's input is not blocked: true takes it (the screen closes, and nothing after this
    /// handler sees it), false passes it on. Called from inside the window's key event: keep it short, do not throw, and
    /// change no ECS world there (note it and act at the next update, as a panel does). A handler may remove itself or
    /// others as it closes.
    /// </summary>
    bool Escape();
}
