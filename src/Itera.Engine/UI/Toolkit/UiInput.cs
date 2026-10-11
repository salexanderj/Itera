using System.Numerics;
using Itera.Engine.Input;

namespace Itera.Engine.UI.Toolkit;

/// <summary>The navigation keys pressed in a frame, for a <see cref="Ui"/>'s keyboard focus.</summary>
[Flags]
public enum UiKeys
{
    None = 0,

    /// <summary>Tab: the next element in the order written.</summary>
    Next = 1,

    /// <summary>Shift+Tab: the element before.</summary>
    Previous = 2,

    /// <summary>The left arrow: the nearest element to the left, or a focused slider down a step.</summary>
    Left = 4,

    /// <summary>The right arrow: the nearest element to the right, or a focused slider up a step.</summary>
    Right = 8,

    /// <summary>The up arrow: the nearest element above.</summary>
    Up = 16,

    /// <summary>The down arrow: the nearest element below.</summary>
    Down = 32,

    /// <summary>Enter or Space: presses the focused element.</summary>
    Press = 64,
}

/// <summary>Marks an element of a <see cref="Ui"/> is declared with: disabled, or the one chosen.</summary>
[Flags]
public enum UiMarks
{
    None = 0,

    /// <summary>It does nothing now: no hover, press, click, or focus, and the style draws it dimmed.</summary>
    Disabled = 1,

    /// <summary>It is the one chosen (a tab shown, a slot picked): the style draws it so.</summary>
    Selected = 2,

    /// <summary>It takes no pointer and no focus: an area or a row that only shows something, inside one that takes them.</summary>
    Passive = 4,
}

/// <summary>
/// A frame's input for a <see cref="Ui"/>: where the pointer is (screen pixels) and whether it is there to point
/// (<see cref="PointerShown"/>: false while the pointer is captured for look), the left button held, gone down, and come
/// up since the last frame, the wheel's steps (positive away from the player), the navigation keys pressed, and the
/// time in seconds (for tweens).
/// </summary>
/// <param name="At">Where the pointer is, in screen pixels from the top left.</param>
/// <param name="PointerShown">The pointer points at the UI.</param>
/// <param name="Down">The left button is held.</param>
/// <param name="Pressed">The left button went down since the last frame.</param>
/// <param name="Released">The left button came up since the last frame.</param>
/// <param name="Wheel">The wheel's steps since the last frame, positive away from the player.</param>
/// <param name="Keys">The navigation keys pressed since the last frame.</param>
/// <param name="Seconds">The time, in seconds, for tweens.</param>
/// <param name="RightPressed">The right button went down since the last frame.</param>
/// <param name="RightReleased">The right button came up since the last frame.</param>
public readonly record struct UiInput(
    Vector2 At = default,
    bool PointerShown = false,
    bool Down = false,
    bool Pressed = false,
    bool Released = false,
    float Wheel = 0,
    UiKeys Keys = UiKeys.None,
    double Seconds = 0,
    bool RightPressed = false,
    bool RightReleased = false
)
{
    /// <summary>The input from the engine's pointer as a menu or a panel reads it, with <paramref name="keys"/> and the time.</summary>
    public static UiInput From(in UiPointer source, bool shown, UiKeys keys, double seconds) =>
        new(
            source.Position,
            shown,
            source.Down,
            source.Pressed,
            source.Released,
            source.Scroll,
            keys,
            seconds,
            source.RightPressed,
            source.RightReleased
        );
}
