using System.Numerics;
using Itera.Engine.Networking;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// <see cref="SimulatedNetwork"/> over <see cref="FakeTransport"/> with a clock the test steps: delay both ways, order
/// under jitter (reliable kept, sequenced free), loss of sequenced messages only, events kept in order, held messages
/// sent before a disconnect or close, the round trip and room, a session's join through delayed links, and no allocation.
/// </summary>
public sealed class SimulatedNetworkTests : IDisposable
{
    private readonly FakeNetwork _network = new();
    private readonly List<IDisposable> _owned = [];
    private long _now;

    // The unwrapped transports of the last link made, for sends that skip a wrapper and for the room.
    private FakeTransport _hostTransport = null!;
    private FakeTransport _clientTransport = null!;

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    [Fact]
    public void Messages_both_ways_arrive_after_the_delay_and_not_before()
    {
        var (host, client, hostInbox, clientInbox) = Linked(new NetworkConditions(100, 0, 0), wrapHost: false);

        // The connection reaches the client after the delay.
        client.Poll(clientInbox);
        Assert.Empty(clientInbox.Entries);
        _now = 99;
        client.Poll(clientInbox);
        Assert.Empty(clientInbox.Entries);
        _now = 100;
        client.Poll(clientInbox);
        Assert.Equal([Kind.Connected], clientInbox.Kinds);

        // A message the client sends at 100 leaves at 200.
        client.Send(1, Bytes(7), SessionChannel.Reliable);
        _now = 199;
        client.Poll(clientInbox);
        host.Poll(hostInbox);
        Assert.Empty(hostInbox.Received);
        _now = 200;
        client.Poll(clientInbox);
        host.Poll(hostInbox);
        Assert.Equal([7], hostInbox.Received);

        // The host's reply is sent at 200 and reaches the client's poll at 300.
        _hostTransport.Send(1, Bytes(8), SessionChannel.Reliable);
        client.Poll(clientInbox);
        _now = 299;
        client.Poll(clientInbox);
        Assert.Empty(clientInbox.Received);
        _now = 300;
        client.Poll(clientInbox);
        Assert.Equal([8], clientInbox.Received);
    }

    [Fact]
    public void Both_peers_wrapped_add_their_delays_one_after_the_other()
    {
        var (host, client, hostInbox, clientInbox) = Linked(new NetworkConditions(50, 0, 0), wrapHost: true);
        Run(60, 1, host, client, hostInbox, clientInbox);
        client.Send(1, Bytes(1), SessionChannel.Reliable);

        var arrivedAt = Run(400, 1, host, client, hostInbox, clientInbox, until: () => hostInbox.Received.Count > 0);

        // Out of the client's wrapper after 50 and into the host's inbox after 50 more.
        Assert.Equal(60 + 100, arrivedAt);
    }

    [Fact]
    public void Reliable_messages_keep_their_order_under_jitter_and_sequenced_ones_can_overtake()
    {
        var conditions = new NetworkConditions(20, 300, 0);
        var reliable = ReceivedOrder(conditions, SessionChannel.Reliable, 1);
        var sequenced = ReceivedOrder(conditions, SessionChannel.Sequenced, 1);

        Assert.Equal(Enumerable.Range(0, 40), reliable);
        Assert.Equal(40, sequenced.Count);
        Assert.Equal(Enumerable.Range(0, 40), sequenced.Order());
        Assert.NotEqual(Enumerable.Range(0, 40), sequenced);
    }

    [Fact]
    public void What_the_wrapped_peer_receives_stays_in_order_under_jitter()
    {
        var (host, client, hostInbox, clientInbox) = Linked(
            new NetworkConditions(20, 300, 0),
            wrapHost: false,
            seed: 4
        );
        Run(400, 1, host, client, hostInbox, clientInbox);
        for (var i = 0; i < 40; i++)
        {
            _hostTransport.Send(1, Bytes(i), SessionChannel.Sequenced);
        }

        Run(1000, 1, host, client, hostInbox, clientInbox);

        Assert.Equal(Enumerable.Range(0, 40), clientInbox.Received);
    }

