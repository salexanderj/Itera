using System.Diagnostics;
using System.Globalization;
using Itera.Engine.Matches;
using Itera.Engine.Saves;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Networking;

/// <summary>What this game is in a session.</summary>
public enum SessionRole
{
    /// <summary>Playing alone; owns its world, as a host does, with no socket.</summary>
    Offline,

    /// <summary>Owns the world and sends its edits to the clients.</summary>
    Host,

    /// <summary>Follows a host's world.</summary>
    Client,
}

/// <summary>How a session ended while the game runs.</summary>
public enum SessionEnd
{
    /// <summary>It has not ended.</summary>
    None,

    /// <summary>The host quit and said so.</summary>
    HostLeft,

    /// <summary>The connection to the host was lost.</summary>
    Lost,

    /// <summary>The host sent something this build cannot accept, or refused this client.</summary>
    Error,
}

/// <summary>
/// A session could not start: a client could not join (no host, a refusal, a payload it could not accept), or a host
/// could not open its session (Steam not running). Not a fault of this game: logged at Error, exit code 1.
/// </summary>
public sealed class SessionStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Hosts or joins a session. The host owns the world: it sends a joining client the match settings and
/// its edit log (the join payload), then every new edit after each fixed step. A client connects in the first start-up
/// step, before anything reads the seed, downloads the payload, and loads it once its world exists; from then on its
/// world takes edits only from the host (<see cref="VoxelWorld.FollowHost"/>). Offline is the host role without a socket.
/// The game adds its own messages (kinds from <see cref="FirstGameKind"/>) through <see cref="ISessionGameHandler"/>s
/// and <see cref="BeginMessage"/>, its match data through <see cref="ISessionMatchData"/>, and reads who plays from
/// <see cref="Players"/>. Main thread only.
/// </summary>
public sealed partial class SessionService : ISessionTransportListener, IDisposable
{
    private readonly SessionSettings _settings;
    private readonly int _fixedStepHz;
    private readonly ulong _configuredSeed;
    private readonly double _statsIntervalSeconds;
    private readonly VoxelWorld _voxels;
    private readonly ISessionTransport _transport;
    private readonly ILogger<SessionService> _logger;
    private readonly Func<double> _clock;
    private readonly Action _wait;
    private readonly MessageWriter _writer = new();
    private readonly MessageWriter _gameWriter = new();
    private readonly MessageWriter _disconnectWriter = new();
    private readonly ISessionGameHandler?[] _handlers = new ISessionGameHandler?[256];
    private readonly ISessionMatchData? _matchData;
    private readonly Func<string?> _steamName;
    private readonly SaveService? _saves;
    private string _playerName = DefaultPlayerName;
    private SessionPlayer[] _players = [];
    private readonly List<(byte Material, int Length)> _runs = [];
    private readonly Dictionary<int, HostPeer> _peers = [];
    private readonly List<int> _dropped = [];
    private MatchSettings? _match;
    private bool _stopped;
    private bool _disposed;

    // Counted between stats lines.
    private long _messagesIn;
    private long _bytesIn;
    private long _messagesOut;
    private long _bytesOut;
    private double _statsStart = double.NaN;

    public SessionService(
        IOptions<EngineOptions> options,
        MatchContext match,
        VoxelWorld voxels,
        ISessionTransports transports,
        SteamService steam,
        ILogger<SessionService> logger,
        IEnumerable<ISessionGameHandler> handlers,
        SaveService saves,
        ISessionMatchData? matchData = null
    )
        : this(
            SettingsFor(options.Value.Session, match.Start),
            options.Value.FixedStepHz,
            match.Start.Seed,
            options.Value.Logging.StatsIntervalSeconds,
            voxels,
            transports.Transport(match.Start.Transport),
            logger,
            MonotonicClock(),
            () => Thread.Sleep(1),
            handlers,
            matchData,
            () => steam.PersonaName,
            saves
        ) { }

