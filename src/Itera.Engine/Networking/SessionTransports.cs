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

/// <summary>LiteNetLib (<see cref="NetworkService"/>) for direct sessions, Steam's sockets (<see cref="SteamTransport"/>) for Steam's.</summary>
internal sealed class SessionTransports(NetworkService network, SteamTransport steam) : ISessionTransports
{
    public ISessionTransport Transport(SessionTransportKind kind) =>
        kind == SessionTransportKind.Steam ? steam : network;
}