    [Fact]
    public void Loss_drops_only_sequenced_messages_close_to_the_share_asked_for()
    {
        var (host, client, hostInbox, clientInbox) = Linked(new NetworkConditions(0, 0, 25), wrapHost: false, seed: 9);
        Run(5, 1, host, client, hostInbox, clientInbox);
        const int Count = 4000;
        for (var i = 0; i < Count; i++)
        {
            client.Send(1, Bytes(i), SessionChannel.Sequenced);
        }

        Run(5, 1, host, client, hostInbox, clientInbox);

        var lost = 100.0 * (Count - hostInbox.Received.Count) / Count;
        Assert.InRange(lost, 22, 28);
    }

    [Fact]
    public void Loss_drops_unreliable_messages_as_it_drops_sequenced_ones()
    {
        var (host, client, hostInbox, clientInbox) = Linked(new NetworkConditions(0, 0, 25), wrapHost: false, seed: 9);
        Run(5, 1, host, client, hostInbox, clientInbox);
        const int Count = 4000;
        for (var i = 0; i < Count; i++)
        {
            client.Send(1, Bytes(i), SessionChannel.Unreliable);
        }

        Run(5, 1, host, client, hostInbox, clientInbox);

        var lost = 100.0 * (Count - hostInbox.Received.Count) / Count;
        Assert.InRange(lost, 22, 28);
    }

    [Fact]
    public void A_flush_sends_what_is_due_before_the_next_poll_and_flushes_the_inner_transport()
    {
        var (host, client, hostInbox, clientInbox) = Linked(new NetworkConditions(100, 0, 0), wrapHost: false);
        Run(100, 1, host, client, hostInbox, clientInbox);
        client.Send(1, Bytes(5), SessionChannel.Unreliable);

        // Not due yet: a flush leaves it held.
        _now += 99;
        client.Flush();
        host.Poll(hostInbox);
        Assert.Empty(hostInbox.Received);

        // Due: the flush after a step sends it, with no poll of the wrapper.
        _now += 1;
        client.Flush();
        host.Poll(hostInbox);
        Assert.Equal([5], hostInbox.Received);
        Assert.Equal(2, _clientTransport.Flushes);
    }

    [Fact]
    public void Loss_never_drops_a_reliable_message_even_at_a_hundred_percent()
    {
        var (host, client, hostInbox, clientInbox) = Linked(new NetworkConditions(0, 0, 100), wrapHost: false, seed: 2);
        Run(5, 1, host, client, hostInbox, clientInbox);
        for (var i = 0; i < 300; i++)
        {
            client.Send(1, Bytes(i), SessionChannel.Reliable);
            client.Send(1, Bytes(-1), SessionChannel.Sequenced);
        }

        Run(5, 1, host, client, hostInbox, clientInbox);

        Assert.Equal(Enumerable.Range(0, 300), hostInbox.Received);
    }

    [Fact]
    public void With_no_conditions_everything_passes_at_once()
    {
        var (host, client, hostInbox, clientInbox) = Linked(default, wrapHost: true);

        client.Poll(clientInbox);
        host.Poll(hostInbox);
        Assert.Equal([Kind.Connected], clientInbox.Kinds);
        Assert.Equal([Kind.Connected], hostInbox.Kinds);
        client.Send(1, Bytes(3), SessionChannel.Reliable);
        host.Poll(hostInbox);
        Assert.Equal([3], hostInbox.Received);
        host.Send(1, Bytes(4), SessionChannel.Sequenced);
        client.Poll(clientInbox);
        Assert.Equal([4], clientInbox.Received);
    }

    [Fact]
    public void Connect_receive_and_disconnect_events_keep_their_order_under_jitter()
    {
        var (host, client, hostInbox, clientInbox) = Linked(new NetworkConditions(50, 200, 0), wrapHost: true, seed: 6);
        Run(300, 1, host, client, hostInbox, clientInbox);
        client.Send(1, Bytes(1), SessionChannel.Reliable);
        client.Send(1, Bytes(2), SessionChannel.Reliable);
        client.Disconnect(1, [9, 8]);

        Run(1500, 1, host, client, hostInbox, clientInbox);

        Assert.Equal([Kind.Connected, Kind.Received, Kind.Received, Kind.Disconnected], hostInbox.Kinds);
        Assert.Equal([1, 2], hostInbox.Received);
        Assert.Equal(TransportDisconnect.Closed, hostInbox.Entries[^1].Reason);
        Assert.Equal([9, 8], hostInbox.Entries[^1].Data);
        Assert.Equal([Kind.Connected, Kind.Disconnected], clientInbox.Kinds);
        Assert.Equal(TransportDisconnect.Local, clientInbox.Entries[^1].Reason);
    }

