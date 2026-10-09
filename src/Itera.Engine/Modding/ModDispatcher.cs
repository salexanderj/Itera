using System.Diagnostics;
using Itera.Engine.Diagnostics;
using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Modding;

/// <summary>
/// Invokes mod lifecycle callbacks with error isolation. A mod that throws is marked faulted and stops
/// receiving update calls, but still gets exit and unload calls so it can release resources. Each update call is timed:
/// a mod's <c>OnFixedUpdate</c> goes into the fixed systems' times (<see cref="SystemTimes"/>, the slowest of which the
/// stats line names), and a callback over <see cref="SlowCallMilliseconds"/> that is the mod's longest yet logs a
/// Warning with its name. The call in progress is kept (<see cref="CurrentCall"/>), so a signal that arrives while a mod
/// hangs the loop can name it.
/// </summary>
internal sealed partial class ModDispatcher
{
    /// <summary>A callback longer than this, and the mod's longest so far, logs a Warning.</summary>
    public const double SlowCallMilliseconds = 50;

    private static readonly long s_slowCallTicks = (long)(SlowCallMilliseconds * Stopwatch.Frequency / 1000);

    private readonly List<LoadedMod> _mods = [];
    private readonly ILogger _logger;
    private readonly SystemTimes? _times;
    private volatile LoadedMod? _current;
    private volatile string? _currentCallback;

    /// <param name="logger">Where failures and slow calls are logged.</param>
    /// <param name="times">The fixed systems' times, which each mod's <c>OnFixedUpdate</c> joins; null for none.</param>
    public ModDispatcher(ILogger logger, SystemTimes? times = null)
    {
        _logger = logger;
        _times = times;
    }

    public IReadOnlyList<LoadedMod> Mods => _mods;

    /// <summary>
    /// The mod and callback running now ("itera.samplemod OnUpdate"), or null between calls. Any thread; allocates.
    /// </summary>
    public string? CurrentCall => _current is { } mod ? mod.Manifest.Id + " " + _currentCallback : null;

    public void Add(LoadedMod mod)
    {
        _mods.Add(mod);
        if (_times is not null && mod.FixedTiming < 0)
        {
            mod.FixedTiming = _times.Add("Mod " + mod.Manifest.Id);
        }
    }

    public bool Remove(LoadedMod mod)
    {
        return _mods.Remove(mod);
    }

    /// <summary>Calls <see cref="IGameMod.OnModLoaded"/>. Returns false and marks the mod faulted if it throws.</summary>
    public bool Load(LoadedMod mod)
    {
        try
        {
            mod.Instance.OnModLoaded(mod.Context);
            return true;
        }
        catch (Exception ex)
        {
            Fault(mod, nameof(IGameMod.OnModLoaded), ex);
            return false;
        }
    }

    public void Update(in FrameTime time)
    {
        // By index: a mod can reach ModLoader through its services and unload a mod during this call, which would
        // make a foreach throw outside the per-mod try. A removal can skip one mod for this call only.
        for (var i = 0; i < _mods.Count; i++)
        {
            var mod = _mods[i];
            if (mod.Faulted)
            {
                continue;
            }

            var start = Enter(mod, nameof(IGameMod.OnUpdate));
            try
            {
                mod.Instance.OnUpdate(in time);
            }
            catch (Exception ex)
            {
                Fault(mod, nameof(IGameMod.OnUpdate), ex);
            }

            Leave(mod, nameof(IGameMod.OnUpdate), start, timing: -1);
        }
    }

    public void FixedUpdate(in FixedStepTime time)
    {
        // By index: a mod can reach ModLoader through its services and unload a mod during this call, which would
        // make a foreach throw outside the per-mod try. A removal can skip one mod for this call only.
        for (var i = 0; i < _mods.Count; i++)
        {
            var mod = _mods[i];
            if (mod.Faulted)
            {
                continue;
            }

            var start = Enter(mod, nameof(IGameMod.OnFixedUpdate));
            try
            {
                mod.Instance.OnFixedUpdate(in time);
            }
            catch (Exception ex)
            {
                Fault(mod, nameof(IGameMod.OnFixedUpdate), ex);
            }

            Leave(mod, nameof(IGameMod.OnFixedUpdate), start, mod.FixedTiming);
        }
    }

