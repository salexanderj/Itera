namespace Itera.Engine.Networking;

/// <summary>The state of a Steam connection, as the transport needs it.</summary>
internal enum SteamConnectionState
{
    /// <summary>Being set up; an incoming one waits to be accepted.</summary>
    Connecting,

    /// <summary>Open.</summary>
    Connected,

    /// <summary>The other side closed it.</summary>
    ClosedByPeer,

    /// <summary>It failed or timed out on this side.</summary>
    Problem,
}

/// <summary>What Steam did with a message given to <see cref="ISteamPlatform.Send"/>.</summary>
internal enum SteamSendResult
{
    /// <summary>Queued to be sent.</summary>
    Sent,

    /// <summary>Not queued: the connection has too much queued already (Steam's send buffer is full).</summary>
    QueueFull,

    /// <summary>Not queued for another reason: too large, or the connection is gone.</summary>
    Refused,
}

/// <summary>A message from a Steam connection; <paramref name="data"/> is valid only during the call.</summary>
internal delegate void SteamMessageHandler(uint connection, ReadOnlySpan<byte> data);

/// <summary>What Steam reports, during <see cref="ISteamPlatform.RunCallbacks"/>.</summary>
internal interface ISteamPlatformListener
{
    /// <summary>
    /// A connection changed state. <paramref name="incoming"/> is true for one that arrived on the listen socket.
    /// <paramref name="endReason"/> and <paramref name="endDebug"/> say why a closed one ended.
    /// </summary>
    void OnConnectionChanged(
        uint connection,
        ulong remoteSteamId,
        SteamConnectionState state,
        bool incoming,
        int endReason,
        string endDebug
    );

    /// <summary>A lobby this player asked for exists (or could not be made).</summary>
    void OnLobbyCreated(ulong lobby, bool succeeded);

    /// <summary>This player entered a lobby (or could not); <paramref name="owner"/> is its host.</summary>
    void OnLobbyEntered(ulong lobby, ulong owner, bool succeeded);
}

/// <summary>
/// What Steam reports about friends and lobbies, during <see cref="ISteamPlatform.RunCallbacks"/>: its own listener,
/// since the transport takes <see cref="ISteamPlatform.Listener"/> while a session runs.
/// </summary>
internal interface ISteamFriendsListener
{
    /// <summary>A friend's name, game, or rich presence changed.</summary>
    void OnFriendChanged(ulong friend);

    /// <summary>A lobby's data arrived or changed (after <see cref="ISteamPlatform.RequestLobbyData"/>).</summary>
    void OnLobbyData(ulong lobby);

    /// <summary>A friend's avatar is ready to read (<see cref="ISteamPlatform.FriendAvatar"/>).</summary>
    void OnAvatarLoaded(ulong friend);

    /// <summary>
    /// A friend picked "Join game" in Steam for this player's friend's game while it runs: <paramref name="address"/> is
    /// <c>lobby:ID</c> (or another <see cref="SteamAddress"/>), or empty when Steam gave nothing this game reads.
    /// </summary>
    void OnJoinRequested(ulong friend, string address);
}

/// <summary>
/// The Steam calls the transport makes: networking sockets (P2P through Steam's relays), lobbies, and rich presence.
/// <see cref="SteamPlatform"/> makes them through Steamworks.NET; tests use a fake. Main thread only.
/// </summary>
internal interface ISteamPlatform
{
    /// <summary>This player's 64-bit Steam id.</summary>
    ulong LocalSteamId { get; }

    /// <summary>This player's Steam name.</summary>
    string PersonaName { get; }

    /// <summary>Where <see cref="RunCallbacks"/> reports.</summary>
    ISteamPlatformListener? Listener { get; set; }

    /// <summary>Where <see cref="RunCallbacks"/> reports about friends and lobbies.</summary>
    ISteamFriendsListener? FriendsListener { get; set; }

    /// <summary>This player's friends (Steam's "immediate" friends).</summary>
    int FriendCount { get; }

    /// <summary>Friend <paramref name="index"/>'s Steam id.</summary>
    ulong FriendAt(int index);

    /// <summary>A friend's Steam name.</summary>
    string FriendName(ulong friend);

    /// <summary>True when <paramref name="steamId"/> is this player's friend (Steam's "immediate" friends).</summary>
    bool IsFriend(ulong steamId);

    /// <summary>True when <paramref name="steamId"/> is a member of <paramref name="lobby"/>, as Steam last told this player.</summary>
    bool InLobby(ulong lobby, ulong steamId);

    /// <summary>
    /// The lobby a friend plays this game in (the game they play is this app, in a lobby, or their rich presence's
    /// <c>connect</c> names one), or 0.
    /// </summary>
    ulong FriendLobby(ulong friend);

    /// <summary>Asks Steam for a lobby's data; <see cref="ISteamFriendsListener.OnLobbyData"/> follows.</summary>
    void RequestLobbyData(ulong lobby);

    /// <summary>A lobby's value for <paramref name="key"/>, or "" until its data has arrived.</summary>
    string LobbyData(ulong lobby, string key);

    /// <summary>
    /// A friend's medium avatar (64 px) as RGBA rows: false while Steam fetches it
    /// (<see cref="ISteamFriendsListener.OnAvatarLoaded"/> follows), true with null pixels for a friend without one.
    /// </summary>
    bool FriendAvatar(ulong friend, out int width, out int height, out byte[]? rgba);

    /// <summary>Delivers Steam's callbacks (connection changes, lobby results, invites) on this thread.</summary>
    void RunCallbacks();

    /// <summary>Opens the socket friends connect to (virtual port 0) and returns it.</summary>
    uint CreateListenSocket();

    void CloseListenSocket(uint socket);

    /// <summary>Starts connecting to a player through Steam and returns the connection.</summary>
    uint ConnectP2P(ulong steamId);

    bool AcceptConnection(uint connection);

    /// <summary>
    /// Closes a connection with an app reason (1000 to 1999) and a short text; <paramref name="linger"/> sends what is
    /// still queued first. Frees the handle; no callback follows.
    /// </summary>
    void CloseConnection(uint connection, int reason, string? debug, bool linger);

    /// <summary>Receives an open connection's messages in <see cref="ReceiveMessages"/>.</summary>
    void Watch(uint connection);

    /// <summary>Queues a message; <paramref name="code"/> is Steam's own result, for the log.</summary>
    SteamSendResult Send(uint connection, ReadOnlySpan<byte> data, bool reliable, out int code);

    /// <summary>The bytes queued on a connection and not sent yet (reliable and unreliable), or -1.</summary>
    int QueuedBytes(uint connection);

    /// <summary>Hands every message that arrived on a watched connection to <paramref name="handler"/>.</summary>
    void ReceiveMessages(SteamMessageHandler handler);

    /// <summary>The round trip on a connection in milliseconds, or -1.</summary>
    int RoundTripMilliseconds(uint connection);

    /// <summary>
    /// Asks for a friends-only lobby of <paramref name="maxMembers"/>; <see cref="ISteamPlatformListener.OnLobbyCreated"/>
    /// follows. A lobby made or entered while no <see cref="Listener"/> listens is left at once.
    /// </summary>
    void CreateLobby(int maxMembers);

    /// <summary>Asks to enter a lobby; <see cref="ISteamPlatformListener.OnLobbyEntered"/> follows.</summary>
    void JoinLobby(ulong lobby);

    void LeaveLobby(ulong lobby);

    void SetLobbyData(ulong lobby, string key, string value);

    /// <summary>Sets a rich presence key; null clears them all.</summary>
    void SetRichPresence(string? key, string? value);
}
