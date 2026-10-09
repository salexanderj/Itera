namespace Itera.Modding;

/// <summary>Version of the mod contract. A mod's manifest must declare the same value to be loaded.</summary>
public static class ModdingApi
{
    /// <summary>Current contract version. Bump it when a breaking change is made to this assembly.</summary>
    public const int Version = 2;
}
