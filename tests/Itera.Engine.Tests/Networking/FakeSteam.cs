using Itera.Engine.Networking;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// Steam in memory: players (one <see cref="FakeSteamPlatform"/> each), friendships, P2P connections between them, and
/// lobbies with their data.
/// What one side does reaches the other side's next <see cref="FakeSteamPlatform.RunCallbacks"/> or
/// <see cref="FakeSteamPlatform.ReceiveMessages"/>, as Steam's callbacks and messages do.
/// </summary>
internal sealed class FakeSteamNetwork
{
    private readonly Dictionary<ulong, FakeSteamPlatform> _players = [];
    private readonly Dictionary<ulong, ulong> _lobbyOwners = [];
    private readonly Dictionary<ulong, Dictionary<string, string>> _lobbyData = [];
    private readonly Dictionary<ulong, List<ulong>> _friends = [];
    private readonly Dictionary<ulong, HashSet<ulong>> _lobbyMembers = [];

    // Steam's handles are large 32-bit numbers (3,714,443,553 on this machine, 2026-09-26): past int.MaxValue.
    private uint _nextConnection = 3_714_443_553;
    private ulong _nextLobby = 109_775_240_000_000_001;

    public FakeSteamPlatform AddPlayer(ulong steamId)
    {
        var player = new FakeSteamPlatform(this, steamId);
        _players[steamId] = player;
        return player;
    }

    public FakeSteamPlatform? Player(ulong steamId) => _players.GetValueOrDefault(steamId);

    public uint NextConnection() => _nextConnection++;

    public ulong OpenLobby(ulong owner)
    {
        var lobby = _nextLobby++;
        _lobbyOwners[lobby] = owner;
        _lobbyMembers[lobby] = [owner];
        return lobby;
    }

    /// <summary>Puts <paramref name="player"/> in an open lobby; false when there is none.</summary>
    public bool EnterLobby(ulong lobby, ulong player)
    {
        if (!_lobbyMembers.TryGetValue(lobby, out var members))
        {
            return false;
        }

        members.Add(player);
        return true;
    }

    public void ExitLobby(ulong lobby, ulong player)
    {
        if (_lobbyMembers.TryGetValue(lobby, out var members))
        {
            members.Remove(player);
        }
    }

    public bool InLobby(ulong lobby, ulong player) =>
        _lobbyMembers.TryGetValue(lobby, out var members) && members.Contains(player);

    public ulong LobbyOwner(ulong lobby) => _lobbyOwners.GetValueOrDefault(lobby);

    public void CloseLobby(ulong lobby)
    {
        _lobbyOwners.Remove(lobby);
        _lobbyData.Remove(lobby);
        _lobbyMembers.Remove(lobby);
    }

    /// <summary>The open lobbies (each until its owner leaves it).</summary>
    public IReadOnlyCollection<ulong> OpenLobbies => _lobbyOwners.Keys;

    /// <summary>Makes two players friends, in the order each one's list holds them.</summary>
    public void Befriend(ulong a, ulong b)
    {
        Friends(a).Add(b);
        Friends(b).Add(a);
    }

    public List<ulong> Friends(ulong player)
    {
        if (!_friends.TryGetValue(player, out var friends))
        {
            friends = [];
            _friends[player] = friends;
        }

        return friends;
    }

    public void SetLobbyData(ulong lobby, string key, string value)
    {
        if (!_lobbyData.TryGetValue(lobby, out var data))
        {
            data = [];
            _lobbyData[lobby] = data;
        }

        data[key] = value;
    }

    public string LobbyData(ulong lobby, string key) =>
        _lobbyData.TryGetValue(lobby, out var data) ? data.GetValueOrDefault(key, "") : "";
}

