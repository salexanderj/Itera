using System.Buffers;
using System.Globalization;
using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Networking;

/// <summary>
/// The Steam transport: Steam's networking sockets between Steam players, through Steam's relays, so no port has to be
/// opened. A host listens on a P2P socket and opens a friends-only lobby, and sets its rich
/// presence so friends can join from their friends list. A client joins a lobby and connects to its owner
/// (<c>lobby:ID</c>), or connects to a player directly (<c>steam:ID</c>). A disconnect's reason travels as a Steam end
/// reason in the app range (1000 plus the kind times 100 plus the reason) with its text. Peers are the connection
/// numbered from 1 as they open (Steam's handles are 32-bit numbers that do not fit a positive int). Steam refuses a
/// message while a connection's send buffer is full (a Steam test of 2026-09-27 lost an edit that way and ended the
/// client), so a sequenced message is skipped while more than <see cref="StateQueueLimitBytes"/> are queued to its
/// peer, and a reliable one Steam refuses for a full buffer is held and sent again, in order, at the next
/// <see cref="Poll"/>; a peer whose held messages pass <see cref="MaxHeldBytes"/> is ended. A join's payload goes out
/// <see cref="JoinWindowBytes"/> at a time (<see cref="ReliableRoom"/>). A host accepts a connection only from a member of
/// its lobby or a Steam friend. Main thread only.
/// </summary>
public sealed partial class SteamTransport : ISessionTransport, ISteamPlatformListener
{
    /// <summary>
    /// A sequenced message is skipped while more than this is queued to its peer: about an eighth of a second at the
    /// 250 KB/s a Steam connection carried in the test of 2026-09-27.
    /// </summary>
    internal const int StateQueueLimitBytes = 32 * 1024;

    /// <summary>A peer whose held reliable messages pass this many bytes cannot keep up and is ended.</summary>
    internal const int MaxHeldBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The most of a join's payload queued to a peer at once: half of Steam's default send buffer (512 KB), so states and
    /// edits keep room behind it (a payload sent in one loop was held past
    /// <see cref="MaxHeldBytes"/>, which ended every join past about 8.5 MB).
    /// </summary>
    internal const int JoinWindowBytes = 256 * 1024;

    private const int AppReasonBase = 1000;

    /// <summary>Steam cuts a close reason's text to 128 characters.</summary>
    private const int MaxCloseTextBytes = 120;

    private readonly SteamService _steam;
    private readonly int _maxPlayers;
    private readonly ILogger<SteamTransport> _logger;
    private readonly Queue<PendingChange> _changes = new();

    // Open connections: Steam's handle for each peer id, and the peer id for each handle.
    private readonly Dictionary<int, uint> _handles = [];
    private readonly Dictionary<uint, int> _peers = [];

    // What waits to be sent to each open peer.
    private readonly Dictionary<int, Outgoing> _outgoing = [];
    private readonly MessageWriter _slowWriter = new();
    private TransportStats _stats;
    private int _nextPeer = 1;
    private readonly MessageWriter _reasonWriter = new();
    private readonly MessageWriter _refusalWriter = new();
    private readonly SteamMessageHandler _onMessage;
    private ISteamPlatform? _platform;
    private ISessionTransportListener? _polling;
    private uint _listenSocket;
    private ulong _lobby;
    private bool _ownsLobby;
    private readonly Dictionary<string, string> _lobbyData = new(StringComparer.Ordinal);
    private uint _connecting;

    public SteamTransport(SteamService steam, IOptions<EngineOptions> options, ILogger<SteamTransport> logger)
        : this(steam, options.Value.Session.MaxPlayers, logger) { }

    internal SteamTransport(SteamService steam, int maxPlayers, ILogger<SteamTransport> logger)
    {
        _steam = steam;
        _maxPlayers = maxPlayers;
        _logger = logger;
        _onMessage = OnMessage;
    }

    /// <summary>Steam sockets have no port: 0.</summary>
    public int LocalPort => 0;

    /// <summary>The lobby this game hosts or joined, or 0.</summary>
    public ulong Lobby => _lobby;

    public void Listen(int port)
    {
        var platform = Start();
        _listenSocket = platform.CreateListenSocket();
        platform.CreateLobby(_maxPlayers);
        LogListening(platform.LocalSteamId);
    }

