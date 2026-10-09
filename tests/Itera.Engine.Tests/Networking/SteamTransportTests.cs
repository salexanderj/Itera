using System.Numerics;
using Itera.Engine.Networking;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// Sessions over <see cref="SteamTransport"/> on a <see cref="FakeSteamNetwork"/>: the lobby, joining through it or by
/// Steam id (lobby members and friends only), refusals and the host's leaving carried as Steam end reasons, a lost
/// connection, a join larger than Steam would hold, Steam that is not there, and no allocation while a client receives
/// edits. Also <see cref="SteamService"/>, the Steam address, and Steam's launch arguments.
/// </summary>
public sealed class SteamTransportTests : IDisposable
{
    private const ulong HostId = 76_561_198_000_000_001;
    private const ulong ClientId = 76_561_198_000_000_002;
    private const ulong HostSeed = 17;

    private static readonly DomeGenerator s_generator = new();

    private readonly FakeSteamNetwork _network = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    [Fact]
    public void A_host_opens_a_friends_lobby_that_friends_can_join_from_their_friends_list()
    {
        var (_, host, platform) = StartHost();
        host.Poll();

        Assert.NotEqual(0UL, platform.OwnedLobby);
        Assert.Equal($"+connect_lobby {platform.OwnedLobby}", platform.RichPresence["connect"]);
        // Nothing reads a protocol or host key: the match key carries the protocol.
        Assert.DoesNotContain("itera_protocol", platform.LobbyData.Keys);
        Assert.DoesNotContain("itera_host", platform.LobbyData.Keys);
    }

