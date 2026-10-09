using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Networking;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Networking;

/// <summary>A real LiteNetLib host and client on 127.0.0.1: the handshake, the join, and one live edit.</summary>
public sealed class LoopbackTests
{
    private static readonly DomeGenerator s_generator = new();

    [Fact]
    public void A_client_joins_a_host_over_loopback_and_gets_its_edits()
    {
        using var hostWorld = TestWorlds.Load(s_generator, Vector3.Zero, seed: 23);
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 200, 0), 30, VoxelEditOperation.Clear));
        using var hostTransport = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        using var host = Session(
            new SessionSettings { Mode = SessionMode.Host, Port = 1024 },
            hostWorld,
            hostTransport,
            () => { }
        );
        // Port 0 would pick a free port, but the options' range starts at 1024: listen through the transport instead.
        hostTransport.Listen(0);
        var port = hostTransport.LocalPort;
        host.Initialize();

        using var clientWorld = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 2,
                Seed = 1,
            },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        using var clientTransport = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        var settings = new SessionSettings { Mode = SessionMode.Join, Address = $"127.0.0.1:{port}" };
        using var client = Session(settings, clientWorld, clientTransport, host.Poll);

        client.Initialize();
        clientWorld.Initialize(client.Match.Seed);
        client.LoadJoinedWorld();
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(40, 200, 0), 20, VoxelEditOperation.Clear));
        host.AfterFixedStep();
        var deadline = Stopwatch.StartNew();
        while (clientWorld.EditCount < 2 && deadline.Elapsed.TotalSeconds < 5)
        {
            host.Poll();
            client.Poll();
            Thread.Yield();
        }

        Assert.Equal(23UL, clientWorld.Seed);
        Assert.Equal(2, clientWorld.EditCount);
        Assert.Equal(1, host.JoinedPeers);

        host.Stop();
        deadline.Restart();
        while (client.End == SessionEnd.None && deadline.Elapsed.TotalSeconds < 5)
        {
            client.Poll();
            Thread.Yield();
        }

        Assert.Equal(SessionEnd.HostLeft, client.End);
    }

    /// <summary>
    /// A host that has made 2,000 edits sends a game message on the sequenced channel after each of its polls while a
    /// client joins, as it sends its player's state every step: the download is longer than LiteNetLib's reliable window,
    /// so the messages overtake it, and the client drops them and joins (a LAN
    /// join to a host that had played a while failed).
    /// </summary>
    [Fact]
    public void A_client_joins_a_host_with_thousands_of_edits_while_the_host_sends_sequenced_messages()
    {
        using var hostWorld = TestWorlds.Load(s_generator, Vector3.Zero, seed: 23);
        // Out of the loaded columns, so each is only logged: 2,000 edits of 51 bytes in the payload.
        for (var i = 0; i < 2000; i++)
        {
            hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(5000 + i * 10, 200, 0), 3, VoxelEditOperation.Clear));
        }

        using var hostTransport = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        using var host = Session(
            new SessionSettings { Mode = SessionMode.Host, Port = 1024 },
            hostWorld,
            hostTransport,
            () => { }
        );
        hostTransport.Listen(0);
        var port = hostTransport.LocalPort;
        host.Initialize();
        using var clientWorld = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 2,
                Seed = 1,
            },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        using var clientTransport = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        var settings = new SessionSettings { Mode = SessionMode.Join, Address = $"127.0.0.1:{port}" };
        var sent = 0;
        // The game's handler of that kind: those that arrive after the download wait for the world, then reach it.
        var received = new CountingHandler();
        using var client = Session(
            settings,
            clientWorld,
            clientTransport,
            () =>
            {
                host.Poll();
                if (host.JoinedPeers == 1)
                {
                    var writer = host.BeginMessage(SessionService.FirstGameKind);
                    writer.WriteInt(sent++);
                    host.SendToAll(SessionChannel.Sequenced);
                }
            },
            [received]
        );

        client.Initialize();
        clientWorld.Initialize(client.Match.Seed);
        client.LoadJoinedWorld();

        Assert.True(sent > received.Count, $"Of {sent} sent, {received.Count} arrived: none overtook the download.");
        Assert.Equal(SessionEnd.None, client.End);
        Assert.Equal(2000, clientWorld.EditCount);
        host.Stop();
    }

    /// <summary>
    /// A host named, not numbered, is looked up off the main thread and joined at the poll that finds it.
    /// </summary>
    [Fact]
    public void A_client_joins_a_host_by_its_name()
    {
        using var hostWorld = TestWorlds.Load(s_generator, Vector3.Zero, seed: 23);
        using var hostTransport = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        using var host = Session(
            new SessionSettings { Mode = SessionMode.Host, Port = 1024 },
            hostWorld,
            hostTransport,
            () => { }
        );
        hostTransport.Listen(0);
        var port = hostTransport.LocalPort;
        host.Initialize();
        using var clientWorld = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 64,
                WorkerThreads = 2,
                Seed = 1,
            },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        using var clientTransport = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        var settings = new SessionSettings { Mode = SessionMode.Join, Address = $"localhost:{port}" };
        using var client = Session(settings, clientWorld, clientTransport, host.Poll);

        client.Initialize();

        Assert.Equal(23UL, client.Match.Seed);
        Assert.Equal(1, host.JoinedPeers);
        host.Stop();
    }

    [Fact]
    public void A_transport_hosts_again_on_its_port_after_a_session_closed_it()
    {
        using var world = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 64,
                Seed = 1,
            },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        using var transport = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        using var probe = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        // A free port, found by binding one and letting it go.
        probe.Listen(0);
        var port = probe.LocalPort;
        probe.Close([]);
        var settings = new SessionSettings { Mode = SessionMode.Host, Port = port };

        var first = new SessionService(
            settings,
            60,
            1,
            0,
            world,
            transport,
            NullLogger<SessionService>.Instance,
            () => 0,
            () => { }
        );
        first.Begin();
        Assert.Equal(port, transport.LocalPort);
        first.Stop();

        var second = new SessionService(
            settings,
            60,
            1,
            0,
            world,
            transport,
            NullLogger<SessionService>.Instance,
            () => 0,
            () => { }
        );
        second.Begin();

        Assert.Equal(SessionRole.Host, second.Role);
        Assert.Equal(port, transport.LocalPort);
        second.Stop();
    }

    [Fact]
    public void Hosting_on_a_port_another_socket_holds_fails_to_start_the_session()
    {
        using var holder = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        holder.Listen(0);
        using var world = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 64,
                Seed = 1,
            },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        using var transport = new NetworkService(
            NullLogger<NetworkService>.Instance,
            NullLoggerFactory.Instance,
            disconnectTimeoutSeconds: 5
        );
        using var host = Session(
            new SessionSettings { Mode = SessionMode.Host, Port = holder.LocalPort },
            world,
            transport,
            () => { }
        );

        var failure = Assert.Throws<SessionStartException>(host.Begin);

        Assert.Contains($"could not bind UDP port {holder.LocalPort}", failure.Message, StringComparison.Ordinal);
    }

    private static SessionService Session(
        SessionSettings settings,
        VoxelWorld world,
        NetworkService transport,
        Action pump,
        IEnumerable<ISessionGameHandler>? handlers = null
    ) =>
        new(
            settings,
            60,
            world.Seed,
            0,
            world,
            new ListeningOnce(transport),
            NullLogger<SessionService>.Instance,
            () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
            () =>
            {
                pump();
                Thread.Sleep(1);
            },
            handlers
        );

    /// <summary>A game message handler of the first game kind, from the host, that counts what it receives.</summary>
    private sealed class CountingHandler : ISessionGameHandler
    {
        public int Count { get; private set; }

        public byte Kind => SessionService.FirstGameKind;

        public SessionMessageDirection Direction => SessionMessageDirection.ToClients;

        public void Receive(int slot, ReadOnlySpan<byte> body) => Count++;
    }

    /// <summary>The real transport, except that a listen after the first is ignored (the test binds a free port first).</summary>
    private sealed class ListeningOnce(NetworkService inner) : ISessionTransport
    {
        public int LocalPort => inner.LocalPort;

        public void Listen(int port)
        {
            if (inner.LocalPort == 0)
            {
                inner.Listen(port);
            }
        }

        public void Connect(string address) => inner.Connect(address);

        public void Poll(ISessionTransportListener listener) => inner.Poll(listener);

        public void Send(int peer, ReadOnlySpan<byte> data, SessionChannel channel) => inner.Send(peer, data, channel);

        public void Disconnect(int peer, ReadOnlySpan<byte> data) => inner.Disconnect(peer, data);

        public int MaxUnreliableBytes => inner.MaxUnreliableBytes;

        public int RoundTripMilliseconds(int peer) => inner.RoundTripMilliseconds(peer);

        public void Close(ReadOnlySpan<byte> data) => inner.Close(data);
    }
}
