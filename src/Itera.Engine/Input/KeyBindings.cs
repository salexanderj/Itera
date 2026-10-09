using System.Text.Json;
using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>An action registered with <see cref="KeyBindings"/>: its place in the registry.</summary>
/// <param name="Index">The action's place, which <see cref="KeyBindings.Rows"/> gives too.</param>
public readonly record struct KeyAction(int Index);

/// <summary>
/// One row of the Keybindings tab: an action that can be bound, or a fixed key shown for the player to know
/// (<paramref name="FixedKeys"/> set, never bound).
/// </summary>
/// <param name="Id">The name <c>settings.json</c> keeps it by; empty for a fixed row.</param>
/// <param name="Label">What the tab calls it.</param>
/// <param name="Hint">What it does, shown under the pointer.</param>
/// <param name="Default">Its binding until the player changes it.</param>
/// <param name="Context">When it works: 0 always; actions of two other contexts never work at once.</param>
/// <param name="FixedKeys">A fixed row's keys ("Escape"); null for an action.</param>
public sealed record KeyRow(string Id, string Label, string Hint, InputBinding Default, int Context, string? FixedKeys)
{
    /// <summary>True for a row that only shows keys that cannot be changed.</summary>
    public bool Fixed => FixedKeys is not null;
}

/// <summary>
/// The player's keybindings each action the engine or the game registers, by id, with
/// one key or mouse button (<see cref="InputBinding"/>; Shift, Ctrl, Alt, and Super match either side), changed on the
/// settings page's Keybindings tab and kept in <c>settings.json</c> under <c>"Keys"</c> (<see cref="Settings.PlayerSettings"/>),
/// only those that differ from their defaults. Escape (the menu) and ` (the console) are fixed: no action takes them,
/// from the menu or the file. Binding a key another action uses unbinds that action, when the two can work at the same
/// time: an action of context 0 works always, and actions of two other contexts never do at once (the game's attacks
/// with the debug tools off and the tools' keys with them on share the mouse buttons). Systems read an action with
/// <see cref="IsDown"/> and <see cref="WasPressed"/>, which allocate nothing. The engine registers the fixed rows Menu,
/// Console, and Capture the pointer, and the stats line (<see cref="Stats"/>, F1); the game its own at its run start.
/// Registries refuse an id twice, so this is run-wide. Main thread.
/// </summary>
public sealed class KeyBindings
{
    private readonly List<KeyRow> _rows = [];
    private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _saved = new(StringComparer.OrdinalIgnoreCase);
    private InputBinding[] _bindings = new InputBinding[32];

    public KeyBindings()
    {
        RegisterFixed("Menu", "Escape", "Opens the menu, and goes back in it.");
        RegisterFixed("Console", "`", "Opens and closes the console.");
        RegisterFixed(
            "Capture the pointer",
            "Left mouse",
            "A left click captures the pointer to look; Escape frees it."
        );
        Stats = Register("stats", "Stats line", "Shows the frame rate and timings.", InputBinding.Of(Key.F1));
    }

    /// <summary>The stats line in the top-right corner (F1 by default).</summary>
    public KeyAction Stats { get; }

    /// <summary>The rows in the order they were registered, actions and fixed rows.</summary>
    public IReadOnlyList<KeyRow> Rows => _rows;

    /// <summary>Rises whenever a binding changes (not when one is read from the file).</summary>
    public int Version { get; private set; }

    /// <summary>The binding of <paramref name="action"/>.</summary>
    public InputBinding this[KeyAction action] => _bindings[action.Index];

    /// <summary>True for Escape and `, which belong to the menu and the console.</summary>
    public static bool IsReserved(InputBinding binding) =>
        binding.Kind == BindingKind.Key && (Key)binding.Code is Key.Escape or Key.GraveAccent;

    /// <summary>
    /// Adds an action bound to <paramref name="default"/>, or to the file's binding for <paramref name="id"/> when it has
    /// one. Refuses an id twice.
    /// </summary>
    public KeyAction Register(string id, string label, string hint, InputBinding @default, int context = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_ids.TryAdd(id, _rows.Count))
        {
            throw new InvalidOperationException($"The key binding '{id}' is registered already.");
        }

        if (IsReserved(@default))
        {
            throw new ArgumentException($"'{id}' cannot take {@default.Label}: it is the menu's or the console's.");
        }

