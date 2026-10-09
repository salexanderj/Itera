using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>
/// The keyboard and mouse as one frame or one fixed step saw them: keys and buttons held when it was taken, keys and
/// buttons pressed since the previous one (even if already released), mouse movement and scroll since the previous one,
/// and whether the pointer was captured for mouse look. Frame systems read it from <see cref="InputService.Frame"/>,
/// fixed systems from <see cref="InputService.Tick"/>.
/// </summary>
public readonly struct InputSnapshot
{
    private readonly KeyBits _held;
    private readonly KeyBits _pressed;
    private readonly uint _buttonsHeld;
    private readonly uint _buttonsPressed;

    internal InputSnapshot(
        in KeyBits held,
        in KeyBits pressed,
        uint buttonsHeld,
        uint buttonsPressed,
        Vector2 mouseDelta,
        float scroll,
        bool pointerCaptured
    )
    {
        _held = held;
        _pressed = pressed;
        _buttonsHeld = buttonsHeld;
        _buttonsPressed = buttonsPressed;
        MouseDelta = mouseDelta;
        Scroll = scroll;
        PointerCaptured = pointerCaptured;
    }

    /// <summary>Mouse movement since the previous snapshot, in screen pixels; +X right, +Y down.</summary>
    public Vector2 MouseDelta { get; }

    /// <summary>Vertical scroll since the previous snapshot, in wheel steps; positive away from the user.</summary>
    public float Scroll { get; }

    /// <summary>True while the pointer is hidden and locked for mouse look (after a left click, until Escape).</summary>
    public bool PointerCaptured { get; }

    /// <summary>True when the key was held as the snapshot was taken.</summary>
    public bool IsDown(Key key) => KeyBits.Test(in _held, key);

    /// <summary>True when the key went down since the previous snapshot.</summary>
    public bool WasPressed(Key key) => KeyBits.Test(in _pressed, key);

    /// <summary>True when the button was held as the snapshot was taken.</summary>
    public bool IsDown(MouseButton button) => ButtonBit(button, _buttonsHeld);

    /// <summary>True when the button went down since the previous snapshot.</summary>
    public bool WasPressed(MouseButton button) => ButtonBit(button, _buttonsPressed);

    internal static bool ButtonBit(MouseButton button, uint buttons)
    {
        var index = (int)button;
        return (uint)index < 32 && ((buttons >> index) & 1) != 0;
    }
}

/// <summary>One bit per <see cref="Key"/> value (Silk.NET's key codes go up to 348).</summary>
[InlineArray(Words)]
internal struct KeyBits
{
    public const int Words = 8;

    private ulong _word;

    public static bool Test(in KeyBits bits, Key key)
    {
        var index = (int)key;
        return (uint)index < Words * 64 && ((bits[index >> 6] >> (index & 63)) & 1) != 0;
    }

    public void Set(Key key, bool value)
    {
        var index = (int)key;
        if ((uint)index >= Words * 64)
        {
            return;
        }

        var bit = 1UL << (index & 63);
        this[index >> 6] = value ? this[index >> 6] | bit : this[index >> 6] & ~bit;
    }
}
