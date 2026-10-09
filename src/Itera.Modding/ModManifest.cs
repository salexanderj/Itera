namespace Itera.Modding;

/// <summary>Metadata describing a mod. Read from <c>mod.json</c> in the mod's folder.</summary>
public sealed record ModManifest
{
    /// <summary>Stable identifier. Lower-case letters, digits, dots, underscores, and hyphens only.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Mod version string, semantic versioning recommended.</summary>
    public required string Version { get; init; }

    /// <summary>File name of the assembly that contains the <see cref="IGameMod"/> type, relative to the mod folder.</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>Full name of the <see cref="IGameMod"/> type. When null, the assembly must contain exactly one.</summary>
    public string? EntryType { get; init; }

    /// <summary>
    /// Contract version the mod was built against. Must equal <see cref="ModdingApi.Version"/>; a manifest that leaves
    /// it out (0) fails the check, so an old mod built before the field never passes as current.
    /// </summary>
    public int ApiVersion { get; init; }

    /// <summary>Ids of mods this mod needs. Reserved; not enforced yet.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}
