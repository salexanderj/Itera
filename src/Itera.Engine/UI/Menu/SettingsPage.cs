using System.Globalization;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.UI.Toolkit;

namespace Itera.Engine.UI.Menu;

/// <summary>
/// The Settings page as a part (<see cref="Build"/>), which the engine's menu shows and a game's own menu can show
/// inside its own: a row of tabs (Game, Graphics, a game's own tabs, Keybindings), then the tab's rows, each a name and
/// a slider (with its value) or a switch, and on Keybindings each action's binding, a click on which waits for the next
/// key or mouse button (<see cref="CapturingBinding"/>, <see cref="BindingPressed"/>); then its buttons
/// (<see cref="Footer"/>): Back, beside Reset keys on Keybindings. Game: the volume,
/// mouse sensitivity, field of view, UI scale, VSync, full screen; Graphics: dynamic render scaling, its threshold,
/// shadows, anti-aliasing; a game's switches (<see cref="SettingsRows"/>) after the engine's on their tab. A setting
/// changes at once (<see cref="PlayerSettings"/>); the caller writes them when the page is left
/// (<see cref="PlayerSettings.Save"/>). Sizes are units for a menu whose body text is 32.4 units (3 % of the reference
/// screen's height). Its texts allocate only when a value they show changes. Main thread.
/// </summary>
public sealed class SettingsPage
{
    /// <summary>A row's width, in units.</summary>
    public const float RowUnits = 950;

    private const float RowHeight = 75.6f;
    private const float ControlWidth = 280.8f;
    private const float ControlHeight = 56.7f;
    private const float TabWidth = 237.6f;
    private const float TabHeight = 59.4f;
    private const float Gap = 16.2f;
    private const float KeyRowHeight = 64.8f;
    private const float KeyButtonHeight = 54;
    private const float KeyListWidth = 691;
    private const int VisibleKeyRows = 8;

    private static readonly int s_tabs = Ui.Id("settings.tabs");
    private static readonly int s_keyList = Ui.Id("settings.keys");
    private static readonly int s_reset = Ui.Id("settings.reset");
    private static readonly int s_back = Ui.Id("settings.back");
    private static readonly int s_volume = Ui.Id("settings.volume");
    private static readonly int s_sensitivity = Ui.Id("settings.sensitivity");
    private static readonly int s_fieldOfView = Ui.Id("settings.fov");
    private static readonly int s_uiScale = Ui.Id("settings.uiscale");
    private static readonly int s_vsync = Ui.Id("settings.vsync");
    private static readonly int s_fullscreen = Ui.Id("settings.fullscreen");
    private static readonly int s_scaling = Ui.Id("settings.scaling");
    private static readonly int s_threshold = Ui.Id("settings.threshold");
    private static readonly int s_shadows = Ui.Id("settings.shadows");
    private static readonly int s_antiAliasing = Ui.Id("settings.aa");

    private readonly PlayerSettings _settings;
    private readonly SettingsRows _rows;
    private readonly Dictionary<int, string> _hints = [];
    private readonly Dictionary<int, (float Value, string Text)> _values = [];
    private string _tab = SettingsRows.GameTab;
    private int _bindAction = -1;
    private string _bindHint = "";

    /// <param name="settings">The player's settings the rows show and change.</param>
    /// <param name="rows">A game's own rows; none when null.</param>
    public SettingsPage(PlayerSettings settings, SettingsRows? rows = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _rows = rows ?? new SettingsRows();
    }

    /// <summary>The tab shown: "Game", "Graphics", "Keybindings", or a game's own.</summary>
    public string Tab => _tab;

    /// <summary>True while the Keybindings tab waits for a key or mouse button to bind.</summary>
    public bool CapturingBinding => _bindAction >= 0 && _tab == SettingsRows.KeysTab;

    /// <summary>The line to show while waiting for a key to bind, or null.</summary>
    public string? Notice => CapturingBinding ? _bindHint : null;

    private KeyBindings Keys => _settings.Keys;

    /// <summary>What element <paramref name="id"/> of the page does, for a hint line; null for none.</summary>
    public string? HintOf(int id) => _hints.TryGetValue(id, out var hint) ? hint : null;

    /// <summary>The page opened: on Game, waiting for no key.</summary>
    public void Opened()
    {
        _tab = SettingsRows.GameTab;
        _bindAction = -1;
    }

