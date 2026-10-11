using System.Numerics;

namespace Itera.Engine.Input;

/// <summary>
/// The input of the frame being built, for frame systems. <see cref="InputService"/> provides it in the game; tests
/// provide snapshots built with <see cref="InputAccumulator"/>.
/// </summary>
public interface IFrameInput
{
    /// <summary>The input collected since the previous frame.</summary>
    ref readonly InputSnapshot Frame { get; }

    /// <summary>
    /// Mouse movement since the last fixed step, in screen pixels, not yet taken by a step. A frame adds it to the look
    /// the last step set; the next step takes the same movement, so the view never jumps back.
    /// </summary>
    Vector2 MouseSinceTick { get; }

    /// <summary>
    /// The pointer as the menu reads it in this frame (<see cref="IMenuInput"/>), or the drawn pointer while it is on
    /// (<see cref="SetDrawnPointer"/>); default where there is none.
    /// </summary>
    UiPointer MenuPointer => default;

    /// <summary>True while the drawn pointer is on (<see cref="SetDrawnPointer"/>).</summary>
    bool DrawnPointerOn => false;

    /// <summary>
    /// Turns the drawn pointer on or off: a pointer the game draws (a radial menu's) while the real one stays captured.
    /// While it is on and the pointer is captured, the mouse's movement moves <see cref="MenuPointer"/>'s position,
    /// starting at <paramref name="centre"/> and kept within <paramref name="radius"/> of it (framebuffer pixels), and
    /// never turns the view; the left button goes to <see cref="MenuPointer"/>, and no button reaches the game (one held
    /// when it turns on is let go). Turning it on while it is on changes nothing; freeing the pointer turns it off.
    /// </summary>
    void SetDrawnPointer(bool shown, Vector2 centre, float radius) { }

    /// <summary>True while the game gets no input: the loading screen covers the picture, no match is played, or the menu shows.</summary>
    bool GameInputBlocked => false;

    /// <summary>
    /// Sets the panel that takes the free pointer (<see cref="IPanelInput"/>), or clears it (null). Setting one frees the
    /// pointer; clearing it captures the pointer again when it was captured as the panel was set, unless the menu or the
    /// console is open or the game's input is blocked. Setting the panel already set changes nothing.
    /// </summary>
    void SetPanel(IPanelInput? panel) { }

    /// <summary>
    /// Clears <paramref name="panel"/> when it is the panel set, as <see cref="SetPanel"/> with null does; another's is
    /// left alone.
    /// </summary>
    void ClearPanel(IPanelInput panel) { }

    /// <summary>The game's text box that takes the typing (<see cref="SetTextFocus"/>), or null.</summary>
    ITextInput? TextFocus => null;

    /// <summary>
    /// Gives the typing to a game's text box (a chat line, a save's name; <see cref="UI.TextEntry"/>), or takes it back
    /// (null). While one is set, and the console and the menu are closed and the game's input is not blocked, every key
    /// goes to it (Escape and Enter too, so the box ends the typing itself; ` too unless the console is on, which ` still
    /// opens), with the characters typed and pasted, and no key reaches the game; the keys held as it is set are let go,
    /// so a player walking stops, and a key it repeats is sent again while held. The mouse, the wheel, and the pointer
    /// stay the game's. Setting the box already set changes nothing.
    /// </summary>
    void SetTextFocus(ITextInput? text) { }

    /// <summary>
    /// Takes the typing back from <paramref name="text"/> when it is the box set, as <see cref="SetTextFocus"/> with
    /// null does; another's is left alone.
    /// </summary>
    void ClearTextFocus(ITextInput text) { }

    /// <summary>
    /// Adds a screen that Escape closes before the menu opens (<see cref="IEscapeHandler"/>); the newest added is asked
    /// first. Adding one already added changes nothing.
    /// </summary>
    void AddEscapeHandler(IEscapeHandler handler) { }

    /// <summary>Removes <paramref name="handler"/>; one not added is ignored.</summary>
    void RemoveEscapeHandler(IEscapeHandler handler) { }
}
