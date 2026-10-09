using System.Globalization;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using SkiaSharp;

namespace Itera.Engine.UI.Menu;

/// <summary>The settings page's tabs.</summary>
public enum SettingsTab
{
    /// <summary>The volume, the mouse, the field of view, VSync, and full screen.</summary>
    Game,

    /// <summary>Dynamic render scaling and its threshold, shadows, and anti-aliasing.</summary>
    Graphics,

    /// <summary>The keys and mouse buttons of each action.</summary>
    Keybindings,
}

/// <summary>
/// The settings page: three tabs under the title, Game, Graphics, and Keybindings, each
/// a row per setting with its name on the left and its slider or switch on the right, then Back. The page opens on
/// Game. A setting changes at once; leaving the page writes them (<see cref="PlayerSettings.Save"/>). Keybindings lists
/// every row of <see cref="KeyBindings"/>, <see cref="VisibleKeyRows"/> at a time (the wheel scrolls): a fixed row
/// shows its keys, an action a button with its binding. Clicking the button waits for the next key or mouse button,
/// which binds it (<see cref="BindingPressed"/>; another action that can work at the same time and has it loses it);
/// Escape stops waiting, and Reset keys puts every binding back.
/// </summary>
public sealed partial class MenuLayer
{
    // The settings, by id: sliders, then switches from DynamicScalingRow on but for AntiAliasingRow, a slider through its
    // choices. A tab lists some of them in its own order.
    private const int VolumeRow = 0;
    private const int SensitivityRow = 1;
    private const int FieldOfViewRow = 2;
    private const int ScalingThresholdRow = 3;
    private const int DynamicScalingRow = 4;
    private const int ShadowsRow = 5;
    private const int VSyncRow = 6;
    private const int FullscreenRow = 7;
    private const int AntiAliasingRow = 8;

    private static readonly int[] s_gameRows = [VolumeRow, SensitivityRow, FieldOfViewRow, VSyncRow, FullscreenRow];
    private static readonly int[] s_graphicsRows =
    [
        DynamicScalingRow,
        ScalingThresholdRow,
        ShadowsRow,
        AntiAliasingRow,
    ];

    /// <summary>
    /// The width of the Game and Graphics tabs' rows, in units of a hundredth of the screen's height: the longest name,
    /// "Dynamic scaling threshold" (about 44), its value, and the 26 of its control (64 until 2026-10-05, when that name
    /// ran into its value). The Keybindings tab's rows stay 64.
    /// </summary>
    public const float SettingRowUnits = 88;

    /// <summary>The Keybindings tab's rows shown at once; the wheel scrolls through more.</summary>
    public const int VisibleKeyRows = 8;

    private SettingsTab _settingsTab;

    /// <summary>The action waiting for a key to bind, or -1.</summary>
    private int _bindAction = -1;
    private int _keyScroll;
    private WheelSteps _keyWheel;
    private int _keysVersion = -1;
    private string _bindHint = "";

    /// <summary>The settings page's tab shown.</summary>
    public SettingsTab SettingsTab => _settingsTab;

    /// <summary>True while the Keybindings tab waits for a key or mouse button to bind.</summary>
    public bool CapturingBinding =>
        _bindAction >= 0 && View == MenuView.Settings && _settingsTab == SettingsTab.Keybindings;

    private KeyBindings Keys => _settings.Keys;

    /// <summary>
    /// The key or button pressed while waiting: it binds the action waiting, unless it is Escape or ` (the menu's and the
    /// console's), which leave it waiting; <see cref="InputBinding.None"/> stops waiting. Makes no click.
    /// </summary>
    public void BindingPressed(InputBinding binding)
    {
        if (!CapturingBinding || KeyBindings.IsReserved(binding))
        {
            return;
        }

        var action = _bindAction;
        StopBinding();
        if (!binding.IsNone)
        {
            Keys.Set(new KeyAction(action), binding);
        }
    }

    /// <summary>Stops waiting for a key to bind, and shows the binding again.</summary>
    private void StopBinding()
    {
        _bindAction = -1;
        _laidOutView = MenuView.None;
        _dirty = true;
    }

    /// <summary>A binding changed: the Keybindings tab is laid out again.</summary>
    private void FollowKeys()
    {
        if (Keys.Version != _keysVersion)
        {
            _keysVersion = Keys.Version;
            _laidOutView = MenuView.None;
            _dirty = true;
        }
    }

