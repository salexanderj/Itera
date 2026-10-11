using System.Globalization;
using System.Numerics;
using Itera.Engine.Input;
using Silk.NET.Input;

namespace Itera.Engine.Control;

/// <summary>
/// The control port's <c>input</c>: a list of actions run in order on the main thread, before the fixed steps, some of
/// them over time on the game's clock. The actions:
/// <list type="bullet">
/// <item><c>down KEY</c>, <c>up KEY</c>: a key goes down or up and stays so.</item>
/// <item><c>tap KEY</c>: down and up at once; the next frame and fixed step see it pressed.</item>
/// <item><c>hold KEY SECONDS</c>: down, then up after that long.</item>
/// <item><c>click left|right|middle [SECONDS]</c>: a button down, then up after that long (0 by default: at once).</item>
/// <item><c>look DX DY</c>: the pointer moves that many screen pixels (look, while captured).</item>
/// <item><c>scroll STEPS</c>: the wheel turns that many steps, away from the user positive (as a wheel's own).</item>
/// <item><c>point X Y</c>: the pointer is at that pixel of the frame (framebuffer pixels from the top left), for the
/// menu; a click after it clicks there.</item>
/// <item><c>type TEXT</c>: the characters of TEXT (the rest of the line) typed, as into the console or a text box.</item>
/// <item><c>capture on|off</c>: the pointer is captured for look, or freed (<see cref="InputService.SetInjectedCapture"/>);
/// the next action waits until it is, since taking the capture drops the pointer's movement so far.</item>
/// <item><c>wait SECONDS</c>: nothing, for that long.</item>
/// </list>
/// KEY is a Silk.NET <see cref="Key"/> name (W, Space, Escape, ShiftLeft, ControlLeft, F3), without case, or a digit.
/// Keys and buttons it holds when it ends early (a time-out, the game stopping) are let go.
/// </summary>
internal sealed class InputScript
{
    private readonly InputService _input;
    private readonly List<Action> _actions;
    private readonly HashSet<Key> _heldKeys = [];
    private readonly HashSet<MouseButton> _heldButtons = [];
    private int _next;
    private double _waitUntil = double.NegativeInfinity;
    private bool? _awaitedCapture;

    private InputScript(InputService input, List<Action> actions)
    {
        _input = input;
        _actions = actions;
    }

    /// <summary>Reads <paramref name="lines"/>; null with <paramref name="error"/> naming the first bad one.</summary>
    public static InputScript? Parse(InputService input, IReadOnlyList<string> lines, out string? error)
    {
        var actions = new List<Action>();
        foreach (var line in lines)
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && words[0].Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                // The text after "type ", spaces and all.
                actions.Add(new Action(ActionKind.Type, Text: line.TrimStart()[5..]));
                continue;
            }

