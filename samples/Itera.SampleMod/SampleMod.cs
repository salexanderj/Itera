using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Itera.SampleMod;

/// <summary>Reference mod: logs its lifecycle and one line per second of fixed ticks.</summary>
public sealed partial class SampleMod : IGameMod
{
    private const ulong LogEveryTicks = 60;
    private ILogger? _logger;

    public void OnModLoaded(IModContext context)
    {
        _logger = context.Logger;
        LogLoaded(_logger, context.Manifest.Name, context.Manifest.Version, context.ModDirectory);
    }

    public void OnFixedUpdate(in FixedStepTime time)
    {
        if (_logger is not null && time.Tick % LogEveryTicks == 0 && time.Tick > 0)
        {
            LogTick(_logger, time.Tick);
        }
    }

    public void OnGameExit()
    {
        if (_logger is not null)
        {
            LogGameExit(_logger);
        }
    }

    public void OnModUnloaded()
    {
        if (_logger is not null)
        {
            LogUnloaded(_logger);
        }

        _logger = null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sample mod loaded: {Name} {Version} from {Directory}")]
    private static partial void LogLoaded(ILogger logger, string name, string version, string directory);

    // Per-tick detail: Trace, as CLAUDE.md's log levels ask (at Information it was a line a second in every run).
    [LoggerMessage(Level = LogLevel.Trace, Message = "Sample mod: fixed tick {Tick}")]
    private static partial void LogTick(ILogger logger, ulong tick);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sample mod: OnGameExit")]
    private static partial void LogGameExit(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sample mod: OnModUnloaded")]
    private static partial void LogUnloaded(ILogger logger);
}