    public void Connect(string address)
    {
        var platform = Start();
        if (!SteamAddress.TryParse(address, out var kind, out var id))
        {
            throw new ArgumentException($"'{address}' is not lobby:ID or steam:ID.", nameof(address));
        }

        if (kind == SteamAddressKind.Lobby)
        {
            platform.JoinLobby(id);
            LogJoiningLobby(id);
        }
        else
        {
            ConnectTo(platform, id);
        }
    }

    public void Poll(ISessionTransportListener listener)
    {
        if (_platform is null)
        {
            return;
        }

        _polling = listener;
        try
        {
            SendHeld();
            _platform.RunCallbacks();
            while (_changes.TryDequeue(out var change))
            {
                Deliver(listener, change);
            }

            _platform.ReceiveMessages(_onMessage);
        }
        finally
        {
            _polling = null;
        }
    }

    public void Send(int peer, ReadOnlySpan<byte> data, SessionChannel channel)
    {
        if (
            _platform is null
            || !_handles.TryGetValue(peer, out var connection)
            || !_outgoing.TryGetValue(peer, out var outgoing)
        )
        {
            return;
        }

        if (channel == SessionChannel.Reliable)
        {
            // Once one is held, the rest wait behind it, so they arrive in the order sent.
            if (outgoing.Held.Count > 0)
            {
                Hold(peer, outgoing, data);
                return;
            }

            var result = _platform.Send(connection, data, reliable: true, out var code);
            if (result == SteamSendResult.Sent)
            {
                outgoing.Queued += data.Length;
            }
            else if (result == SteamSendResult.QueueFull)
            {
                Hold(peer, outgoing, data);
            }
            else
            {
                Refuse(code);
            }

            return;
        }

        if (outgoing.Held.Count > 0 || Queued(connection, outgoing) > StateQueueLimitBytes)
        {
            _stats = _stats with { SkippedStates = _stats.SkippedStates + 1 };
            return;
        }

        var sent = _platform.Send(connection, data, reliable: false, out var refusal);
        if (sent == SteamSendResult.Sent)
        {
            outgoing.Queued += data.Length;
        }
        else if (sent == SteamSendResult.QueueFull)
        {
            _stats = _stats with { SkippedStates = _stats.SkippedStates + 1 };
        }
        else
        {
            Refuse(refusal);
        }
    }

    public TransportStats TakeStats()
    {
        var stats = _stats;
        _stats = default;
        return stats;
    }

    public void Disconnect(int peer, ReadOnlySpan<byte> data)
    {
        if (_platform is null || !_handles.Remove(peer, out var connection))
        {
            return;
        }

        _peers.Remove(connection);
        Forget(peer);
        var (reason, text) = EndReason(data);
        _platform.CloseConnection(connection, reason, text, linger: true);
        // Steam reports nothing for a connection this side closed; the session still hears of it, as with LiteNetLib.
        var said = data.IsEmpty ? 0 : reason;
        _changes.Enqueue(new PendingChange(connection, SteamConnectionState.Problem, said, text, LocalPeer: peer));
    }

    /// <summary>
    /// Steam splits an unreliable message across packets (and loses it whole if any is lost), so smaller is better; a
    /// kilobyte keeps a batch near one packet.
    /// </summary>
    public int MaxUnreliableBytes => 1024;

    public int RoundTripMilliseconds(int peer) =>
        _platform is not null && _handles.TryGetValue(peer, out var connection)
            ? _platform.RoundTripMilliseconds(connection)
            : -1;

    /// <summary>
    /// What fits under <see cref="JoinWindowBytes"/> of the bytes queued to <paramref name="peer"/>; none while any of its
    /// messages are held.
    /// </summary>
    public int ReliableRoom(int peer)
    {
        if (
            _platform is null
            || !_handles.TryGetValue(peer, out var connection)
            || !_outgoing.TryGetValue(peer, out var outgoing)
            || outgoing.Held.Count > 0
        )
        {
            return 0;
        }

        return Math.Max(JoinWindowBytes - Queued(connection, outgoing), 0);
    }

    /// <summary>Writes the value into the lobby this host opened, now or once it exists; kept until <see cref="Close"/>.</summary>
    public void SetLobbyData(string key, string value)
    {
        _lobbyData[key] = value;
        if (_ownsLobby && _platform is not null)
        {
            _platform.SetLobbyData(_lobby, key, value);
        }
    }

