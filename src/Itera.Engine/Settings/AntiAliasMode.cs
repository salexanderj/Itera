namespace Itera.Engine.Settings;

/// <summary>
/// The settings' Anti-aliasing: none, SMAA 1x on a world drawn with one
/// sample a pixel, or the world drawn with 2, 4, or 8 samples a pixel (MSAA, which SMAA does not follow).
/// </summary>
public enum AntiAliasMode
{
    Off,
    Smaa,
    Msaa2,
    Msaa4,
    Msaa8,
}

/// <summary>The modes' names and samples.</summary>
public static class AntiAliasModes
{
    /// <summary>The name the menu shows.</summary>
    public static string Label(AntiAliasMode mode) =>
        mode switch
        {
            AntiAliasMode.Smaa => "SMAA",
            AntiAliasMode.Msaa2 => "MSAA 2x",
            AntiAliasMode.Msaa4 => "MSAA 4x",
            AntiAliasMode.Msaa8 => "MSAA 8x",
            _ => "Off",
        };

    /// <summary>The name the console's <c>settings aa</c> takes and prints.</summary>
    public static string ConsoleName(AntiAliasMode mode) =>
        mode switch
        {
            AntiAliasMode.Smaa => "smaa",
            AntiAliasMode.Msaa2 => "2x",
            AntiAliasMode.Msaa4 => "4x",
            AntiAliasMode.Msaa8 => "8x",
            _ => "off",
        };

    /// <summary>The world's samples a pixel the mode draws with.</summary>
    public static int Samples(AntiAliasMode mode) =>
        mode switch
        {
            AntiAliasMode.Msaa2 => 2,
            AntiAliasMode.Msaa4 => 4,
            AntiAliasMode.Msaa8 => 8,
            _ => 1,
        };

    /// <summary>1, 2, 4, or 8: the most of those not above <paramref name="samples"/>.</summary>
    public static int SnapSamples(int samples) =>
        samples >= 8 ? 8
        : samples >= 4 ? 4
        : samples >= 2 ? 2
        : 1;
}
