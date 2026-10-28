using System.Globalization;
using Itera.Engine.Commands;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Systems;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Replication;

/// <summary>
/// A match's replication of the entities a game marks (<see cref="Add"/>, by kinds from <see cref="ReplicatedKinds"/>):
/// on a host, each client is sent, every few steps (<c>Engine:Replication:SendHz</c>), the entities within its reach,
/// the nearest and longest unsent first, each as its changes from the last state that client acknowledged, in packets
/// that fit the transport, until that client's bytes a second are spent (<c>BytesPerSecond</c>, saved up to
/// <c>PeakBytesPerSecond</c>); new entities and removed ids go in the same packets until acknowledged. A client keeps
/// about two seconds of states for each entity, estimates the host's tick from the packets, and shows each entity at
/// that tick less <c>InterpolationMilliseconds</c>, blended between the two states round it (after a gap in which other
/// snapshots came, the move to the next state takes one send). Its reply each step says which packets arrived and whether
/// its loading screen still shows, and carries its own player's last commands; until its first reply (its world loaded)
/// it is sent nothing. A client's own player is
/// predicted and corrected by the host (<see cref="Predict"/>, <c>ReplicationService.Prediction.cs</c>). The engine's systems call it:
/// <c>ReplicationSendSystem</c> on a host, <c>ReplicationReceiveSystem</c> and <c>ReplicationReplySystem</c> on a
/// client. Offline it keeps its table and sends nothing. Main thread; a snapshot is read whole as it arrives (in the
/// session's poll), so a damaged one ends the session as any bad message does, and the ECS is touched only in the steps.
/// </summary>
public sealed partial class ReplicationService : IReplicationEndpoint
{
    /// <summary>
    /// Seconds a client may read no snapshot, and a host may have no packet of a client's acknowledged, before it logs a
    /// warning with the connection's state (and a client shows a notice): the host sends each client that replies at
    /// least a packet each send, 30 a second by default. A Steam test of 2026-10-28 had a client read nothing for up to
    /// 67 s while its connection stayed open and its replies reached the host.
    /// </summary>
    public const int SilenceSeconds = 2;

    private readonly SessionService _session;
    private readonly ReplicatedKinds _kinds;
    private readonly EcsWorld _ecs;
    private readonly WorldOrigin _origin;
    private readonly ReplicationSettings _settings;
    private readonly int _fixedStepHz;
    private readonly CharacterStepper _stepper;
    private readonly ILogger<ReplicationService> _logger;
    private readonly MatchControl? _matches;

    /// <summary>
    /// The match's replication. <paramref name="matches"/> is the run's, whose phase a client's replies tell the host of:
    /// loading until it plays (<see cref="IsLoading"/>); with none, a client plays.
    /// </summary>
    public ReplicationService(
        SessionService session,
        ReplicatedKinds kinds,
        EcsWorld ecs,
        WorldOrigin origin,
        IOptions<EngineOptions> options,
        CharacterStepper stepper,
        ILogger<ReplicationService> logger,
        MatchControl? matches = null
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(options);
        _session = session;
        _kinds = kinds;
        _ecs = ecs;
        _origin = origin;
        _settings = options.Value.Replication;
        _fixedStepHz = options.Value.FixedStepHz;
        _stepper = stepper;
        _logger = logger;
        _matches = matches;
        SendIntervalTicks = Math.Max(1, (int)Math.Round((double)_fixedStepHz / _settings.SendHz));
        InterpolationMilliseconds = _settings.InterpolationMilliseconds;
        // The first match seals the kinds: a hello carries their fingerprint from now on.
        _kinds.Seal();
        session.Attach(this);
    }

    /// <summary>The fixed steps between two sends to a client.</summary>
    public int SendIntervalTicks { get; }

    /// <summary>
    /// How far behind the host a client shows what it replicates, in milliseconds (<c>InterpolationMilliseconds</c>;
    /// the console's <c>replication delay</c> changes it).
    /// </summary>
    public int InterpolationMilliseconds { get; set; }

    public ulong KindsFingerprint => _kinds.Fingerprint;

    /// <summary>The run's kinds.</summary>
    public ReplicatedKinds Kinds => _kinds;

    private ReplicationContext Context => new(_ecs.World, _origin);

    /// <summary>
    /// The console's <c>replication</c>: what this game sends or shows, and <c>replication delay MS</c>, which sets how
    /// far behind the host a client shows it.
    /// </summary>
    internal void RegisterCommands(ConsoleCommands commands) =>
        commands.Register(
            "replication",
            "[delay MS]",
            "Prints what the host sends each client, or what this client shows; 'delay MS' sets how far behind the host a client shows it (0 to 1000).",
            // A client's: what it shows, and its own delay.
            CommandScope.Local,
            Command
        );

    private void Command(CommandContext context)
    {
        if (context.Count == 2 && string.Equals(context[0], "delay", StringComparison.Ordinal))
        {
            if (!context.TryGetInt(1, out var milliseconds) || milliseconds < 0 || milliseconds > 1000)
            {
                context.PrintUsage();
                return;
            }

            InterpolationMilliseconds = milliseconds;
            context.Print(string.Create(CultureInfo.InvariantCulture, $"replication delay {milliseconds} ms"));
            return;
        }

        if (context.Count != 0)
        {
            context.PrintUsage();
            return;
        }

        if (_session.Role == SessionRole.Client)
        {
            PrintClient(context);
        }
        else
        {
            PrintHost(context);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Replication: client {Slot} joined; sending at {Hz} a second, {Bytes} bytes a second"
    )]
    private partial void LogClientJoined(int slot, int hz, int bytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replication: client {Slot} left")]
    private partial void LogClientLeft(int slot);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Replication: no snapshot from the host for {Seconds:F1} s, though the session is open; the connection: {Connection}"
    )]
    private partial void LogNoSnapshots(double seconds, string connection);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Replication: snapshots from the host arrive again after {Seconds:F1} s without"
    )]
    private partial void LogSnapshotsAgain(double seconds);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Replication: client {Slot} has acknowledged no packet for {Seconds:F1} s ({Packets} sent since); its last reply came {ReplySeconds:F1} s ago; the connection: {Connection}"
    )]
    private partial void LogNoAcknowledgements(
        int slot,
        double seconds,
        long packets,
        double replySeconds,
        string connection
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Replication: client {Slot} acknowledges packets again after {Seconds:F1} s without"
    )]
    private partial void LogAcknowledgedAgain(int slot, double seconds);

    /// <summary>The transport's account of a connection for a warning, or a word that it gave none.</summary>
    private static string Described(string connection) =>
        connection.Length > 0 ? connection : "the transport reports nothing more";
}
