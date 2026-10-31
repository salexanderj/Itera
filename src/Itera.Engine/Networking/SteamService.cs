using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Networking;

/// <summary>
/// Starts the Steam API at startup when <c>Engine:Steam:Enabled</c> is true and the Steam client runs, runs its
/// callbacks once per main-loop iteration, and shuts it down last. Steam is optional: without the client, or on a
/// platform without Steam's library (Linux and Windows on arm64), the game logs a Warning and plays offline or over
/// Direct; only a session with <c>Transport=Steam</c> then fails to start. Main thread only.
/// </summary>
public sealed partial class SteamService : IDisposable
{
    private readonly SteamSettings _settings;
    private readonly ILogger<SteamService> _logger;
    private readonly Func<(ISteamPlatform? Platform, string Reason)> _start;
    private bool _disposed;

    public SteamService(IOptions<EngineOptions> options, ILogger<SteamService> logger)
        : this(options.Value.Steam, logger, null) { }

    /// <param name="settings">Whether to start Steam, and the App ID.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="start">Starts a platform (tests pass a fake); null starts the Steam API.</param>
    internal SteamService(
        SteamSettings settings,
        ILogger<SteamService> logger,
        Func<(ISteamPlatform? Platform, string Reason)>? start
    )
    {
        _settings = settings;
        _logger = logger;
        _start = start ?? StartSteam;
    }

    /// <summary>The running platform, or null when Steam is off or not available.</summary>
    internal ISteamPlatform? Platform { get; private set; }

    /// <summary>True when the Steam API runs.</summary>
    public bool IsAvailable => Platform is not null;

    /// <summary>This player's Steam name, or null when Steam is off or not available.</summary>
    public string? PersonaName => Platform?.PersonaName;

    /// <summary>Why Steam is not available, for a session that needs it.</summary>
    public string UnavailableReason { get; private set; } = "Steam has not started";

    public void Initialize()
    {
        if (!_settings.Enabled)
        {
            UnavailableReason = "Engine:Steam:Enabled is false";
            LogDisabled();
            return;
        }

        if (_settings.AppId == 0)
        {
            UnavailableReason = "Engine:Steam:AppId is 0";
            LogNoAppId();
            return;
        }

        try
        {
            var (platform, reason) = _start();
            if (platform is null)
            {
                UnavailableReason = reason;
                LogUnavailable(reason);
                return;
            }

            Platform = platform;
        }
        catch (Exception ex)
            when (ex
                    is DllNotFoundException
                        or BadImageFormatException
                        or EntryPointNotFoundException
                        or FileNotFoundException
            )
        {
            UnavailableReason = "this platform has no Steam library";
            LogNoLibrary(ex.GetType().Name, ex.Message);
            return;
        }

        var name = Platform.PersonaName;
        LogStarted(name, Platform.LocalSteamId, _settings.AppId);
    }

    /// <summary>Delivers Steam's callbacks; call once per main-loop iteration.</summary>
    public void RunCallbacks() => Platform?.RunCallbacks();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Platform is IDisposable disposable)
        {
            disposable.Dispose();
            LogStopped();
        }

        Platform = null;
    }

    private (ISteamPlatform? Platform, string Reason) StartSteam()
    {
        var platform = SteamPlatform.TryStart(_settings.AppId, _logger, out var reason);
        return (platform, reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam: off (Engine:Steam:Enabled is false)")]
    private partial void LogDisabled();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Steam: off (Engine:Steam:AppId is 0: the game has no App ID)"
    )]
    private partial void LogNoAppId();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Steam: not available ({Reason}); sessions through Steam are off"
    )]
    private partial void LogUnavailable(string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Steam: not available (no Steam library for this platform: {Error}: {Message}); sessions through Steam are off"
    )]
    private partial void LogNoLibrary(string error, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam: signed in as {Name} ({SteamId}), app {AppId}")]
    private partial void LogStarted(string name, ulong steamId, uint appId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam: shut down")]
    private partial void LogStopped();
}
