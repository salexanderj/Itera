using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>
/// Decides when the pointer is captured for mouse look: a left click in the focused window captures it, Escape releases
/// it, Escape while it is free asks to quit, and losing focus releases it. The click that captures reaches no system,
/// so it neither digs nor fires. Events only record what is wanted; <see cref="Update"/> applies it once per loop
/// iteration, outside GLFW's callbacks. Knows nothing about GLFW, so it is tested without a window. Main thread only.
/// </summary>
public sealed class PointerCapture
{
    private bool _captureWanted;
    private bool _releaseWanted;

    /// <summary>True while the pointer is captured, as of the last <see cref="Update"/>.</summary>
    public bool Captured { get; private set; }

    /// <summary>
    /// A mouse button went down. Returns false when the press only captures the pointer and must not reach the game.
    /// </summary>
    /// <param name="button">The button.</param>
    /// <param name="focused">Whether the window has focus.</param>
    public bool ButtonDown(MouseButton button, bool focused)
    {
        if (Captured && !_releaseWanted)
        {
            return true;
        }

        if (button == MouseButton.Left && focused)
        {
            _captureWanted = true;
            _releaseWanted = false;
            return false;
        }

        return true;
    }

    /// <summary>Escape went down. Returns true when it asks to quit, because the pointer is free.</summary>
    public bool Escape()
    {
        if ((Captured && !_releaseWanted) || _captureWanted)
        {
            _releaseWanted = true;
            _captureWanted = false;
            return false;
        }

        return true;
    }

    /// <summary>Asks to free the pointer without the Escape's meaning of quitting when it is already free.</summary>
    public void Release()
    {
        _releaseWanted = Captured || _captureWanted;
        _captureWanted = false;
    }

    /// <summary>Asks to capture the pointer, as a left click in the focused window does.</summary>
    public void Capture()
    {
        _captureWanted = true;
        _releaseWanted = false;
    }

    /// <summary>Applies what the events since the last call asked for, and returns whether the pointer is now captured.</summary>
    /// <param name="available">Whether the window has focus and a mouse exists.</param>
    public bool Update(bool available)
    {
        var captured = Captured;
        if (!available || _releaseWanted)
        {
            captured = false;
        }
        else if (_captureWanted)
        {
            captured = true;
        }

        _captureWanted = false;
        _releaseWanted = false;
        Captured = captured;
        return captured;
    }
}