/// <summary>One Steam player of a <see cref="FakeSteamNetwork"/>.</summary>
internal sealed class FakeSteamPlatform(FakeSteamNetwork network, ulong steamId) : ISteamPlatform
{
    private readonly Queue<Action<ISteamPlatformListener>> _callbacks = new();
    private readonly Queue<Action<ISteamFriendsListener>> _friendCallbacks = new();
    private readonly HashSet<ulong> _requestedLobbies = [];
    private readonly Queue<(uint Connection, byte[] Data)> _inbox = new();
    private readonly HashSet<uint> _watched = [];

    // This side's connection -> the other player and their side's connection.
    private readonly Dictionary<uint, (FakeSteamPlatform Remote, uint There)> _links = [];

    // The bytes each connection has queued since the last Drain; messages still arrive at once.
    private readonly Dictionary<uint, int> _queued = [];

    public ulong LocalSteamId { get; } = steamId;

    public string PersonaName { get; set; } = $"Steam {steamId}";

    public ISteamPlatformListener? Listener { get; set; }

    public ISteamFriendsListener? FriendsListener { get; set; }

    /// <summary>A picture the player's friends see (RGBA rows), or null for none; while <see cref="AvatarPending"/>, none yet.</summary>
    public (int Width, int Height, byte[] Rgba)? Avatar { get; set; }

    /// <summary>True while Steam would still be fetching the picture.</summary>
    public bool AvatarPending { get; set; }

    /// <summary>Lobbies whose data this player asked for.</summary>
    public IReadOnlyCollection<ulong> RequestedLobbies => _requestedLobbies;

    public int FriendCount => network.Friends(LocalSteamId).Count;

    public ulong FriendAt(int index) => network.Friends(LocalSteamId)[index];

    public string FriendName(ulong friend) => network.Player(friend)?.PersonaName ?? "";

    public bool IsFriend(ulong steamId) => network.Friends(LocalSteamId).Contains(steamId);

    public bool InLobby(ulong lobby, ulong steamId) => network.InLobby(lobby, steamId);

    public ulong FriendLobby(ulong friend) => network.Player(friend)?.OwnedLobby ?? 0;

    public void RequestLobbyData(ulong lobby)
    {
        _requestedLobbies.Add(lobby);
        _friendCallbacks.Enqueue(l => l.OnLobbyData(lobby));
    }

    string ISteamPlatform.LobbyData(ulong lobby, string key) => network.LobbyData(lobby, key);

    public bool FriendAvatar(ulong friend, out int width, out int height, out byte[]? rgba)
    {
        width = 0;
        height = 0;
        rgba = null;
        var player = network.Player(friend);
        if (player is null)
        {
            return true;
        }

        if (player.AvatarPending)
        {
            return false;
        }

        if (player.Avatar is { } avatar)
        {
            (width, height, rgba) = avatar;
        }

        return true;
    }

    /// <summary>Steam reports to this player that <paramref name="friend"/> changed (name, game, presence).</summary>
    public void ReportFriendChanged(ulong friend) => _friendCallbacks.Enqueue(l => l.OnFriendChanged(friend));

    /// <summary>Steam reports to this player that <paramref name="friend"/>'s picture arrived.</summary>
    public void ReportAvatarLoaded(ulong friend) => _friendCallbacks.Enqueue(l => l.OnAvatarLoaded(friend));

    /// <summary>A friend picked "Join game" in Steam for this player's game: Steam reports it here.</summary>
    public void RequestJoin(ulong friend, string address) =>
        _friendCallbacks.Enqueue(l => l.OnJoinRequested(friend, address));

    public bool Listening { get; private set; }

    public ulong OwnedLobby { get; private set; }

    public Dictionary<string, string> RichPresence { get; } = [];

    public Dictionary<string, string> LobbyData { get; } = [];

    /// <summary>
    /// Steam's send buffer: a message that would take a connection's queued bytes past it is refused as
    /// <see cref="SteamSendResult.QueueFull"/> (Steam's k_EResultLimitExceeded, 25). No limit by default.
    /// </summary>
    public int SendBufferBytes { get; set; } = int.MaxValue;