    /// <summary>The wheel on the Keybindings tab moves its rows.</summary>
    private void ScrollKeys(float steps)
    {
        var most = Math.Max(0, Keys.Rows.Count - VisibleKeyRows);
        var scroll = Math.Clamp(_keyScroll - _keyWheel.Take(steps), 0, most);
        if (scroll != _keyScroll)
        {
            _keyScroll = scroll;
            _laidOutView = MenuView.None;
            _dirty = true;
        }
    }

    /// <summary>A binding's button: wait for the next key or mouse button for action <paramref name="action"/>.</summary>
    private void StartBinding(int action)
    {
        _bindAction = action;
        _bindHint =
            $"Press a key or mouse button for {Keys.Rows[action].Label}; Escape keeps {Keys[new KeyAction(action)].Label}.";
        _laidOutView = MenuView.None;
        _dirty = true;
    }

    /// <summary>The settings of the tab shown, top to bottom.</summary>
    private int[] TabRows =>
        _settingsTab switch
        {
            SettingsTab.Game => s_gameRows,
            SettingsTab.Graphics => s_graphicsRows,
            _ => [],
        };

    /// <summary>The settings page's elements: the tabs, the tab's rows, then Back.</summary>
    private void LayoutSettings(int width, float unit)
    {
        LayoutTabs(width, unit);
        if (_settingsTab == SettingsTab.Keybindings)
        {
            LayoutKeys(width, unit);
            return;
        }

        var rowWidth = SettingRowUnits * unit;
        var left = (width - rowWidth) / 2;
        var rows = TabRows;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var top = SettingTop(i) * unit;
            var control = new SKRect(left + rowWidth - 26 * unit, top + 0.75f * unit, left + rowWidth, top + 6 * unit);
            var toggle = IsSwitch(row);
            _elements.Add(
                new Element(
                    control,
                    toggle ? (SettingOn(row) ? "On" : "Off") : "",
                    toggle ? MenuAction.Toggle : MenuAction.Slider,
                    true,
                    SettingHint(row),
                    row
                )
            );
        }

