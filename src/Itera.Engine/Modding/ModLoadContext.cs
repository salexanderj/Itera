using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Modding;

/// <summary>
/// Collectible load context for one mod. Host-shared assemblies resolve to the host's copies;
/// everything else resolves from the mod folder through its deps.json.
/// </summary>
internal sealed partial class ModLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HostSharedAssemblies _shared;
    private readonly ILogger _logger;
    private readonly string _modId;

    /// <param name="modId">Mod id, used in the context name.</param>
    /// <param name="entryAssemblyPath">Path of the mod's entry assembly; its deps.json drives resolution.</param>
    /// <param name="shared">Assemblies that resolve to the host's copies.</param>
    /// <param name="logger">Receives one Trace line per resolution decision. Null disables that.</param>
    public ModLoadContext(string modId, string entryAssemblyPath, HostSharedAssemblies shared, ILogger? logger = null)
        : base($"Mod:{modId}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _shared = shared;
        _logger = logger ?? NullLogger.Instance;
        _modId = modId;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name ?? assemblyName.FullName;
        if (assemblyName.Name is { } simpleName && _shared.Contains(simpleName))
        {
            LogSharedWithHost(_modId, name);
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            LogNotResolved(_modId, name);
            return null;
        }

        LogResolved(_modId, name, path);
        return LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            LogNativeNotResolved(_modId, unmanagedDllName);
            return nint.Zero;
        }

        LogNativeResolved(_modId, unmanagedDllName, path);
        return LoadUnmanagedDllFromPath(path);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Mod {ModId} assembly {Assembly}: shared with host")]
    private partial void LogSharedWithHost(string modId, string assembly);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Mod {ModId} assembly {Assembly}: loaded from {Path}")]
    private partial void LogResolved(string modId, string assembly, string path);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Mod {ModId} assembly {Assembly}: not in the mod folder; deferring to the default context"
    )]
    private partial void LogNotResolved(string modId, string assembly);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Mod {ModId} native library {Library}: loaded from {Path}")]
    private partial void LogNativeResolved(string modId, string library, string path);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Mod {ModId} native library {Library}: not in the mod folder; deferring to the default probing"
    )]
    private partial void LogNativeNotResolved(string modId, string library);
}
