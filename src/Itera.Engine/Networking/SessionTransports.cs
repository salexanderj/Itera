using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Networking;

/// <summary>
/// The run's transports by kind: a match's session takes the one its start names (<see cref="Matches.MatchStart.Transport"/>).
/// The game registers <see cref="SessionTransports"/>; tests register one that hands out an in-memory transport, so a
/// match's flow runs a session with no sockets.
/// </summary>
public interface ISessionTransports
{
    /// <summary>The transport for <paramref name="kind"/>; the same one for every match of the run.</summary>
    ISessionTransport Transport(SessionTransportKind kind);
}

/// <summary>
/// LiteNetLib (<see cref="NetworkService"/>) for direct sessions, Steam's sockets (<see cref="SteamTransport"/>) for Steam's.
/// With added delay, jitter, or loss in the options (<see cref="SessionSettings.AddedDelayMilliseconds"/>), each is
/// handed out inside a <see cref="SimulatedNetwork"/>, made on first use and kept for the run.
/// </summary>
internal sealed partial class SessionTransports : ISessionTransports
{
    private readonly NetworkService _network;
    private readonly SteamTransport _steam;
    private readonly NetworkConditions _conditions;
    private readonly ILogger<SessionTransports> _logger;
    private SimulatedNetwork? _simulatedNetwork;
    private SimulatedNetwork? _simulatedSteam;
    private Func<long>? _clock;

    public SessionTransports(
        NetworkService network,
        SteamTransport steam,
        IOptions<EngineOptions> options,
        ILogger<SessionTransports> logger
    )
    {
        _network = network;
        _steam = steam;
        _logger = logger;
        var session = options.Value.Session;
        _conditions = new NetworkConditions(
            session.AddedDelayMilliseconds,
            session.AddedJitterMilliseconds,
            session.AddedLossPercent
        );
    }

    public ISessionTransport Transport(SessionTransportKind kind)
    {
        if (!_conditions.Any)
        {
            return kind == SessionTransportKind.Steam ? _steam : _network;
        }

        return kind == SessionTransportKind.Steam
            ? _simulatedSteam ??= Simulate(_steam)
            : _simulatedNetwork ??= Simulate(_network);
    }

    private SimulatedNetwork Simulate(ISessionTransport inner)
    {
        if (_clock is null)
        {
            var start = Stopwatch.GetTimestamp();
            _clock = () => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            LogSimulating(_conditions.DelayMilliseconds, _conditions.JitterMilliseconds, _conditions.LossPercent);
        }

        return new SimulatedNetwork(inner, _conditions, _clock, Environment.TickCount);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Simulated network: this peer's links add {Delay} ms of delay each way, up to {Jitter} ms of jitter, and drop {Loss} % of the sequenced and unreliable messages it sends"
    )]
    private partial void LogSimulating(int delay, int jitter, double loss);
}
