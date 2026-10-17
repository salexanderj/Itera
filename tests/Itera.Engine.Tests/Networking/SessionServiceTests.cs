using System.Numerics;
using Itera.Engine.Networking;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// Sessions over <see cref="FakeTransport"/>: the handshake and its refusals, the join payload (seed, edits, and
/// pieces), live edits from the host, the end of a session, and no allocation while the host sends edits.
/// </summary>
[RunsLong]
public sealed class SessionServiceTests : IDisposable
{
    private const int Floor = 200;
    private const ulong HostSeed = 17;

    // A block of many materials on a floor, so lifted pieces hold mixed voxels.
    private static readonly BoxesGenerator s_generator = new(
        Floor,
        new VoxelBox(-40, 200, -40, 40, 260, 40, BoxesGenerator.Pattern),
        new VoxelBox(-20, 261, -20, 20, 280, 20, BoxesGenerator.Stone)
    );

    private readonly FakeNetwork _network = new();
    private readonly List<IDisposable> _owned = [];
    private double _hostClock;
    private double _clientClock;

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    [Fact]
    public void A_client_takes_the_hosts_seed_edits_and_pieces()
    {
        var (hostWorld, host) = StartHost();
        var sequence = Lift(hostWorld, VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 25, VoxelEditOperation.Lift));
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(30, 230, -20), 18, VoxelEditOperation.Clear));
        Assert.True(hostWorld.TryGetPiece(sequence, out var piece));
        hostWorld.Apply(
            VoxelEdit.PieceAt(
                sequence,
                piece.Radius,
                new VoxelCoord(90, 262, 60),
                VoxelEdit.PackTurn(new Quaternion(0.3f, 0.5f, 0.1f, 0.8f))
            )
        );

        var (clientWorld, client) = Join(host);

        Assert.Equal(SessionRole.Client, client.Role);
        Assert.False(client.IsAuthority);
        Assert.Equal(1, client.Slot);
        Assert.Equal(HostSeed, client.Match.Seed);
        Assert.Equal(HostSeed, clientWorld.Seed);
        Assert.Equal(3, clientWorld.EditCount);
        Assert.True(clientWorld.FollowsHost);
        Assert.Equal(1, host.JoinedPeers);
        Assert.Equal(Snapshot(hostWorld), Snapshot(clientWorld));
    }

    [Fact]
    public void The_hosts_new_edits_reach_the_client_in_order_lifts_with_their_pieces()
    {
        var (hostWorld, host) = StartHost();
        var (clientWorld, client) = Join(host);

        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(-10, 250, 10), 12, VoxelEditOperation.Clear));
        var sequence = Lift(hostWorld, VoxelEdit.Sphere(new VoxelCoord(10, 235, -5), 20, VoxelEditOperation.Lift));
        Assert.True(hostWorld.TryGetPiece(sequence, out var piece));
        hostWorld.Apply(VoxelEdit.PieceAt(sequence, piece.Radius, new VoxelCoord(-80, 262, 70), VoxelEdit.NoTurn));
        host.AfterFixedStep();
        client.Poll();

        Assert.Equal(3, clientWorld.EditCount);
        Assert.Equal(SessionEnd.None, client.End);
        Assert.Equal(Snapshot(hostWorld), Snapshot(clientWorld));
        Assert.True(clientWorld.TryGetPiece(sequence, out var copied));
        Assert.Equal(piece.SolidVoxels, copied.SolidVoxels);
    }

    [Fact]
    public void Each_edit_reaches_the_client_with_its_maker_and_the_clients_removals_hear_the_made_clears()
    {
        var (hostWorld, host) = StartHost();
        var (clientWorld, client) = Join(host);
        var heard = new List<(long Sequence, int Maker, long Removed)>();
        clientWorld.Removals = new HeardRemovals(heard);

        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(-10, 250, 10), 12, VoxelEditOperation.Clear), maker: 0);
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(40, 250, 10), 12, VoxelEditOperation.Clear), maker: 3);
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(40, 250, 60), 12, VoxelEditOperation.Clear));
        Assert.True(
            hostWorld.TryLift(
                VoxelEdit.Sphere(new VoxelCoord(10, 235, -5), 20, VoxelEditOperation.Lift),
                out var lift,
                15
            )
        );
        host.AfterFixedStep();
        client.Poll();

        Assert.Equal(SessionEnd.None, client.End);
        Assert.Equal(4, clientWorld.EditCount);
        Assert.Equal(0, clientWorld.MakerOf(1));
        Assert.Equal(3, clientWorld.MakerOf(2));
        Assert.Equal(VoxelEdit.NoMaker, clientWorld.MakerOf(3));
        Assert.Equal(15, clientWorld.MakerOf(lift));
        Assert.Equal([1L, 2L], heard.Select(made => made.Sequence));
        Assert.Equal([0, 3], heard.Select(made => made.Maker));
        Assert.All(heard, made => Assert.True(made.Removed > 0));
        Assert.Equal(Snapshot(hostWorld), Snapshot(clientWorld));
    }

    [Fact]
    public void An_edit_made_by_a_slot_past_the_roster_ends_the_clients_session()
    {
        var (_, host) = StartHost();
        var (clientWorld, client) = Join(host);
        var writer = new MessageWriter();
        writer.WriteByte((byte)MessageKind.Edit);
        SessionProtocol.WriteEdit(
            writer,
            VoxelEdit.Sphere(new VoxelCoord(0, 250, 0), 10, VoxelEditOperation.Clear) with
            {
                Sequence = 1,
            }
        );
        writer.WriteByte(SessionProtocol.MaxSlots);

        _network.Host!.Send(_network.Host.Peers.Single(), writer.Written, SessionChannel.Reliable);
        client.Poll();

        Assert.Equal(SessionEnd.Error, client.End);
        Assert.Equal(0, clientWorld.EditCount);
    }

    /// <summary>
    /// A host's live edits that would take the client's edit log past its index budget end the client's session, with
    /// the edits before them kept (each ~60-byte edit of the largest size asked the client
    /// for up to 770 KB of index, with no limit on their number). The budget here is a test's, not 256 MB.
    /// </summary>
    [Fact]
    public void A_hosts_live_edits_past_the_clients_index_budget_end_its_session()
    {
        var (_, host) = StartHost();
        var (clientWorld, client) = Join(host);
        clientWorld.LimitEditIndex(100_000);
        var writer = new MessageWriter();

        for (var sequence = 1; sequence <= 20 && client.End == SessionEnd.None; sequence++)
        {
            writer.Reset();
            writer.WriteByte((byte)MessageKind.Edit);
            SessionProtocol.WriteEdit(
                writer,
                VoxelEdit.Sphere(default, VoxelConstants.MaxEditExtent, VoxelEditOperation.Clear) with
                {
                    Sequence = sequence,
                }
            );
            SessionProtocol.WriteMaker(writer, VoxelEdit.NoMaker);
            _network.Host!.Send(_network.Host.Peers.Single(), writer.Written, SessionChannel.Reliable);
            client.Poll();
        }

        Assert.Equal(SessionEnd.Error, client.End);
        Assert.Contains("index entries", client.EndReason, StringComparison.Ordinal);
        Assert.InRange(clientWorld.EditCount, 1, 19);
    }

    [Fact]
    public void The_hosts_reset_reaches_the_client_and_a_late_joiner_gets_the_world_after_it()
    {
        var (hostWorld, host) = StartHost();
        var (clientWorld, client) = Join(host);
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(-10, 250, 10), 12, VoxelEditOperation.Clear));
        Lift(hostWorld, VoxelEdit.Sphere(new VoxelCoord(10, 235, -5), 20, VoxelEditOperation.Lift));
        host.AfterFixedStep();
        client.Poll();
        var dug = Snapshot(clientWorld);

        hostWorld.Reset();
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(30, 230, -20), 18, VoxelEditOperation.Clear));
        host.AfterFixedStep();
        client.Poll();
        TestWorlds.WaitUntilLoaded(hostWorld);
        TestWorlds.WaitUntilLoaded(clientWorld);
        var (lateWorld, _) = Join(host);

        Assert.Equal(SessionEnd.None, client.End);
        Assert.Equal(3, clientWorld.LastReset);
        Assert.Equal(4, clientWorld.EditCount);
        Assert.Equal(Snapshot(hostWorld), Snapshot(clientWorld));
        Assert.NotEqual(dug, Snapshot(clientWorld));
        Assert.Equal(3, lateWorld.LastReset);
        Assert.Equal(Snapshot(hostWorld), Snapshot(lateWorld));
    }

    [Fact]
    public void Edits_made_while_the_client_downloads_are_applied_after_the_payload()
    {
        var (hostWorld, host) = StartHost();
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 250, 0), 15, VoxelEditOperation.Clear));
        var edited = false;

        var (clientWorld, _) = Join(
            host,
            afterHostPoll: () =>
            {
                if (!edited && host.JoinedPeers == 1)
                {
                    // Sent right behind the payload, so they reach the client before its world exists.
                    hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(20, 240, 20), 15, VoxelEditOperation.Clear));
                    Lift(hostWorld, VoxelEdit.Sphere(new VoxelCoord(-20, 240, -20), 15, VoxelEditOperation.Lift));
                    host.AfterFixedStep();
                    edited = true;
                }
            }
        );

        Assert.True(edited);
        Assert.Equal(3, clientWorld.EditCount);
        Assert.Equal(Snapshot(hostWorld), Snapshot(clientWorld));
    }

    /// <summary>
    /// A game message on the sequenced channel that overtakes the welcome and the download (LiteNetLib's channels are
    /// separate, and the host sends its player's state every step once the payload is queued) is dropped, and the join
    /// goes on (it failed the join as a protocol error).
    /// </summary>
    [Fact]
    public void A_game_message_that_overtakes_the_download_is_dropped_and_the_join_goes_on()
    {
        var (hostWorld, host) = StartHost();
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 250, 0), 15, VoxelEditOperation.Clear));
        _network.Host!.SequencedFirst = true;
        var sent = 0;

        var (clientWorld, client) = Join(
            host,
            afterHostPoll: () =>
            {
                if (host.JoinedPeers == 1)
                {
                    var writer = host.BeginMessage(SessionService.FirstGameKind);
                    writer.WriteInt(sent++);
                    host.SendToAll(SessionChannel.Sequenced);
                }
            }
        );

        Assert.True(sent > 0);
        Assert.Equal(SessionEnd.None, client.End);
        Assert.Equal(1, clientWorld.EditCount);
        Assert.Equal(Snapshot(hostWorld), Snapshot(clientWorld));
    }

    /// <summary>
    /// A host whose transport takes only so much at a time sends a join's payload a share at each poll, and the client
    /// gets the whole world (the payload went in one loop, past what Steam holds).
    /// </summary>
    [Fact]
    public void A_join_goes_out_a_share_at_each_poll_and_arrives_whole()
    {
        var (hostWorld, host) = StartHost();
        for (var i = 0; i < 2000; i++)
        {
            hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(5000 + i * 10, 250, 0), 3, VoxelEditOperation.Clear));
        }

        var hostTransport = _network.Host!;
        hostTransport.ReliableBytesPerPoll = 20_000;
        var polls = 0;

        var (clientWorld, client) = Join(host, afterHostPoll: () => polls += host.JoinedPeers == 0 ? 1 : 0);

        // About 100 KB at a chunk of 16 KB a poll.
        Assert.InRange(polls, 6, 100);
        Assert.Equal(SessionEnd.None, client.End);
        Assert.Equal(2000, clientWorld.EditCount);
        Assert.Equal(Snapshot(hostWorld), Snapshot(clientWorld));
    }

    /// <summary>A client that says hello again while it downloads is dropped, and its download stops.</summary>
    [Fact]
    public void A_second_hello_while_downloading_drops_the_client()
    {
        var (hostWorld, host) = StartHost();
        for (var i = 0; i < 2000; i++)
        {
            hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(5000 + i * 10, 250, 0), 3, VoxelEditOperation.Clear));
        }

        _network.Host!.ReliableBytesPerPoll = 20_000;
        var raw = new RawPeer(_network);
        raw.Connect();
        host.Poll();
        raw.Poll();
        var hello = new MessageWriter();
        hello.WriteByte((byte)MessageKind.Hello);
        hello.WriteInt(SessionProtocol.Version);
        hello.WriteString(s_generator.Id);
        hello.WriteInt(s_generator.Version);
        hello.WriteInt(60);
        hello.WriteString("Twice");
        hello.WriteULong(0);
        hello.WriteULong(0);

        raw.Send(hello.Written);
        host.Poll();
        raw.Send(hello.Written);
        host.Poll();
        host.Poll();
        raw.Poll();

        Assert.Equal(0, host.JoinedPeers);
        Assert.True(SessionProtocol.TryReadDisconnect(raw.DisconnectData, out var kind, out _, out _));
        Assert.Equal(DisconnectKind.ProtocolError, kind);
    }

    /// <summary>
    /// A client that leaves and another that takes its slot before the host's next step have different joins, on the
    /// host and on the newcomer's game, so the game's systems see the change (a slot's
    /// presence alone did not change, and the newcomer inherited the leaver's states, sheet, and place).
    /// </summary>
    [Fact]
    public void A_slot_freed_and_taken_again_has_a_new_join()
    {
        var (_, host) = StartHost();
        var (_, first) = Join(host);
        var firstJoin = host.Players[1].Join;
        Assert.Equal(firstJoin, first.Players[1].Join);
        Assert.Equal(1, host.Players[0].Join);

        first.Stop();
        host.Poll();
        var (_, second) = Join(host);

        Assert.Equal(1, second.Slot);
        Assert.True(host.Players[1].Present);
        Assert.NotEqual(firstJoin, host.Players[1].Join);
        Assert.Equal(host.Players[1].Join, second.Players[1].Join);
        var seen = new SlotJoins();
        Assert.True(seen.Changed(host.Players, 1));
        Assert.False(seen.Changed(host.Players, 1));
    }

    /// <summary>A roster whose join does not fit its slot's presence ends the client's session.</summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    [InlineData(true, -1)]
    public void A_roster_with_a_join_that_does_not_fit_ends_the_clients_session(bool present, int join)
    {
        var (_, host) = StartHost();
        var (_, client) = Join(host);
        var writer = new MessageWriter();
        writer.WriteByte((byte)MessageKind.Roster);
        writer.WriteByte(2);
        writer.WriteByte(1);
        writer.WriteString("Host");
        writer.WriteVarInt(1);
        writer.WriteByte(present ? (byte)1 : (byte)0);
        writer.WriteString("Client");
        writer.WriteVarInt(join);

        _network.Host!.Send(_network.Host.Peers.Single(), writer.Written, SessionChannel.Reliable);
        client.Poll();

        Assert.Equal(SessionEnd.Error, client.End);
    }

    [Fact]
    public void A_client_of_another_generator_is_refused()
    {
        var (_, host) = StartHost();

        var failure = Assert.Throws<SessionStartException>(() => Join(host, generator: new DomeGenerator()));

        Assert.Contains("refused", failure.Message, StringComparison.Ordinal);
        Assert.Contains("test.dome", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.JoinedPeers);
    }

    [Fact]
    public void A_client_with_another_step_rate_is_refused()
    {
        var (_, host) = StartHost();

        var failure = Assert.Throws<SessionStartException>(() => Join(host, fixedStepHz: 30));

        Assert.Contains("steps 60 times a second", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_session_refuses_the_next_client()
    {
        var (_, host) = StartHost(maxPlayers: 2);
        Join(host);

        var failure = Assert.Throws<SessionStartException>(() => Join(host));

        Assert.Contains("2 players", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, host.JoinedPeers);
    }

    [Fact]
    public void A_client_of_another_protocol_version_is_refused_with_the_reason()
    {
        var (_, host) = StartHost();
        var raw = new RawPeer(_network);
        raw.Connect();
        raw.Poll();
        var writer = new MessageWriter();
        writer.WriteByte((byte)MessageKind.Hello);
        writer.WriteInt(SessionProtocol.Version + 1);
        raw.Send(writer.Written);

        host.Poll();
        raw.Poll();

        Assert.True(SessionProtocol.TryReadDisconnect(raw.DisconnectData, out var kind, out var reason, out _));
        Assert.Equal(DisconnectKind.Reject, kind);
        Assert.Equal((byte)RejectReason.Protocol, reason);
    }

    [Fact]
    public void A_refusal_quoting_a_long_generator_id_stays_short_enough_to_send()
    {
        var (_, host) = StartHost();
        var raw = new RawPeer(_network);
        raw.Connect();
        raw.Poll();
        var writer = new MessageWriter();
        writer.WriteByte((byte)MessageKind.Hello);
        writer.WriteInt(SessionProtocol.Version);
        writer.WriteString(new string('g', 500));
        writer.WriteInt(1);
        writer.WriteInt(60);
        writer.WriteString(new string('n', 500));
        writer.WriteULong(0);
        writer.WriteULong(0);
        raw.Send(writer.Written);

        host.Poll();
        raw.Poll();

        Assert.True(SessionProtocol.TryReadDisconnect(raw.DisconnectData, out var kind, out var reason, out var text));
        Assert.Equal(DisconnectKind.Reject, kind);
        Assert.Equal((byte)RejectReason.Identity, reason);
        Assert.Contains("ggg...", text, StringComparison.Ordinal);
        Assert.True(raw.DisconnectData!.Length <= SessionProtocol.MaxDisconnectTextBytes + 4);
    }

    [Fact]
    public void Receiving_the_hosts_edits_allocates_nothing()
    {
        var (hostWorld, host) = StartHost();
        var (clientWorld, client) = Join(host);
        var dig = VoxelEdit.Sphere(new VoxelCoord(0, 250, 0), 10, VoxelEditOperation.Clear);
        hostWorld.Apply(in dig);
        host.AfterFixedStep();
        client.Poll();
        hostWorld.Apply(in dig);
        host.AfterFixedStep();

        var before = SystemAllocation.StartMeasurement();
        client.Poll();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2, clientWorld.EditCount);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_connection_without_a_hello_is_dropped_after_the_connect_timeout()
    {
        var (_, host) = StartHost();
        var raw = new RawPeer(_network);
        raw.Connect();
        host.Poll();
        _hostClock += 5;
        host.Poll();
        raw.Poll();
        Assert.Null(raw.DisconnectData);

        _hostClock += 6;
        host.Poll();
        raw.Poll();

        Assert.True(SessionProtocol.TryReadDisconnect(raw.DisconnectData, out _, out var reason, out _));
        Assert.Equal((byte)RejectReason.NoHello, reason);
    }

    [Fact]
    public void A_hello_before_the_hosts_world_is_made_waits_for_it_and_then_joins()
    {
        var (hostWorld, host) = StartHost(worldMade: false);
        var client = NewClient(new FakeTransport(_network));
        client.Begin();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(JoinStage.Welcoming, client.PollJoin().Stage);
            host.Poll();
        }

        // Its slot is taken at the hello, so the host does not drop it as silent while the world is made.
        _hostClock += 11;
        host.Poll();
        Assert.Equal(JoinStage.Welcoming, client.PollJoin().Stage);

        hostWorld.Initialize(HostSeed);
        JoinProgress progress;
        var polls = 0;
        do
        {
            host.Poll();
            progress = client.PollJoin();
        } while (progress.Stage is not (JoinStage.Joined or JoinStage.Failed) && ++polls < 100);

        Assert.Null(progress.Failure);
        Assert.Equal(JoinStage.Joined, progress.Stage);
        Assert.Equal(HostSeed, client.Match.Seed);
    }

    [Fact]
    public void A_client_and_host_whose_generator_id_is_longer_than_a_refusal_quotes_join()
    {
        var generator = new BoxesGenerator(Floor) { Id = "test." + new string('l', 95) };
        var (_, host) = StartHost(generator: generator);

        var (_, client) = Join(host, generator: generator);

        Assert.Equal(1, host.JoinedPeers);
        Assert.Equal(generator.Id, client.Match.Identity);
    }

    [Fact]
    public void A_client_hears_no_peer_but_its_host()
    {
        var (hostWorld, host) = StartHost();
        var (clientWorld, client) = Join(host);
        var stranger = 99;
        var writer = new MessageWriter();
        writer.WriteByte((byte)MessageKind.Welcome);
        writer.WriteByte(3);

        client.OnConnected(stranger);
        client.OnReceive(stranger, writer.Written);
        writer.Reset();
        SessionProtocol.WriteDisconnect(writer, DisconnectKind.Bye, 0, "a stranger left");
        client.OnDisconnected(stranger, TransportDisconnect.Closed, writer.Written);
        client.Poll();
        host.Poll();

        // The host's next edit still reaches it: the stranger took nothing of the join.
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(-10, 250, 10), 12, VoxelEditOperation.Clear));
        host.AfterFixedStep();
        client.Poll();

        Assert.Equal(SessionEnd.None, client.End);
        Assert.Equal(1, host.JoinedPeers);
        Assert.Equal(1, client.Slot);
        Assert.Equal(1, clientWorld.EditCount);
    }

    [Fact]
    public void A_join_polled_once_a_step_goes_through_its_stages_without_waiting()
    {
        var (hostWorld, host) = StartHost();
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(30, 230, -20), 18, VoxelEditOperation.Clear));
        var client = NewClient(new FakeTransport(_network));
        client.Begin();
        var stages = new List<JoinStage>();
        JoinProgress progress;
        do
        {
            progress = client.PollJoin();
            stages.Add(progress.Stage);
            host.Poll();
        } while (progress.Stage is not (JoinStage.Joined or JoinStage.Failed) && stages.Count < 100);

        Assert.Null(progress.Failure);
        Assert.Equal(JoinStage.Joined, stages[^1]);
        // The fake transport connects at the first poll; the welcome and the download take polls of their own.
        Assert.Equal(JoinStage.Welcoming, stages[0]);
        Assert.True(stages.Count > 1);
        Assert.Equal(HostSeed, client.Match.Seed);
        Assert.Equal(progress.TotalBytes, progress.DownloadedBytes);
        Assert.True(progress.TotalBytes > 0);
        // Once joined, it stays joined.
        Assert.Equal(JoinStage.Joined, client.PollJoin().Stage);
    }

    [Fact]
    public void A_refused_join_fails_its_poll_without_throwing_and_stays_failed()
    {
        var (_, host) = StartHost();
        var client = NewClient(new FakeTransport(_network), fixedStepHz: 30);
        client.Begin();

        JoinProgress progress = default;
        for (var i = 0; i < 10 && progress.Stage != JoinStage.Failed; i++)
        {
            progress = client.PollJoin();
            host.Poll();
        }

        Assert.Equal(JoinStage.Failed, progress.Stage);
        Assert.Contains("the host refused", progress.Failure, StringComparison.Ordinal);
        Assert.Equal(progress, client.PollJoin());
    }

    [Fact]
    public void A_join_with_no_welcome_fails_its_poll_at_the_connect_timeout()
    {
        _ = StartHost();
        var client = NewClient(new FakeTransport(_network));
        client.Begin();
        Assert.NotEqual(JoinStage.Failed, client.PollJoin().Stage);

        // The host never polls, so the hello waits unanswered.
        _clientClock += 10.5;
        var progress = client.PollJoin();

        Assert.Equal(JoinStage.Failed, progress.Stage);
        Assert.Contains("no welcome within 10 s", progress.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_download_that_stalls_fails_its_poll_after_the_connect_timeout_with_no_data()
    {
        var raw = new RawHost(_network, payloadBytes: 200, chunk: new byte[100], done: false);
        var client = NewClient(new FakeTransport(_network));
        client.Begin();
        JoinProgress progress = default;
        for (var i = 0; i < 10 && progress.Stage != JoinStage.Downloading; i++)
        {
            raw.Poll();
            progress = client.PollJoin();
        }

        Assert.Equal(JoinStage.Downloading, progress.Stage);

        // Most of the timeout passes with no more data: still downloading.
        _clientClock += 9;
        Assert.Equal(JoinStage.Downloading, client.PollJoin().Stage);

        _clientClock += 1.5;
        progress = client.PollJoin();

        Assert.Equal(JoinStage.Failed, progress.Stage);
        Assert.Contains("no data from the host within 10 s", progress.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Stopping_a_client_while_it_downloads_says_goodbye_to_the_host()
    {
        var raw = new RawHost(_network, payloadBytes: 200, chunk: new byte[100], done: false);
        var client = NewClient(new FakeTransport(_network));
        client.Begin();
        JoinProgress progress = default;
        for (var i = 0; i < 10 && progress.Stage != JoinStage.Downloading; i++)
        {
            raw.Poll();
            progress = client.PollJoin();
        }

        Assert.Equal(new JoinProgress(JoinStage.Downloading, 100, 200, null), progress);

        client.Stop();
        raw.Poll();

        Assert.True(SessionProtocol.TryReadDisconnect(raw.DisconnectData, out var kind, out _, out var text));
        Assert.Equal(DisconnectKind.Bye, kind);
        Assert.Equal("the player left", text);
    }

    [Fact]
    public void A_host_that_ended_hosts_again_on_the_same_transport_for_a_new_client()
    {
        var world = TestWorlds.Load(s_generator, Vector3.Zero, seed: HostSeed);
        _owned.Add(world);
        var transport = new FakeTransport(_network);
        var settings = new SessionSettings { Mode = SessionMode.Host };
        var first = CreateSession(settings, transport, world, () => _hostClock, () => { }, HostSeed);
        first.Initialize();
        var (_, oldClient) = Join(first);
        first.Stop();
        oldClient.Poll();
        Assert.Equal(SessionEnd.HostLeft, oldClient.End);

        // The next match: a new session (its own world) on the run's transport.
        var second = CreateSession(settings, transport, world, () => _hostClock, () => { }, HostSeed);
        _owned.Add(second);
        second.Initialize();
        var (_, client) = Join(second);

        Assert.Equal(1, second.JoinedPeers);
        Assert.Equal(1, client.Slot);
        Assert.Equal(SessionEnd.None, client.End);
    }

    [Fact]
    public void Joining_with_no_host_fails_at_once()
    {
        var failure = Assert.Throws<SessionStartException>(() => CreateClient(new FakeTransport(_network), () => { }));

        Assert.Contains("no host answered", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Joining_a_host_that_never_answers_times_out()
    {
        _ = StartHost();

        // The host never polls, so the hello waits unanswered.
        var failure = Assert.Throws<SessionStartException>(() => CreateClient(new FakeTransport(_network), () => { }));

        Assert.Contains("no welcome within 10 s", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_over_the_limit_is_refused_before_it_is_downloaded()
    {
        var raw = new RawHost(_network, payloadBytes: 3L << 20, chunk: []);

        var failure = Assert.Throws<SessionStartException>(() =>
            CreateClient(new FakeTransport(_network), raw.Poll, maxJoinMegabytes: 2)
        );

        Assert.Contains("MaxJoinMegabytes allows 2", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, raw.ChunksSent);
    }

    /// <summary>
    /// A welcome that gives the client a slot the host could not give (16 or more) is refused: the game indexes its
    /// players' arrays by it, and its first step crashed the run.
    /// </summary>
    [Theory]
    [InlineData(SessionProtocol.MaxSlots)]
    [InlineData(255)]
    public void A_welcome_with_a_slot_past_the_last_is_refused(byte slot)
    {
        var raw = new RawHost(_network, payloadBytes: 100, chunk: new byte[100], slot: slot);

        var failure = Assert.Throws<SessionStartException>(() => CreateClient(new FakeTransport(_network), raw.Poll));

        Assert.Contains("the host's welcome is not valid", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_payload_fails_the_join_when_it_is_loaded()
    {
        var raw = new RawHost(_network, payloadBytes: 100, chunk: new byte[100]);
        var (world, client) = CreateClient(new FakeTransport(_network), raw.Poll);
        world.Initialize(client.Match.Seed);

        var failure = Assert.Throws<SessionStartException>(client.LoadJoinedWorld);

        Assert.Contains("could not be loaded", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, world.EditCount);
    }

    [Fact]
    public void A_payload_longer_than_announced_fails_the_join()
    {
        var raw = new RawHost(_network, payloadBytes: 10, chunk: new byte[11]);

        var failure = Assert.Throws<SessionStartException>(() => CreateClient(new FakeTransport(_network), raw.Poll));

        Assert.Contains("longer than the host announced", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_quitting_ends_the_clients_session()
    {
        var (_, host) = StartHost();
        var (_, client) = Join(host);

        host.Stop();
        client.Poll();

        Assert.Equal(SessionEnd.HostLeft, client.End);
        Assert.Contains("the host quit", client.EndReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_connection_ends_the_clients_session()
    {
        var (_, host) = StartHost();
        var (_, client) = Join(host);

        _network.Host!.Lose(_network.Host.Peers.Single());
        client.Poll();

        host.Poll();

        Assert.Equal(SessionEnd.Lost, client.End);
        Assert.Equal(0, host.JoinedPeers);
    }

    [Fact]
    public void An_edit_out_of_sequence_from_the_host_ends_the_clients_session()
    {
        var (_, host) = StartHost();
        var (clientWorld, client) = Join(host);
        var writer = new MessageWriter();
        writer.WriteByte((byte)MessageKind.Edit);
        SessionProtocol.WriteEdit(
            writer,
            VoxelEdit.Sphere(new VoxelCoord(0, 250, 0), 10, VoxelEditOperation.Clear) with
            {
                Sequence = 5,
            }
        );
        SessionProtocol.WriteMaker(writer, VoxelEdit.NoMaker);

        _network.Host!.Send(_network.Host.Peers.Single(), writer.Written, SessionChannel.Reliable);
        client.Poll();

        Assert.Equal(SessionEnd.Error, client.End);
        Assert.Equal(0, clientWorld.EditCount);
    }

    [Fact]
    public void A_client_leaving_frees_its_slot()
    {
        var (_, host) = StartHost(maxPlayers: 2);
        var (_, first) = Join(host);

        first.Stop();
        host.Poll();
        var (_, second) = Join(host);

        Assert.Equal(1, second.Slot);
        Assert.Equal(1, host.JoinedPeers);
    }

    [Fact]
    public void Offline_owns_its_world_and_opens_nothing()
    {
        var world = new VoxelWorld(WorldSettings(5), 0, s_generator, NullLogger<VoxelWorld>.Instance);
        _owned.Add(world);
        var transport = new FakeTransport(_network);
        var session = CreateSession(new SessionSettings(), transport, world, () => 0, () => { });

        Assert.Throws<InvalidOperationException>(() => session.Match);
        session.Initialize();
        session.Poll();
        session.AfterFixedStep();

        Assert.Equal(SessionRole.Offline, session.Role);
        Assert.True(session.IsAuthority);
        Assert.Equal(5UL, session.Match.Seed);
        Assert.Null(_network.Host);
    }

    [Fact]
    public void Sending_edits_to_clients_allocates_nothing()
    {
        var (hostWorld, host) = StartHost();
        Join(host);
        var transport = _network.Host!;
        transport.CountSendsOnly = true;
        var dig = VoxelEdit.Sphere(new VoxelCoord(0, 250, 0), 10, VoxelEditOperation.Clear);
        var lift = VoxelEdit.Sphere(new VoxelCoord(20, 240, 20), 12, VoxelEditOperation.Lift);
        // The first round grows the writer and the runs list.
        hostWorld.Apply(in dig);
        Lift(hostWorld, lift);
        host.AfterFixedStep();
        hostWorld.Apply(in dig);
        Lift(hostWorld, lift with { Origin = new VoxelCoord(-20, 240, 20) });
        var sends = transport.Sends;

        var before = SystemAllocation.StartMeasurement();
        host.AfterFixedStep();
        host.AfterFixedStep();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(sends + 2, transport.Sends);
        Assert.Equal(0, allocated);
    }

    private (VoxelWorld World, SessionService Session) StartHost(
        int maxPlayers = 4,
        IChunkGenerator? generator = null,
        bool worldMade = true
    )
    {
        var world = worldMade
            ? TestWorlds.Load(generator ?? s_generator, Vector3.Zero, seed: HostSeed)
            : new VoxelWorld(WorldSettings(HostSeed), 0, generator ?? s_generator, NullLogger<VoxelWorld>.Instance);
        _owned.Add(world);
        var settings = new SessionSettings { Mode = SessionMode.Host, MaxPlayers = maxPlayers };
        var session = CreateSession(
            settings,
            new FakeTransport(_network),
            world,
            () => _hostClock,
            () => { },
            HostSeed
        );
        session.Initialize();
        return (world, session);
    }

    /// <summary>
    /// Joins <paramref name="host"/> with a new client whose options name another seed; while it waits, the host polls
    /// and then <paramref name="afterHostPoll"/> runs. Loads the client's world around the block.
    /// </summary>
    private (VoxelWorld World, SessionService Session) Join(
        SessionService host,
        Action? afterHostPoll = null,
        IChunkGenerator? generator = null,
        int fixedStepHz = 60
    )
    {
        var (world, client) = CreateClient(
            new FakeTransport(_network),
            () =>
            {
                host.Poll();
                afterHostPoll?.Invoke();
            },
            generator: generator,
            fixedStepHz: fixedStepHz
        );
        world.Initialize(client.Match.Seed);
        client.LoadJoinedWorld();
        world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(world);
        return (world, client);
    }

    private (VoxelWorld World, SessionService Session) CreateClient(
        FakeTransport transport,
        Action pumpHost,
        int maxJoinMegabytes = 256,
        IChunkGenerator? generator = null,
        int fixedStepHz = 60
    )
    {
        var world = new VoxelWorld(WorldSettings(99), 0, generator ?? s_generator, NullLogger<VoxelWorld>.Instance);
        _owned.Add(world);
        var clock = 0.0;
        var settings = new SessionSettings
        {
            Mode = SessionMode.Join,
            Address = "host:47600",
            PlayerName = "Tester",
            MaxJoinMegabytes = maxJoinMegabytes,
        };
        var session = CreateSession(
            settings,
            transport,
            world,
            () => clock,
            () =>
            {
                clock += 0.01;
                pumpHost();
            },
            99,
            fixedStepHz
        );
        _owned.Add(session);
        session.Initialize();
        return (world, session);
    }

    /// <summary>A client that joins the host on <see cref="FakeTransport"/> when its join is polled, on <see cref="_clientClock"/>.</summary>
    private SessionService NewClient(FakeTransport transport, int fixedStepHz = 60)
    {
        var world = new VoxelWorld(WorldSettings(99), 0, s_generator, NullLogger<VoxelWorld>.Instance);
        _owned.Add(world);
        var settings = new SessionSettings
        {
            Mode = SessionMode.Join,
            Address = "host:47600",
            PlayerName = "Tester",
        };
        var session = CreateSession(
            settings,
            transport,
            world,
            () => _clientClock,
            () => throw new InvalidOperationException("A polled join never waits."),
            99,
            fixedStepHz
        );
        _owned.Add(session);
        return session;
    }

    private static SessionService CreateSession(
        SessionSettings settings,
        ISessionTransport transport,
        VoxelWorld world,
        Func<double> clock,
        Action wait,
        ulong configuredSeed = 5,
        int fixedStepHz = 60
    ) =>
        new(
            settings,
            fixedStepHz,
            configuredSeed,
            0,
            world,
            transport,
            NullLogger<SessionService>.Instance,
            clock,
            wait
        );

    private static VoxelSettings WorldSettings(ulong seed) =>
        new()
        {
            StreamingRadiusMetres = 3,
            Levels = 1,
            MemoryBudgetMegabytes = 256,
            WorkerThreads = 2,
            Seed = seed,
        };

    private static long Lift(VoxelWorld world, in VoxelEdit edit)
    {
        Assert.True(world.TryLift(in edit, out var sequence), "The lift's columns are not resident.");
        return sequence;
    }

    /// <summary>Every level-0 voxel of the block and the air above it, where the edits land.</summary>
    private static byte[] Snapshot(VoxelWorld world)
    {
        var voxels = new List<byte>();
        for (var z = -100; z < 100; z += 1)
        {
            for (var y = 190; y < 300; y += 1)
            {
                for (var x = -100; x < 100; x += 1)
                {
                    Assert.True(world.TryGetMaterial(new VoxelCoord(x, y, z), out var material));
                    voxels.Add(material);
                }
            }
        }

        return [.. voxels];
    }

    /// <summary>Keeps each made clear the world hears: its sequence, maker, and the voxels it removes.</summary>
    private sealed class HeardRemovals(List<(long Sequence, int Maker, long Removed)> heard) : IVoxelRemovals
    {
        public void Removed(in VoxelEdit edit, int maker, ReadOnlySpan<long> byMaterial)
        {
            var removed = 0L;
            foreach (var count in byMaterial)
            {
                removed += count;
            }

            heard.Add((edit.Sequence, maker, removed));
        }
    }

    /// <summary>A client that speaks the protocol by hand, to send what a real client would not.</summary>
    private sealed class RawPeer(FakeNetwork network) : ISessionTransportListener
    {
        private readonly FakeTransport _transport = new(network);
        private int _host = -1;

        public byte[]? DisconnectData { get; private set; }

        public void Connect() => _transport.Connect("host:47600");

        public void Poll() => _transport.Poll(this);

        public void Send(ReadOnlySpan<byte> data) => _transport.Send(_host, data, SessionChannel.Reliable);

        public void OnConnected(int peer) => _host = peer;

        public void OnDisconnected(int peer, TransportDisconnect reason, ReadOnlySpan<byte> data) =>
            DisconnectData = data.ToArray();

        public void OnReceive(int peer, ReadOnlySpan<byte> data) { }
    }

    /// <summary>A host that answers any hello with a welcome announcing a payload, then sends one chunk.</summary>
    private sealed class RawHost : ISessionTransportListener
    {
        private readonly FakeTransport _transport;
        private readonly long _payloadBytes;
        private readonly byte[] _chunk;
        private readonly bool _done;
        private readonly byte _slot;

        /// <param name="network">The fake network.</param>
        /// <param name="payloadBytes">The payload's size the welcome announces.</param>
        /// <param name="chunk">The one chunk sent after the welcome.</param>
        /// <param name="done">False to leave the download unfinished (no JoinDone).</param>
        /// <param name="slot">The slot the welcome gives the client.</param>
        public RawHost(FakeNetwork network, long payloadBytes, byte[] chunk, bool done = true, byte slot = 1)
        {
            _transport = new FakeTransport(network);
            _transport.Listen(47600);
            _payloadBytes = payloadBytes;
            _chunk = chunk;
            _done = done;
            _slot = slot;
        }

        public int ChunksSent { get; private set; }

        /// <summary>What the client sent as it left, or null.</summary>
        public byte[]? DisconnectData { get; private set; }

        public void Poll() => _transport.Poll(this);

        public void OnConnected(int peer) { }

        public void OnDisconnected(int peer, TransportDisconnect reason, ReadOnlySpan<byte> data) =>
            DisconnectData = data.ToArray();

        public void OnReceive(int peer, ReadOnlySpan<byte> data)
        {
            var writer = new MessageWriter();
            writer.WriteByte((byte)MessageKind.Welcome);
            writer.WriteByte(_slot);
            SessionProtocol.WriteMatch(writer, new MatchSettings(HostSeed, 60, s_generator.Id, s_generator.Version));
            writer.WriteLong(_payloadBytes);
            writer.WriteLong(0);
            _transport.Send(peer, writer.Written, SessionChannel.Reliable);
            if (_payloadBytes > 2L << 20)
            {
                return;
            }

            writer.Reset();
            writer.WriteByte((byte)MessageKind.JoinChunk);
            writer.WriteBytes(_chunk);
            _transport.Send(peer, writer.Written, SessionChannel.Reliable);
            ChunksSent++;
            if (!_done)
            {
                return;
            }

            writer.Reset();
            writer.WriteByte((byte)MessageKind.JoinDone);
            _transport.Send(peer, writer.Written, SessionChannel.Reliable);
        }
    }
}
