using Itera.Engine.Settings;

namespace Itera.Engine.UI.Menu;

/// <summary>A game's switch shown as a row of the engine's Settings page.</summary>
/// <param name="Switch">The switch (<see cref="PlayerSettings.RegisterSwitch"/>).</param>
/// <param name="Label">The row's name.</param>
/// <param name="Hint">What it does, shown while the pointer is on it.</param>
/// <param name="Tab">The tab it is on: the engine's "Game" or "Graphics", or one of the game's own.</param>
public sealed record SettingsSwitchRow(GameSwitch Switch, string Label, string Hint, string Tab);

/// <summary>
/// A game's own rows on the Settings page (<see cref="SettingsPage"/>): its switches, each on the engine's "Game" or
/// "Graphics" tab after the engine's rows, or on a tab of the game's own (shown before Keybindings, in the order first
/// named). A game adds them once a run, in its <c>IRunStartup</c>, after registering the switch. Run-wide; main thread.
/// </summary>
public sealed class SettingsRows
{
    /// <summary>The engine's Game tab.</summary>
    public const string GameTab = "Game";

    /// <summary>The engine's Graphics tab.</summary>
    public const string GraphicsTab = "Graphics";

    /// <summary>The engine's Keybindings tab.</summary>
    public const string KeysTab = "Keybindings";

    private readonly List<SettingsSwitchRow> _switches = [];
    private readonly List<string> _tabs = [];

    /// <summary>The switches added, in order.</summary>
    public IReadOnlyList<SettingsSwitchRow> Switches => _switches;

    /// <summary>The game's own tabs, in the order first named.</summary>
    public IReadOnlyList<string> GameTabs => _tabs;

    /// <summary>Adds a row for <paramref name="which"/> on <paramref name="tab"/>.</summary>
    public void AddSwitch(GameSwitch which, string label, string hint, string tab = GameTab)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentException.ThrowIfNullOrWhiteSpace(tab);
        if (tab == KeysTab)
        {
            throw new ArgumentException("The Keybindings tab holds keys only.", nameof(tab));
        }

        _switches.Add(new SettingsSwitchRow(which, label, hint, tab));
        if (tab != GameTab && tab != GraphicsTab && !_tabs.Contains(tab))
        {
            _tabs.Add(tab);
        }
    }
}