    [Fact]
    public void Disconnect_sends_that_peers_held_messages_first_and_leaves_the_others_held()
    {
        var hostInner = new FakeTransport(_network);
        var host = new SimulatedNetwork(hostInner, new NetworkConditions(1000, 0, 0), () => _now, 1);
        host.Listen(47600);
        var (first, firstInbox) = Connected();
        var (second, secondInbox) = Connected();
        var hostInbox = new Inbox();
        host.Poll(hostInbox);
        _now = 1000;
        host.Poll(hostInbox);
        Assert.Equal([Kind.Connected, Kind.Connected], hostInbox.Kinds);
        host.Send(1, Bytes(1), SessionChannel.Reliable);
        host.Send(2, Bytes(2), SessionChannel.Reliable);
        host.Send(1, Bytes(3), SessionChannel.Sequenced);

        host.Disconnect(1, [5]);
        first.Poll(firstInbox);
        second.Poll(secondInbox);

        Assert.Equal([Kind.Connected, Kind.Received, Kind.Received, Kind.Disconnected], firstInbox.Kinds);
        Assert.Equal([1, 3], firstInbox.Received);
        Assert.Equal([5], firstInbox.Entries[^1].Data);
        Assert.Equal([Kind.Connected], secondInbox.Kinds);
        _now = 2000;
        host.Poll(hostInbox);
        second.Poll(secondInbox);
        Assert.Equal([2], secondInbox.Received);
    }

    [Fact]
    public void Close_sends_every_held_message_first_and_leaves_nothing_held()
    {
        var (host, client, hostInbox, clientInbox) = Linked(new NetworkConditions(500, 0, 0), wrapHost: false);
        _now = 500;
        client.Poll(clientInbox);
        client.Send(1, Bytes(1), SessionChannel.Reliable);
        client.Send(1, Bytes(2), SessionChannel.Sequenced);

        client.Close([4]);
        host.Poll(hostInbox);

        Assert.Equal([Kind.Connected, Kind.Received, Kind.Received, Kind.Disconnected], hostInbox.Kinds);
        Assert.Equal([1, 2], hostInbox.Received);
        Assert.Equal([4], hostInbox.Entries[^1].Data);
        // Nothing waits to be sent or told after it.
        var before = clientInbox.Entries.Count;
        _now = 5000;
        client.Poll(clientInbox);
        host.Poll(hostInbox);
        Assert.Equal(before, clientInbox.Entries.Count);
        Assert.Equal(4, hostInbox.Entries.Count);
    }

    [Fact]
    public void The_round_trip_adds_twice_the_delay_when_it_is_known()
    {
        var (_, client, _, _) = Linked(new NetworkConditions(40, 0, 0), wrapHost: false);

        Assert.Equal(1 + 80, client.RoundTripMilliseconds(1));
        Assert.Equal(-1, client.RoundTripMilliseconds(77));
    }

    [Fact]
    public void The_reliable_room_is_the_inner_room_less_the_reliable_bytes_held()
    {
        var (_, client, _, clientInbox) = Linked(new NetworkConditions(100, 0, 0), wrapHost: false);
        _clientTransport.ReliableBytesPerPoll = 1000;
        Assert.Equal(1000, client.ReliableRoom(1));

        client.Send(1, new byte[300], SessionChannel.Reliable);
        client.Send(1, new byte[200], SessionChannel.Sequenced);
        Assert.Equal(700, client.ReliableRoom(1));
        client.Send(1, new byte[900], SessionChannel.Reliable);
        Assert.Equal(0, client.ReliableRoom(1));

        _now = 100;
        client.Poll(clientInbox);
        Assert.Equal(1000, client.ReliableRoom(1));
    }

