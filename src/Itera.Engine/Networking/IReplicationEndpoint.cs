namespace Itera.Engine.Networking;

/// <summary>
/// What the session hands the match's replication (<c>Replication.ReplicationService</c>): its kinds' fingerprint for the
/// hello, a client's snapshots, and a host's replies, each during the session's poll on the main thread; a body that
/// cannot be read throws <see cref="InvalidDataException"/>, which drops the peer as any bad message does.
/// </summary>
internal interface IReplicationEndpoint
{
    /// <summary>The replicated kinds' fingerprint: a client's hello carries it, and a host refuses one of another.</summary>
    ulong KindsFingerprint { get; }

    /// <summary>On a client, a snapshot from the host (after its kind byte).</summary>
    void ReceiveSnapshot(ReadOnlySpan<byte> body);

    /// <summary>On a host, a reply from the client in <paramref name="slot"/> (after its kind byte).</summary>
    void ReceiveReply(int slot, ReadOnlySpan<byte> body);

    /// <summary>
    /// On a host, counts a game message of <paramref name="bytes"/> to the client in <paramref name="slot"/> against its
    /// bytes; false, counting nothing, for one that <paramref name="mayLose"/> once the game's share of them is spent, which
    /// is not sent. True for a client that has not replied yet, on a client, and offline.
    /// </summary>
    bool SpendGame(int slot, int bytes, bool mayLose);
}
