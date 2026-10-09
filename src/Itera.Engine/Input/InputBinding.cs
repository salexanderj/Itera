using System.Globalization;
using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>What an <see cref="InputBinding"/> is: nothing, a key, or a mouse button.</summary>
public enum BindingKind
{
    None,
    Key,
    Mouse,
}

/// <summary>
/// One key or mouse button an action is bound to (<see cref="KeyBindings"/>), or none. A left or right Shift, Ctrl, Alt,
/// or Super is kept as the left one and matches either side, as the game always did. <see cref="Name"/> and
/// <see cref="TryParse"/> give the name <c>settings.json</c> keeps (Silk.NET's key names, <c>MouseLeft</c>,
/// <c>MouseRight</c>, <c>MouseMiddle</c>, <c>Mouse4</c> to <c>Mouse12</c>, <c>None</c>); <see cref="Label"/> the
/// name the menu shows ("W", "Space", "Shift", "Left mouse"), from tables made once, so showing one allocates nothing.
/// </summary>
/// <param name="Kind">A key, a mouse button, or nothing.</param>
/// <param name="Code">The <see cref="Key"/> or <see cref="MouseButton"/> value.</param>
public readonly record struct InputBinding(BindingKind Kind, int Code)
{
    private const int KeyCodes = 512;
    private const int MouseCodes = 12;
    private static readonly string[] s_keyLabels = KeyLabels();
    private static readonly string[] s_mouseLabels =
    [
        "Left mouse",
        "Right mouse",
        "Middle mouse",
        "Mouse 4",
        "Mouse 5",
        "Mouse 6",
        "Mouse 7",
        "Mouse 8",
        "Mouse 9",
        "Mouse 10",
        "Mouse 11",
        "Mouse 12",
    ];

    /// <summary>Bound to nothing.</summary>
    public static InputBinding None => default;

    /// <summary>True when bound to nothing.</summary>
    public bool IsNone => Kind == BindingKind.None;

    /// <summary><paramref name="key"/>, a right-hand modifier kept as the left one; <see cref="None"/> for an unknown key.</summary>
    public static InputBinding Of(Key key)
    {
        var left = key switch
        {
            Key.ShiftRight => Key.ShiftLeft,
            Key.ControlRight => Key.ControlLeft,
            Key.AltRight => Key.AltLeft,
            Key.SuperRight => Key.SuperLeft,
            _ => key,
        };
        return (uint)left < KeyCodes ? new InputBinding(BindingKind.Key, (int)left) : None;
    }

    /// <summary><paramref name="button"/>; <see cref="None"/> for an unknown button.</summary>
    public static InputBinding Of(MouseButton button) =>
        (uint)button < MouseCodes ? new InputBinding(BindingKind.Mouse, (int)button) : None;

    /// <summary>True when the key or button was held as <paramref name="input"/> was taken; a modifier on either side.</summary>
    public bool IsDown(in InputSnapshot input) =>
        Kind switch
        {
            BindingKind.Key => input.IsDown((Key)Code) || (Right(Code) is { } right && input.IsDown(right)),
            BindingKind.Mouse => input.IsDown((MouseButton)Code),
            _ => false,
        };

    /// <summary>True when the key or button went down since the snapshot before <paramref name="input"/>.</summary>
    public bool WasPressed(in InputSnapshot input) =>
        Kind switch
        {
            BindingKind.Key => input.WasPressed((Key)Code) || (Right(Code) is { } right && input.WasPressed(right)),
            BindingKind.Mouse => input.WasPressed((MouseButton)Code),
            _ => false,
        };

    /// <summary>The name the menu shows: "W", "Space", "Shift", "Left mouse", "None".</summary>
    public string Label =>
        Kind switch
        {
            BindingKind.Key when (uint)Code < KeyCodes && s_keyLabels[Code] is { } label => label,
            BindingKind.Mouse when (uint)Code < MouseCodes => s_mouseLabels[Code],
            _ => "None",
        };

    /// <summary>The name <c>settings.json</c> keeps: <c>W</c>, <c>ShiftLeft</c>, <c>MouseLeft</c>, <c>Mouse4</c>, <c>None</c>.</summary>
    public string Name =>
        Kind switch
        {
            BindingKind.Key => Enum.GetName((Key)Code) ?? "None",
            BindingKind.Mouse => (MouseButton)Code switch
            {
                MouseButton.Left => "MouseLeft",
                MouseButton.Right => "MouseRight",
                MouseButton.Middle => "MouseMiddle",
                _ => string.Create(CultureInfo.InvariantCulture, $"Mouse{Code + 1}"),
            },
            _ => "None",
        };

    /// <summary>Reads a <see cref="Name"/>, without case; false for anything else.</summary>
    public static bool TryParse(string? text, out InputBinding binding)
    {
        binding = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.StartsWith("Mouse", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[5..];
            var button =
                rest.Equals("Left", StringComparison.OrdinalIgnoreCase) ? 0
                : rest.Equals("Right", StringComparison.OrdinalIgnoreCase) ? 1
                : rest.Equals("Middle", StringComparison.OrdinalIgnoreCase) ? 2
                : int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number - 1
                : -1;
            binding = Of((MouseButton)button);
            return !binding.IsNone;
        }

        // A name, not a number: Enum.TryParse would take "65" as A.
        if (char.IsDigit(text[0]) || !Enum.TryParse<Key>(text, ignoreCase: true, out var key) || !Enum.IsDefined(key))
        {
            return false;
        }

        binding = Of(key);
        return !binding.IsNone;
    }

    /// <summary>The right-hand key of a left-hand modifier, which matches it too.</summary>
    private static Key? Right(int code) =>
        (Key)code switch
        {
            Key.ShiftLeft => Key.ShiftRight,
            Key.ControlLeft => Key.ControlRight,
            Key.AltLeft => Key.AltRight,
            Key.SuperLeft => Key.SuperRight,
            _ => null,
        };

    private static string[] KeyLabels()
    {
        var labels = new string[KeyCodes];
        foreach (var key in Enum.GetValues<Key>())
        {
            if ((uint)key >= KeyCodes)
            {
                continue;
            }

            var name = Enum.GetName(key) ?? "";
            labels[(int)key] = key switch
            {
                Key.ShiftLeft or Key.ShiftRight => "Shift",
                Key.ControlLeft or Key.ControlRight => "Ctrl",
                Key.AltLeft or Key.AltRight => "Alt",
                Key.SuperLeft or Key.SuperRight => "Super",
                Key.Apostrophe => "'",
                Key.Comma => ",",
                Key.Minus => "-",
                Key.Period => ".",
                Key.Slash => "/",
                Key.Semicolon => ";",
                Key.Equal => "=",
                Key.LeftBracket => "[",
                Key.BackSlash => "\\",
                Key.RightBracket => "]",
                Key.GraveAccent => "`",
                _ when name.StartsWith("Number", StringComparison.Ordinal) => name[6..],
                _ when name.StartsWith("Keypad", StringComparison.Ordinal) => "Keypad " + name[6..],
                _ => name,
            };
        }

        return labels;
    }
}
