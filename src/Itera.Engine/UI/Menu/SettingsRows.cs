using Itera.Engine.Settings;

namespace Itera.Engine.UI.Menu;

/// <summary>A game's switch shown as a row of the engine's Settings page.</summary>
/// <param name="Switch">The switch (<see cref="PlayerSettings.RegisterSwitch"/>).</param>
/// <param name="Label">The row's name.</param>
/// <param name="Hint">What it does, shown while the pointer is on it.</param>
/// <param name="Tab">The tab it is on: the engine's "Game", "Graphics", or "Sound", or one of the game's own.</param>
public sealed record SettingsSwitchRow(GameSwitch Switch, string Label, string Hint, string Tab);

/// <summary>A game's number setting shown as a slider row of the engine's Settings page.</summary>
/// <param name="Value">The value (<see cref="PlayerSettings.RegisterValue"/>); its bounds are the slider's.</param>
/// <param name="Label">The row's name.</param>
/// <param name="Hint">What it does, shown while the pointer is on it.</param>
/// <param name="Tab">The tab it is on: the engine's "Game", "Graphics", or "Sound", or one of the game's own.</param>
/// <param name="Text">Gives the text shown for a value, such as <c>v =&gt; $"{v:P0}"</c>.</param>
public sealed record SettingsSliderRow(
    GameValue Value,
    string Label,
    string Hint,
    string Tab,
    Func<float, string> Text
);

/// <summary>Where a game row is among the rows added: a switch or a slider, and its place in that list.</summary>
internal readonly record struct SettingsGameRow(bool IsSlider, int Index);

/// <summary>
/// A game's own rows on the Settings page (<see cref="SettingsPage"/>): its switches and sliders, each on the engine's
/// "Game", "Sound", or "Graphics" tab after the engine's rows, or on a tab of the game's own (shown before Keybindings,
/// in the order first named). A tab shows its rows in the order they were added. A game adds them once a run, in its
/// <c>IRunStartup</c>, after registering the switch or value. Run-wide; main thread.
/// </summary>
public sealed class SettingsRows
{
    /// <summary>The engine's Game tab.</summary>
    public const string GameTab = "Game";

    /// <summary>The engine's Graphics tab.</summary>
    public const string GraphicsTab = "Graphics";

    /// <summary>The engine's Sound tab.</summary>
    public const string SoundTab = "Sound";

    /// <summary>The engine's Keybindings tab.</summary>
    public const string KeysTab = "Keybindings";

    private readonly List<SettingsSwitchRow> _switches = [];
    private readonly List<SettingsSliderRow> _sliders = [];
    private readonly List<SettingsGameRow> _order = [];
    private readonly List<string> _tabs = [];

    /// <summary>The switches added, in order.</summary>
    public IReadOnlyList<SettingsSwitchRow> Switches => _switches;

    /// <summary>The sliders added, in order.</summary>
    public IReadOnlyList<SettingsSliderRow> Sliders => _sliders;

    /// <summary>Every switch and slider added, in the order added.</summary>
    internal IReadOnlyList<SettingsGameRow> Order => _order;

    /// <summary>The game's own tabs, in the order first named.</summary>
    public IReadOnlyList<string> GameTabs => _tabs;

    /// <summary>Adds a row for <paramref name="which"/> on <paramref name="tab"/>.</summary>
    public void AddSwitch(GameSwitch which, string label, string hint, string tab = GameTab)
    {
        Check(label, hint, tab);
        _order.Add(new SettingsGameRow(false, _switches.Count));
        _switches.Add(new SettingsSwitchRow(which, label, hint, tab));
        AddTab(tab);
    }

    /// <summary>
    /// Adds a slider row for <paramref name="which"/> on <paramref name="tab"/>. The slider runs from the value's least to
    /// its most (<see cref="PlayerSettings.LeastOf"/>, <see cref="PlayerSettings.MostOf"/>). <paramref name="text"/> gives
    /// the text shown beside it for a value; the page calls it only when the value changes.
    /// </summary>
    public void AddSlider(GameValue which, string label, string hint, string tab, Func<float, string> text)
    {
        Check(label, hint, tab);
        ArgumentNullException.ThrowIfNull(text);
        _order.Add(new SettingsGameRow(true, _sliders.Count));
        _sliders.Add(new SettingsSliderRow(which, label, hint, tab, text));
        AddTab(tab);
    }

    private static void Check(string label, string hint, string tab)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentException.ThrowIfNullOrWhiteSpace(tab);
        if (tab == KeysTab)
        {
            throw new ArgumentException("The Keybindings tab holds keys only.", nameof(tab));
        }
    }

    private void AddTab(string tab)
    {
        if (tab != GameTab && tab != GraphicsTab && tab != SoundTab && !_tabs.Contains(tab))
        {
            _tabs.Add(tab);
        }
    }
}
