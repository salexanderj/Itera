using System.Buffers;

namespace Itera.Engine.Networking;

/// <summary>
/// The delay, jitter, and loss <see cref="SimulatedNetwork"/> adds: a fixed delay and a random extra of up to
/// <paramref name="JitterMilliseconds"/> (milliseconds, both), and the share of sequenced and unreliable messages
/// dropped (0 to 100).
/// </summary>
public readonly record struct NetworkConditions(int DelayMilliseconds, int JitterMilliseconds, double LossPercent)
{
    /// <summary>True when any of the three is above zero.</summary>
    public bool Any => DelayMilliseconds > 0 || JitterMilliseconds > 0 || LossPercent > 0;
}

/// <summary>
/// Makes this peer's links behave as if the other side were far away, over any <see cref="ISessionTransport"/>:
/// LiteNetLib's own simulation is not in its NuGet build. A sent message is held until the clock passes the delay plus a
/// random jitter. A sequenced or unreliable one may overtake others and is dropped at the loss share; a reliable one is
/// never dropped and keeps its order to its peer. What is due also leaves when the session flushes after a step. Everything the inner transport delivers is held the same way, in order and with no
/// loss (the receiver cannot tell a message's channel), so the conditions on one peer delay both directions of its
/// links, while loss and reordering apply to what it sends. Held messages are copies in pooled buffers; once its lists
/// have grown to the traffic it allocates nothing. Main thread only.
/// </summary>
public sealed class SimulatedNetwork : ISessionTransport
{
    private readonly ISessionTransport _inner;
    private readonly NetworkConditions _conditions;
    private readonly Func<long> _nowMilliseconds;
    private readonly Random _random;
    private readonly HoldingListener _holding;

    // Held sends in the order they were made; a sequenced one may be due before an earlier one.
    private readonly List<HeldSend> _sends = [];
    private readonly Queue<HeldEvent> _events = new();
    private readonly Dictionary<int, PeerState> _peers = [];
    private long _lastEventRelease;
    private long _pollTime;

    /// <param name="inner">The transport whose links get the conditions.</param>
    /// <param name="conditions">What to add; with none, every call passes straight through.</param>
    /// <param name="nowMilliseconds">The clock, in milliseconds from any start.</param>
    /// <param name="seed">Seeds the random draws of jitter and loss.</param>
    public SimulatedNetwork(ISessionTransport inner, NetworkConditions conditions, Func<long> nowMilliseconds, int seed)
    {
        _inner = inner;
        _conditions = conditions;
        _nowMilliseconds = nowMilliseconds;
        _random = new Random(seed);
        _holding = new HoldingListener(this);
    }

    public int LocalPort => _inner.LocalPort;

    public int MaxUnreliableBytes => _inner.MaxUnreliableBytes;

    public void Listen(int port) => _inner.Listen(port);

    public void Connect(string address) => _inner.Connect(address);

    public TransportStats TakeStats() => _inner.TakeStats();

    public void SetLobbyData(string key, string value) => _inner.SetLobbyData(key, value);

    public int RoundTripMilliseconds(int peer)
    {
        var inner = _inner.RoundTripMilliseconds(peer);
        return inner < 0 ? inner : inner + 2 * _conditions.DelayMilliseconds;
    }

    public int ReliableRoom(int peer)
    {
        var room = _inner.ReliableRoom(peer);
        if (!_peers.TryGetValue(peer, out var state))
        {
            return room;
        }

        return Math.Max(0, room - state.HeldReliableBytes);
    }

    public void Send(int peer, ReadOnlySpan<byte> data, SessionChannel channel)
    {
        if (!_conditions.Any)
        {
            _inner.Send(peer, data, channel);
            return;
        }

        var reliable = channel == SessionChannel.Reliable;
        if (!reliable && _conditions.LossPercent > 0 && _random.NextDouble() * 100 < _conditions.LossPercent)
        {
            return;
        }

        var releaseAt = _nowMilliseconds() + _conditions.DelayMilliseconds + Jitter();
        if (!_peers.TryGetValue(peer, out var state))
        {
            state = new PeerState();
            _peers.Add(peer, state);
        }

        if (reliable)
        {
            releaseAt = Math.Max(releaseAt, state.LastReliableRelease);
            state.LastReliableRelease = releaseAt;
            state.HeldReliableBytes += data.Length;
        }

        _sends.Add(new HeldSend(releaseAt, peer, Copy(data), data.Length, channel));
    }

    /// <summary>Hands every due held send to the inner transport, then flushes it.</summary>
    public void Flush()
    {
        if (_conditions.Any)
        {
            ReleaseSends(_nowMilliseconds());
        }

        _inner.Flush();
    }

