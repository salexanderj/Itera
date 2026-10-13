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
}
