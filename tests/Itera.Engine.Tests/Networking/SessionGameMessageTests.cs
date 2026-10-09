using System.Numerics;
using Itera.Engine.Networking;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// The game's side of a session (roadmap item 5, part C): its messages reach the right handler with the sender's slot,
/// only from the side their handler takes them from, a handler that fails drops only that peer, the roster follows joins
/// and leaves, and the host's match data reaches a client.
/// </summary>
public sealed class SessionGameMessageTests : IDisposable
{
    private const byte Kind = SessionService.FirstGameKind + 10;
    private static readonly DomeGenerator s_generator = new();

    private readonly FakeNetwork _network = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    [Fact]
    public void Game_messages_reach_their_handler_with_the_senders_slot()
    {
        var hostHandler = new Recorder();
        var clientHandler = new Recorder();
        var host = StartHost(hostHandler);
        var client = Join(host, clientHandler);

        client.BeginMessage(Kind).WriteInt(41);
        client.SendToHost(SessionChannel.Reliable);
        host.Poll();
        host.BeginMessage(Kind).WriteInt(42);
        host.SendToAll(SessionChannel.Reliable);
        client.Poll();

        Assert.Equal([(1, 41)], hostHandler.Received);
        Assert.Equal([(0, 42)], clientHandler.Received);
    }

    [Fact]
    public void A_game_message_without_a_handler_drops_the_client()
    {
        var host = StartHost(new Recorder());
        var client = Join(host, new Recorder());

        client.BeginMessage(Kind + 1).WriteInt(1);
        client.SendToHost(SessionChannel.Reliable);
        host.Poll();
        client.Poll();

        Assert.Equal(0, host.JoinedPeers);
        Assert.Equal(SessionEnd.Error, client.End);
    }

    [Fact]
    public void Messages_reach_their_handler_from_the_side_it_takes_them_from()
    {
        var hostToHost = new Recorder(SessionMessageDirection.ToHost);
        var hostToClients = new Recorder(SessionMessageDirection.ToClients, Kind + 1);
        var clientToHost = new Recorder(SessionMessageDirection.ToHost);
        var clientToClients = new Recorder(SessionMessageDirection.ToClients, Kind + 1);
        var host = StartHost([hostToHost, hostToClients]);
        var client = Join(host, [clientToHost, clientToClients]);

        client.BeginMessage(Kind).WriteInt(41);
        client.SendToHost(SessionChannel.Reliable);
        host.Poll();
        host.BeginMessage(Kind + 1).WriteInt(42);
        host.SendToAll(SessionChannel.Reliable);
        client.Poll();

        Assert.Equal([(1, 41)], hostToHost.Received);
        Assert.Equal([(0, 42)], clientToClients.Received);
        Assert.Equal(1, host.JoinedPeers);
        Assert.Equal(SessionEnd.None, client.End);
    }

    /// <summary>
    /// A modified client that sends the host a message only a host sends (a correction of the host's own player, a
    /// character spawn) is dropped before the handler sees it.
    /// </summary>
    [Fact]
    public void A_client_that_sends_a_message_only_the_host_sends_is_dropped()
    {
        var hostHandler = new Recorder(SessionMessageDirection.ToClients);
        var host = StartHost([hostHandler]);
        var client = Join(host, [new Recorder(SessionMessageDirection.ToClients)]);

        client.BeginMessage(Kind).WriteInt(1);
        client.SendToHost(SessionChannel.Reliable);
        host.Poll();
        client.Poll();

        Assert.Empty(hostHandler.Received);
        Assert.Equal(0, host.JoinedPeers);
        Assert.Equal(SessionEnd.Error, client.End);
    }

    [Fact]
    public void A_host_that_sends_a_message_only_clients_send_ends_the_clients_session()
    {
        var host = StartHost([new Recorder(SessionMessageDirection.ToHost)]);
        var clientHandler = new Recorder(SessionMessageDirection.ToHost);
        var client = Join(host, [clientHandler]);

        host.BeginMessage(Kind).WriteInt(1);
        host.SendToAll(SessionChannel.Reliable);
        client.Poll();

        Assert.Empty(clientHandler.Received);
        Assert.Equal(SessionEnd.Error, client.End);
    }

