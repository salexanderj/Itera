using Itera.Engine.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// <see cref="SteamFriendList"/> on <see cref="FakeSteamNetwork"/>: only friends hosting a lobby are listed, by name,
/// with the players and the limit from the lobby's data and another version marked; the data is asked for on the timed
/// reads; a picture arrives later and is listed then; a friend who stops hosting goes; nothing is read while the join
/// page is not shown; and a friend's "Join game" becomes <see cref="SteamFriendList.JoinRequested"/>.
/// </summary>
public sealed class SteamFriendListTests : IDisposable
{
    private const ulong Me = 76_561_198_000_000_010;
    private const ulong Alice = 76_561_198_000_000_011;
    private const ulong Bob = 76_561_198_000_000_012;
    private const ulong Carol = 76_561_198_000_000_013;
    private const string MatchKey = "22;test;1;60";

    private readonly FakeSteamNetwork _network = new();
    private readonly FakeSteamPlatform _me;
    private readonly SteamService _steam;
    private readonly SteamFriendList _friends;

    public SteamFriendListTests()
    {
        _me = _network.AddPlayer(Me);
        _steam = new SteamService(
            new SteamSettings { AppId = 480 },
            NullLogger<SteamService>.Instance,
            () => (_me, "")
        );
        _steam.Initialize();
        _friends = new SteamFriendList(_steam, MatchKey, NullLogger<SteamFriendList>.Instance);
        foreach (var (id, name) in new[] { (Carol, "Carol"), (Bob, "Bob"), (Alice, "Alice") })
        {
            _network.AddPlayer(id).PersonaName = name;
            _network.Befriend(Me, id);
        }
    }

    public void Dispose() => _steam.Dispose();

    [Fact]
    public void Friends_hosting_are_listed_by_name_with_their_players_and_another_version_marked()
    {
        var alice = Host(Alice, players: 2, max: 4, MatchKey);
        var carol = Host(Carol, players: 1, max: 4, "21;test;1;60");

        _friends.Update(0, watching: true);

        Assert.Equal(
            [
                new FriendHost(Alice, "Alice", alice, 2, 4, false, null),
                new FriendHost(Carol, "Carol", carol, 1, 4, true, null),
            ],
            _friends.Hosts
        );
        Assert.Equal([alice, carol], _me.RequestedLobbies.Order());
    }

    [Fact]
    public void A_lobby_without_data_yet_is_listed_without_players_and_not_as_another_version()
    {
        var lobby = Host(Bob, players: 0, max: 0, matchKey: null);

        _friends.Update(0, watching: true);

        Assert.Equal([new FriendHost(Bob, "Bob", lobby, 0, 0, false, null)], _friends.Hosts);
    }

    [Fact]
    public void A_picture_steam_still_fetches_is_listed_once_it_arrives()
    {
        var alice = _network.Player(Alice)!;
        alice.AvatarPending = true;
        Host(Alice, 1, 4, MatchKey);
        _friends.Update(0, watching: true);
        Assert.Null(_friends.Hosts[0].Avatar);
        var version = _friends.Version;

        alice.AvatarPending = false;
        alice.Avatar = (2, 2, new byte[16]);
        _me.ReportAvatarLoaded(Alice);
        _steam.RunCallbacks();
        _friends.Update(0.1, watching: true);

        Assert.Equal(new FriendAvatar(2, 2, alice.Avatar.Value.Rgba), _friends.Hosts[0].Avatar);
        Assert.NotEqual(version, _friends.Version);
    }

    [Fact]
    public void A_friend_who_stops_hosting_goes_from_the_list_when_steam_reports_it()
    {
        var lobby = Host(Alice, 1, 4, MatchKey);
        _friends.Update(0, watching: true);
        Assert.Single(_friends.Hosts);

        _network.Player(Alice)!.LeaveLobby(lobby);
        _me.ReportFriendChanged(Alice);
        _steam.RunCallbacks();
        _friends.Update(0.1, watching: true);

        Assert.Empty(_friends.Hosts);
    }

    [Fact]
    public void Nothing_is_read_while_the_join_page_is_not_shown_and_a_mark_reads_it_again()
    {
        _friends.Update(0, watching: false);
        Host(Alice, 1, 4, MatchKey);
        _friends.Update(0.1, watching: true);
        Assert.Single(_friends.Hosts);

        // Within the 5 s, a new host is seen only once marked stale.
        Host(Bob, 1, 4, MatchKey);
        _friends.Update(0.2, watching: true);
        Assert.Single(_friends.Hosts);
        _friends.MarkStale();
        _friends.Update(0.3, watching: true);
        Assert.Equal(2, _friends.Hosts.Count);
    }

    [Fact]
    public void A_friends_join_game_from_steam_is_raised_with_the_name_and_the_lobby()
    {
        var joins = new List<(string Name, string Address)>();
        _friends.JoinRequested += (name, address) => joins.Add((name, address));
        _friends.Update(0, watching: false);

        _me.RequestJoin(Alice, "lobby:109775240000000001");
        _me.RequestJoin(Bob, "");
        _steam.RunCallbacks();

        Assert.Equal([("Alice", "lobby:109775240000000001")], joins);
    }

    /// <summary>A friend hosting: a lobby of theirs with the given data (none for a null match key).</summary>
    private ulong Host(ulong friend, int players, int max, string? matchKey)
    {
        var player = _network.Player(friend)!;
        player.CreateLobby(max);
        var lobby = player.OwnedLobby;
        if (matchKey is not null)
        {
            player.SetLobbyData(lobby, SessionProtocol.LobbyPlayersKey, players.ToString());
            player.SetLobbyData(lobby, SessionProtocol.LobbyMaxKey, max.ToString());
            player.SetLobbyData(lobby, SessionProtocol.LobbyMatchKey, matchKey);
        }

        return lobby;
    }
}