    /// <param name="settings">Mode, address, limits.</param>
    /// <param name="fixedStepHz">This game's fixed step rate.</param>
    /// <param name="configuredSeed">The seed from the options: the match's on a host or offline.</param>
    /// <param name="statsIntervalSeconds">Seconds between stats lines; zero or less turns them off.</param>
    /// <param name="voxels">The world whose edits the session carries.</param>
    /// <param name="transport">Carries the messages.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Seconds from any start, for timeouts and stats.</param>
    /// <param name="wait">Called while a client waits for the host during start-up.</param>
    /// <param name="handlers">The game's message handlers, one per kind.</param>
    /// <param name="matchData">Writes the game's match data on a host or offline; null for none.</param>
    /// <param name="steamName">This player's Steam name, or null without Steam; read when the session starts.</param>
    /// <param name="saves">The named save a host or offline game starts from; null for none.</param>
    internal SessionService(
        SessionSettings settings,
        int fixedStepHz,
        ulong configuredSeed,
        double statsIntervalSeconds,
        VoxelWorld voxels,
        ISessionTransport transport,
        ILogger<SessionService> logger,
        Func<double> clock,
        Action wait,
        IEnumerable<ISessionGameHandler>? handlers = null,
        ISessionMatchData? matchData = null,
        Func<string?>? steamName = null,
        SaveService? saves = null
    )
    {
        _settings = settings;
        _fixedStepHz = fixedStepHz;
        _configuredSeed = configuredSeed;
        _statsIntervalSeconds = statsIntervalSeconds;
        _voxels = voxels;
        _transport = transport;
        _logger = logger;
        _clock = clock;
        _wait = wait;
        _matchData = matchData;
        _steamName = steamName ?? (() => null);
        _saves = saves;
        foreach (var handler in handlers ?? [])
        {
            if (handler.Kind < FirstGameKind || _handlers[handler.Kind] is not null)
            {
                throw new InvalidOperationException(
                    $"Game message kind {handler.Kind} is below {FirstGameKind} or has two handlers."
                );
            }

            _handlers[handler.Kind] = handler;
        }
    }

    /// <summary>The name a player gives when neither the options nor Steam name it.</summary>
    public const string DefaultPlayerName = "Player";

    /// <summary>The first message kind the game may use.</summary>
    public const byte FirstGameKind = SessionProtocol.FirstGameKind;

    /// <summary>The most slots a session has (the host's included).</summary>
    public const int MaxSlots = SessionProtocol.MaxSlots;

    public SessionRole Role { get; private set; }

    /// <summary>
    /// On a client, the round trip to the host in milliseconds as the transport measures it; -1 on a host, offline,
    /// or when the transport does not know it yet.
    /// </summary>
    public int HostRoundTripMilliseconds =>
        Role == SessionRole.Client && _host >= 0 ? _transport.RoundTripMilliseconds(_host) : -1;

    /// <summary>True when this game owns the world (offline or hosting): only then may it make edits of its own.</summary>
    public bool IsAuthority => Role != SessionRole.Client;

    /// <summary>
    /// The match this game plays: the host's on a client. Set by <see cref="Initialize"/>, the first start-up step;
    /// reading it before throws, so nothing can build the world from the configured seed by mistake.
    /// </summary>
    public MatchSettings Match =>
        _match
        ?? throw new InvalidOperationException(
            "The session has not started yet, so the match (its seed) is not known. Resolve this later than the session step."
        );

    /// <summary>
    /// True when the match's settings, the game's match data (its rules) among them, came from the named save this host
    /// or offline game started from (<see cref="SaveService"/>): the game then plays by them, as a client plays by the
    /// host's.
    /// </summary>
    public bool MatchFromSave { get; private set; }

    /// <summary>This player's slot: 0 for the host (and offline), from 1 on a client.</summary>
    public int Slot { get; private set; }

    /// <summary>
    /// Each slot's player, the host in slot 0: from the host's roster on a client. Empty before the session starts (and
    /// on a client until the first roster). <see cref="RosterVersion"/> rises whenever it changes.
    /// </summary>
    public ReadOnlySpan<SessionPlayer> Players => _players;

    /// <summary>Rises whenever <see cref="Players"/> changes, so the game can poll it.</summary>
    public int RosterVersion { get; private set; }

    /// <summary>Clients that have joined (on a host).</summary>
    public int JoinedPeers
    {
        get
        {
            var count = 0;
            foreach (var peer in _peers.Values)
            {
                count += peer.Joined ? 1 : 0;
            }

            return count;
        }
    }

    /// <summary>How the session ended, or <see cref="SessionEnd.None"/>. The main loop stops when it is not None.</summary>
    public SessionEnd End { get; private set; }

