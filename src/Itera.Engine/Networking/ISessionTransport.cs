namespace Itera.Engine.Networking;

/// <summary>How a message travels.</summary>
public enum SessionChannel
{
    /// <summary>Arrives once, in the order sent: the join, edits, and requests.</summary>
    Reliable,

    /// <summary>
    /// May be lost, and may arrive out of order (LiteNetLib drops a stale one; Steam does not), and a transport skips it
    /// while too much is queued to a peer, so reliable messages keep their room: for state sent every tick, which must
    /// carry what orders it (its tick).
    /// </summary>
    Sequenced,

    /// <summary>
    /// May be lost and may arrive out of order, and no transport drops one for arriving after a newer one; skipped while
    /// too much is queued to a peer, as <see cref="Sequenced"/> is. For messages that carry their own numbers and
    /// acknowledgements (replication's packets: several sent together each step, any of which may come first).
    /// </summary>
    Unreliable,
}

/// <summary>Why a connection ended.</summary>
public enum TransportDisconnect
{
    /// <summary>The other side closed it, perhaps with data saying why.</summary>
    Closed,

    /// <summary>Nothing arrived for the disconnect timeout.</summary>
    TimedOut,

    /// <summary>A connection attempt found no host, or the host refused it.</summary>
    ConnectFailed,

    /// <summary>This side closed it.</summary>
    Local,
}

/// <summary>
/// What a transport could not send at once since the last <see cref="ISessionTransport.TakeStats"/>: sequenced messages
/// it skipped because too much was queued, reliable messages it held to send again later (and the most bytes held for
/// one peer), messages refused for another reason (with the last refusal's code), and peers it ended because their
/// held messages passed its limit.
/// </summary>
public readonly record struct TransportStats(
    int SkippedStates,
    int HeldReliable,
    int MostHeldBytes,
    int Refused,
    int LastRefusal,
    int EndedPeers
)
{
    public bool Any => SkippedStates > 0 || HeldReliable > 0 || Refused > 0 || EndedPeers > 0;
}

/// <summary>
/// A connection as its transport measures it now (<see cref="ISessionTransport.TryGetStatus"/>), for the log: the round
/// trip; the share of packets that arrive, as this end and the other end measure them (0 to 1, or -1 while not known);
/// the packets and bytes a second each way; the rate the transport lets itself send at; the bytes queued to send and how
/// long the next would wait; the reliable bytes sent and not yet acknowledged; and its route ("direct", or the relay it
/// goes through).
/// </summary>
public readonly record struct ConnectionStatus(
    int RoundTripMilliseconds,
    float QualityHere,
    float QualityThere,
    float OutPacketsPerSecond,
    float OutBytesPerSecond,
    float InPacketsPerSecond,
    float InBytesPerSecond,
    int SendRateBytesPerSecond,
    int QueuedReliableBytes,
    int QueuedUnreliableBytes,
    int UnacknowledgedReliableBytes,
    long QueueMicroseconds,
    string Route
);

/// <summary>Receives what a transport's <see cref="ISessionTransport.Poll"/> delivers, on the thread that polls.</summary>
public interface ISessionTransportListener
{
    /// <summary>A connection to <paramref name="peer"/> is open (on a client, to the host).</summary>
    void OnConnected(int peer);

    /// <summary>
    /// The connection to <paramref name="peer"/> ended. <paramref name="data"/> is what the other side sent with its
    /// disconnect, or empty; it is valid only during the call.
    /// </summary>
    void OnDisconnected(int peer, TransportDisconnect reason, ReadOnlySpan<byte> data);

    /// <summary>A message from <paramref name="peer"/>; <paramref name="data"/> is valid only during the call.</summary>
    void OnReceive(int peer, ReadOnlySpan<byte> data);
}

/// <summary>
/// Carries a session's messages between peers. LiteNetLib (<see cref="NetworkService"/>) carries LAN and direct
/// addresses; Steam's networking sockets (<see cref="SteamTransport"/>) carry internet play through Steam's relays. Peers
/// are plain ids that each transport maps to its own connections. Every call runs on the main thread.
/// </summary>
public interface ISessionTransport
{
    /// <summary>Opens a socket that accepts connections on <paramref name="port"/> (0 picks a free one).</summary>
    void Listen(int port);

    /// <summary>The port a listening transport is bound to, or 0.</summary>
    int LocalPort { get; }

    /// <summary>
    /// Opens a socket and starts connecting to the host at <paramref name="address"/>, in this transport's form (checked by
    /// the options); <see cref="ISessionTransportListener.OnConnected"/> follows, or a disconnect saying why not.
    /// </summary>
    void Connect(string address);

    /// <summary>Delivers everything that arrived since the last call to <paramref name="listener"/>.</summary>
    void Poll(ISessionTransportListener listener);

    /// <summary>Sends <paramref name="data"/> to <paramref name="peer"/>; the transport copies it before returning.</summary>
    void Send(int peer, ReadOnlySpan<byte> data, SessionChannel channel);

    /// <summary>Closes the connection to <paramref name="peer"/>, sending <paramref name="data"/> (small) with it.</summary>
    void Disconnect(int peer, ReadOnlySpan<byte> data);

    /// <summary>
    /// The largest message the <see cref="SessionChannel.Sequenced"/> and <see cref="SessionChannel.Unreliable"/>
    /// channels take now, to every connected peer, in bytes. Per-step state goes in batches no larger than this:
    /// LiteNetLib does not split such a message, and the session refuses a larger one.
    /// </summary>
    int MaxUnreliableBytes { get; }

    /// <summary>
    /// Starts sending what was handed to <see cref="Send"/> at once, where the transport would otherwise wait for its own
    /// next send (LiteNetLib's thread sends every 15 ms): the session calls it after each fixed step. A transport that
    /// sends at once does nothing (the default).
    /// </summary>
    void Flush() { }

    /// <summary>The round trip to <paramref name="peer"/> in milliseconds, or -1 when unknown.</summary>
    int RoundTripMilliseconds(int peer);

    /// <summary>
    /// What the transport measures of the connection to <paramref name="peer"/> now, for the session's stats line; false
    /// when it measures nothing past the round trip (the default), or the peer is gone.
    /// </summary>
    bool TryGetStatus(int peer, out ConnectionStatus status)
    {
        status = default;
        return false;
    }

    /// <summary>
    /// The transport's own account of the connection to <paramref name="peer"/> (Steam's detailed status), on one line, for
    /// the log when nothing arrives over it; empty when it has none (the default), or the peer is gone.
    /// </summary>
    string DescribeConnection(int peer) => "";

    /// <summary>
    /// Bytes of reliable messages <paramref name="peer"/> takes now without the transport holding them back: a host
    /// sends a join's payload a share at a time while there is room. A transport that queues everything has room for any
    /// amount (the default).
    /// </summary>
    int ReliableRoom(int peer) => int.MaxValue;

    /// <summary>What could not be sent at once since the last call; a transport that queues everything reports none.</summary>
    TransportStats TakeStats() => default;

    /// <summary>
    /// Sets a value friends can read without joining (Steam's lobby data: the players, the limit, the match's key), kept
    /// until <see cref="Close"/>; a transport without a lobby keeps nothing.
    /// </summary>
    void SetLobbyData(string key, string value) { }

    /// <summary>Closes every connection, sending <paramref name="data"/> with each, and the socket. Safe to call again.</summary>
    void Close(ReadOnlySpan<byte> data);
}
