using System.Numerics;
using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>
/// Collects input events and turns them into <see cref="InputSnapshot"/>s for two readers: the frame being built
/// (<see cref="TakeFrame"/>) and the fixed step being run (<see cref="TakeTick"/>). Held keys and buttons are shared;
/// presses, mouse movement, and scroll are kept once for each reader, so each sees every event once whatever the number
/// of frames per step. It knows nothing about GLFW, so it is tested without a window, and tests of systems use it to
/// build snapshots. Main thread only; nothing allocates.
/// </summary>
public sealed class InputAccumulator
{
    private KeyBits _held;
    private uint _buttonsHeld;
    private Changes _sinceFrame;
    private Changes _sinceTick;

    /// <summary>True while <paramref name="button"/> is held.</summary>
    public bool IsDown(MouseButton button) => InputSnapshot.ButtonBit(button, _buttonsHeld);

    /// <summary>
    /// Mouse movement since the last fixed step, in screen pixels: what the next <see cref="TakeTick"/> will hand over.
    /// Frames add it to the look the last step set, so looking stays smooth between steps.
    /// </summary>
    public Vector2 MouseSinceTick => _sinceTick.MouseDelta;

    /// <summary>A key went down.</summary>
    public void KeyDown(Key key)
    {
        _held.Set(key, true);
        _sinceFrame.Pressed.Set(key, true);
        _sinceTick.Pressed.Set(key, true);
    }

    /// <summary>A key went up.</summary>
    public void KeyUp(Key key) => _held.Set(key, false);

    /// <summary>A mouse button went down.</summary>
    public void ButtonDown(MouseButton button)
    {
        var bit = ButtonMask(button);
        _buttonsHeld |= bit;
        _sinceFrame.ButtonsPressed |= bit;
        _sinceTick.ButtonsPressed |= bit;
    }

    /// <summary>A mouse button went up.</summary>
    public void ButtonUp(MouseButton button) => _buttonsHeld &= ~ButtonMask(button);

    /// <summary>The mouse moved by <paramref name="delta"/> screen pixels.</summary>
    public void AddMouseDelta(Vector2 delta)
    {
        _sinceFrame.MouseDelta += delta;
        _sinceTick.MouseDelta += delta;
    }

    /// <summary>The wheel turned by <paramref name="steps"/>; positive away from the user.</summary>
    public void AddScroll(float steps)
    {
        _sinceFrame.Scroll += steps;
        _sinceTick.Scroll += steps;
    }

    /// <summary>Forgets mouse movement not yet taken by either reader, so a view does not jump when the pointer is captured.</summary>
    public void ClearMouseDelta()
    {
        _sinceFrame.MouseDelta = Vector2.Zero;
        _sinceTick.MouseDelta = Vector2.Zero;
    }

    /// <summary>
    /// Forgets every held key and button, for when the window loses focus: their release events may go to another
    /// window. Presses already seen still count for the next frame and step.
    /// </summary>
    public void ReleaseAll()
    {
        _held = default;
        _buttonsHeld = 0;
    }

    /// <summary>
    /// Forgets every held mouse button, for when something else takes the buttons (a drawn pointer): a held attack stops.
    /// Keys stay held.
    /// </summary>
    public void ReleaseButtons() => _buttonsHeld = 0;

    /// <summary>
    /// Forgets every held key, for when something else takes the keys (a game's text box): a player walking stops. Mouse
    /// buttons stay held.
    /// </summary>
    public void ReleaseKeys() => _held = default;

    /// <summary>The snapshot for the frame being built; starts collecting the next frame's presses and movement.</summary>
    public InputSnapshot TakeFrame(bool pointerCaptured) => Take(ref _sinceFrame, pointerCaptured);

    /// <summary>The snapshot for the fixed step being run; starts collecting the next step's presses and movement.</summary>
    public InputSnapshot TakeTick(bool pointerCaptured) => Take(ref _sinceTick, pointerCaptured);

    private InputSnapshot Take(ref Changes changes, bool pointerCaptured)
    {
        var snapshot = new InputSnapshot(
            in _held,
            in changes.Pressed,
            _buttonsHeld,
            changes.ButtonsPressed,
            changes.MouseDelta,
            changes.Scroll,
            pointerCaptured
        );
        changes = default;
        return snapshot;
    }

    private static uint ButtonMask(MouseButton button)
    {
        var index = (int)button;
        return (uint)index < 32 ? 1u << index : 0;
    }

    /// <summary>What one reader has not taken yet.</summary>
    private struct Changes
    {
        public KeyBits Pressed;
        public uint ButtonsPressed;
        public Vector2 MouseDelta;
        public float Scroll;
    }
}