    /// <summary>Why the session ended, for the log.</summary>
    public string EndReason { get; private set; } = "";

    /// <summary>
    /// Starts the session and, on a client, waits for the join to finish (<see cref="Begin"/>, then
    /// <see cref="PollJoin"/> until the host's world is downloaded, waiting between polls). Throws
    /// <see cref="SessionStartException"/> when the session cannot start or a client cannot join.
    /// </summary>
    public void Initialize()
    {
        Begin();
        while (true)
        {
            var progress = PollJoin();
            if (progress.Failure is { } failure)
            {
                throw new SessionStartException(failure);
            }

            if (progress.Stage == JoinStage.Joined)
            {
                return;
            }

            _wait();
        }
    }

    /// <summary>
    /// The match's session step: settles the match at once offline or hosting (a host binds its port); a client starts
    /// connecting, and <see cref="PollJoin"/> carries the join on. Throws <see cref="SessionStartException"/> when a host
    /// cannot open its session (its port taken, no Steam) or a client cannot start connecting.
    /// </summary>
    public void Begin()
    {
        var steamName = _steamName();
        _playerName = ChoosePlayerName(_settings.PlayerName, steamName);
        var source =
            _settings.PlayerName is not null ? "Engine:Session:PlayerName"
            : _playerName == DefaultPlayerName && string.IsNullOrWhiteSpace(steamName) ? "the default"
            : "Steam";
        LogPlayerName(_playerName, source);
        var generator = _voxels.Generator;
        switch (_settings.Mode)
        {
            case SessionMode.Host:
                _match = OwnMatch(generator);
                try
                {
                    _transport.Listen(_settings.Port);
                }
                catch (InvalidOperationException ex)
                {
                    throw new SessionStartException("Could not host: " + ex.Message, ex);
                }

                Role = SessionRole.Host;
                // What friends read before they join: the match a client must share, and the seats.
                _transport.SetLobbyData(
                    SessionProtocol.LobbyMatchKey,
                    SessionProtocol.MatchKey(generator.Id, generator.Version, _fixedStepHz)
                );
                _transport.SetLobbyData(
                    SessionProtocol.LobbyMaxKey,
                    _settings.MaxPlayers.ToString(CultureInfo.InvariantCulture)
                );
                UpdateRoster();
                var where =
                    _settings.Transport == SessionTransportKind.Steam
                        ? "through Steam"
                        : string.Create(CultureInfo.InvariantCulture, $"on UDP port {_transport.LocalPort}");
                LogHosting(where, _settings.MaxPlayers, _match.Value.Seed);
                break;
            case SessionMode.Join:
                Role = SessionRole.Client;
                _saves?.IgnoreOnClient();
                StartJoin();
                break;
            default:
                _match = OwnMatch(generator);
                Role = SessionRole.Offline;
                _players = [new SessionPlayer(true, _playerName, HostJoin)];
                RosterVersion++;
                LogOffline(_match.Value.Seed);
                break;
        }
    }

    /// <summary>
    /// The match of a game that owns its world: the named save's seed, step rate, and match data when it starts from
    /// one (a save of format 2 or older has no match data, so the game's own is used), otherwise the options'.
    /// </summary>
    private MatchSettings OwnMatch(IChunkGenerator generator)
    {
        var saved = _saves?.ReadStart(_fixedStepHz, _configuredSeed);
        if (saved is not { } header)
        {
            return new MatchSettings(_configuredSeed, _fixedStepHz, generator.Id, generator.Version, GameData());
        }

        MatchFromSave = header.Game is not null;
        return new MatchSettings(header.Seed, _fixedStepHz, generator.Id, generator.Version, header.Game ?? GameData());
    }

    /// <summary>
    /// The step after the world starts: a client loads the join payload into it, applies what the host sent since, and
    /// from then on follows the host; a host or offline game started from its named save loads the save's edits.
    /// </summary>
    public void LoadJoinedWorld()
    {
        if (Role == SessionRole.Client)
        {
            LoadDownload();
        }
        else
        {
            _saves?.LoadEdits();
        }
    }