    /// <summary>
    /// A host's Steam id is public, so a player who is neither in its lobby nor its friend is refused with the reason
    /// when it joins by Steam id; a lobby member who is not a friend joins.
    /// </summary>
    [Fact]
    public void Only_lobby_members_and_friends_may_join()
    {
        var (_, host, platform) = StartHost();
        host.Poll();

        var refused = Assert.Throws<SessionStartException>(() => Join(host, $"steam:{HostId}", befriend: false));
        var (_, member) = Join(host, $"lobby:{platform.OwnedLobby}", clientId: ClientId + 1, befriend: false);

        Assert.Contains(
            "the host refused: the host takes only its lobby's members",
            refused.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(SessionEnd.None, member.End);
        Assert.Equal(1, host.JoinedPeers);
    }

    /// <summary>
    /// A join payload past what Steam would hold (8 MB) goes through Steam's default
    /// send buffer of 512 KB a share at a time, and the client gets every edit.
    /// </summary>
    [Fact]
    public void A_join_larger_than_steam_holds_goes_out_a_share_at_a_time()
    {
        var (hostWorld, host, platform) = StartHost();
        // Out of the loaded columns, so each is only logged: 9 MB of edits.
        const int edits = 180_000;
        for (var i = 0; i < edits; i++)
        {
            hostWorld.Apply(
                VoxelEdit.Sphere(
                    new VoxelCoord(5000 + i % 400 * 10, 200, 5000 + i / 400 * 10),
                    3,
                    VoxelEditOperation.Clear
                )
            );
        }

        platform.SendBufferBytes = 512 * 1024;
        platform.FreedOnReceipt = true;

        var (clientWorld, client) = Join(host, $"steam:{HostId}");

        Assert.Equal(SessionEnd.None, client.End);
        Assert.Equal(edits, clientWorld.EditCount);
        Assert.Equal(1, host.JoinedPeers);
    }

    /// <summary>
    /// A lobby Steam makes after its transport closed is left, not kept open to friends with no session behind it.
    /// </summary>
    [Fact]
    public void A_lobby_made_after_the_transport_closed_is_left()
    {
        var platform = _network.AddPlayer(HostId);
        var transport = new SteamTransport(Steam(platform), 4, NullLogger<SteamTransport>.Instance);
        transport.Listen(0);
        transport.Close([]);

        platform.RunCallbacks();

        Assert.Empty(_network.OpenLobbies);
    }

    [Fact]
    public void The_hosts_lobby_tells_friends_its_players_its_limit_and_its_match()
    {
        var (_, host, platform) = StartHost(maxPlayers: 3);
        host.Poll();
        string Data(string key) => platform.LobbyData[key];

        Assert.Equal("1", Data(SessionProtocol.LobbyPlayersKey));
        Assert.Equal("3", Data(SessionProtocol.LobbyMaxKey));
        Assert.Equal(
            SessionProtocol.MatchKey(s_generator.Id, s_generator.Version, 60),
            Data(SessionProtocol.LobbyMatchKey)
        );

        var (_, client) = Join(host, $"lobby:{platform.OwnedLobby}");
        Assert.Equal("2", Data(SessionProtocol.LobbyPlayersKey));

        client.Stop();
        host.Poll();
        Assert.Equal("1", Data(SessionProtocol.LobbyPlayersKey));
    }

    [Fact]
    public void A_host_that_ended_hosts_again_on_the_same_transport_through_a_new_lobby()
    {
        var platform = _network.AddPlayer(HostId);
        var world = TestWorlds.Load(s_generator, Vector3.Zero, seed: HostSeed);
        _owned.Add(world);
        var steam = Steam(platform);
        var transport = new SteamTransport(steam, 4, NullLogger<SteamTransport>.Instance);
        var settings = new SessionSettings { Mode = SessionMode.Host, Transport = SessionTransportKind.Steam };
        var first = Session(settings, world, transport, () => { }, HostSeed, steam);
        first.Initialize();
        first.Poll();
        var firstLobby = platform.OwnedLobby;

        first.Stop();
        Assert.Equal(0UL, platform.OwnedLobby);
        Assert.Empty(platform.RichPresence);

        // The next match: a new session on the run's transport.
        var second = Session(settings, world, transport, () => { }, HostSeed, steam);
        _owned.Add(second);
        second.Initialize();
        second.Poll();
        Assert.NotEqual(0UL, platform.OwnedLobby);
        Assert.NotEqual(firstLobby, platform.OwnedLobby);
        Assert.Equal($"+connect_lobby {platform.OwnedLobby}", platform.RichPresence["connect"]);

        var (_, client) = Join(second, $"lobby:{platform.OwnedLobby}");
        Assert.Equal(1, second.JoinedPeers);
        Assert.Equal(SessionEnd.None, client.End);
    }

    [Fact]
    public void A_client_joins_through_the_lobby_and_gets_the_world_and_the_hosts_edits()
    {
        var (hostWorld, host, platform) = StartHost();
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 200, 0), 30, VoxelEditOperation.Clear));
        host.Poll();

        var (clientWorld, client) = Join(host, $"lobby:{platform.OwnedLobby}");
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(40, 200, 0), 20, VoxelEditOperation.Clear));
        host.AfterFixedStep();
        client.Poll();

        Assert.Equal(HostSeed, client.Match.Seed);
        Assert.Equal(2, clientWorld.EditCount);
        Assert.Equal(1, host.JoinedPeers);
        Assert.Equal(SessionEnd.None, client.End);
    }

    [Fact]
    public void A_client_can_join_the_host_by_its_steam_id()
    {
        var (_, host, _) = StartHost();

        var (_, client) = Join(host, $"steam:{HostId}");

        Assert.Equal(1, client.Slot);
    }

    [Fact]
    public void Joining_a_lobby_that_does_not_exist_fails()
    {
        var failure = Assert.Throws<SessionStartException>(() => CreateClient("lobby:12345", () => { }));

        Assert.Contains("no host answered", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_reaches_the_client_with_its_reason()
    {
        var (_, host, _) = StartHost(maxPlayers: 2);
        Join(host, $"steam:{HostId}");

        var failure = Assert.Throws<SessionStartException>(() => Join(host, $"steam:{HostId}", clientId: ClientId + 1));

        Assert.Contains("the host refused: the session has its 2 players", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_quitting_reaches_the_client_as_the_host_leaving()
    {
        var (_, host, _) = StartHost();
        var (_, client) = Join(host, $"steam:{HostId}");

        host.Stop();
        client.Poll();

        Assert.Equal(SessionEnd.HostLeft, client.End);
        Assert.Contains("the host quit", client.EndReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_connection_ends_the_clients_session_and_frees_its_slot_on_the_host()
    {
        var (_, host, platform) = StartHost();
        var (_, client) = Join(host, $"steam:{HostId}");

        platform.Lose(platform.Connections.Single());
        host.Poll();
        client.Poll();

        Assert.Equal(SessionEnd.Lost, client.End);
        Assert.Equal(0, host.JoinedPeers);
    }

    [Fact]
    public void Hosting_through_steam_without_steam_fails_to_start()
    {
        var steam = new SteamService(
            new SteamSettings { AppId = 480 },
            NullLogger<SteamService>.Instance,
            () => (null, "the Steam client is not running")
        );
        steam.Initialize();
        var transport = new SteamTransport(steam, 4, NullLogger<SteamTransport>.Instance);

        var failure = Assert.Throws<SessionStartException>(() => transport.Listen(0));

        Assert.Contains("the Steam client is not running", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Receiving_the_hosts_edits_through_steam_allocates_nothing()
    {
        var (hostWorld, host, _) = StartHost();
        var (clientWorld, client) = Join(host, $"steam:{HostId}");
        var dig = VoxelEdit.Sphere(new VoxelCoord(0, 200, 0), 10, VoxelEditOperation.Clear);
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
    public void Edits_steam_refuses_for_a_full_buffer_are_held_and_arrive_in_order()
    {
        var (hostWorld, host, platform) = StartHost();
        var (clientWorld, client) = Join(host, $"steam:{HostId}");
        platform.Drain();
        platform.SendBufferBytes = 0;
        for (var i = 0; i < 5; i++)
        {
            hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(i * 20, 200, 0), 8, VoxelEditOperation.Clear));
            host.AfterFixedStep();
        }

        client.Poll();
        Assert.Equal(0, clientWorld.EditCount);
        Assert.Equal(1, platform.RefusedReliable);

        // Room for some of them: those go, the rest wait, and a new edit waits behind them.
        platform.SendBufferBytes = 100;
        host.Poll();
        hostWorld.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 200, 40), 8, VoxelEditOperation.Clear));
        host.AfterFixedStep();
        client.Poll();
        Assert.InRange(clientWorld.EditCount, 1, 4);

        platform.SendBufferBytes = int.MaxValue;
        host.Poll();
        client.Poll();

        Assert.Equal(6, clientWorld.EditCount);
        Assert.Equal(SessionEnd.None, client.End);
    }

    [Fact]
    public void State_messages_are_skipped_while_too_much_is_queued_and_a_reliable_one_still_arrives()
    {
        var (host, platform, client, received) = ConnectTransports();
        var state = new byte[1000];

        for (var i = 0; i < 100; i++)
        {
            host.Send(1, state, SessionChannel.Sequenced);
        }

        host.Send(1, [7], SessionChannel.Reliable);
        client.Poll(received);
        var stats = host.TakeStats();

        // Sent while at most the limit was queued: 33 of 1000 bytes, then skipped.
        var sent = SteamTransport.StateQueueLimitBytes / state.Length + 1;
        Assert.Equal(sent + 1, received.Messages.Count);
        Assert.Equal([7], received.Messages[^1]);
        Assert.Equal(100 - sent, stats.SkippedStates);
        Assert.Equal(0, stats.HeldReliable);
        Assert.Equal(default, host.TakeStats());

        // Once the queue is carried away (the next poll asks Steam again), states go again.
        platform.Drain();
        host.Poll(new RecordingListener());
        host.Send(1, state, SessionChannel.Sequenced);
        client.Poll(received);
        Assert.Equal(sent + 2, received.Messages.Count);
    }

    [Fact]
    public void A_peer_whose_held_messages_pass_the_limit_is_ended_with_the_reason()
    {
        var (host, platform, client, received) = ConnectTransports();
        var hostListener = new RecordingListener();
        platform.SendBufferBytes = 0;
        var big = new byte[1024 * 1024];

        for (var i = 0; i < SteamTransport.MaxHeldBytes / big.Length; i++)
        {
            host.Send(1, big, SessionChannel.Reliable);
        }

        host.Poll(hostListener);
        Assert.Empty(hostListener.Disconnects);

        host.Send(1, big, SessionChannel.Reliable);
        host.Poll(hostListener);
        client.Poll(received);

        var stats = host.TakeStats();
        Assert.Equal(1, stats.EndedPeers);
        Assert.Equal(SteamTransport.MaxHeldBytes / big.Length, stats.HeldReliable);
        var (peer, reason, data) = Assert.Single(hostListener.Disconnects);
        Assert.Equal((1, TransportDisconnect.Local), (peer, reason));
        Assert.True(SessionProtocol.TryReadDisconnect(data, out _, out _, out var hostText));
        Assert.Contains("could not keep up", hostText, StringComparison.Ordinal);
        var (_, _, clientData) = Assert.Single(received.Disconnects);
        Assert.True(SessionProtocol.TryReadDisconnect(clientData, out _, out _, out var clientText));
        Assert.Contains("could not keep up", clientText, StringComparison.Ordinal);
        Assert.Empty(received.Messages);
    }

    [Fact]
    public void Players_are_named_by_their_steam_names()
    {
        var (_, host, _) = StartHost();

        Join(host, $"steam:{HostId}");

        Assert.Equal(
            [$"Steam {HostId}", $"Steam {ClientId}"],
            host.Players.ToArray().Where(p => p.Present).Select(p => p.Name)
        );
    }

    [Theory]
    [InlineData("Alice", "piscander", "Alice")]
    [InlineData(null, "  piscander ", "piscander")]
    [InlineData(null, "   ", "Player")]
    [InlineData(null, null, "Player")]
    [InlineData(null, "abcdefghijklmnopqrstuvwxyz0123456789", "abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData(null, "abcdefghijklmnopqrstuvwxyz01234\ud83d\ude00", "abcdefghijklmnopqrstuvwxyz01234")]
    public void The_configured_name_wins_then_the_steam_name_then_player(
        string? configured,
        string? steamName,
        string expected
    )
    {
        var unescaped = steamName is null ? null : System.Text.RegularExpressions.Regex.Unescape(steamName);

        Assert.Equal(expected, SessionService.ChoosePlayerName(configured, unescaped));
    }

    [Fact]
    public void Steam_is_optional()
    {
        var off = new SteamService(
            new SteamSettings { Enabled = false },
            NullLogger<SteamService>.Instance,
            () => throw new InvalidOperationException()
        );
        off.Initialize();
        var missing = new SteamService(
            new SteamSettings { AppId = 480 },
            NullLogger<SteamService>.Instance,
            () => throw new DllNotFoundException("steam_api")
        );
        missing.Initialize();
        var platform = _network.AddPlayer(HostId);
        var running = new SteamService(
            new SteamSettings { AppId = 480 },
            NullLogger<SteamService>.Instance,
            () => (platform, "")
        );
        running.Initialize();

        Assert.False(off.IsAvailable);
        Assert.Contains("Enabled is false", off.UnavailableReason, StringComparison.Ordinal);
        Assert.False(missing.IsAvailable);
        Assert.Contains("no Steam library", missing.UnavailableReason, StringComparison.Ordinal);
        Assert.True(running.IsAvailable);
        Assert.Null(off.PersonaName);
        Assert.Equal($"Steam {HostId}", running.PersonaName);
        running.RunCallbacks();
        running.Dispose();
        Assert.False(running.IsAvailable);
    }

    [Theory]
    [InlineData("lobby:109775240000000001", true, SteamAddressKind.Lobby, 109775240000000001UL)]
    [InlineData("steam:76561198000000001", true, SteamAddressKind.Player, 76561198000000001UL)]
    [InlineData("lobby:0", false, SteamAddressKind.Lobby, 0UL)]
    [InlineData("lobby:", false, SteamAddressKind.Lobby, 0UL)]
    [InlineData("lobby:-5", false, SteamAddressKind.Lobby, 0UL)]
    [InlineData("127.0.0.1:47600", false, SteamAddressKind.Lobby, 0UL)]
    [InlineData(null, false, SteamAddressKind.Lobby, 0UL)]
    public void Steam_addresses_name_a_lobby_or_a_player(string? text, bool valid, SteamAddressKind kind, ulong id)
    {
        Assert.Equal(valid, SteamAddress.TryParse(text, out var parsedKind, out var parsedId));
        if (valid)
        {
            Assert.Equal(kind, parsedKind);
            Assert.Equal(id, parsedId);
        }
    }

    [Fact]
    public void A_connect_string_names_its_lobby_and_nothing_else_is_read_as_one()
    {
        Assert.True(SteamLaunchArguments.TryReadLobby("+connect_lobby 109775240000000001", out var lobby));
        Assert.Equal(109_775_240_000_000_001UL, lobby);
        Assert.False(SteamLaunchArguments.TryReadLobby("+connect_lobby", out _));
        Assert.False(SteamLaunchArguments.TryReadLobby("+connect_lobby 0", out _));
        Assert.False(SteamLaunchArguments.TryReadLobby("+connect_lobby x1", out _));
        Assert.False(SteamLaunchArguments.TryReadLobby("+connect 5", out _));
        Assert.False(SteamLaunchArguments.TryReadLobby(null, out _));
    }

    [Fact]
    public void Steams_join_arguments_become_the_menu_joining_the_lobby()
    {
        Assert.Equal(
            [
                "--Game:Sample:Option=true",
                "--Engine:Session:Mode=Menu",
                "--Engine:Session:Transport=Steam",
                "--Engine:Session:Address=lobby:123",
            ],
            SteamLaunchArguments.Translate(["+connect_lobby", "123", "--Game:Sample:Option=true"])
        );
        string[] plain = ["--Engine:Voxels:Seed=7"];
        Assert.Same(plain, SteamLaunchArguments.Translate(plain));
        string[] dangling = ["+connect_lobby"];
        Assert.Same(dangling, SteamLaunchArguments.Translate(dangling));
    }

    private (VoxelWorld World, SessionService Session, FakeSteamPlatform Platform) StartHost(int maxPlayers = 4)
    {
        var platform = _network.AddPlayer(HostId);
        var world = TestWorlds.Load(s_generator, Vector3.Zero, seed: HostSeed);
        _owned.Add(world);
        var steam = Steam(platform);
        var transport = new SteamTransport(steam, maxPlayers, NullLogger<SteamTransport>.Instance);
        var settings = new SessionSettings
        {
            Mode = SessionMode.Host,
            Transport = SessionTransportKind.Steam,
            MaxPlayers = maxPlayers,
        };
        var session = Session(settings, world, transport, () => { }, HostSeed, steam);
        session.Initialize();
        return (world, session, platform);
    }

    private (VoxelWorld World, SessionService Session) Join(
        SessionService host,
        string address,
        ulong clientId = ClientId,
        bool befriend = true
    )
    {
        var (world, client) = CreateClient(address, host.Poll, clientId, befriend);
        world.Initialize(client.Match.Seed);
        client.LoadJoinedWorld();
        world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(world);
        return (world, client);
    }

    /// <summary>A client of Steam player <paramref name="clientId"/>, the host's friend unless <paramref name="befriend"/> is false.</summary>
    private (VoxelWorld World, SessionService Session) CreateClient(
        string address,
        Action pumpHost,
        ulong clientId = ClientId,
        bool befriend = true
    )
    {
        var platform = _network.AddPlayer(clientId);
        if (befriend)
        {
            _network.Befriend(HostId, clientId);
        }

        var world = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 2,
                Seed = 99,
            },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        _owned.Add(world);
        var steam = Steam(platform);
        var transport = new SteamTransport(steam, 4, NullLogger<SteamTransport>.Instance);
        var settings = new SessionSettings
        {
            Mode = SessionMode.Join,
            Transport = SessionTransportKind.Steam,
            Address = address,
        };
        var session = Session(settings, world, transport, pumpHost, 99, steam);
        _owned.Add(session);
        session.Initialize();
        return (world, session);
    }

    /// <summary>A host and a client transport, connected; the host sees the client as peer 1.</summary>
    private (
        SteamTransport Host,
        FakeSteamPlatform Platform,
        SteamTransport Client,
        RecordingListener Received
    ) ConnectTransports()
    {
        var platform = _network.AddPlayer(HostId);
        var host = new SteamTransport(Steam(platform), 4, NullLogger<SteamTransport>.Instance);
        var client = new SteamTransport(Steam(_network.AddPlayer(ClientId)), 4, NullLogger<SteamTransport>.Instance);
        _network.Befriend(HostId, ClientId);
        var hostListener = new RecordingListener();
        var clientListener = new RecordingListener();
        host.Listen(0);
        client.Connect($"steam:{HostId}");
        for (var i = 0; i < 4; i++)
        {
            host.Poll(hostListener);
            client.Poll(clientListener);
        }

        Assert.Equal([1], hostListener.Connected);
        Assert.Single(clientListener.Connected);
        platform.Drain();
        return (host, platform, client, clientListener);
    }

    private static SteamService Steam(FakeSteamPlatform platform)
    {
        var steam = new SteamService(
            new SteamSettings { AppId = 480 },
            NullLogger<SteamService>.Instance,
            () => (platform, "")
        );
        steam.Initialize();
        return steam;
    }

    private static SessionService Session(
        SessionSettings settings,
        VoxelWorld world,
        ISessionTransport transport,
        Action pump,
        ulong seed,
        SteamService steam
    )
    {
        var clock = 0.0;
        return new SessionService(
            settings,
            60,
            seed,
            0,
            world,
            transport,
            NullLogger<SessionService>.Instance,
            () => clock,
            () =>
            {
                clock += 0.01;
                pump();
            },
            steamName: () => steam.PersonaName
        );
    }

    private sealed class RecordingListener : ISessionTransportListener
    {
        public List<int> Connected { get; } = [];

        public List<byte[]> Messages { get; } = [];

        public List<(int Peer, TransportDisconnect Reason, byte[] Data)> Disconnects { get; } = [];

        public void OnConnected(int peer) => Connected.Add(peer);

        public void OnDisconnected(int peer, TransportDisconnect reason, ReadOnlySpan<byte> data) =>
            Disconnects.Add((peer, reason, data.ToArray()));

        public void OnReceive(int peer, ReadOnlySpan<byte> data) => Messages.Add(data.ToArray());
    }
}