    public void Poll(ISessionTransportListener listener)
    {
        if (!_conditions.Any)
        {
            _inner.Poll(listener);
            return;
        }

        var now = _nowMilliseconds();
        _pollTime = now;
        ReleaseSends(now);
        _inner.Poll(_holding);
        while (_events.TryPeek(out var next) && next.ReleaseAt <= now)
        {
            _events.Dequeue();
            try
            {
                var data = new ReadOnlySpan<byte>(next.Buffer, 0, next.Length);
                switch (next.Kind)
                {
                    case EventKind.Connected:
                        listener.OnConnected(next.Peer);
                        break;
                    case EventKind.Disconnected:
                        // The peer's link is over: its ordering state goes with it.
                        _peers.Remove(next.Peer);
                        listener.OnDisconnected(next.Peer, next.Reason, data);
                        break;
                    default:
                        listener.OnReceive(next.Peer, data);
                        break;
                }
            }
            finally
            {
                Return(next.Buffer);
            }
        }
    }

    public void Disconnect(int peer, ReadOnlySpan<byte> data)
    {
        // What the peer was sent before it is told goodbye reaches it first.
        var kept = 0;
        for (var i = 0; i < _sends.Count; i++)
        {
            var held = _sends[i];
            if (held.Peer == peer)
            {
                Release(held);
            }
            else
            {
                _sends[kept++] = held;
            }
        }

        _sends.RemoveRange(kept, _sends.Count - kept);
        _peers.Remove(peer);
        _inner.Disconnect(peer, data);
    }

    public void Close(ReadOnlySpan<byte> data)
    {
        foreach (var held in _sends)
        {
            Release(held);
        }

        _sends.Clear();
        _peers.Clear();
        _inner.Close(data);
        while (_events.TryDequeue(out var dropped))
        {
            Return(dropped.Buffer);
        }

        _lastEventRelease = 0;
    }

    /// <summary>Hands every due held send to the inner transport, in the order they were made.</summary>
    private void ReleaseSends(long now)
    {
        var kept = 0;
        for (var i = 0; i < _sends.Count; i++)
        {
            var held = _sends[i];
            if (held.ReleaseAt <= now)
            {
                Release(held);
            }
            else
            {
                _sends[kept++] = held;
            }
        }

        _sends.RemoveRange(kept, _sends.Count - kept);
    }

    /// <summary>Sends a held message on and gives its buffer back.</summary>
    private void Release(in HeldSend held)
    {
        if (held.Channel == SessionChannel.Reliable && _peers.TryGetValue(held.Peer, out var state))
        {
            state.HeldReliableBytes = Math.Max(0, state.HeldReliableBytes - held.Length);
        }

        try
        {
            _inner.Send(held.Peer, new ReadOnlySpan<byte>(held.Buffer, 0, held.Length), held.Channel);
        }
        finally
        {
            Return(held.Buffer);
        }
    }

    /// <summary>Holds an event from the inner transport until its release time, never before an earlier one.</summary>
    private void Hold(EventKind kind, int peer, TransportDisconnect reason, ReadOnlySpan<byte> data)
    {
        var releaseAt = Math.Max(_pollTime + _conditions.DelayMilliseconds + Jitter(), _lastEventRelease);
        _lastEventRelease = releaseAt;
        _events.Enqueue(new HeldEvent(releaseAt, kind, peer, reason, Copy(data), data.Length));
    }

    private int Jitter() => _conditions.JitterMilliseconds > 0 ? _random.Next(_conditions.JitterMilliseconds + 1) : 0;

    private static byte[] Copy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return [];
        }

        var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buffer);
        return buffer;
    }

    private static void Return(byte[] buffer)
    {
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private enum EventKind
    {
        Connected,
        Disconnected,
        Received,
    }

    private readonly record struct HeldSend(
        long ReleaseAt,
        int Peer,
        byte[] Buffer,
        int Length,
        SessionChannel Channel
    );

    private readonly record struct HeldEvent(
        long ReleaseAt,
        EventKind Kind,
        int Peer,
        TransportDisconnect Reason,
        byte[] Buffer,
        int Length
    );

    private sealed class PeerState
    {
        public long LastReliableRelease;
        public int HeldReliableBytes;
    }

    /// <summary>Takes what the inner transport delivers and holds it.</summary>
    private sealed class HoldingListener(SimulatedNetwork owner) : ISessionTransportListener
    {
        public void OnConnected(int peer) => owner.Hold(EventKind.Connected, peer, default, []);

        public void OnDisconnected(int peer, TransportDisconnect reason, ReadOnlySpan<byte> data) =>
            owner.Hold(EventKind.Disconnected, peer, reason, data);

        public void OnReceive(int peer, ReadOnlySpan<byte> data) => owner.Hold(EventKind.Received, peer, default, data);
    }
}