    /// <summary>
    /// Escape on the page: while waiting for a key it stops waiting (the binding is kept) and takes the Escape (true);
    /// otherwise false, so the caller leaves the page.
    /// </summary>
    public bool Escape()
    {
        if (!CapturingBinding)
        {
            return false;
        }

        _bindAction = -1;
        return true;
    }

    /// <summary>
    /// The key or button pressed while waiting: it binds the action waiting, unless it is Escape or ` (the menu's and the
    /// console's), which leave it waiting; <see cref="InputBinding.None"/> stops waiting.
    /// </summary>
    public void BindingPressed(InputBinding binding)
    {
        if (!CapturingBinding || KeyBindings.IsReserved(binding))
        {
            return;
        }

        var action = _bindAction;
        _bindAction = -1;
        if (!binding.IsNone)
        {
            Keys.Set(new KeyAction(action), binding);
        }
    }

    /// <summary>
    /// Declares the page: the tabs and the tab's rows, inside the caller's container (its title is the caller's; its
    /// buttons follow with <see cref="Footer"/>).
    /// </summary>
    public void Build(Ui ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        Tabs(ui);
        if (_tab == SettingsRows.KeysTab)
        {
            KeyRows(ui);
            return;
        }

        using (ui.Column(0, new UiBox { Gap = 0 }))
        {
            if (_tab == SettingsRows.GameTab)
            {
                Slider(ui, s_volume, "Volume", "How loud the game is.", _settings.Volume, 0, 1, Percent);
                Slider(
                    ui,
                    s_sensitivity,
                    "Sensitivity",
                    "The mouse: how far the view turns as it moves (degrees a pixel).",
                    _settings.MouseSensitivity,
                    PlayerSettings.LeastSensitivity,
                    PlayerSettings.MostSensitivity,
                    Hundredths
                );
                Slider(
                    ui,
                    s_fieldOfView,
                    "Field of view",
                    "How wide the view is, across a 16:9 screen.",
                    _settings.FieldOfViewDegrees,
                    PlayerSettings.LeastFieldOfView,
                    PlayerSettings.MostFieldOfView,
                    Degrees
                );
                Slider(
                    ui,
                    s_uiScale,
                    "UI scale",
                    "How large the menus and the game's other screens are.",
                    _settings.UiScale,
                    PlayerSettings.LeastUiScale,
                    PlayerSettings.MostUiScale,
                    Percent
                );
                Switch(
                    ui,
                    s_vsync,
                    "VSync",
                    "Frames wait for the screen: no tearing, a little more lag.",
                    _settings.VSync
                );
                Switch(ui, s_fullscreen, "Full screen", "The window covers its screen.", _settings.Fullscreen);
            }
            else if (_tab == SettingsRows.GraphicsTab)
            {
                Switch(
                    ui,
                    s_scaling,
                    "Dynamic render scaling",
                    "Frames that take too long draw the world smaller, and FSR 1 fills the screen.",
                    _settings.DynamicScaling
                );
                Slider(
                    ui,
                    s_threshold,
                    "Dynamic scaling threshold",
                    "The frame rate dynamic render scaling holds, or the screen's refresh rate if lower.",
                    ThresholdStep(_settings.ScalingThreshold),
                    0,
                    DynamicRenderScale.Thresholds.Length - 1,
                    Threshold
                );
                Switch(
                    ui,
                    s_shadows,
                    "Shadows",
                    "The sun casts shadows. Off is quicker, and everything facing the sun is lit.",
                    _settings.Shadows
                );
                Slider(
                    ui,
                    s_antiAliasing,
                    "Anti-aliasing",
                    "Smooths the steps along edges: SMAA after drawing, or MSAA's 2, 4, or 8 samples a pixel. Off is quickest.",
                    (int)_settings.AntiAliasMode,
                    0,
                    (int)AntiAliasMode.Msaa8,
                    AntiAliasing
                );
            }

            GameRows(ui);
        }
    }

    private void Tabs(Ui ui)
    {
        using (ui.Row(s_tabs, new UiBox { Gap = Gap }))
        {
            TabButton(
                ui,
                SettingsRows.GameTab,
                "The volume, the mouse, the field of view, the UI's size, and the screen."
            );
            TabButton(ui, SettingsRows.GraphicsTab, "How the world is drawn.");
            foreach (var tab in _rows.GameTabs)
            {
                TabButton(ui, tab, "The game's own settings.");
            }

            TabButton(ui, SettingsRows.KeysTab, "The keys and mouse buttons of each action.");
        }
    }