    /// <summary>
    /// A handler that throws something other than <see cref="InvalidDataException"/> (a bug, such as the <see
    /// cref="OverflowException"/> of <c>Math.Abs(int.MinValue)</c> in a message's check, which once ended the host's
    /// game) drops the client that sent the message, logs the exception at Error, and leaves the host and its other
    /// clients playing.
    /// </summary>
    [Fact]
    public void A_handler_that_fails_drops_only_that_client_and_logs_an_error()
    {
        var logger = new RecordingLogger();
        var host = StartHost([new Recorder(failure: new OverflowException("A test failure."))], logger: logger);
        var failing = Join(host, [new Recorder()], "Failing");
        var other = Join(host, [new Recorder()], "Other");

        failing.BeginMessage(Kind).WriteInt(1);
        failing.SendToHost(SessionChannel.Reliable);
        host.Poll();
        failing.Poll();
        other.Poll();

        Assert.Equal(1, host.JoinedPeers);
        Assert.Equal(SessionEnd.Error, failing.End);
        Assert.Equal(SessionEnd.None, other.End);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is OverflowException);
    }

    [Fact]
    public void The_roster_follows_joins_and_leaves()
    {
        var host = StartHost(new Recorder());
        var version = host.RosterVersion;
        var first = Join(host, new Recorder(), "First");
        var second = Join(host, new Recorder(), "Second");
        first.Poll();

        Assert.True(host.RosterVersion > version);
        Assert.Equal(["Host", "First", "Second", ""], host.Players.ToArray().Select(p => p.Present ? p.Name : ""));
        Assert.Equal(host.Players.ToArray(), first.Players.ToArray());

        second.Stop();
        host.Poll();
        first.Poll();

        Assert.False(host.Players[2].Present);
        Assert.False(first.Players[2].Present);
    }

    [Fact]
    public void The_hosts_match_data_reaches_the_client()
    {
        var host = StartHost(new Recorder(), new Data([7, 8, 9]));

        var client = Join(host, new Recorder());

        Assert.Equal([7, 8, 9], client.Match.Game);
        Assert.Equal([7, 8, 9], host.Match.Game);
    }

    [Fact]
    public void Two_handlers_of_one_kind_are_refused()
    {
        var world = new VoxelWorld(new VoxelSettings(), 0, s_generator, NullLogger<VoxelWorld>.Instance);
        _owned.Add(world);

        Assert.Throws<InvalidOperationException>(() =>
            Session(
                new SessionSettings(),
                world,
                new FakeTransport(_network),
                () => { },
                [new Recorder(), new Recorder()]
            )
        );
    }

    private SessionService StartHost(Recorder handler, ISessionMatchData? data = null) => StartHost([handler], data);

    private SessionService StartHost(
        ISessionGameHandler[] handlers,
        ISessionMatchData? data = null,
        ILogger<SessionService>? logger = null
    )
    {
        var world = TestWorlds.Load(s_generator, Vector3.Zero, seed: 5);
        _owned.Add(world);
        var settings = new SessionSettings { Mode = SessionMode.Host, PlayerName = "Host" };
        var host = Session(settings, world, new FakeTransport(_network), () => { }, handlers, data, logger);
        host.Initialize();
        return host;
    }

    private SessionService Join(SessionService host, Recorder handler, string name = "Client") =>
        Join(host, [handler], name);

    private SessionService Join(SessionService host, ISessionGameHandler[] handlers, string name = "Client")
    {
        var world = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 1,
            },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        _owned.Add(world);
        var settings = new SessionSettings
        {
            Mode = SessionMode.Join,
            Address = "host:47600",
            PlayerName = name,
        };
        var client = Session(settings, world, new FakeTransport(_network), host.Poll, handlers);
        client.Initialize();
        world.Initialize(client.Match.Seed);
        client.LoadJoinedWorld();
        client.Poll();
        return client;
    }

    private SessionService Session(
        SessionSettings settings,
        VoxelWorld world,
        ISessionTransport transport,
        Action pump,
        ISessionGameHandler[] handlers,
        ISessionMatchData? data = null,
        ILogger<SessionService>? logger = null
    )
    {
        var clock = 0.0;
        var session = new SessionService(
            settings,
            60,
            world.Seed,
            0,
            world,
            transport,
            logger ?? NullLogger<SessionService>.Instance,
            () => clock,
            () =>
            {
                clock += 0.01;
                pump();
            },
            handlers,
            data
        );
        _owned.Add(session);
        return session;
    }

    /// <summary>Records what it receives, or throws <paramref name="failure"/> for every message.</summary>
    private sealed class Recorder(
        SessionMessageDirection direction = SessionMessageDirection.Both,
        int kind = SessionGameMessageTests.Kind,
        Exception? failure = null
    ) : ISessionGameHandler
    {
        public List<(int Slot, int Value)> Received { get; } = [];

        public byte Kind => (byte)kind;

        public SessionMessageDirection Direction => direction;

        public void Receive(int slot, ReadOnlySpan<byte> body)
        {
            if (failure is not null)
            {
                throw failure;
            }

            var reader = new MessageReader(body);
            Received.Add((slot, reader.ReadInt()));
        }
    }

    private sealed class RecordingLogger : ILogger<SessionService>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, exception));
    }

    private sealed class Data(byte[] bytes) : ISessionMatchData
    {
        public void Write(MessageWriter writer) => writer.WriteBytes(bytes);
    }
}