    /// <summary>
    /// Takes what arrived: a host answers hellos and sends joining clients the world; a client applies the host's
    /// edits. Call once per main-loop iteration, before the world's update.
    /// </summary>
    public void Poll()
    {
        if (Role == SessionRole.Offline || _stopped)
        {
            return;
        }

        _transport.Poll(this);
        if (Role == SessionRole.Host)
        {
            DropSilentPeers();
            SendDownloads();
        }

        UpdateStats();
    }

    /// <summary>After each fixed step (and the mods'), a host sends every joined client the edits made since.</summary>
    public void AfterFixedStep()
    {
        if (Role == SessionRole.Host)
        {
            SendNewEdits();
        }
    }

    /// <summary>
    /// Starts a game message of <paramref name="kind"/> (from <see cref="FirstGameKind"/>) in the session's reused writer
    /// and returns it; write the body, then send it with <see cref="SendToHost"/>, <see cref="SendTo"/>, or
    /// <see cref="SendToAll"/> before starting another.
    /// </summary>
    public MessageWriter BeginMessage(byte kind)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(kind, FirstGameKind);
        _gameWriter.Reset();
        _gameWriter.WriteByte(kind);
        return _gameWriter;
    }

    /// <summary>The largest message the sequenced channel takes now, in bytes (<see cref="ISessionTransport.MaxUnreliableBytes"/>).</summary>
    public int MaxUnreliableBytes => _transport.MaxUnreliableBytes;

    /// <summary>On a client that has joined, sends the begun message to the host; otherwise does nothing.</summary>
    public void SendToHost(SessionChannel channel)
    {
        if (Role == SessionRole.Client && _join == JoinState.Loaded && _host >= 0)
        {
            SendGame(_host, channel);
        }
    }

    /// <summary>On a host, sends the begun message to the joined client in <paramref name="slot"/>, if any.</summary>
    public void SendTo(int slot, SessionChannel channel)
    {
        if (Role != SessionRole.Host)
        {
            return;
        }

        foreach (var (id, peer) in _peers)
        {
            if (peer.Joined && peer.Slot == slot)
            {
                SendGame(id, channel);
                return;
            }
        }
    }

    /// <summary>On a host, sends the begun message to every joined client but the one in <paramref name="exceptSlot"/>.</summary>
    public void SendToAll(SessionChannel channel, int exceptSlot = -1)
    {
        if (Role != SessionRole.Host)
        {
            return;
        }

        foreach (var (id, peer) in _peers)
        {
            if (peer.Joined && peer.Slot != exceptSlot)
            {
                SendGame(id, channel);
            }
        }
    }

    /// <summary>Tells the peers this game is leaving and closes the socket. Safe to call again.</summary>
    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        if (Role == SessionRole.Offline)
        {
            return;
        }

        var text = Role == SessionRole.Host ? "the host quit" : "the player left";
        SessionProtocol.WriteDisconnect(_disconnectWriter, DisconnectKind.Bye, 0, text);
        _transport.Close(_disconnectWriter.Written);
        _peers.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    public void OnConnected(int peer)
    {
        if (Role == SessionRole.Host)
        {
            _peers[peer] = new HostPeer(_clock());
        }
        else
        {
            OnConnectedToHost(peer);
        }
    }

    public void OnDisconnected(int peer, TransportDisconnect reason, ReadOnlySpan<byte> data)
    {
        if (Role == SessionRole.Host)
        {
            if (_peers.Remove(peer, out var left) && left.Joined)
            {
                var why = SessionProtocol.TryReadDisconnect(data, out _, out _, out var text)
                    ? text
                    : reason.ToString();
                LogPlayerLeft(left.Name, left.Slot, why);
                UpdateRoster();
            }
        }
        else
        {
            OnDisconnectedFromHost(reason, data);
        }
    }

    public void OnReceive(int peer, ReadOnlySpan<byte> data)
    {
        _messagesIn++;
        _bytesIn += data.Length;
        try
        {
            if (Role == SessionRole.Host)
            {
                ReceiveAsHost(peer, data);
            }
            else
            {
                ReceiveAsClient(data);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            ProtocolError(peer, ex.Message);
        }
    }

    /// <summary>Drops a peer that sent something this build cannot accept; a client ends its session.</summary>
    private void ProtocolError(int peer, string problem)
    {
        LogProtocolError(peer, problem);
        SessionProtocol.WriteDisconnect(_disconnectWriter, DisconnectKind.ProtocolError, 0, problem);
        _transport.Disconnect(peer, _disconnectWriter.Written);
        if (Role == SessionRole.Host)
        {
            if (_peers.Remove(peer, out var dropped) && dropped.Joined)
            {
                UpdateRoster();
            }
        }
        else if (_join < JoinState.Downloaded)
        {
            _refusal ??= "the host sent something this game cannot accept: " + problem;
        }
        else
        {
            EndSession(SessionEnd.Error, "the host sent something this game cannot accept: " + problem);
        }
    }

    private void EndSession(SessionEnd end, string reason)
    {
        if (End == SessionEnd.None)
        {
            End = end;
            EndReason = reason;
        }
    }

    private void SendGame(int peer, SessionChannel channel)
    {
        _messagesOut++;
        _bytesOut += _gameWriter.Length;
        _transport.Send(peer, _gameWriter.Written, channel);
    }

    /// <summary>
    /// Hands a game message to its handler. A kind without one, a message from the side its handler does not take it
    /// from (<see cref="ISessionGameHandler.Direction"/>), and any exception the handler throws are protocol errors; an
    /// exception other than <see cref="InvalidDataException"/> or <see cref="ArgumentException"/> is also logged at
    /// Error, since it is a bug in the handler.
    /// </summary>
    private void ReceiveGame(int slot, byte kind, ReadOnlySpan<byte> body)
    {
        var handler = _handlers[kind] ?? throw new InvalidDataException($"No handler for game message {kind}.");
        var refused = Role == SessionRole.Host ? SessionMessageDirection.ToClients : SessionMessageDirection.ToHost;
        if (handler.Direction == refused)
        {
            throw new InvalidDataException(
                Role == SessionRole.Host
                    ? $"Game message {kind} comes only from the host."
                    : $"Game message {kind} goes only to the host."
            );
        }

        try
        {
            handler.Receive(slot, body);
        }
        catch (Exception ex) when (ex is not (InvalidDataException or ArgumentException or OutOfMemoryException))
        {
            LogHandlerFailed(ex, kind, slot);
            throw new InvalidDataException($"Game message {kind} could not be read.", ex);
        }
    }

    /// <summary>The game's match data, or empty.</summary>
    private byte[] GameData()
    {
        if (_matchData is null)
        {
            return [];
        }

        var writer = new MessageWriter();
        _matchData.Write(writer);
        if (writer.Length > SessionProtocol.MaxMatchGameBytes)
        {
            throw new InvalidOperationException("The game's match data is too long.");
        }

        return writer.Written.ToArray();
    }

    private void Send(int peer, SessionChannel channel = SessionChannel.Reliable)
    {
        _messagesOut++;
        _bytesOut += _writer.Length;
        _transport.Send(peer, _writer.Written, channel);
    }

    private void UpdateStats()
    {
        if (_statsIntervalSeconds <= 0)
        {
            return;
        }

        var now = _clock();
        if (double.IsNaN(_statsStart))
        {
            _statsStart = now;
            return;
        }

        if (now - _statsStart < _statsIntervalSeconds)
        {
            return;
        }

        // A host counts its joined clients; a client its host.
        var peers =
            Role == SessionRole.Host ? JoinedPeers
            : _host >= 0 ? 1
            : 0;
        var kilobytesIn = _bytesIn / 1024.0;
        var kilobytesOut = _bytesOut / 1024.0;
        var seconds = now - _statsStart;
        // A word, not the enum: the log templates format values that are not strings as JSON, in quotes.
        var role = Role == SessionRole.Host ? "host" : "client";
        LogStats(role, peers, _messagesIn, kilobytesIn, _messagesOut, kilobytesOut, seconds);
        var sending = _transport.TakeStats();
        if (sending.Any)
        {
            var mostHeldKilobytes = sending.MostHeldBytes / 1024.0;
            LogSendingBehind(
                sending.SkippedStates,
                sending.HeldReliable,
                mostHeldKilobytes,
                sending.Refused,
                sending.LastRefusal,
                sending.EndedPeers,
                seconds
            );
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var (id, peer) in _peers)
            {
                var roundTrip = _transport.RoundTripMilliseconds(id);
                LogPeerStats(id, peer.Name, peer.Slot, roundTrip);
            }
        }

        _messagesIn = _bytesIn = _messagesOut = _bytesOut = 0;
        _statsStart = now;
    }

    private static Func<double> MonotonicClock()
    {
        var start = Stopwatch.GetTimestamp();
        return () => Stopwatch.GetElapsedTime(start).TotalSeconds;
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>A client, from the host's side.</summary>
    private sealed class HostPeer(double connectedAt)
    {
        public double ConnectedAt { get; } = connectedAt;

        public bool Joined { get; set; }

        public int Slot { get; set; }

        public string Name { get; set; } = "";

        /// <summary>The join the host gave it as it joined (<see cref="SessionPlayer.Join"/>), or 0 before.</summary>
        public int Join { get; set; }

        /// <summary>The newest edit this client has been sent (while it downloads, the newest its payload holds).</summary>
        public long SentThrough { get; set; }

        /// <summary>Its join payload while it downloads, or null.</summary>
        public MemoryStream? Download { get; set; }

        /// <summary>Bytes of <see cref="Download"/> sent so far.</summary>
        public int DownloadSent { get; set; }

        /// <summary>When its hello was accepted (a <see cref="Stopwatch"/> timestamp), for the join's log line.</summary>
        public long JoinStarted { get; set; }

        /// <summary>How long saving its payload took.</summary>
        public double SavedMilliseconds { get; set; }
    }

    /// <summary>The options' session settings, with the mode, transport, and address of <paramref name="start"/>.</summary>
    private static SessionSettings SettingsFor(SessionSettings options, MatchStart start) =>
        new()
        {
            Mode = start.Mode,
            Transport = start.Transport,
            Port = options.Port,
            Address = start.Address,
            PlayerName = options.PlayerName,
            MaxPlayers = options.MaxPlayers,
            ConnectTimeoutSeconds = options.ConnectTimeoutSeconds,
            DisconnectTimeoutSeconds = options.DisconnectTimeoutSeconds,
            MaxJoinMegabytes = options.MaxJoinMegabytes,
        };

    /// <summary>
    /// The name this player gives: the configured one, else the Steam name (trimmed and cut to
    /// <see cref="SessionProtocol.MaxNameLength"/> characters), else <see cref="DefaultPlayerName"/>.
    /// </summary>
    internal static string ChoosePlayerName(string? configured, string? steamName)
    {
        if (configured is not null)
        {
            return configured;
        }

        var name = steamName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return DefaultPlayerName;
        }

        return SessionProtocol.CutName(name);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session: this player's name is {Name}, from {Source}")]
    private partial void LogPlayerName(string name, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session: offline, seed {Seed}")]
    private partial void LogOffline(ulong seed);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Session: hosting {Where} for up to {MaxPlayers} players, seed {Seed}"
    )]
    private partial void LogHosting(string where, int maxPlayers, ulong seed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session: {Name} left slot {Slot} ({Reason})")]
    private partial void LogPlayerLeft(string name, int slot, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Session: peer {Peer} sent what this game cannot accept: {Problem}"
    )]
    private partial void LogProtocolError(int peer, string problem);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Session: the handler of game message {Kind} failed on a message from slot {Slot}"
    )]
    private partial void LogHandlerFailed(Exception exception, byte kind, int slot);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Session: {Role}, {Peers} peer(s); in {MessagesIn} messages ({KilobytesIn:F1} KB), out {MessagesOut} ({KilobytesOut:F1} KB) over {Seconds:F1} s"
    )]
    private partial void LogStats(
        string role,
        int peers,
        long messagesIn,
        double kilobytesIn,
        long messagesOut,
        double kilobytesOut,
        double seconds
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Session: the connection could not send everything at once over {Seconds:F1} s: {Skipped} state message(s) skipped, {Held} reliable message(s) held to send later (at most {HeldKilobytes:F1} KB for one peer), {Refused} refused (last result {LastRefusal}), {Ended} peer(s) ended for falling behind"
    )]
    private partial void LogSendingBehind(
        int skipped,
        int held,
        double heldKilobytes,
        int refused,
        int lastRefusal,
        int ended,
        double seconds
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Session: peer {Peer} ({Name}, slot {Slot}) round trip {Milliseconds} ms"
    )]
    private partial void LogPeerStats(int peer, string name, int slot, int milliseconds);
}
