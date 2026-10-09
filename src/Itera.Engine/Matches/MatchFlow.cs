using System.Diagnostics;
using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Networking;
using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Matches;

/// <summary>
/// What the main loop needs of a match's flow that only it can do: the match's world on the GPU, and what is put back
/// or shown when a world starts and when a match ends. Delegates, so the flow itself needs no window or GPU.
/// </summary>
/// <param name="Gpu">Puts a match's world on the GPU and takes it off (<see cref="MatchGpu"/>).</param>
/// <param name="WorldStarted">Called once a match's world steps have run: the loop starts its loading screen.</param>
/// <param name="Ended">Called once a match has ended and its scope is gone: the loop puts back what the run kept of it.</param>
public sealed record MatchFlowHooks(MatchGpu Gpu, Action<MatchServices> WorldStarted, Action Ended);

/// <summary>
/// The match flow of a run a match starts
/// from the options or from <see cref="MatchControl"/>'s requests (the console's, the menu's), as a DI scope of its own
/// (<see cref="MatchServices"/>) whose session step runs at once; offline or hosting its world starts once a frame
/// saying so was shown (or half a second passed), joining once the host's world has been downloaded
/// (<see cref="PollSession"/>); a leave, a new start, or the end of a client's session ends it, and the reason stays
/// for the player. A start or join from the options that fails throws <see cref="SessionStartException"/>, which ends
/// the run with exit code 1; from the menu it ends the match with the reason, and so does an exception in the match's
/// work (<see cref="EndAfterFault"/>). Main thread; no window or GPU, so tests drive it with the game's services.
/// </summary>
public sealed partial class MatchFlow
{
    /// <summary>How long a starting world waits for a frame saying so before it starts anyway, in seconds.</summary>
    private const double StartFrameSeconds = 0.5;

    private readonly IServiceScopeFactory _scopes;
    private readonly MatchControl _matches;
    private readonly SystemTimes _systemTimes;
    private readonly MatchFlowHooks _hooks;
    private readonly Func<double> _clock;
    private readonly Action<string> _phase;
    private readonly ILogger _logger;
    private IServiceScope? _scope;
    private EngineSteps? _sessionSteps;
    private EngineSteps? _worldSteps;
    private bool _worldPending;
    private ulong _worldPendingFrame;
    private double _worldPendingSince;

    /// <param name="scopes">Makes each match's scope.</param>
    /// <param name="matches">The run's view of the match, and the requests to start or leave one.</param>
    /// <param name="systemTimes">Timed afresh for each match's systems.</param>
    /// <param name="hooks">What only the main loop can do.</param>
    /// <param name="clock">Seconds since the run started.</param>
    /// <param name="phase">Told what the flow is doing, for the run's fatal-error line.</param>
    /// <param name="logger">The main loop's logger: match lines keep its name.</param>
    public MatchFlow(
        IServiceScopeFactory scopes,
        MatchControl matches,
        SystemTimes systemTimes,
        MatchFlowHooks hooks,
        Func<double> clock,
        Action<string> phase,
        ILogger logger
    )
    {
        _scopes = scopes;
        _matches = matches;
        _systemTimes = systemTimes;
        _hooks = hooks;
        _clock = clock;
        _phase = phase;
        _logger = logger;
    }

    /// <summary>The match, from its start until it ends; null without one.</summary>
    public MatchServices? Match { get; private set; }

    /// <summary>The match while its world has started (its steps ran), else null: what the loop steps and draws.</summary>
    public MatchServices? Playing => _worldSteps is not null ? Match : null;

    /// <summary>True when the match's session has ended: the host left, or the connection was lost or refused.</summary>
    public bool SessionEnded => Match is { } match && match.Session.End != SessionEnd.None;

    /// <summary>
    /// True when the session's end ends the run: for a match the options started (scripts and two games on one machine
    /// see its exit code); any other match's end goes back to the menu.
    /// </summary>
    public bool SessionEndsRun => SessionEnded && Match!.Start.FromCommandLine;

    /// <summary>
    /// True when an exception in the match's work ends only the match (<see cref="EndAfterFault"/>): a match the menu or
    /// the console started. An exception in a match the
    /// options started still ends the run with exit code 1, as its scripts expect, and so does one with no match.
    /// </summary>
    public bool EndsAfterFault => Match is { Start.FromCommandLine: false };

    /// <summary>What the menu says after a match ended for an exception (<see cref="EndAfterFault"/>).</summary>
    public const string FaultReason = "The match ended because of an error; the log has the details.";