    public void Close(ReadOnlySpan<byte> data)
    {
        if (_platform is null)
        {
            return;
        }

        var (reason, text) = EndReason(data);
        foreach (var connection in _handles.Values)
        {
            _platform.CloseConnection(connection, reason, text, linger: true);
        }

        _handles.Clear();
        _peers.Clear();
        foreach (var outgoing in _outgoing.Values)
        {
            outgoing.Clear();
        }

        _outgoing.Clear();
        _changes.Clear();
        if (_connecting != 0)
        {
            _platform.CloseConnection(_connecting, reason, text, linger: false);
            _connecting = 0;
        }

        if (_listenSocket != 0)
        {
            _platform.CloseListenSocket(_listenSocket);
            _listenSocket = 0;
        }

        if (_lobby != 0)
        {
            _platform.LeaveLobby(_lobby);
            _lobby = 0;
        }

        _ownsLobby = false;
        _lobbyData.Clear();

        _platform.SetRichPresence(null, null);
        _platform.Listener = null;
        _platform = null;
    }

    void ISteamPlatformListener.OnConnectionChanged(
        uint connection,
        ulong remoteSteamId,
        SteamConnectionState state,
        bool incoming,
        int endReason,
        string endDebug
    )
    {
        // The other side may write its own close text.
        endDebug = LogText.Printable(endDebug);
        if (state == SteamConnectionState.Connecting)
        {
            if (!incoming || _listenSocket == 0 || _platform is null)
            {
                return;
            }

            if (!Invited(_platform, remoteSteamId))
            {
                LogNotInvited(remoteSteamId);
                SessionProtocol.WriteDisconnect(
                    _refusalWriter,
                    DisconnectKind.Reject,
                    (byte)RejectReason.NotInvited,
                    "the host takes only its lobby's members and its Steam friends"
                );
                var (reason, text) = EndReason(_refusalWriter.Written);
                _platform.CloseConnection(connection, reason, text, linger: false);
            }
            else if (!_platform.AcceptConnection(connection))
            {
                LogAcceptFailed(remoteSteamId);
            }

            return;
        }

        if (state == SteamConnectionState.Connected)
        {
            LogConnected(remoteSteamId);
        }
        else
        {
            LogClosed(remoteSteamId, state, endReason, endDebug);
        }

        _changes.Enqueue(new PendingChange(connection, state, endReason, endDebug, LocalPeer: 0));
    }

    void ISteamPlatformListener.OnLobbyCreated(ulong lobby, bool succeeded)
    {
        if (!succeeded || _platform is null)
        {
            LogLobbyFailed();
            return;
        }

        if (_listenSocket == 0 || _ownsLobby)
        {
            // Made for a session that has since ended, or a second one: nobody would leave it.
            _platform.LeaveLobby(lobby);
            return;
        }

        _lobby = lobby;
        _ownsLobby = true;
        foreach (var (key, value) in _lobbyData)
        {
            _platform.SetLobbyData(lobby, key, value);
        }

        // What Steam passes to a friend's game when they pick "Join game" (SteamLaunchArguments reads it).
        var connect = string.Create(CultureInfo.InvariantCulture, $"{SteamLaunchArguments.ConnectLobby} {lobby}");
        _platform.SetRichPresence("connect", connect);
        LogLobbyOpen(lobby);
    }

    void ISteamPlatformListener.OnLobbyEntered(ulong lobby, ulong owner, bool succeeded)
    {
        if (_platform is null)
        {
            return;
        }

        if (succeeded && (_listenSocket != 0 || _lobby != 0))
        {
            // Entered after this transport began hosting or entered another: nobody would leave it.
            _platform.LeaveLobby(lobby);
            return;
        }

        if (!succeeded || owner == 0 || owner == _platform.LocalSteamId)
        {
            LogLobbyEnterFailed(lobby, succeeded ? "it has no other owner" : "Steam refused");
            _changes.Enqueue(new PendingChange(0, SteamConnectionState.Problem, 0, "", LocalPeer: 0));
            return;
        }

        _lobby = lobby;
        ConnectTo(_platform, owner);
    }

    private ISteamPlatform Start()
    {
        if (_platform is not null)
        {
            throw new InvalidOperationException("The Steam transport is already started.");
        }

        var platform =
            _steam.Platform
            ?? throw new SessionStartException("Steam is not available (" + _steam.UnavailableReason + ")");
        platform.Listener = this;
        _platform = platform;
        return platform;
    }

