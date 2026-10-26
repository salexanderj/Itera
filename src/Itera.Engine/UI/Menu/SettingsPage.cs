using System.Globalization;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.UI.Toolkit;

namespace Itera.Engine.UI.Menu;

/// <summary>
/// The Settings page as a part (<see cref="Build"/>), which the engine's menu shows and a game's own menu can show
/// inside its own: a row of tabs (Game, Graphics, Sound, a game's own tabs, Keybindings), then the tab's rows, each a name and
/// a slider (with its value) or a switch, and on Keybindings each action's binding, a click on which waits for the next
/// key or mouse button (<see cref="CapturingBinding"/>, <see cref="BindingPressed"/>); then its buttons
/// (<see cref="Footer"/>): Back, beside Reset keys on Keybindings. Game: mouse sensitivity, field of view, UI scale,
/// VSync, full screen, and the screen it covers (shown while there is a screen to choose); Graphics: Quality (a preset,
/// <see cref="GraphicsPreset"/>), dynamic render scaling, its threshold, shadows, anti-aliasing, object detail, effects
/// detail, and the render scale (disabled while dynamic scaling is on); Sound: the volume, and
/// under it music's and the effects'; a game's switches and sliders (<see cref="SettingsRows"/>) after the engine's on their tab. A setting
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
    private static readonly int s_musicVolume = Ui.Id("settings.music");
    private static readonly int s_effectsVolume = Ui.Id("settings.effects");
    private static readonly int s_sensitivity = Ui.Id("settings.sensitivity");
    private static readonly int s_fieldOfView = Ui.Id("settings.fov");
    private static readonly int s_uiScale = Ui.Id("settings.uiscale");
    private static readonly int s_vsync = Ui.Id("settings.vsync");
    private static readonly int s_fullscreen = Ui.Id("settings.fullscreen");
    private static readonly int s_screen = Ui.Id("settings.screen");
    private static readonly int s_scaling = Ui.Id("settings.scaling");
    private static readonly int s_threshold = Ui.Id("settings.threshold");
    private static readonly int s_shadows = Ui.Id("settings.shadows");
    private static readonly int s_antiAliasing = Ui.Id("settings.aa");
    private static readonly int s_quality = Ui.Id("settings.quality");
    private static readonly int s_objectDetail = Ui.Id("settings.objectdetail");
    private static readonly int s_effectsDetail = Ui.Id("settings.effectsdetail");
    private static readonly int s_renderScale = Ui.Id("settings.renderscale");

    private readonly PlayerSettings _settings;
    private readonly SettingsRows _rows;
    private readonly Dictionary<int, string> _hints = [];
    private readonly Dictionary<int, (float Value, string Text)> _values = [];

    /// <summary>
    /// The Screen slider's steps: the main screen (empty), each connected screen, then the setting's when it is not
    /// connected.
    /// </summary>
    private readonly List<string> _screens = [];

    /// <summary>
    /// The Screen slider's text, made once, as a method group of this page would make a delegate at each build.
    /// </summary>
    private readonly Func<int, float, string> _screenText;
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
        _screenText = ScreenText;
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

    /// <summary>The page opened: on Game, waiting for no key, with the connected screens read again.</summary>
    public void Opened()
    {
        _tab = SettingsRows.GameTab;
        _bindAction = -1;
        _settings.ReadScreens();
        _values.Remove(s_screen);
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
                ScreenRow(ui);
            }
            else if (_tab == SettingsRows.SoundTab)
            {
                Slider(
                    ui,
                    s_volume,
                    "Master Volume",
                    "How loud the game is: music and effects alike.",
                    _settings.Volume,
                    0,
                    1,
                    Percent
                );
                Slider(
                    ui,
                    s_musicVolume,
                    "Music Volume",
                    "How loud the music is, under the volume.",
                    _settings.MusicVolume,
                    0,
                    1,
                    Percent
                );
                Slider(
                    ui,
                    s_effectsVolume,
                    "Effects Volume",
                    "How loud every other sound is, under the volume.",
                    _settings.EffectsVolume,
                    0,
                    1,
                    Percent
                );
            }
            else if (_tab == SettingsRows.GraphicsTab)
            {
                Slider(
                    ui,
                    s_quality,
                    "Quality",
                    "Low, Medium, or High sets anti-aliasing, shadows, object detail, and effects detail at once. Custom: they match no preset.",
                    (int)_settings.GraphicsPreset,
                    0,
                    (int)GraphicsPreset.Custom,
                    Quality
                );
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
                Slider(
                    ui,
                    s_objectDetail,
                    "Object detail",
                    "How near objects such as trees switch to coarser models. Low is quickest.",
                    // The slider runs from Low on the left to High on the right, as the other rows run from less to more;
                    // an object detail past its ends (from the options) shows at the nearer end.
                    ObjectDetailStep(
                        Math.Clamp(
                            _settings.ObjectDetail,
                            PlayerSettings.SliderFinestObjectDetail,
                            PlayerSettings.SliderCoarsestObjectDetail
                        )
                    ),
                    PlayerSettings.SliderFinestObjectDetail,
                    PlayerSettings.SliderCoarsestObjectDetail,
                    ObjectDetail
                );
                Slider(
                    ui,
                    s_effectsDetail,
                    "Effects detail",
                    "The most of the cosmetic effects drawn: sparks, dust, and the like. Dynamic scaling lowers it first.",
                    _settings.EffectsDetail,
                    EffectsBudget.MinimumShare,
                    EffectsBudget.MostShare,
                    Percent
                );
                Slider(
                    ui,
                    s_renderScale,
                    "Render scale",
                    "The size the world is drawn at while dynamic render scaling is off; FSR 1 fills the screen.",
                    _settings.RenderScale,
                    RenderScale.Min,
                    RenderScale.Max,
                    Percent,
                    _settings.DynamicScaling ? UiMarks.Disabled : UiMarks.None
                );
            }

            GameRows(ui);
        }
    }

    /// <summary>
    /// The Screen row, a slider through <see cref="_screens"/>, when there is more than the main screen to choose from.
    /// Its steps are made again each build into the same list, so it allocates nothing.
    /// </summary>
    private void ScreenRow(Ui ui)
    {
        _screens.Clear();
        _screens.Add("");
        var connected = _settings.Screens;
        for (var i = 0; i < connected.Count; i++)
        {
            _screens.Add(connected[i]);
        }

        var at = _settings.Screen.Length == 0 ? 0 : _screens.IndexOf(_settings.Screen);
        if (at < 0)
        {
            at = _screens.Count;
            _screens.Add(_settings.Screen);
        }

        if (_screens.Count < 2)
        {
            return;
        }

        Slider(
            ui,
            s_screen,
            "Screen",
            "The screen full screen covers; Main is the system's main screen.",
            at,
            0,
            _screens.Count - 1,
            _screenText
        );
    }

    /// <summary>
    /// The Screen slider's text at a step: Main, a screen's name, or the setting's with "not connected".
    /// </summary>
    private string ScreenText(int id, float step)
    {
        var at = Math.Clamp((int)MathF.Round(step), 0, _screens.Count - 1);
        var name = _screens[at];
        if (at == 0)
        {
            return "Main";
        }

        return at > _settings.Screens.Count ? $"{name} (not connected)" : name;
    }

    private void Tabs(Ui ui)
    {
        using (ui.Row(s_tabs, new UiBox { Gap = Gap }))
        {
            TabButton(ui, SettingsRows.GameTab, "The mouse, the field of view, the UI's size, and the screen.");
            TabButton(ui, SettingsRows.GraphicsTab, "How the world is drawn.");
            TabButton(ui, SettingsRows.SoundTab, "The volume, and music's and the effects' under it.");
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

    /// <summary>The game's switches and sliders on the tab shown, in the order added, after the engine's rows.</summary>
    private void GameRows(Ui ui)
    {
        var order = _rows.Order;
        for (var i = 0; i < order.Count; i++)
        {
            var at = order[i];
            if (at.IsSlider)
            {
                GameSlider(ui, _rows.Sliders[at.Index]);
            }
            else
            {
                GameSwitchRow(ui, _rows.Switches[at.Index]);
            }
        }
    }

    private void GameSwitchRow(Ui ui, SettingsSwitchRow row)
    {
        if (row.Tab != _tab)
        {
            return;
        }

        var id = Ui.Id(Ui.Id("settings.game"), row.Switch.Index);
        if (Switch(ui, id, row.Label, row.Hint, _settings.IsOn(row.Switch)))
        {
            _settings.SetSwitch(row.Switch, !_settings.IsOn(row.Switch));
        }
    }

    private void GameSlider(Ui ui, SettingsSliderRow row)
    {
        if (row.Tab != _tab)
        {
            return;
        }

        // Its own seed, so no slider's id is a switch's.
        var id = Ui.Id(Ui.Id("settings.game.value"), row.Value.Index);
        var value = _settings.ValueOf(row.Value);
        var moved = SliderRow(
            ui,
            id,
            row.Label,
            row.Hint,
            value,
            _settings.LeastOf(row.Value),
            _settings.MostOf(row.Value),
            ValueText(id, value, row.Text)
        );
        if (moved is { } to)
        {
            _settings.SetValue(row.Value, to);
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
        Func<int, float, string> text,
        UiMarks marks = UiMarks.None
    )
    {
        if (SliderRow(ui, id, name, hint, value, least, most, ValueText(id, value, text), marks) is { } moved)
        {
            Set(id, moved);
        }
    }

    /// <summary>
    /// A row of a name, a value's text, and a slider; the value the slider moved to, or null. A disabled row's name is dim
    /// and its slider does not move.
    /// </summary>
    private float? SliderRow(
        Ui ui,
        int id,
        string name,
        string hint,
        float value,
        float least,
        float most,
        string valueText,
        UiMarks marks = UiMarks.None
    )
    {
        _hints[id] = hint;
        using (ui.Row(0, Row()))
        {
            var disabled = (marks & UiMarks.Disabled) != 0;
            ui.Text(
                name,
                disabled ? UiText.Body with { Tone = UiTone.Dim } : UiText.Body,
                new UiBox { Width = UiSize.Grow() }
            );
            ui.Text(valueText, UiText.Body with { Tone = UiTone.Dim });
            if (
                ui.Slider(id, value, least, most, UiBox.Fixed(ControlWidth, ControlHeight), marks) is
                { Changed: true } moved
            )
            {
                return moved.Value;
            }
        }

        return null;
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
        else if (id == s_musicVolume)
        {
            _settings.SetMusicVolume(value);
        }
        else if (id == s_effectsVolume)
        {
            _settings.SetEffectsVolume(value);
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
        else if (id == s_quality)
        {
            // Custom, at the slider's end, changes nothing: it shows that the values match no preset.
            _settings.SetGraphicsPreset((GraphicsPreset)(int)MathF.Round(value));
        }
        else if (id == s_objectDetail)
        {
            // In half pixels.
            _settings.SetObjectDetail(MathF.Round(ObjectDetailStep(value) * 2) / 2);
        }
        else if (id == s_effectsDetail)
        {
            _settings.SetEffectsDetail(MathF.Round(value * 20) / 20);
        }
        else if (id == s_renderScale)
        {
            _settings.SetRenderScale(MathF.Round(value * 20) / 20);
        }
        else if (id == s_screen)
        {
            _settings.SetScreen(_screens[Math.Clamp((int)MathF.Round(value), 0, _screens.Count - 1)]);
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

    /// <summary>A game slider's text, made again only when the value changes (the game's function runs then alone).</summary>
    private string ValueText(int id, float value, Func<float, string> text)
    {
        if (_values.TryGetValue(id, out var known) && known.Value == value)
        {
            return known.Text;
        }

        var made = text(value);
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

    private static string Quality(int id, float value) =>
        GraphicsPresets.Label((GraphicsPreset)(int)MathF.Round(value));

    /// <summary>The object detail's name at a slider step: High at 1 pixel, Medium up to 2.5, Low above.</summary>
    private static string ObjectDetail(int id, float step)
    {
        var pixels = ObjectDetailStep(step);
        return pixels < 1.5f ? "High"
            : pixels <= 2.5f ? "Medium"
            : "Low";
    }

    /// <summary>
    /// The object detail's pixels at a slider step, and the step at some pixels: the slider runs the other way, so its
    /// right end is the finest.
    /// </summary>
    private static float ObjectDetailStep(float value) =>
        PlayerSettings.SliderFinestObjectDetail + PlayerSettings.SliderCoarsestObjectDetail - value;

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
