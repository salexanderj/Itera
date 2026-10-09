using Itera.Engine.Input;
using Silk.NET.Input;

namespace Itera.Engine.Systems;

/// <summary>
/// The movement keybindings the engine's player and camera systems read, registered with the engine's
/// <see cref="KeyBindings"/> as this is made, in the order the Keybindings tab lists them: walking (the fly camera moves
/// by the same keys, Jump up and Sprint faster, and has its own Fly down), crouching, and the third-person camera (the
/// wheel moves it, fixed). A game makes this once a run, before its own keys (an <c>IRunStartup</c>), so its own keys
/// list after these. Main thread.
/// </summary>
public sealed class MovementKeys
{
    /// <param name="bindings">The engine's keybindings, which get the movement actions.</param>
    public MovementKeys(KeyBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        Bindings = bindings;
        Forward = bindings.Register("forward", "Forward", "Walks forward; flies forward.", InputBinding.Of(Key.W));
        Back = bindings.Register("back", "Back", "Walks back; flies back.", InputBinding.Of(Key.S));
        Left = bindings.Register("left", "Left", "Walks left; flies left.", InputBinding.Of(Key.A));
        Right = bindings.Register("right", "Right", "Walks right; flies right.", InputBinding.Of(Key.D));
        Jump = bindings.Register("jump", "Jump", "Jumps, once a press; flies up.", InputBinding.Of(Key.Space));
        Sprint = bindings.Register(
            "sprint",
            "Sprint",
            "Sprints while held; flies faster.",
            InputBinding.Of(Key.ShiftLeft)
        );
        Crouch = bindings.Register("crouch", "Crouch", "Crouches while held.", InputBinding.Of(Key.ControlLeft));
        FlyDown = bindings.Register("fly-down", "Fly down", "Flies down (the fly camera).", InputBinding.Of(Key.C));
        ThirdPerson = bindings.Register(
            "third-person",
            "Third person",
            "The camera at the eye, or behind the right shoulder.",
            InputBinding.Of(Key.Tab)
        );
        bindings.RegisterFixed("Camera distance", "Wheel", "Moves the third-person camera in and out.");
    }

    /// <summary>The engine's keybindings, which hold these.</summary>
    public KeyBindings Bindings { get; }

    public KeyAction Forward { get; }
    public KeyAction Back { get; }
    public KeyAction Left { get; }
    public KeyAction Right { get; }
    public KeyAction Jump { get; }
    public KeyAction Sprint { get; }
    public KeyAction Crouch { get; }
    public KeyAction FlyDown { get; }
    public KeyAction ThirdPerson { get; }

    /// <summary>A fresh set on keybindings of their own, at their defaults: tests and tools.</summary>
    public static MovementKeys Defaults() => new(new KeyBindings());

    /// <summary>True when <paramref name="action"/>'s key or button is held in <paramref name="input"/>.</summary>
    public bool IsDown(in InputSnapshot input, KeyAction action) => Bindings.IsDown(in input, action);

    /// <summary>True when <paramref name="action"/>'s key or button went down since the snapshot before.</summary>
    public bool WasPressed(in InputSnapshot input, KeyAction action) => Bindings.WasPressed(in input, action);
}