    /// <summary>
    /// True for a Steam friend of this host or a member of its lobby. A friend is checked first: a friends-only lobby's
    /// members are friends, and Steam may tell the host of a new member after its connection arrives.
    /// </summary>
    private bool Invited(ISteamPlatform platform, ulong steamId) =>
        platform.IsFriend(steamId) || (_ownsLobby && platform.InLobby(_lobby, steamId));

    private void ConnectTo(ISteamPlatform platform, ulong steamId)
    {
        _connecting = platform.ConnectP2P(steamId);
        LogConnecting(steamId);
    }

    private void Deliver(ISessionTransportListener listener, PendingChange change)
    {
        var connection = change.Connection;
        if (change.State == SteamConnectionState.Connected)
        {
            if (connection == _connecting)
            {
                _connecting = 0;
            }

            var id = _nextPeer++;
            _handles[id] = connection;
            _peers[connection] = id;
            _outgoing[id] = new Outgoing();
            _platform!.Watch(connection);
            listener.OnConnected(id);
            return;
        }

        if (change.LocalPeer != 0)
        {
            // With the reason this side gave, so the session can say why the peer went.
            listener.OnDisconnected(change.LocalPeer, TransportDisconnect.Local, Said(change));
            return;
        }

        var wasOpen = _peers.Remove(connection, out var peer);
        if (wasOpen)
        {
            _handles.Remove(peer);
            Forget(peer);
        }

        var wasConnecting = connection == _connecting || connection == 0;
        if (connection != 0)
        {
            // Frees the handle of a connection the other side or the network ended.
            _platform!.CloseConnection(connection, 0, null, linger: false);
        }

        if (connection == _connecting)
        {
            _connecting = 0;
        }

        if (!wasOpen && !wasConnecting)
        {
            return;
        }

        var said = Said(change);
        if (!said.IsEmpty)
        {
            listener.OnDisconnected(peer, TransportDisconnect.Closed, said);
        }
        else
        {
            var why =
                !wasOpen ? TransportDisconnect.ConnectFailed
                : IsTimeout(change.EndReason) ? TransportDisconnect.TimedOut
                : TransportDisconnect.Closed;
            listener.OnDisconnected(peer, why, []);
        }
    }

    /// <summary>A disconnect's data from a Steam end reason in the app range and its text; empty for another reason.</summary>
    private ReadOnlySpan<byte> Said(PendingChange change)
    {
        var reason = change.EndReason;
        if (reason is < AppReasonBase or >= AppReasonBase + 1000)
        {
            return [];
        }

        var kind = (DisconnectKind)((reason - AppReasonBase) / 100);
        var code = (byte)((reason - AppReasonBase) % 100);
        SessionProtocol.WriteDisconnect(_reasonWriter, kind, code, change.EndDebug);
        return _reasonWriter.Written;
    }

    /// <summary>Sends each peer's held messages in order, until Steam's buffer for it is full again.</summary>
    private void SendHeld()
    {
        foreach (var (peer, outgoing) in _outgoing)
        {
            outgoing.Fresh = false;
            if (!_handles.TryGetValue(peer, out var connection))
            {
                continue;
            }

            while (outgoing.Held.TryPeek(out var held))
            {
                var result = _platform!.Send(
                    connection,
                    held.Buffer.AsSpan(0, held.Length),
                    reliable: true,
                    out var code
                );
                if (result == SteamSendResult.QueueFull)
                {
                    break;
                }

                if (result == SteamSendResult.Refused)
                {
                    Refuse(code);
                }

                outgoing.Held.Dequeue();
                outgoing.HeldBytes -= held.Length;
                ArrayPool<byte>.Shared.Return(held.Buffer);
            }
        }
    }