    private void TabButton(Ui ui, string tab, string hint)
    {
        var id = Ui.Id(Ui.Id("settings.tab"), StableHash(tab));
        _hints[id] = hint;
        var marks = tab == _tab ? UiMarks.Selected : UiMarks.None;
        if (ui.Button(id, tab, box: Fixed(TabWidth, TabHeight), marks: marks).Clicked)
        {
            _tab = tab;
            _bindAction = -1;
        }
    }

    /// <summary>The game's switches on the tab shown, after the engine's rows.</summary>
    private void GameRows(Ui ui)
    {
        for (var i = 0; i < _rows.Switches.Count; i++)
        {
            var row = _rows.Switches[i];
            if (row.Tab != _tab)
            {
                continue;
            }

            var id = Ui.Id(Ui.Id("settings.game"), row.Switch.Index);
            if (Switch(ui, id, row.Label, row.Hint, _settings.IsOn(row.Switch)))
            {
                _settings.SetSwitch(row.Switch, !_settings.IsOn(row.Switch));
            }
        }
    }

    /// <summary>A row with a slider and its value; the value moved by the slider is set at once.</summary>
    private void Slider(
        Ui ui,
        int id,
        string name,
        string hint,
        float value,
        float least,
        float most,
        Func<int, float, string> text
    )
    {
        _hints[id] = hint;
        using (ui.Row(0, Row()))
        {
            ui.Text(name, box: new UiBox { Width = UiSize.Grow() });
            ui.Text(ValueText(id, value, text), UiText.Body with { Tone = UiTone.Dim });
            if (ui.Slider(id, value, least, most, UiBox.Fixed(ControlWidth, ControlHeight)) is { Changed: true } moved)
            {
                Set(id, moved.Value);
            }
        }
    }

    /// <summary>A row with a switch; true when it was clicked.</summary>
    private bool Switch(Ui ui, int id, string name, string hint, bool on)
    {
        _hints[id] = hint;
        using (ui.Row(0, Row()))
        {
            ui.Text(name, box: new UiBox { Width = UiSize.Grow() });
            if (!ui.Switch(id, on, UiBox.Fixed(ControlWidth, ControlHeight)).Clicked)
            {
                return false;
            }
        }

        if (id == s_vsync)
        {
            _settings.SetVSync(!on);
        }
        else if (id == s_fullscreen)
        {
            _settings.SetFullscreen(!on);
        }
        else if (id == s_scaling)
        {
            _settings.SetDynamicScaling(!on);
        }
        else if (id == s_shadows)
        {
            _settings.SetShadows(!on);
        }

        return true;
    }

    private static UiBox Row() =>
        new()
        {
            Width = UiSize.Fixed(RowUnits),
            Height = UiSize.Fixed(RowHeight),
            AlignY = UiAlign.Centre,
            Gap = Gap,
        };

    /// <summary>Sets the setting a slider moved.</summary>
    private void Set(int id, float value)
    {
        if (id == s_volume)
        {
            _settings.SetVolume(value);
        }
        else if (id == s_sensitivity)
        {
            _settings.SetMouseSensitivity(value);
        }
        else if (id == s_fieldOfView)
        {
            _settings.SetFieldOfView(MathF.Round(value));
        }
        else if (id == s_uiScale)
        {
            // In steps of 5 %, as a player reads it.
            _settings.SetUiScale(MathF.Round(value * 20) / 20);
        }
        else if (id == s_threshold)
        {
            _settings.SetScalingThreshold(DynamicRenderScale.Thresholds[(int)MathF.Round(value)]);
        }
        else if (id == s_antiAliasing)
        {
            _settings.SetAntiAliasMode((AntiAliasMode)(int)MathF.Round(value));
        }
    }