    /// <summary>
    /// The run's first match: the one the options start, or, with an address and no mode, a join from the menu (Steam's
    /// "Join game": the menu joins at once and comes back with the reason if it cannot). Nothing for the menu alone.
    /// </summary>
    public void Begin(EngineOptions options, MatchCommands commands)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commands);
        if (MatchStart.FromOptions(options) is { } first)
        {
            Start(first);
        }
        else if (options.Session.Address is { Length: > 0 } address)
        {
            _matches.RequestStart(commands.JoinStart(address));
        }
    }

    /// <summary>
    /// A client's session that ended (the host left, the connection was lost) ends its match; then what
    /// <see cref="MatchControl"/> was asked: a leave ends the match, a start ends it and starts the new one.
    /// </summary>
    public void HandleRequests(ulong frame)
    {
        if (SessionEnded && !SessionEndsRun)
        {
            var reason = Capitalised(Match!.Session.EndReason);
            LogMatchSessionEnded(_logger, reason);
            End(clean: true, reason);
        }

        if (_matches.TakeLeave() is { } leave && Match is not null)
        {
            End(clean: true, leave);
        }

        if (_matches.TakeStart() is { } start)
        {
            if (Match is not null)
            {
                End(clean: true, "Another match started.");
            }

            Start(start, frame);
        }
    }

    /// <summary>
    /// Starts a match: makes its scope and runs its session step. Offline or hosting, the match is settled at once and
    /// its world starts once a frame saying so has been shown; joining, <see cref="PollSession"/> carries the join on. A
    /// start that fails (<see cref="SessionStartException"/>) is thrown when the options asked for it, and otherwise ends
    /// the match with the reason.
    /// </summary>
    public void Start(MatchStart start, ulong frame = 0)
    {
        ArgumentNullException.ThrowIfNull(start);
        try
        {
            BeginMatch(start, frame);
        }
        catch (SessionStartException ex) when (!start.FromCommandLine)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>
    /// The match's session: a joining client's join goes on (the world starts once the host's is downloaded; a failed
    /// join from the options throws <see cref="SessionStartException"/>); otherwise what arrived is taken.
    /// </summary>
    public void PollSession(ulong frame)
    {
        if (Match is not { } match)
        {
            return;
        }

        if (_matches.Phase != MatchPhase.Joining)
        {
            match.Session.Poll();
            return;
        }

        var progress = match.Session.PollJoin();
        if (progress.Failure is { } failure)
        {
            if (match.Start.FromCommandLine)
            {
                throw new SessionStartException(failure);
            }

            Fail(failure);
            return;
        }

        _matches.JoinProgress = progress;
        if (progress.Stage == JoinStage.Joined)
        {
            StartWorldSoon(frame);
        }
    }

    /// <summary>
    /// Starts the match's world once a frame saying so (the menu's "Starting…") has been built since it was due, frame
    /// <paramref name="frame"/> being the next, or half a second has gone when no frame is asked for, so the picture
    /// does not stand still without a word while its steps run.
    /// </summary>
    public void StartWorldWhenShown(ulong frame)
    {
        if (!_worldPending || (frame == _worldPendingFrame && _clock() - _worldPendingSince < StartFrameSeconds))
        {
            return;
        }

        _worldPending = false;
        try
        {
            StartWorld(Match!);
        }
        catch (SessionStartException ex) when (!Match!.Start.FromCommandLine)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>
    /// Ends the match: its session says goodbye first, then its steps shut down in reverse (the named save is written
    /// only when <paramref name="clean"/>), then its scope frees what is left, and the loop puts back what the run kept
    /// of it (<see cref="MatchFlowHooks.Ended"/>). <paramref name="reason"/>, when given, stays for the player. Nothing
    /// without a match.
    /// </summary>
    public void End(bool clean, string? reason = null)
    {
        if (Match is not { } match)
        {
            return;
        }

        _phase("match end");
        match.EndedCleanly = clean;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // The goodbye goes out before anything else stops, so the peers hear of it at once.
            match.Session.Stop();
        }
        catch (Exception ex)
        {
            LogMatchEndFailed(_logger, ex, "session");
        }

        _worldSteps?.Shutdown();
        _sessionSteps?.Shutdown();
        try
        {
            _scope?.Dispose();
        }
        catch (Exception ex)
        {
            LogMatchEndFailed(_logger, ex, "scope");
        }

        // A save that failed is told at the menu with the reason the match ended.
        var saveFailure = match.Saves.Failure;
        _worldSteps = null;
        _sessionSteps = null;
        _worldPending = false;
        _scope = null;
        Match = null;
        _matches.Current = null;
        _matches.Phase = MatchPhase.Menu;
        _matches.LastEnd =
            saveFailure is null ? reason
            : reason is null ? saveFailure
            : reason + " " + saveFailure;
        _matches.JoinAddress = null;
        _matches.JoinProgress = default;
        _hooks.Ended();
        var megabytes = VoxelWorld.LiveMemoryBytes / (1024.0 * 1024.0);
        LogMatchEnded(_logger, stopwatch.Elapsed.TotalMilliseconds, megabytes);
    }

    /// <summary>
    /// Ends a match whose work threw <paramref name="exception"/> (only when <see cref="EndsAfterFault"/>): logged at
    /// Error, and ended as a failed match is, so the named save is not written (the world may be half changed) and the
    /// menu shows <see cref="FaultReason"/>. The run goes on at the menu.
    /// </summary>
    public void EndAfterFault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!EndsAfterFault)
        {
            throw new InvalidOperationException(
                "Only a match the menu or the console started ends alone after a fault."
            );
        }

        LogMatchFaulted(_logger, exception, Describe(Match!.Start));
        End(clean: false, FaultReason);
    }

    /// <summary>
    /// The run's exit code from the match's session: 1 when it was lost or failed (logged), else 0; 0 without a match.
    /// </summary>
    public int SessionExitCode()
    {
        if (Match is not { } match)
        {
            return 0;
        }

        switch (match.Session.End)
        {
            case SessionEnd.Lost:
                LogSessionLost(_logger, match.Session.EndReason);
                return 1;
            case SessionEnd.Error:
                LogSessionError(_logger, match.Session.EndReason);
                return 1;
            default:
                return 0;
        }
    }

    /// <summary>"offline, seed 1", "host through Steam, seed 7", "join 127.0.0.1:47600": words, not the enums.</summary>
    public static string Describe(MatchStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var steam = start.Transport == SessionTransportKind.Steam ? " through Steam" : "";
        return start.Mode switch
        {
            SessionMode.Host => string.Create(culture, $"host{steam}, seed {start.Seed}"),
            SessionMode.Join => $"join {start.Address}{steam}",
            _ => string.Create(culture, $"offline, seed {start.Seed}"),
        };
    }

    /// <summary>A session's reason as the player reads it: its first letter capitalised, ending in a full stop.</summary>
    public static string Capitalised(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (reason.Length == 0)
        {
            return reason;
        }

        var text = char.ToUpperInvariant(reason[0]) + reason[1..];
        return text.EndsWith('.') ? text : text + ".";
    }

    /// <summary>A match that could not start or join ends, and the reason stays for the player (and the log).</summary>
    private void Fail(string reason)
    {
        LogMatchFailed(_logger, reason);
        End(clean: false, reason);
    }

    private void BeginMatch(MatchStart start, ulong frame)
    {
        _phase("match start");
        _matches.LastEnd = null;
        _matches.Started++;
        _scope = _scopes.CreateScope();
        _scope.ServiceProvider.GetRequiredService<MatchContext>().Set(start);
        var match = _scope.ServiceProvider.GetRequiredService<MatchServices>();
        Match = match;
        _matches.Current = match;
        var what = Describe(start);
        LogMatchStarting(_logger, _matches.Started, what);
        _sessionSteps = new EngineSteps(match.CreateSessionSteps(), _logger);
        _sessionSteps.Initialize(_phase);
        _phase("main loop");
        if (match.Session.Role == SessionRole.Client)
        {
            // The menu shows how far the join is (MatchControl.JoinProgress).
            _matches.Phase = MatchPhase.Joining;
            _matches.JoinAddress = match.Session.JoinAddress;
            _matches.JoinProgress = default;
        }
        else
        {
            StartWorldSoon(frame);
        }
    }

    /// <summary>The world starts once the frame after <paramref name="frame"/> was shown, or half a second has gone.</summary>
    private void StartWorldSoon(ulong frame)
    {
        _matches.Phase = MatchPhase.Starting;
        _worldPending = true;
        _worldPendingFrame = frame;
        _worldPendingSince = _clock();
    }

    private void StartWorld(MatchServices match)
    {
        // The match's systems are timed afresh (EcsWorld adds each by name).
        _systemTimes.Clear();
        _worldSteps = new EngineSteps(match.CreateWorldSteps(_hooks.Gpu, _clock), _logger);
        _worldSteps.Initialize(_phase);
        _phase("main loop");
        _matches.Phase = MatchPhase.Loading;
        _hooks.WorldStarted(match);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Match {Number}: {What}")]
    private static partial void LogMatchStarting(ILogger logger, int number, string what);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Match: ended in {Ms:F0} ms; voxel memory left in the process {Megabytes:F1} MB"
    )]
    private static partial void LogMatchEnded(ILogger logger, double ms, double megabytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Match: {Reason}")]
    private static partial void LogMatchFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Match: the session ended: {Reason}")]
    private static partial void LogMatchSessionEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Match: {What} failed; it ends and the run goes back to the menu")]
    private static partial void LogMatchFaulted(ILogger logger, Exception exception, string what);

    [LoggerMessage(Level = LogLevel.Error, Message = "Match: ending its {Part} failed; continuing")]
    private static partial void LogMatchEndFailed(ILogger logger, Exception exception, string part);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session: {Reason}")]
    private static partial void LogSessionLost(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session: {Reason}")]
    private static partial void LogSessionError(ILogger logger, string reason);
}