    [Fact]
    public void A_host_and_a_client_complete_a_join_through_links_of_100_ms_each_way()
    {
        var generator = new BoxesGenerator(200, new VoxelBox(-40, 200, -40, 40, 260, 40, BoxesGenerator.Pattern));
        var conditions = new NetworkConditions(100, 0, 0);
        var world = TestWorlds.Load(generator, Vector3.Zero, seed: 17);
        _owned.Add(world);
        var host = Session(
            new SessionSettings { Mode = SessionMode.Host, MaxPlayers = 4 },
            new SimulatedNetwork(new FakeTransport(_network), conditions, () => _now, 1),
            world,
            17
        );
        host.Initialize();
        var clientWorld = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 2,
                Seed = 99,
            },
            0,
            generator,
            NullLogger<VoxelWorld>.Instance
        );
        _owned.Add(clientWorld);
        var client = Session(
            new SessionSettings
            {
                Mode = SessionMode.Join,
                Address = "host:47600",
                PlayerName = "Tester",
            },
            new SimulatedNetwork(new FakeTransport(_network), conditions, () => _now, 2),
            clientWorld,
            99
        );
        client.Begin();

        JoinProgress progress;
        do
        {
            _now += 10;
            progress = client.PollJoin();
            host.Poll();
        } while (progress.Stage is not (JoinStage.Joined or JoinStage.Failed) && _now < 10_000);

        Assert.Null(progress.Failure);
        Assert.Equal(JoinStage.Joined, progress.Stage);
        Assert.Equal(17UL, client.Match.Seed);
        Assert.True(progress.TotalBytes > 0);
        Assert.Equal(progress.TotalBytes, progress.DownloadedBytes);
        // Connecting, the hello, the welcome, and the first chunk each take 100 ms in each wrapper they cross.
        Assert.True(_now >= 400, $"The join took {_now} ms.");
        Assert.Equal(1, host.JoinedPeers);
    }

    [Fact]
    public void A_steady_state_of_sends_and_polls_allocates_nothing()
    {
        var inner = new StubTransport();
        var network = new SimulatedNetwork(inner, new NetworkConditions(5, 6, 10), () => _now, 12);
        var sink = new Inbox { KeepEntries = false };
        var message = new byte[100];
        for (var i = 0; i < 3000; i++)
        {
            Step(network, message, sink);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 2000; i++)
        {
            Step(network, message, sink);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(inner.Sent > 4000);
        Assert.True(sink.ReceivedCount > 1500);
    }

    private void Step(SimulatedNetwork network, byte[] message, Inbox sink)
    {
        _now++;
        network.Send(1, message, SessionChannel.Reliable);
        network.Send(1, message, SessionChannel.Sequenced);
        network.Poll(sink);
    }

    /// <summary>Sends 40 numbered messages from a wrapped client and returns the order the host hears them in.</summary>
    private List<int> ReceivedOrder(NetworkConditions conditions, SessionChannel channel, int seed)
    {
        var network = new FakeNetwork();
        var (host, client, hostInbox, clientInbox) = Linked(conditions, wrapHost: false, seed: seed, network: network);
        Run(400, 1, host, client, hostInbox, clientInbox);
        for (var i = 0; i < 40; i++)
        {
            client.Send(1, Bytes(i), channel);
        }

        Run(1500, 1, host, client, hostInbox, clientInbox);
        return hostInbox.Received;
    }

    /// <summary>
    /// A host that listens and a client that connects to it, with <paramref name="conditions"/> on the client (and on the
    /// host when asked), the clock at 0.
    /// </summary>
    private (ISessionTransport Host, SimulatedNetwork Client, Inbox HostInbox, Inbox ClientInbox) Linked(
        NetworkConditions conditions,
        bool wrapHost,
        int seed = 1,
        FakeNetwork? network = null
    )
    {
        network ??= _network;
        _now = 0;
        _hostTransport = new FakeTransport(network);
        _clientTransport = new FakeTransport(network);
        ISessionTransport host = wrapHost
            ? new SimulatedNetwork(_hostTransport, conditions, () => _now, seed + 100)
            : _hostTransport;
        var client = new SimulatedNetwork(_clientTransport, conditions, () => _now, seed);
        host.Listen(47600);
        client.Connect("host");
        var hostInbox = new Inbox();
        var clientInbox = new Inbox();
        if (!wrapHost)
        {
            host.Poll(hostInbox);
        }

        return (host, client, hostInbox, clientInbox);
    }

    /// <summary>A client of the host that listens on the test's network, connected on a transport of its own, and its inbox.</summary>
    private (FakeTransport Client, Inbox Inbox) Connected()
    {
        var client = new FakeTransport(_network);
        client.Connect("host");
        var inbox = new Inbox();
        client.Poll(inbox);
        return (client, inbox);
    }

    /// <summary>Steps the clock <paramref name="step"/> ms at a time to <paramref name="until"/> ms past now, polling both.</summary>
    private long Run(
        int milliseconds,
        int step,
        ISessionTransport host,
        SimulatedNetwork client,
        Inbox hostInbox,
        Inbox clientInbox,
        Func<bool>? until = null
    )
    {
        var end = _now + milliseconds;
        while (_now < end)
        {
            _now += step;
            client.Poll(clientInbox);
            host.Poll(hostInbox);
            if (until is not null && until())
            {
                break;
            }
        }

        return _now;
    }

    private SessionService Session(SessionSettings settings, ISessionTransport transport, VoxelWorld world, ulong seed)
    {
        var session = new SessionService(
            settings,
            60,
            seed,
            0,
            world,
            transport,
            NullLogger<SessionService>.Instance,
            () => _now / 1000.0,
            () => { }
        );
        _owned.Add(session);
        return session;
    }

    private static byte[] Bytes(int value) => BitConverter.GetBytes(value);

    private enum Kind
    {
        Connected,
        Disconnected,
        Received,
    }

    private sealed record Entry(Kind Kind, int Peer, TransportDisconnect Reason, byte[] Data);

    /// <summary>Keeps what a transport delivers, copying the data that is valid only during the call.</summary>
    private sealed class Inbox : ISessionTransportListener
    {
        public bool KeepEntries { get; init; } = true;

        public List<Entry> Entries { get; } = [];

        public int ReceivedCount { get; private set; }

        public List<Kind> Kinds => [.. Entries.Select(entry => entry.Kind)];

        /// <summary>The numbers in the messages received.</summary>
        public List<int> Received =>
            [.. Entries.Where(entry => entry.Kind == Kind.Received).Select(entry => BitConverter.ToInt32(entry.Data))];

        public void OnConnected(int peer)
        {
            if (KeepEntries)
            {
                Entries.Add(new Entry(Kind.Connected, peer, default, []));
            }
        }

        public void OnDisconnected(int peer, TransportDisconnect reason, ReadOnlySpan<byte> data)
        {
            if (KeepEntries)
            {
                Entries.Add(new Entry(Kind.Disconnected, peer, reason, data.ToArray()));
            }
        }

        public void OnReceive(int peer, ReadOnlySpan<byte> data)
        {
            ReceivedCount++;
            if (KeepEntries)
            {
                Entries.Add(new Entry(Kind.Received, peer, default, data.ToArray()));
            }
        }
    }

    /// <summary>A transport that counts what it is sent and delivers one message each poll, allocating nothing.</summary>
    private sealed class StubTransport : ISessionTransport
    {
        private readonly byte[] _message = new byte[100];

        public int Sent { get; private set; }

        public int LocalPort => 0;

        public int MaxUnreliableBytes => 1024;

        public void Listen(int port) { }

        public void Connect(string address) { }

        public void Poll(ISessionTransportListener listener) => listener.OnReceive(1, _message);

        public void Send(int peer, ReadOnlySpan<byte> data, SessionChannel channel) => Sent++;

        public void Disconnect(int peer, ReadOnlySpan<byte> data) { }

        public int RoundTripMilliseconds(int peer) => 1;

        public void Close(ReadOnlySpan<byte> data) { }
    }
}
