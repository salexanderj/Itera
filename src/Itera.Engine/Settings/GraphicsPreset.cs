namespace Itera.Engine.Settings;

/// <summary>
/// The settings' Quality: a preset that sets the graphics settings in its table at once
/// (<see cref="PlayerSettings.SetGraphicsPreset"/>), or Custom when they match no preset.
/// </summary>
public enum GraphicsPreset
{
    /// <summary>SMAA, shadows, object detail 2 or the game's coarser default, effects 50 %.</summary>
    Low,

    /// <summary>MSAA 2x, shadows, object detail 2 or the game's coarser default, effects 75 %.</summary>
    Medium,

    /// <summary>MSAA 4x, shadows, object detail 1 or the game's coarser default, effects 100 %.</summary>
    High,

    /// <summary>The settings match no preset.</summary>
    Custom,
}

/// <summary>The presets' names, as the menu and the console show them.</summary>
public static class GraphicsPresets
{
    private static readonly string[] s_labels = ["Low", "Medium", "High", "Custom"];
    private static readonly string[] s_consoleNames = ["low", "medium", "high", "custom"];

    /// <summary>The name the menu and <c>settings.json</c> give <paramref name="preset"/>. Allocates nothing.</summary>
    public static string Label(GraphicsPreset preset) => s_labels[Math.Clamp((int)preset, 0, s_labels.Length - 1)];

    /// <summary>The console's name for <paramref name="preset"/>. Allocates nothing.</summary>
    public static string ConsoleName(GraphicsPreset preset) =>
        s_consoleNames[Math.Clamp((int)preset, 0, s_consoleNames.Length - 1)];
}
