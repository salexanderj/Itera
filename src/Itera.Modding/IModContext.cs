using Microsoft.Extensions.Logging;

namespace Itera.Modding;

/// <summary>What the host gives a mod when it is loaded.</summary>
public interface IModContext
{
    /// <summary>The manifest read from the mod's <c>mod.json</c>.</summary>
    ModManifest Manifest { get; }

    /// <summary>Absolute path of the folder the mod was loaded from.</summary>
    string ModDirectory { get; }

    /// <summary>Logger whose category is the mod id.</summary>
    ILogger Logger { get; }

    /// <summary>The host's service provider. Resolve engine services from it.</summary>
    IServiceProvider Services { get; }
}