    /// <summary>Keeps a reliable message to send later, or ends the peer when too much is held for it already.</summary>
    private void Hold(int peer, Outgoing outgoing, ReadOnlySpan<byte> data)
    {
        if (outgoing.HeldBytes + data.Length > MaxHeldBytes)
        {
            _stats = _stats with { EndedPeers = _stats.EndedPeers + 1 };
            LogTooSlow(peer, outgoing.HeldBytes / 1024);
            SessionProtocol.WriteDisconnect(
                _slowWriter,
                DisconnectKind.Bye,
                0,
                "the host ended this connection: it could not keep up"
            );
            Disconnect(peer, _slowWriter.Written);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        outgoing.Held.Enqueue((buffer, data.Length));
        outgoing.HeldBytes += data.Length;
        _stats = _stats with
        {
            HeldReliable = _stats.HeldReliable + 1,
            MostHeldBytes = (int)Math.Max(_stats.MostHeldBytes, outgoing.HeldBytes),
        };
    }

    private void Refuse(int code) => _stats = _stats with { Refused = _stats.Refused + 1, LastRefusal = code };

    /// <summary>
    /// The bytes queued to a peer: asked of Steam once a poll (the call takes Steam's lock), then counted up by what this
    /// side sends until the next.
    /// </summary>
    private int Queued(uint connection, Outgoing outgoing)
    {
        if (!outgoing.Fresh)
        {
            outgoing.Queued = Math.Max(_platform!.QueuedBytes(connection), 0);
            outgoing.Fresh = true;
        }

        return outgoing.Queued;
    }

    private void Forget(int peer)
    {
        if (_outgoing.Remove(peer, out var outgoing))
        {
            outgoing.Clear();
        }
    }

    private void OnMessage(uint connection, ReadOnlySpan<byte> data)
    {
        if (_peers.TryGetValue(connection, out var peer))
        {
            _polling?.OnReceive(peer, data);
        }
    }

    /// <summary>A disconnect's data as a Steam end reason in the app range and its text.</summary>
    private static (int Reason, string Text) EndReason(ReadOnlySpan<byte> data)
    {
        if (!SessionProtocol.TryReadDisconnect(data, out var kind, out var code, out var text))
        {
            return (AppReasonBase, "");
        }

        return (
            AppReasonBase + (int)kind * 100 + Math.Min((int)code, 99),
            SessionProtocol.Cut(text, MaxCloseTextBytes)
        );
    }

    // k_ESteamNetConnectionEnd_Remote_Timeout (4001) and k_ESteamNetConnectionEnd_Misc_Timeout (5003).
    private static bool IsTimeout(int reason) => reason is 4001 or 5003;

    /// <summary>What waits to be sent to one peer, and what is queued to it.</summary>
    private sealed class Outgoing
    {
        /// <summary>Reliable messages Steam refused for a full buffer, oldest first, in pooled buffers.</summary>
        public readonly Queue<(byte[] Buffer, int Length)> Held = new();

        public long HeldBytes;

        /// <summary>The bytes queued to the peer as Steam last said, plus what was sent since.</summary>
        public int Queued;

        /// <summary>Whether <see cref="Queued"/> was asked of Steam since the last poll.</summary>
        public bool Fresh;

        public void Clear()
        {
            while (Held.TryDequeue(out var held))
            {
                ArrayPool<byte>.Shared.Return(held.Buffer);
            }

            HeldBytes = 0;
        }
    }

    private readonly record struct PendingChange(
        uint Connection,
        SteamConnectionState State,
        int EndReason,
        string EndDebug,
        int LocalPeer
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam: hosting as {SteamId}; opening a friends-only lobby")]
    private partial void LogListening(ulong steamId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Steam: lobby {Lobby} is open to friends; they join from their friends list, or with --Engine:Session:Transport=Steam --Engine:Session:Mode=Join --Engine:Session:Address=lobby:{Lobby}"
    )]
    private partial void LogLobbyOpen(ulong lobby);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Steam: the lobby could not be made; friends can still join with --Engine:Session:Address=steam:ID (this player's Steam id)"
    )]
    private partial void LogLobbyFailed();

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam: joining lobby {Lobby}")]
    private partial void LogJoiningLobby(ulong lobby);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steam: could not enter lobby {Lobby}: {Reason}")]
    private partial void LogLobbyEnterFailed(ulong lobby, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam: connecting to {SteamId}")]
    private partial void LogConnecting(ulong steamId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Steam: connected to {SteamId}")]
    private partial void LogConnected(ulong steamId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Steam: connection to {SteamId} ended ({State}, reason {Reason}: {Debug})"
    )]
    private partial void LogClosed(ulong steamId, SteamConnectionState state, int reason, string debug);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steam: could not accept a connection from {SteamId}")]
    private partial void LogAcceptFailed(ulong steamId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Steam: refused a connection from {SteamId}: neither in the lobby nor a friend"
    )]
    private partial void LogNotInvited(ulong steamId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Steam: ending peer {Peer}: it could not keep up ({Kilobytes} KB of reliable messages held for it)"
    )]
    private partial void LogTooSlow(int peer, long kilobytes);
}