        var y = (SettingTop(Math.Max(rows.Length, 5)) + 1) * unit;
        Button(ref y, width, unit, "Back", MenuAction.Back, true, "Keeps the settings for the next start too.");
    }

    /// <summary>
    /// The Keybindings tab: a button for each action shown with its binding ("Press a key…" while it waits), then Reset
    /// keys and Back side by side.
    /// </summary>
    private void LayoutKeys(int width, float unit)
    {
        var rowWidth = 64 * unit;
        var left = (width - rowWidth) / 2;
        var rows = Keys.Rows;
        _keyScroll = Math.Clamp(_keyScroll, 0, Math.Max(0, rows.Count - VisibleKeyRows));
        var shown = Math.Min(VisibleKeyRows, rows.Count - _keyScroll);
        for (var i = 0; i < shown; i++)
        {
            var index = _keyScroll + i;
            if (rows[index].Fixed)
            {
                continue;
            }

            var top = KeyTop(i) * unit;
            var control = new SKRect(
                left + rowWidth - 26 * unit,
                top + 0.5f * unit,
                left + rowWidth,
                top + 5.5f * unit
            );
            var label = index == _bindAction ? "Press a key…" : Keys[new KeyAction(index)].Label;
            _elements.Add(new Element(control, label, MenuAction.Bind, true, rows[index].Hint, index));
        }

        var buttonWidth = 20 * unit;
        var gap = 2 * unit;
        var y = 83 * unit;
        var x = (width - (2 * buttonWidth + gap)) / 2;
        _elements.Add(
            new Element(
                new SKRect(x, y, x + buttonWidth, y + 5.5f * unit),
                "Reset keys",
                MenuAction.ResetKeys,
                true,
                "Every action back to its first key."
            )
        );
        x += buttonWidth + gap;
        _elements.Add(
            new Element(
                new SKRect(x, y, x + buttonWidth, y + 5.5f * unit),
                "Back",
                MenuAction.Back,
                true,
                "Keeps the settings for the next start too."
            )
        );
    }

    /// <summary>The top of the Keybindings tab's row <paramref name="index"/> shown, in units.</summary>
    private static float KeyTop(int index) => 31 + index * 6;

    /// <summary>The three tabs in a row under the title; the one shown is drawn sunk.</summary>
    private void LayoutTabs(int width, float unit)
    {
        var tabWidth = 22 * unit;
        var gap = 1.5f * unit;
        var left = (width - (3 * tabWidth + 2 * gap)) / 2;
        for (var tab = 0; tab < 3; tab++)
        {
            var x = left + tab * (tabWidth + gap);
            var rect = new SKRect(x, 23 * unit, x + tabWidth, 28.5f * unit);
            var (label, hint) = (SettingsTab)tab switch
            {
                SettingsTab.Game => ("Game", "The volume, the mouse, the field of view, and the screen."),
                SettingsTab.Graphics => ("Graphics", "How the world is drawn."),
                _ => ("Keybindings", "The keys and mouse buttons of each action."),
            };
            _elements.Add(new Element(rect, label, MenuAction.Tab, true, hint, tab, tab == (int)_settingsTab));
        }
    }

    /// <summary>The top of the tab's row <paramref name="index"/>, in units (a hundredth of the screen's height).</summary>
    private static float SettingTop(int index) => 31 + index * 7;

    /// <summary>True for a setting with a switch (On, Off), false for one with a slider.</summary>
    private static bool IsSwitch(int row) => row >= DynamicScalingRow && row != AntiAliasingRow;

    /// <summary>Flips setting <paramref name="row"/>'s switch.</summary>
    private void Toggle(int row)
    {
        switch (row)
        {
            case DynamicScalingRow:
                _settings.SetDynamicScaling(!_settings.DynamicScaling);
                break;
            case ShadowsRow:
                _settings.SetShadows(!_settings.Shadows);
                break;
            case VSyncRow:
                _settings.SetVSync(!_settings.VSync);
                break;
            case FullscreenRow:
                _settings.SetFullscreen(!_settings.Fullscreen);
                break;
        }
    }

    /// <summary>Moves setting <paramref name="row"/>'s slider to the pointer at <paramref name="x"/>.</summary>
    private void Drag(int row, float x)
    {
        foreach (var element in _elements)
        {
            if (element.Action != MenuAction.Slider || element.Row != row)
            {
                continue;
            }

            var t = Math.Clamp((x - element.Rect.Left) / element.Rect.Width, 0, 1);
            var (least, most) = SliderRange(row);
            var value = least + t * (most - least);
            switch (row)
            {
                case VolumeRow:
                    _settings.SetVolume(value);
                    break;
                case SensitivityRow:
                    _settings.SetMouseSensitivity(value);
                    break;
                case FieldOfViewRow:
                    _settings.SetFieldOfView(MathF.Round(value));
                    break;
                case ScalingThresholdRow:
                    // The slider steps through the offered thresholds, one step each.
                    _settings.SetScalingThreshold(DynamicRenderScale.Thresholds[(int)MathF.Round(value)]);
                    break;
                case AntiAliasingRow:
                    // Off, SMAA, MSAA 2x, 4x, and 8x, one step each.
                    _settings.SetAntiAliasMode((AntiAliasMode)(int)MathF.Round(value));
                    break;
            }

            return;
        }
    }

    private static (float Least, float Most) SliderRange(int row) =>
        row switch
        {
            VolumeRow => (0, 1),
            SensitivityRow => (PlayerSettings.LeastSensitivity, PlayerSettings.MostSensitivity),
            FieldOfViewRow => (PlayerSettings.LeastFieldOfView, PlayerSettings.MostFieldOfView),
            AntiAliasingRow => (0, (int)AntiAliasMode.Msaa8),
            _ => (0, DynamicRenderScale.Thresholds.Length - 1),
        };

    private float SliderValue(int row) =>
        row switch
        {
            VolumeRow => _settings.Volume,
            SensitivityRow => _settings.MouseSensitivity,
            FieldOfViewRow => _settings.FieldOfViewDegrees,
            AntiAliasingRow => (int)_settings.AntiAliasMode,
            _ => ThresholdStep(_settings.ScalingThreshold),
        };

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

    private bool SettingOn(int row) =>
        row switch
        {
            DynamicScalingRow => _settings.DynamicScaling,
            ShadowsRow => _settings.Shadows,
            VSyncRow => _settings.VSync,
            _ => _settings.Fullscreen,
        };

    private static string SettingName(int row) =>
        row switch
        {
            VolumeRow => "Volume",
            SensitivityRow => "Sensitivity",
            FieldOfViewRow => "Field of view",
            DynamicScalingRow => "Dynamic render scaling",
            ScalingThresholdRow => "Dynamic scaling threshold",
            ShadowsRow => "Shadows",
            VSyncRow => "VSync",
            AntiAliasingRow => "Anti-aliasing",
            _ => "Full screen",
        };

    private static string SettingHint(int row) =>
        row switch
        {
            VolumeRow => "How loud the game is.",
            SensitivityRow => "The mouse: how far the view turns as it moves (degrees a pixel).",
            FieldOfViewRow => "How wide the view is, across a 16:9 screen.",
            DynamicScalingRow => "Frames that take too long draw the world smaller, and FSR 1 fills the screen.",
            ScalingThresholdRow =>
                "The frame rate dynamic render scaling holds, or the screen's refresh rate if lower.",
            ShadowsRow => "The sun casts shadows. Off is quicker, and everything facing the sun is lit.",
            VSyncRow => "Frames wait for the screen: no tearing, a little more lag.",
            AntiAliasingRow =>
                "Smooths the steps along edges: SMAA after drawing, or MSAA's 2, 4, or 8 samples a pixel. Off is quickest.",
            _ => "The window covers its screen.",
        };

    /// <summary>A setting's value as the page shows it ("30 %", "0.10", "90°", "120 fps").</summary>
    private string SettingText(int row)
    {
        var value = SliderValue(row);
        return row switch
        {
            SensitivityRow => value.ToString("F2", CultureInfo.InvariantCulture),
            FieldOfViewRow => string.Create(CultureInfo.InvariantCulture, $"{value:F0}°"),
            ScalingThresholdRow => string.Create(CultureInfo.InvariantCulture, $"{_settings.ScalingThreshold:F0} fps"),
            AntiAliasingRow => AntiAliasModes.Label(_settings.AntiAliasMode),
            _ => string.Create(CultureInfo.InvariantCulture, $"{value * 100:F0} %"),
        };
    }

    /// <summary>The tab's names on the left of each row, and each slider's value.</summary>
    private void DrawSettingNames(SKCanvas canvas, int width, float unit)
    {
        var rowWidth = (_settingsTab == SettingsTab.Keybindings ? 64 : SettingRowUnits) * unit;
        var left = (width - rowWidth) / 2;
        var font = _font!;
        var rows = TabRows;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var middle = (SettingTop(i) + 3.4f) * unit;
            var baseline = middle - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
            _text!.Color = _style.Text;
            canvas.DrawText(SettingName(row), left, baseline, SKTextAlign.Left, font, _text);
            if (!IsSwitch(row))
            {
                _text.Color = _style.TextDim;
                var right = left + rowWidth - 27.5f * unit;
                canvas.DrawText(SettingText(row), right, baseline, SKTextAlign.Right, font, _text);
            }
        }

        if (_settingsTab == SettingsTab.Keybindings)
        {
            DrawKeyNames(canvas, left, rowWidth, width, unit);
        }
    }

    /// <summary>The Keybindings tab's labels, the fixed rows' keys, and where the rows shown are in the list.</summary>
    private void DrawKeyNames(SKCanvas canvas, float left, float rowWidth, int width, float unit)
    {
        var font = _font!;
        var rows = Keys.Rows;
        var shown = Math.Min(VisibleKeyRows, rows.Count - _keyScroll);
        for (var i = 0; i < shown; i++)
        {
            var row = rows[_keyScroll + i];
            var middle = (KeyTop(i) + 3) * unit;
            var baseline = middle - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
            _text!.Color = row.Fixed ? _style.TextDim : _style.Text;
            canvas.DrawText(row.Label, left, baseline, SKTextAlign.Left, font, _text);
            if (row.FixedKeys is { } keys)
            {
                // Fixed: its keys where a button would be, with no button.
                canvas.DrawText(keys, left + rowWidth - 13 * unit, baseline, SKTextAlign.Center, font, _text);
            }
        }

        if (rows.Count > VisibleKeyRows)
        {
            var more = string.Create(
                CultureInfo.InvariantCulture,
                $"{_keyScroll + 1}-{_keyScroll + shown} of {rows.Count} (wheel)"
            );
            DrawLine(canvas, more, _style.TextDim, width, 81 * unit);
        }
    }
}