            if (!TryParse(words, actions))
            {
                error =
                    $"input: '{line}' is not an action; actions: down KEY, up KEY, tap KEY, hold KEY S, "
                    + "click left|right|middle [S], look DX DY, scroll STEPS, point X Y, type TEXT, capture on|off, wait S";
                return null;
            }
        }

        error = null;
        return new InputScript(input, actions);
    }

    /// <summary>Runs the actions that are due; true once the last has run. Main thread, with the clock in seconds.</summary>
    public bool Step(double now)
    {
        while (_next < _actions.Count)
        {
            if (now < _waitUntil || (_awaitedCapture is { } wanted && _input.PointerCaptured != wanted))
            {
                return false;
            }

            _awaitedCapture = null;

            var action = _actions[_next++];
            switch (action.Kind)
            {
                case ActionKind.KeyDown:
                    _input.InjectKey(action.Key, down: true);
                    _heldKeys.Add(action.Key);
                    break;
                case ActionKind.KeyUp:
                    _input.InjectKey(action.Key, down: false);
                    _heldKeys.Remove(action.Key);
                    break;
                case ActionKind.ButtonDown:
                    _input.InjectButton(action.Button, down: true);
                    _heldButtons.Add(action.Button);
                    break;
                case ActionKind.ButtonUp:
                    _input.InjectButton(action.Button, down: false);
                    _heldButtons.Remove(action.Button);
                    break;
                case ActionKind.Look:
                    _input.InjectLook(action.Look);
                    break;
                case ActionKind.Scroll:
                    _input.HandleScroll(action.Steps);
                    break;
                case ActionKind.Point:
                    _input.InjectPointer(action.Look);
                    break;
                case ActionKind.Type:
                    foreach (var character in action.Text!.EnumerateRunes())
                    {
                        _input.HandleCharacter(character);
                    }

                    break;
                case ActionKind.Capture:
                    _input.SetInjectedCapture(action.On);
                    _awaitedCapture = action.On;
                    break;
                case ActionKind.Wait:
                    _waitUntil = now + action.Seconds;
                    break;
            }
        }

        return now >= _waitUntil && (_awaitedCapture is not { } last || _input.PointerCaptured == last);
    }

    /// <summary>Lets go of every key and button it still holds. Main thread.</summary>
    public void Release()
    {
        foreach (var key in _heldKeys)
        {
            _input.InjectKey(key, down: false);
        }

        foreach (var button in _heldButtons)
        {
            _input.InjectButton(button, down: false);
        }

        _heldKeys.Clear();
        _heldButtons.Clear();
    }

    private static bool TryParse(string[] words, List<Action> actions)
    {
        if (words.Length == 0)
        {
            return false;
        }

        switch (words[0].ToUpperInvariant())
        {
            case "DOWN" when words.Length == 2 && TryKey(words[1], out var key):
                actions.Add(new Action(ActionKind.KeyDown, Key: key));
                return true;
            case "UP" when words.Length == 2 && TryKey(words[1], out var key):
                actions.Add(new Action(ActionKind.KeyUp, Key: key));
                return true;
            case "TAP" when words.Length == 2 && TryKey(words[1], out var key):
                actions.Add(new Action(ActionKind.KeyDown, Key: key));
                actions.Add(new Action(ActionKind.KeyUp, Key: key));
                return true;
            case "HOLD"
                when words.Length == 3 && TryKey(words[1], out var key) && TrySeconds(words[2], out var seconds):
                actions.Add(new Action(ActionKind.KeyDown, Key: key));
                actions.Add(new Action(ActionKind.Wait, Seconds: seconds));
                actions.Add(new Action(ActionKind.KeyUp, Key: key));
                return true;
            case "CLICK" when words.Length is 2 or 3 && TryButton(words[1], out var button):
                var held = 0.0;
                if (words.Length == 3 && !TrySeconds(words[2], out held))
                {
                    return false;
                }

                actions.Add(new Action(ActionKind.ButtonDown, Button: button));
                if (held > 0)
                {
                    actions.Add(new Action(ActionKind.Wait, Seconds: held));
                }

                actions.Add(new Action(ActionKind.ButtonUp, Button: button));
                return true;
            case "LOOK"
                when words.Length == 3
                    && float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dx)
                    && float.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dy)
                    && float.IsFinite(dx)
                    && float.IsFinite(dy):
                actions.Add(new Action(ActionKind.Look, Look: new Vector2(dx, dy)));
                return true;
            case "SCROLL"
                when words.Length == 2
                    && float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var steps)
                    && float.IsFinite(steps):
                actions.Add(new Action(ActionKind.Scroll, Steps: steps));
                return true;
            case "POINT"
                when words.Length == 3
                    && float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && float.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && float.IsFinite(x)
                    && float.IsFinite(y):
                actions.Add(new Action(ActionKind.Point, Look: new Vector2(x, y)));
                return true;
            case "CAPTURE" when words.Length == 2 && words[1].ToUpperInvariant() is "ON" or "OFF":
                actions.Add(
                    new Action(ActionKind.Capture, On: words[1].Equals("on", StringComparison.OrdinalIgnoreCase))
                );
                return true;
            case "WAIT" when words.Length == 2 && TrySeconds(words[1], out var wait):
                actions.Add(new Action(ActionKind.Wait, Seconds: wait));
                return true;
            default:
                return false;
        }
    }

    private static bool TryKey(string word, out Key key)
    {
        if (word.Length == 1 && char.IsAsciiDigit(word[0]))
        {
            key = Key.Number0 + (word[0] - '0');
            return true;
        }

        return Enum.TryParse(word, ignoreCase: true, out key) && Enum.IsDefined(key) && key != Key.Unknown;
    }

    private static bool TryButton(string word, out MouseButton button)
    {
        switch (word.ToUpperInvariant())
        {
            case "LEFT":
                button = MouseButton.Left;
                return true;
            case "RIGHT":
                button = MouseButton.Right;
                return true;
            case "MIDDLE":
                button = MouseButton.Middle;
                return true;
            default:
                button = MouseButton.Unknown;
                return false;
        }
    }

    private static bool TrySeconds(string word, out double seconds) =>
        double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
        && seconds >= 0
        && seconds <= ControlService.MostTimeoutSeconds;

    private enum ActionKind
    {
        KeyDown,
        KeyUp,
        ButtonDown,
        ButtonUp,
        Look,
        Scroll,
        Point,
        Type,
        Capture,
        Wait,
    }

    private readonly record struct Action(
        ActionKind Kind,
        Key Key = Key.Unknown,
        MouseButton Button = MouseButton.Unknown,
        Vector2 Look = default,
        bool On = false,
        double Seconds = 0,
        string? Text = null,
        float Steps = 0
    );
}
