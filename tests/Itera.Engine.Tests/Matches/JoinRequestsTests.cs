using Itera.Engine.Commands;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// <see cref="JoinRequests"/>: a friend's Steam join with no match joins at once; during a match it waits for the
/// menu's answer (accept joins, decline stays), and goes when the match it came in ends; whatever menu shows.
/// </summary>
public sealed class JoinRequestsTests : IDisposable
{
    private const string Lobby = "lobby:109775240000000005";
    private readonly MatchControl _matches = new();
    private readonly SteamService _steam;
    private readonly SteamFriendList _friends;
    private readonly JoinRequests _joins;

    public JoinRequestsTests()
    {
        _steam = new SteamService(
            new SteamSettings { Enabled = false },
            NullLogger<SteamService>.Instance,
            () => (null, "off")
        );
        _steam.Initialize();
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        var starts = new MatchCommands(commands, _matches, _steam, Options.Create(new EngineOptions()));
        _friends = new SteamFriendList(_steam, "test", NullLogger<SteamFriendList>.Instance);
        _joins = new JoinRequests(_friends, _matches, starts);
    }

    public void Dispose()
    {
        _joins.Dispose();
        _steam.Dispose();
    }

    [Fact]
    public void With_no_match_a_friends_join_joins_at_once()
    {
        Request();

        var start = _matches.TakeStart()!;
        Assert.Equal(SessionMode.Join, start.Mode);
        Assert.Equal(Lobby, start.Address);
        Assert.Null(_joins.Pending);
    }

    [Fact]
    public void During_a_match_it_waits_and_accept_joins()
    {
        Play();
        var version = _joins.Version;
        Request();

        Assert.Null(_matches.TakeStart());
        Assert.Equal(new JoinRequest("a friend", Lobby), _joins.Pending);
        Assert.True(_joins.Version > version);

        _joins.Accept();
        Assert.Equal(Lobby, _matches.TakeStart()!.Address);
        Assert.Null(_joins.Pending);
    }

    [Fact]
    public void Decline_stays_in_the_match()
    {
        Play();
        Request();
        _joins.Decline();
        _joins.Accept();

        Assert.Null(_joins.Pending);
        Assert.Null(_matches.TakeStart());
    }

    [Fact]
    public void A_request_goes_when_the_match_it_came_in_ends_or_another_starts()
    {
        Play();
        Request();
        _matches.Phase = MatchPhase.Menu;
        Assert.Null(_joins.Pending);

        Play();
        Assert.Null(_joins.Pending);
        _joins.Accept();
        Assert.Null(_matches.TakeStart());
    }

    [Fact]
    public void Disposed_it_takes_no_more_requests()
    {
        _joins.Dispose();
        _joins.Dispose();
        Request();

        Assert.Null(_matches.TakeStart());
    }

    /// <summary>A match is played: the next one counted.</summary>
    private void Play()
    {
        _matches.Started++;
        _matches.Phase = MatchPhase.Playing;
    }

    /// <summary>A friend's "Join game" from the Steam overlay, as Steam reports it to the list (no name without Steam).</summary>
    private void Request() => ((ISteamFriendsListener)_friends).OnJoinRequested(5, Lobby);
}
