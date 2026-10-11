using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// What an element of a <see cref="Ui"/> is doing, given back to the code that declared it: where it was last laid out
/// (screen pixels; empty before its first layout), whether the pointer is over it or pressing it, whether it was clicked
/// (released over the element it was pressed on) or a key pressed it, whether it has the keyboard's focus or is being
/// dragged, and a slider's new value (<see cref="Changed"/>). Input is matched against the last layout, so a state
/// describes the frame's input over what was last drawn.
/// </summary>
/// <param name="Id">The element's id.</param>
/// <param name="Rect">Where the element was last laid out, in screen pixels.</param>
/// <param name="Hovered">The pointer is over it.</param>
/// <param name="Pressed">The pointer's button went down on it and is held.</param>
/// <param name="Clicked">Released over it after going down on it, or pressed by a key while it has the focus.</param>
/// <param name="Focused">It has the keyboard's focus.</param>
/// <param name="Dragging">The pointer's button is held after going down on it (also off it).</param>
/// <param name="Changed">A slider's value was moved (<see cref="Value"/>).</param>
/// <param name="Value">A slider's value, moved by a drag or a key when <see cref="Changed"/>; else the value given.</param>
/// <param name="Typing">A text box has the typing (<see cref="Ui.Typing"/>).</param>
public readonly record struct UiState(
    int Id,
    SKRect Rect,
    bool Hovered = false,
    bool Pressed = false,
    bool Clicked = false,
    bool Focused = false,
    bool Dragging = false,
    bool Changed = false,
    float Value = 0,
    bool Typing = false
);
