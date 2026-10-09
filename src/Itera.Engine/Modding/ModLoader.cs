using System.Runtime.CompilerServices;
using Itera.Engine.Diagnostics;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Modding;

/// <summary>Discovers mods under the mods folder, loads each into its own load context, and drives their lifecycle.</summary>
public sealed partial class ModLoader : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ModLoader> _logger;
    private readonly IServiceProvider _services;
    private readonly ModDispatcher _dispatcher;
    private HostSharedAssemblies? _shared;
    private bool _disposed;

    public ModLoader(
        IOptions<EngineOptions> options,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        ILogger<ModLoader> logger,
        SystemTimes? times = null
    )
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
        _services = services;
        _dispatcher = new ModDispatcher(logger, times);
        ModsDirectory = Path.GetFullPath(
            options.Value.Mods.Directory ?? Path.Combine(AppContext.BaseDirectory, "Mods")
        );
    }

    /// <summary>Absolute path of the folder scanned for mods.</summary>
    public string ModsDirectory { get; }

    /// <summary>
    /// False when the most recently unloaded mod could not be collected, which means something still references it.
    /// True after a clean unload or when no mod has been unloaded yet.
    /// </summary>
    public bool LastUnloadWasCollected { get; private set; } = true;

    /// <summary>Ids of the mods currently loaded, in load order.</summary>
    public IReadOnlyList<string> LoadedModIds => _dispatcher.Mods.Select(m => m.Manifest.Id).ToArray();

    /// <summary>Returns every immediate sub-folder of <paramref name="root"/> that contains a manifest, in ordinal order.</summary>
    public static IReadOnlyList<string> DiscoverModDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, ModManifestReader.FileName)))
            .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Loads every discovered mod. Never throws; failures are logged and counted.</summary>
    public ModLoadSummary LoadAll()
    {
        _shared ??= HostSharedAssemblies.FromHost();

        var directories = DiscoverModDirectories(ModsDirectory);
        if (directories.Count == 0)
        {
            LogNoMods(ModsDirectory);
            return new ModLoadSummary(0, 0, 0);
        }

        var loaded = 0;
        var failed = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            if (TryLoad(directory, seenIds))
            {
                loaded++;
            }
            else
            {
                failed++;
            }
        }

        LogSummary(ModsDirectory, directories.Count, loaded, failed);
        return new ModLoadSummary(directories.Count, loaded, failed);
    }

    /// <summary>The mod and callback running now ("itera.samplemod OnUpdate"), or null. Any thread; allocates.</summary>
    public string? CurrentCall => _dispatcher.CurrentCall;

    public void Update(in FrameTime time)
    {
        _dispatcher.Update(in time);
    }

    public void FixedUpdate(in FixedStepTime time)
    {
        _dispatcher.FixedUpdate(in time);
    }

    /// <summary>Tells every mod a match started; <paramref name="match"/> resolves its services.</summary>
    public void MatchStarted(IServiceProvider match)
    {
        _dispatcher.MatchStarted(match);
    }

    /// <summary>Tells every mod the match ended, before its services are freed.</summary>
    public void MatchEnded()
    {
        _dispatcher.MatchEnded();
    }

    /// <summary>Tells every mod the game is exiting. Call before <see cref="UnloadAll"/>.</summary>
    public void NotifyGameExit()
    {
        _dispatcher.NotifyGameExit();
    }

    /// <summary>Unloads every mod in reverse load order.</summary>
    public void UnloadAll()
    {
        while (_dispatcher.Mods.Count > 0)
        {
            var (id, reference) = UnloadAt(_dispatcher.Mods.Count - 1);
            WarnIfStillReferenced(id, reference);
        }
    }

    /// <summary>Unloads one mod by id. Returns false if no such mod is loaded.</summary>
    public bool Unload(string modId)
    {
        var index = IndexOf(modId);
        if (index < 0)
        {
            return false;
        }

        var (id, reference) = UnloadAt(index);
        WarnIfStillReferenced(id, reference);
        return true;
    }

    /// <summary>
    /// Kept out of <see cref="Unload(string)"/> on purpose: in unoptimised builds the JIT keeps every temporary of a
    /// method alive until it returns, and a <see cref="LoadedMod"/> temporary would pin the mod during the unload check.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int IndexOf(string modId)
    {
        for (var i = 0; i < _dispatcher.Mods.Count; i++)
        {
            if (string.Equals(_dispatcher.Mods[i].Manifest.Id, modId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnloadAll();
    }

    private bool TryLoad(string directory, HashSet<string> seenIds)
    {
        var manifestPath = Path.Combine(directory, ModManifestReader.FileName);
        ModLoadContext? loadContext = null;

        try
        {
            LogDiscovered(directory);
            var manifest = ModManifestReader.ReadFile(manifestPath);
            var errors = ModManifestReader.Validate(manifest, directory);
            if (errors.Count > 0)
            {
                LogInvalidManifest(manifestPath, string.Join(" ", errors));
                return false;
            }

            if (!seenIds.Add(manifest.Id))
            {
                LogDuplicateId(manifest.Id, directory);
                return false;
            }

            var entryPath = ModManifestReader.GetEntryAssemblyPath(manifest, directory);
            var contextLogger = _loggerFactory.CreateLogger(typeof(ModLoadContext).FullName!);
            loadContext = new ModLoadContext(manifest.Id, entryPath, _shared!, contextLogger);
            var assembly = loadContext.LoadFromAssemblyPath(entryPath);

            var entryType = manifest.EntryType is { } typeName
                ? assembly.GetType(typeName, throwOnError: true)!
                : FindSingleModType(assembly, manifest.Id);

            if (Activator.CreateInstance(entryType) is not IGameMod instance)
            {
                throw new InvalidOperationException(
                    $"Type '{entryType.FullName}' does not implement {nameof(IGameMod)}."
                );
            }

            var modLogger = _loggerFactory.CreateLogger($"Mod.{manifest.Id}");
            var context = new ModContext(manifest, directory, modLogger, _services);
            var mod = new LoadedMod(manifest, directory, loadContext, instance, context);
            _dispatcher.Add(mod);

            if (!_dispatcher.Load(mod))
            {
                _dispatcher.Remove(mod);
                loadContext.Unload();
                return false;
            }

            LogLoaded(manifest.Id, manifest.Name, manifest.Version, directory);
            return true;
        }
        catch (Exception ex)
        {
            LogLoadFailed(ex, directory);
            loadContext?.Unload();
            return false;
        }
    }

    private static Type FindSingleModType(System.Reflection.Assembly assembly, string modId)
    {
        var candidates = assembly
            .GetExportedTypes()
            .Where(t => typeof(IGameMod).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToArray();

        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                $"Mod '{modId}' has no public type implementing {nameof(IGameMod)}. Set EntryType in the manifest or add one."
            ),
            _ => throw new InvalidOperationException(
                $"Mod '{modId}' has {candidates.Length} public types implementing {nameof(IGameMod)}. Set EntryType in the manifest."
            ),
        };
    }

    /// <summary>
    /// Unloads the mod at <paramref name="index"/> and returns a weak reference to its load context.
    /// Kept in its own non-inlined method so no local here keeps the mod alive during the check that follows.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (string Id, WeakReference? Reference) UnloadAt(int index)
    {
        var mod = _dispatcher.Mods[index];
        var id = mod.Manifest.Id;
        _dispatcher.NotifyUnloaded(mod);
        _dispatcher.Remove(mod);

        var loadContext = mod.LoadContext;
        if (loadContext is null)
        {
            return (id, null);
        }

        // Non-generic WeakReference on purpose: IsAlive never materialises the target, while
        // WeakReference<T>.TryGetTarget writes it to a stack slot that keeps it alive during the checks below.
        var reference = new WeakReference(loadContext, trackResurrection: true);
        loadContext.Unload();
        LogUnloaded(id);
        return (id, reference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WarnIfStillReferenced(string id, WeakReference? reference)
    {
        if (reference is null)
        {
            LastUnloadWasCollected = true;
            return;
        }

        // Unloading is rare, so forcing a few collections here is acceptable. It turns a silent leak into a warning.
        for (var i = 0; i < 10 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        LastUnloadWasCollected = !reference.IsAlive;
        if (!LastUnloadWasCollected)
        {
            LogStillReferenced(id);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Mods: no mods found under {Directory}")]
    private partial void LogNoMods(string directory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mods: found manifest in {Directory}")]
    private partial void LogDiscovered(string directory);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Mods: directory {Directory}, found {Found}, loaded {Loaded}, failed {Failed}"
    )]
    private partial void LogSummary(string directory, int found, int loaded, int failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded mod {ModId} ({Name} {Version}) from {Directory}")]
    private partial void LogLoaded(string modId, string name, string version, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unloaded mod {ModId}")]
    private partial void LogUnloaded(string modId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod {ModId} did not unload; something still references it")]
    private partial void LogStillReferenced(string modId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invalid manifest {Path}: {Errors}")]
    private partial void LogInvalidManifest(string path, string errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Duplicate mod id {ModId} in {Directory}; skipped")]
    private partial void LogDuplicateId(string modId, string directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mod in {Directory} failed to load")]
    private partial void LogLoadFailed(Exception exception, string directory);
}

/// <summary>Counts from one <see cref="ModLoader.LoadAll"/> call.</summary>
/// <param name="Found">Folders with a manifest.</param>
/// <param name="Loaded">Mods that loaded and ran <see cref="IGameMod.OnModLoaded"/> without error.</param>
/// <param name="Failed">Mods that were skipped or threw.</param>
public readonly record struct ModLoadSummary(int Found, int Loaded, int Failed);
