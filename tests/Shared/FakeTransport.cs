using Itera.Engine.Networking;

namespace Itera.TestSupport;

/// <summary>A network of <see cref="FakeTransport"/>s in one process: one host and any number of clients.</summary>
internal sealed class FakeNetwork
{
    public FakeTransport? Host { get; set; }
}

/// <summary>
/// A transport that delivers in memory: what one side sends reaches the other side's next <see cref="Poll"/>, in order
/// (unless <see cref="SequencedFirst"/>), and nothing is lost. No sockets and no clocks, so session tests do not depend
/// on timing.
/// </summary>
internal sealed class FakeTransport(FakeNetwork network) : ISessionTransport
{
    private readonly LinkedList<Event> _inbox = new();

    // Peer id here -> the other side's transport and this side's id there.
    private readonly Dictionary<int, (FakeTransport Remote, int IdThere)> _links = [];
    private int _nextPeer = 1;

    /// <summary>When true, sends are only counted: for allocation tests, since a delivered message is a copy.</summary>
    public bool CountSendsOnly { get; set; }

    /// <summary>
    /// When true, a sequenced or unreliable message this side sends reaches the other side ahead of the messages still
    /// waiting there, as LiteNetLib's separate channels may deliver it past a long reliable download.
    /// </summary>
    public bool SequencedFirst { get; set; }

    /// <summary>How many times the session asked this side to send at once (<see cref="Flush"/>).</summary>
    public int Flushes { get; private set; }

    /// <summary>
    /// The reliable bytes this side may send between two of its polls before <see cref="ReliableRoom"/> says there is no
    /// room, as a send buffer that empties as the network carries it would; no limit by default.
    /// </summary>
    public int ReliableBytesPerPoll { get; set; } = int.MaxValue;

    private long _reliableSincePoll;

    /// <summary>When set, a copy of every message and disconnect this side sends, in order: for the wire fingerprint.</summary>
    public List<byte[]>? Recorded { get; set; }

    public int Sends { get; private set; }

    public long BytesSent { get; private set; }

    public int LocalPort { get; private set; }

    public bool Closed { get; private set; }

    public void Listen(int port)
    {
        // A transport closed by one session listens again for the next (a new match hosting on it).
        Closed = false;
        network.Host = this;
        LocalPort = port == 0 ? 50_000 : port;
    }

    public void Connect(string address)
    {
        var target = network.Host;
        if (target is null || target.Closed)
        {
            _inbox.AddLast(new Event(EventKind.Disconnected, 0, TransportDisconnect.ConnectFailed, []));
            return;
        }

        var idAtHost = target._nextPeer++;
        var idHere = _nextPeer++;
        _links[idHere] = (target, idAtHost);
        target._links[idAtHost] = (this, idHere);
        target._inbox.AddLast(new Event(EventKind.Connected, idAtHost, default, []));
        _inbox.AddLast(new Event(EventKind.Connected, idHere, default, []));
    }

    public void Poll(ISessionTransportListener listener)
    {
        _reliableSincePoll = 0;
        while (_inbox.First is { } first)
        {
            var next = first.Value;
            _inbox.RemoveFirst();
            switch (next.Kind)
            {
                case EventKind.Connected:
                    listener.OnConnected(next.Peer);
                    break;
                case EventKind.Disconnected:
                    listener.OnDisconnected(next.Peer, next.Reason, next.Data);
                    break;
                default:
                    listener.OnReceive(next.Peer, next.Data);
                    break;
            }
        }
    }

    public void Send(int peer, ReadOnlySpan<byte> data, SessionChannel channel)
    {
        if (channel != SessionChannel.Reliable && data.Length > MaxUnreliableBytes)
        {
            throw new InvalidOperationException(
                $"A message of {data.Length} bytes that may be lost is over the {MaxUnreliableBytes}-byte limit."
            );
        }

        Sends++;
        BytesSent += data.Length;
        Recorded?.Add(data.ToArray());
        if (channel == SessionChannel.Reliable)
        {
            _reliableSincePoll += data.Length;
        }

        if (CountSendsOnly || !_links.TryGetValue(peer, out var link))
        {
            return;
        }

        var delivered = new Event(EventKind.Received, link.IdThere, default, data.ToArray());
        if (SequencedFirst && channel != SessionChannel.Reliable)
        {
            link.Remote.Overtake(delivered);
        }
        else
        {
            link.Remote._inbox.AddLast(delivered);
        }
    }

    /// <summary>Puts <paramref name="message"/> ahead of the messages waiting here, behind connections and leavings.</summary>
    private void Overtake(Event message)
    {
        for (var node = _inbox.First; node is not null; node = node.Next)
        {
            if (node.Value.Kind == EventKind.Received)
            {
                _inbox.AddBefore(node, message);
                return;
            }
        }

        _inbox.AddLast(message);
    }

    public void Disconnect(int peer, ReadOnlySpan<byte> data)
    {
        Recorded?.Add(data.ToArray());
        if (!_links.Remove(peer, out var link))
        {
            return;
        }

        link.Remote._links.Remove(link.IdThere);
        link.Remote._inbox.AddLast(
            new Event(EventKind.Disconnected, link.IdThere, TransportDisconnect.Closed, data.ToArray())
        );
        _inbox.AddLast(new Event(EventKind.Disconnected, peer, TransportDisconnect.Local, []));
    }

    /// <summary>Makes the connection to <paramref name="peer"/> time out on both sides, as a lost network would.</summary>
    public void Lose(int peer)
    {
        if (_links.Remove(peer, out var link))
        {
            link.Remote._links.Remove(link.IdThere);
            link.Remote._inbox.AddLast(
                new Event(EventKind.Disconnected, link.IdThere, TransportDisconnect.TimedOut, [])
            );
            _inbox.AddLast(new Event(EventKind.Disconnected, peer, TransportDisconnect.TimedOut, []));
        }
    }

    /// <summary>The sequenced and unreliable channels' limit the fake reports; sends above it fail the test.</summary>
    public int MaxUnreliableBytes { get; set; } = 1024;

    public void Flush() => Flushes++;

    public int RoundTripMilliseconds(int peer) => _links.ContainsKey(peer) ? 1 : -1;

    public int ReliableRoom(int peer) => (int)Math.Clamp(ReliableBytesPerPoll - _reliableSincePoll, 0, int.MaxValue);

    public void Close(ReadOnlySpan<byte> data)
    {
        foreach (var peer in _links.Keys.ToArray())
        {
            Disconnect(peer, data);
        }

        _inbox.Clear();
        Closed = true;
    }

    /// <summary>The ids of this side's open connections.</summary>
    public IReadOnlyCollection<int> Peers => _links.Keys;

    private enum EventKind
    {
        Connected,
        Disconnected,
        Received,
    }

    private readonly record struct Event(EventKind Kind, int Peer, TransportDisconnect Reason, byte[] Data);
}