    /// <summary>
    /// When true, what this player sends leaves its connection's queue as the other side receives it, as a network
    /// carrying it would; otherwise only <see cref="Drain"/> empties the queue.
    /// </summary>
    public bool FreedOnReceipt { get; set; }

    /// <summary>Messages refused so far, reliable ones counted apart.</summary>
    public int RefusedReliable { get; private set; }

    /// <summary>Empties every connection's queue, as the network carrying it away would.</summary>
    public void Drain() => _queued.Clear();

    public void RunCallbacks()
    {
        while (_callbacks.TryDequeue(out var callback))
        {
            if (Listener is not null)
            {
                callback(Listener);
            }
            else
            {
                // As SteamPlatform does: a lobby made or entered with nobody listening is left.
                callback(new LeavingListener(this));
            }
        }

        while (_friendCallbacks.TryDequeue(out var callback))
        {
            if (FriendsListener is not null)
            {
                callback(FriendsListener);
            }
        }
    }

    public uint CreateListenSocket()
    {
        Listening = true;
        return 77;
    }

    public void CloseListenSocket(uint socket) => Listening = false;

    public uint ConnectP2P(ulong steamId)
    {
        var here = network.NextConnection();
        var remote = network.Player(steamId);
        if (remote is null || !remote.Listening)
        {
            // k_ESteamNetConnectionEnd_Misc_P2P_Rendezvous: nobody answered.
            _callbacks.Enqueue(l =>
                l.OnConnectionChanged(here, steamId, SteamConnectionState.Problem, false, 5008, "no answer")
            );
            return here;
        }

        var there = network.NextConnection();
        _links[here] = (remote, there);
        remote._links[there] = (this, here);
        remote._callbacks.Enqueue(l =>
            l.OnConnectionChanged(there, LocalSteamId, SteamConnectionState.Connecting, true, 0, "")
        );
        return here;
    }

    public bool AcceptConnection(uint connection)
    {
        if (!_links.TryGetValue(connection, out var link))
        {
            return false;
        }

        _callbacks.Enqueue(l =>
            l.OnConnectionChanged(connection, link.Remote.LocalSteamId, SteamConnectionState.Connected, true, 0, "")
        );
        link.Remote._callbacks.Enqueue(l =>
            l.OnConnectionChanged(link.There, LocalSteamId, SteamConnectionState.Connected, false, 0, "")
        );
        return true;
    }

    public void CloseConnection(uint connection, int reason, string? debug, bool linger)
    {
        _watched.Remove(connection);
        if (!_links.Remove(connection, out var link))
        {
            return;
        }

        link.Remote._links.Remove(link.There);
        link.Remote._callbacks.Enqueue(l =>
            l.OnConnectionChanged(
                link.There,
                LocalSteamId,
                SteamConnectionState.ClosedByPeer,
                false,
                reason,
                debug ?? ""
            )
        );
    }

    /// <summary>Ends a connection as a network failure would: a timeout on both sides.</summary>
    public void Lose(uint connection)
    {
        if (_links.Remove(connection, out var link))
        {
            link.Remote._links.Remove(link.There);
            _callbacks.Enqueue(l =>
                l.OnConnectionChanged(
                    connection,
                    link.Remote.LocalSteamId,
                    SteamConnectionState.Problem,
                    false,
                    4001,
                    "timed out"
                )
            );
            link.Remote._callbacks.Enqueue(l =>
                l.OnConnectionChanged(link.There, LocalSteamId, SteamConnectionState.Problem, false, 4001, "timed out")
            );
        }
    }

    public IReadOnlyCollection<uint> Connections => _links.Keys;

    public void Watch(uint connection) => _watched.Add(connection);