    /// <summary>The Keybindings tab: a list of the actions with their bindings (the wheel scrolls it), then Reset keys.</summary>
    private void KeyRows(Ui ui)
    {
        var rows = Keys.Rows;
        using (
            ui.Scroll(
                s_keyList,
                new UiBox { Width = UiSize.Fixed(KeyListWidth), Height = UiSize.Fixed(VisibleKeyRows * KeyRowHeight) }
            )
        )
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                using (
                    ui.Row(
                        0,
                        new UiBox
                        {
                            Width = UiSize.Grow(),
                            Height = UiSize.Fixed(KeyRowHeight),
                            AlignY = UiAlign.Centre,
                        }
                    )
                )
                {
                    var tone = row.FixedKeys is not null ? UiTone.Dim : UiTone.Normal;
                    ui.Text(row.Label, UiText.Body with { Tone = tone }, new UiBox { Width = UiSize.Grow() });
                    if (row.FixedKeys is { } keys)
                    {
                        ui.Text(
                            keys,
                            UiText.Body with
                            {
                                Tone = tone,
                                Align = UiAlign.Centre,
                            },
                            new UiBox { Width = UiSize.Fixed(ControlWidth) }
                        );
                        continue;
                    }

                    var id = Ui.Id(s_keyList, i);
                    _hints[id] = row.Hint;
                    var label = i == _bindAction ? "Press a key…" : Keys[new KeyAction(i)].Label;
                    if (ui.Button(id, label, box: Fixed(ControlWidth, KeyButtonHeight)).Clicked)
                    {
                        _bindAction = i;
                        _bindHint =
                            $"Press a key or mouse button for {row.Label}; Escape keeps {Keys[new KeyAction(i)].Label}.";
                    }
                }
            }
        }
    }

    /// <summary>
    /// Declares the page's buttons after <see cref="Build"/>: Back (<paramref name="back"/>), on Keybindings beside Reset
    /// keys. True when Back is clicked: the caller leaves the page and writes the settings (<see cref="PlayerSettings.Save"/>).
    /// </summary>
    public bool Footer(Ui ui, string back = "Back")
    {
        ArgumentNullException.ThrowIfNull(ui);
        _hints[s_back] = "Keeps the settings for the next start too.";
        if (_tab != SettingsRows.KeysTab)
        {
            return ui.Button(s_back, back, box: Fixed(367.2f, 64.8f)).Clicked;
        }

        using (ui.Row(0, new UiBox { Gap = 21.6f }))
        {
            _hints[s_reset] = "Every action back to its first key.";
            if (ui.Button(s_reset, "Reset keys", box: Fixed(216, 59.4f)).Clicked)
            {
                _bindAction = -1;
                Keys.ResetAll();
            }

            return ui.Button(s_back, back, box: Fixed(216, 59.4f)).Clicked;
        }
    }

    /// <summary>
    /// A button's box of a fixed size with narrow label margins of its own, so its label fits whatever margins the
    /// menu's style gives buttons.
    /// </summary>
    private static UiBox Fixed(float width, float height) =>
        UiBox.Fixed(width, height) with
        {
            Padding = new UiEdges(8, 0),
        };

    /// <summary>A value's text, made again only when the value changes (the formats are static, so passing one allocates nothing).</summary>
    private string ValueText(int id, float value, Func<int, float, string> text)
    {
        if (_values.TryGetValue(id, out var known) && known.Value == value)
        {
            return known.Text;
        }

        var made = text(id, value);
        _values[id] = (value, made);
        return made;
    }

    private static string Percent(int id, float value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value * 100:F0} %");

    private static string Hundredths(int id, float value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Degrees(int id, float value) => string.Create(CultureInfo.InvariantCulture, $"{value:F0}°");

    /// <summary>The threshold at a slider step (between two offered rates where the step lies between theirs).</summary>
    private static string Threshold(int id, float step)
    {
        var thresholds = DynamicRenderScale.Thresholds;
        var below = Math.Clamp((int)MathF.Floor(step), 0, thresholds.Length - 1);
        var above = Math.Min(below + 1, thresholds.Length - 1);
        var fps = thresholds[below] + (thresholds[above] - thresholds[below]) * (step - below);
        return string.Create(CultureInfo.InvariantCulture, $"{fps:F0} fps");
    }

    private static string AntiAliasing(int id, float value) =>
        AntiAliasModes.Label((AntiAliasMode)(int)MathF.Round(value));

    /// <summary>
    /// Where <paramref name="fps"/> lies among the offered thresholds, in steps from the first: a threshold read from the
    /// file or the command line between two of them lies between their steps.
    /// </summary>
    private static float ThresholdStep(float fps)
    {
        var thresholds = DynamicRenderScale.Thresholds;
        if (fps <= thresholds[0])
        {
            return 0;
        }

        for (var i = 1; i < thresholds.Length; i++)
        {
            if (fps <= thresholds[i])
            {
                return i - 1 + (fps - thresholds[i - 1]) / (thresholds[i] - thresholds[i - 1]);
            }
        }

        return thresholds.Length - 1;
    }

    /// <summary>A tab name's number for its id, the same in every run.</summary>
    private static int StableHash(string text) => Ui.Id(text);
}