    /// <summary>
    /// Calls <see cref="IGameMod.OnMatchStarted"/> in load order, skipping faulted mods. Each mod's fixed-step time joins
    /// the match's systems' afresh: they were cleared as the match's world started (a mod's old index would name a system, or
    /// none).
    /// </summary>
    public void MatchStarted(IServiceProvider match)
    {
        for (var i = 0; i < _mods.Count; i++)
        {
            var mod = _mods[i];
            if (_times is not null)
            {
                mod.FixedTiming = _times.Add("Mod " + mod.Manifest.Id);
            }

            if (mod.Faulted)
            {
                continue;
            }

            var start = Enter(mod, nameof(IGameMod.OnMatchStarted));
            try
            {
                mod.Instance.OnMatchStarted(match);
            }
            catch (Exception ex)
            {
                Fault(mod, nameof(IGameMod.OnMatchStarted), ex);
            }

            Leave(mod, nameof(IGameMod.OnMatchStarted), start, timing: -1);
        }
    }

    /// <summary>Calls <see cref="IGameMod.OnMatchEnded"/> in reverse load order, including on faulted mods.</summary>
    public void MatchEnded()
    {
        for (var i = _mods.Count - 1; i >= 0; i--)
        {
            var mod = _mods[i];
            try
            {
                mod.Instance.OnMatchEnded();
            }
            catch (Exception ex)
            {
                Fault(mod, nameof(IGameMod.OnMatchEnded), ex);
            }
        }
    }

    /// <summary>Calls <see cref="IGameMod.OnGameExit"/> in reverse load order, including on faulted mods.</summary>
    public void NotifyGameExit()
    {
        for (var i = _mods.Count - 1; i >= 0; i--)
        {
            var mod = _mods[i];
            try
            {
                mod.Instance.OnGameExit();
            }
            catch (Exception ex)
            {
                Fault(mod, nameof(IGameMod.OnGameExit), ex);
            }
        }
    }

    /// <summary>Calls <see cref="IGameMod.OnModUnloaded"/>, including on faulted mods.</summary>
    public void NotifyUnloaded(LoadedMod mod)
    {
        try
        {
            mod.Instance.OnModUnloaded();
        }
        catch (Exception ex)
        {
            Fault(mod, nameof(IGameMod.OnModUnloaded), ex);
        }
    }

    private long Enter(LoadedMod mod, string callback)
    {
        _currentCallback = callback;
        _current = mod;
        return Stopwatch.GetTimestamp();
    }

    private void Leave(LoadedMod mod, string callback, long start, int timing)
    {
        _current = null;
        var ticks = Stopwatch.GetTimestamp() - start;
        if (timing >= 0)
        {
            _times?.Record(timing, ticks);
        }

        if (ticks > s_slowCallTicks && ticks > mod.LongestSlowTicks)
        {
            mod.LongestSlowTicks = ticks;
            var milliseconds = ticks * 1000.0 / Stopwatch.Frequency;
            LogSlowCall(mod.Manifest.Id, callback, milliseconds);
        }
    }

    private void Fault(LoadedMod mod, string callback, Exception exception)
    {
        mod.Faulted = true;
        LogCallbackFailed(exception, mod.Manifest.Id, callback);
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Mod {ModId} threw in {Callback}; it will receive no further update calls"
    )]
    private partial void LogCallbackFailed(Exception exception, string modId, string callback);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mod {ModId}'s {Callback} took {Milliseconds:F1} ms, its longest yet"
    )]
    private partial void LogSlowCall(string modId, string callback, double milliseconds);
}
