using Itera.Modding;

namespace Itera.Engine.Modding;

/// <summary>Per-mod state held by the loader.</summary>
internal sealed class LoadedMod
{
    public LoadedMod(
        ModManifest manifest,
        string directory,
        ModLoadContext? loadContext,
        IGameMod instance,
        ModContext context
    )
    {
        Manifest = manifest;
        Directory = directory;
        LoadContext = loadContext;
        Instance = instance;
        Context = context;
    }

    public ModManifest Manifest { get; }

    public string Directory { get; }

    /// <summary>Null for mods that live in the host process, such as test doubles.</summary>
    public ModLoadContext? LoadContext { get; }

    public IGameMod Instance { get; }

    public ModContext Context { get; }

    /// <summary>Set after the mod throws. Faulted mods receive no more update calls.</summary>
    public bool Faulted { get; set; }

    /// <summary>Its <c>OnFixedUpdate</c>'s entry in <see cref="Diagnostics.SystemTimes"/>; -1 when not timed.</summary>
    public int FixedTiming { get; set; } = -1;

    /// <summary>Its longest callback so far that passed the slow-call limit, in Stopwatch ticks.</summary>
    public long LongestSlowTicks { get; set; }
}