        var index = _rows.Count;
        _rows.Add(new KeyRow(id, label, hint, @default, context, null));
        Grow();
        _bindings[index] = Saved(id, out var saved) ? saved : @default;
        ResolveClashes();
        return new KeyAction(index);
    }

    /// <summary>Adds a row that shows <paramref name="keys"/> for something that cannot be bound.</summary>
    public void RegisterFixed(string label, string keys, string hint)
    {
        _rows.Add(new KeyRow("", label, hint, InputBinding.None, 0, keys));
        Grow();
    }

    /// <summary>True when <paramref name="action"/>'s key or button is held in <paramref name="input"/>.</summary>
    public bool IsDown(in InputSnapshot input, KeyAction action) => _bindings[action.Index].IsDown(in input);

    /// <summary>True when <paramref name="action"/>'s key or button went down since the snapshot before.</summary>
    public bool WasPressed(in InputSnapshot input, KeyAction action) => _bindings[action.Index].WasPressed(in input);

    /// <summary>
    /// Binds <paramref name="action"/> to <paramref name="binding"/>, and unbinds any other action that can work at the
    /// same time and has it. False, with nothing changed, for a fixed row or Escape and `.
    /// </summary>
    public bool Set(KeyAction action, InputBinding binding)
    {
        var row = _rows[action.Index];
        if (row.Fixed || IsReserved(binding))
        {
            return false;
        }

        if (_bindings[action.Index] == binding)
        {
            return true;
        }

        if (!binding.IsNone)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (
                    i != action.Index
                    && !_rows[i].Fixed
                    && _bindings[i] == binding
                    && Overlap(row.Context, _rows[i].Context)
                )
                {
                    _bindings[i] = InputBinding.None;
                }
            }
        }

        _bindings[action.Index] = binding;
        Version++;
        return true;
    }

    /// <summary>Every action back to its default.</summary>
    public void ResetAll()
    {
        var changed = false;
        for (var i = 0; i < _rows.Count; i++)
        {
            changed |= !_rows[i].Fixed && _bindings[i] != _rows[i].Default;
            _bindings[i] = _rows[i].Default;
        }

        _saved.Clear();
        if (changed)
        {
            Version++;
        }
    }

    /// <summary>
    /// Takes the file's <c>"Keys"</c> object: each id's binding, for the actions registered now and those registered
    /// later. Ids and names it does not know, and Escape and `, are left out (unknown ids are kept and written back).
    /// </summary>
    public void Load(JsonElement keys)
    {
        if (keys.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in keys.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text)
            {
                _saved[property.Name] = text;
            }
        }

        foreach (var (id, index) in _ids)
        {
            if (Saved(id, out var binding))
            {
                _bindings[index] = binding;
            }
        }

        ResolveClashes();
    }

    /// <summary>
    /// Writes <c>"Keys"</c>: each action whose binding differs from its default (<c>"None"</c> for an unbound one), and
    /// the file's ids no action registered this run. Nothing when there are none.
    /// </summary>
    public void Write(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var started = false;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].Fixed && _bindings[i] != _rows[i].Default)
            {
                Start(writer, ref started);
                writer.WriteString(_rows[i].Id, _bindings[i].Name);
            }
        }

        foreach (var (id, text) in _saved)
        {
            if (!_ids.ContainsKey(id))
            {
                Start(writer, ref started);
                writer.WriteString(id, text);
            }
        }

        if (started)
        {
            writer.WriteEndObject();
        }
    }

    private static void Start(Utf8JsonWriter writer, ref bool started)
    {
        if (!started)
        {
            writer.WriteStartObject("Keys");
            started = true;
        }
    }

    /// <summary>Makes room for a binding per row.</summary>
    private void Grow()
    {
        if (_rows.Count > _bindings.Length)
        {
            Array.Resize(ref _bindings, Math.Max(_rows.Count, _bindings.Length * 2));
        }
    }

    /// <summary>
    /// Unbinds one of each two actions that can work at the same time and share a binding, as <see cref="Set"/> would
    /// have: the file's binding keeps it over a default (a new action's default, or one the file moved another action
    /// onto), and otherwise the action registered first does. After
    /// <see cref="Register"/> and <see cref="Load"/>.
    /// </summary>
    private void ResolveClashes()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            for (var j = i + 1; j < _rows.Count && !_bindings[i].IsNone; j++)
            {
                if (
                    _rows[i].Fixed
                    || _rows[j].Fixed
                    || _bindings[j] != _bindings[i]
                    || !Overlap(_rows[i].Context, _rows[j].Context)
                )
                {
                    continue;
                }

                if (Saved(_rows[j].Id, out _) && !Saved(_rows[i].Id, out _))
                {
                    _bindings[i] = InputBinding.None;
                }
                else
                {
                    _bindings[j] = InputBinding.None;
                }
            }
        }
    }

    /// <summary>Two actions can work at the same time unless they belong to two different contexts other than 0.</summary>
    private static bool Overlap(int a, int b) => a == 0 || b == 0 || a == b;

    private bool Saved(string id, out InputBinding binding)
    {
        binding = InputBinding.None;
        return _saved.TryGetValue(id, out var text) && InputBinding.TryParse(text, out binding) && !IsReserved(binding);
    }
}
