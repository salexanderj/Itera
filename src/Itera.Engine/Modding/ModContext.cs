using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Modding;

/// <summary>What a loaded mod receives from the host.</summary>
internal sealed class ModContext : IModContext
{
    public ModContext(ModManifest manifest, string modDirectory, ILogger logger, IServiceProvider services)
    {
        Manifest = manifest;
        ModDirectory = modDirectory;
        Logger = logger;
        Services = services;
    }

    public ModManifest Manifest { get; }

    public string ModDirectory { get; }

    public ILogger Logger { get; }

    public IServiceProvider Services { get; }
}