    public SteamSendResult Send(uint connection, ReadOnlySpan<byte> data, bool reliable, out int code)
    {
        if (!_links.TryGetValue(connection, out var link))
        {
            // k_EResultNoConnection.
            code = 3;
            return SteamSendResult.Refused;
        }

        var queued = _queued.GetValueOrDefault(connection);
        if ((long)queued + data.Length > SendBufferBytes)
        {
            RefusedReliable += reliable ? 1 : 0;
            code = 25;
            return SteamSendResult.QueueFull;
        }

        _queued[connection] = queued + data.Length;
        link.Remote._inbox.Enqueue((link.There, data.ToArray()));
        code = 1;
        return SteamSendResult.Sent;
    }

    public int QueuedBytes(uint connection) =>
        _links.ContainsKey(connection) ? _queued.GetValueOrDefault(connection) : -1;

    public void ReceiveMessages(SteamMessageHandler handler)
    {
        var count = _inbox.Count;
        for (var i = 0; i < count; i++)
        {
            var (connection, data) = _inbox.Dequeue();
            if (_watched.Contains(connection))
            {
                if (_links.TryGetValue(connection, out var link) && link.Remote.FreedOnReceipt)
                {
                    link.Remote._queued[link.There] = link.Remote._queued.GetValueOrDefault(link.There) - data.Length;
                }

                handler(connection, data);
            }
            else if (_links.ContainsKey(connection))
            {
                // Not watched yet: kept, as Steam keeps a connection's messages until they are received.
                _inbox.Enqueue((connection, data));
            }
        }
    }

    public int RoundTripMilliseconds(uint connection) => _links.ContainsKey(connection) ? 30 : -1;

    public bool TryGetStatus(uint connection, out ConnectionStatus status)
    {
        if (!_links.ContainsKey(connection))
        {
            status = default;
            return false;
        }

        var queued = _queued.GetValueOrDefault(connection);
        status = new ConnectionStatus(30, 1, 1, 60, 40_000, 60, 4_000, 256 * 1024, queued, 0, 0, 0, "direct");
        return true;
    }

    public string DetailedStatus(uint connection) =>
        _links.TryGetValue(connection, out var link) ? $"connection {connection} to {link.Remote.LocalSteamId}" : "";

    public void CreateLobby(int maxMembers)
    {
        var lobby = network.OpenLobby(LocalSteamId);
        OwnedLobby = lobby;
        _callbacks.Enqueue(l => l.OnLobbyCreated(lobby, true));
    }

    public void JoinLobby(ulong lobby)
    {
        var owner = network.LobbyOwner(lobby);
        var entered = owner != 0 && network.EnterLobby(lobby, LocalSteamId);
        _callbacks.Enqueue(l => l.OnLobbyEntered(lobby, owner, entered));
    }

    public void LeaveLobby(ulong lobby)
    {
        network.ExitLobby(lobby, LocalSteamId);
        if (lobby == OwnedLobby)
        {
            network.CloseLobby(lobby);
            OwnedLobby = 0;
        }
    }

    public void SetLobbyData(ulong lobby, string key, string value)
    {
        LobbyData[key] = value;
        network.SetLobbyData(lobby, key, value);
    }

    public void SetRichPresence(string? key, string? value)
    {
        if (key is null)
        {
            RichPresence.Clear();
        }
        else
        {
            RichPresence[key] = value ?? "";
        }
    }
}

/// <summary>Takes a lobby callback that arrives with no listener, and leaves the lobby it made or entered.</summary>
internal sealed class LeavingListener(FakeSteamPlatform platform) : ISteamPlatformListener
{
    public void OnConnectionChanged(
        uint connection,
        ulong remoteSteamId,
        SteamConnectionState state,
        bool incoming,
        int endReason,
        string endDebug
    ) { }

    public void OnLobbyCreated(ulong lobby, bool succeeded)
    {
        if (succeeded)
        {
            platform.LeaveLobby(lobby);
        }
    }

    public void OnLobbyEntered(ulong lobby, ulong owner, bool succeeded)
    {
        if (succeeded)
        {
            platform.LeaveLobby(lobby);
        }
    }
}
