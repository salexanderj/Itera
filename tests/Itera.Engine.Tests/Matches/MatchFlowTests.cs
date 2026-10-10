using Itera.Engine.Diagnostics;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// The match flow of a run (<see cref="MatchFlow"/>), headless with the engine's real services, a test game's
/// (<see cref="EngineComposition"/>), and an in-memory transport: an offline start waits for a frame saying so (or half
/// a second), then starts its world, and its end goes back to the menu; a join from the menu with no host ends with its
/// reason, and the same join from the options throws (exit code 1); a leave ends the match and a start ends it and
/// starts the new one; a host that quits sends a client of the menu back to it with the reason, while a client of the
/// options ends its run; and an exception in a menu match ends it unsaved with the reason, while one in a match of the
/// options is left to end the run.
/// </summary>
public sealed class MatchFlowTests : IDisposable
{
    private readonly FakeNetwork _network = new();
    private readonly List<Game> _games = [];

    public void Dispose()
    {
        foreach (var game in _games)
        {
            game.Flow.End(clean: true);
            game.Provider.Dispose();
        }
    }

    [Fact]
    public void An_offline_start_waits_for_a_frame_then_starts_its_world_and_its_end_goes_back_to_the_menu()
    {
        var game = NewGame();
        game.Flow.Start(Offline(fromCommandLine: true), frame: 0);
        Assert.Equal(MatchPhase.Starting, game.Matches.Phase);
        Assert.NotNull(game.Flow.Match);

        // No frame shown since, and no half second: not yet.
        game.Flow.StartWorldWhenShown(0);
        Assert.Null(game.Flow.Playing);

        game.Flow.StartWorldWhenShown(1);
        Assert.NotNull(game.Flow.Playing);
        Assert.Equal(MatchPhase.Loading, game.Matches.Phase);
        Assert.Equal(1, game.WorldsStarted);

        game.Flow.End(clean: true, "Left.");
        Assert.Null(game.Flow.Match);
        Assert.Equal(MatchPhase.Menu, game.Matches.Phase);
        Assert.Equal("Left.", game.Matches.LastEnd);
        Assert.Equal(1, game.Ended);
        Assert.Equal(0, game.Flow.SessionExitCode());
    }

    [Fact]
    public void An_exception_in_a_menu_match_ends_it_unsaved_with_the_reason_but_not_an_options_match()
    {
        var game = NewGame();
        game.Flow.Start(Offline(fromCommandLine: false), frame: 0);
        game.Flow.StartWorldWhenShown(1);
        var match = game.Flow.Match!;
        Assert.True(game.Flow.EndsAfterFault);

        game.Flow.EndAfterFault(new InvalidOperationException("A system failed."));

        Assert.Null(game.Flow.Match);
        Assert.Equal(MatchPhase.Menu, game.Matches.Phase);
        Assert.Equal(MatchFlow.FaultReason, game.Matches.LastEnd);
        // Not ended cleanly: the named save is not written over a world the exception may have left half changed.
        Assert.False(match.EndedCleanly);
        Assert.Equal(1, game.Ended);
        Assert.False(game.Flow.EndsAfterFault);

        // A match the options started is left to end the run (exit code 1), as its scripts expect.
        game.Flow.Start(Offline(fromCommandLine: true), frame: 2);
        Assert.False(game.Flow.EndsAfterFault);
        Assert.Throws<InvalidOperationException>(() => game.Flow.EndAfterFault(new InvalidOperationException("x")));
        Assert.NotNull(game.Flow.Match);
    }

    [Fact]
    public void Without_a_frame_the_world_starts_after_half_a_second()
    {
        var game = NewGame();
        game.Flow.Start(Offline(fromCommandLine: false), frame: 4);
        game.Clock = 0.4;
        game.Flow.StartWorldWhenShown(4);
        Assert.Null(game.Flow.Playing);

        game.Clock = 0.6;
        game.Flow.StartWorldWhenShown(4);
        Assert.NotNull(game.Flow.Playing);
    }

    [Fact]
    public void A_join_from_the_menu_with_no_host_ends_with_its_reason_and_from_the_options_throws()
    {
        var game = NewGame();
        game.Matches.RequestStart(Join(fromCommandLine: false));
        game.Flow.HandleRequests(0);
        Assert.Equal(MatchPhase.Joining, game.Matches.Phase);

        game.Flow.PollSession(0);

        Assert.Null(game.Flow.Match);
        Assert.Equal(MatchPhase.Menu, game.Matches.Phase);
        Assert.Contains("Could not join", game.Matches.LastEnd, StringComparison.Ordinal);

        game.Flow.Start(Join(fromCommandLine: true));
        Assert.Throws<SessionStartException>(() => game.Flow.PollSession(0));
    }

    [Fact]
    public void A_leave_ends_the_match_and_a_start_ends_it_and_starts_the_new_one()
    {
        var game = NewGame();
        game.Flow.Start(Offline(fromCommandLine: false));
        game.Matches.RequestLeave("You left.");
        game.Flow.HandleRequests(1);
        Assert.Null(game.Flow.Match);
        Assert.Equal("You left.", game.Matches.LastEnd);

        game.Flow.Start(Offline(fromCommandLine: false));
        game.Flow.StartWorldWhenShown(1);
        var first = game.Flow.Match;
        game.Matches.RequestStart(Offline(fromCommandLine: false) with { Seed = 7 });
        game.Flow.HandleRequests(2);

        Assert.NotNull(game.Flow.Match);
        Assert.NotSame(first, game.Flow.Match);
        Assert.Equal(3, game.Matches.Started);
        Assert.Equal(2, game.Ended);
        Assert.Equal(MatchPhase.Starting, game.Matches.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_host_that_quits_sends_a_menu_client_back_and_ends_an_options_clients_run(bool fromCommandLine)
    {
        var host = NewGame();
        host.Flow.Start(new MatchStart(SessionMode.Host, SessionTransportKind.Direct, null, 1, null, false));
        host.Flow.StartWorldWhenShown(1);
        Assert.NotNull(host.Flow.Playing);

        var client = NewGame();
        client.Flow.Start(Join(fromCommandLine));
        for (var poll = 0; poll < 200 && client.Matches.Phase == MatchPhase.Joining; poll++)
        {
            host.Flow.PollSession(1);
            client.Flow.PollSession(1);
        }

        Assert.Equal(MatchPhase.Starting, client.Matches.Phase);
        host.Flow.End(clean: true);
        for (var poll = 0; poll < 20 && !client.Flow.SessionEnded; poll++)
        {
            client.Flow.PollSession(2);
        }

        Assert.True(client.Flow.SessionEnded);
        Assert.Equal(fromCommandLine, client.Flow.SessionEndsRun);
        client.Flow.HandleRequests(2);
        if (fromCommandLine)
        {
            // The run ends: the loop stops, and its exit code comes from the session (a host that quit: 0).
            Assert.NotNull(client.Flow.Match);
            Assert.Equal(0, client.Flow.SessionExitCode());
        }
        else
        {
            Assert.Null(client.Flow.Match);
            Assert.Equal(MatchPhase.Menu, client.Matches.Phase);
            Assert.StartsWith("The host", client.Matches.LastEnd, StringComparison.Ordinal);
            Assert.EndsWith(".", client.Matches.LastEnd, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Without_terrain_an_offline_match_starts_and_ends()
    {
        var game = NewGame(terrain: false);
        game.Flow.Start(Offline(fromCommandLine: false), frame: 0);
        game.Flow.StartWorldWhenShown(1);

        Assert.NotNull(game.Flow.Playing);
        Assert.Null(game.Flow.Playing!.Terrain);
        Assert.Equal(1, game.WorldsStarted);
        game.Flow.End(clean: true, "Left.");
        Assert.Equal(MatchPhase.Menu, game.Matches.Phase);
    }

    [Fact]
    public void Without_terrain_a_client_joins_a_host_and_starts()
    {
        var host = NewGame(terrain: false);
        host.Flow.Start(new MatchStart(SessionMode.Host, SessionTransportKind.Direct, null, 3, null, false));
        host.Flow.StartWorldWhenShown(1);
        var client = NewGame(terrain: false);
        client.Flow.Start(Join(fromCommandLine: false));

        for (var poll = 0; poll < 200 && client.Matches.Phase == MatchPhase.Joining; poll++)
        {
            host.Flow.PollSession(1);
            client.Flow.PollSession(1);
        }

        Assert.Equal(MatchPhase.Starting, client.Matches.Phase);
        client.Flow.StartWorldWhenShown(2);
        Assert.NotNull(client.Flow.Playing);
        Assert.Equal(3UL, client.Flow.Playing!.Session.Match.Seed);
        Assert.Equal(SessionRole.Client, client.Flow.Playing.Session.Role);
    }

    private static MatchStart Offline(bool fromCommandLine) =>
        new(SessionMode.Offline, SessionTransportKind.Direct, null, 1, null, fromCommandLine);

    private static MatchStart Join(bool fromCommandLine) =>
        new(SessionMode.Join, SessionTransportKind.Direct, "127.0.0.1:47600", 0, null, fromCommandLine);

    /// <summary>A run's services and its match flow, its sessions on the shared in-memory network.</summary>
    private Game NewGame(bool terrain = true)
    {
        var provider = EngineComposition.Build(
            configure: services =>
                services.AddSingleton<ISessionTransports>(new Transports(new FakeTransport(_network))),
            terrain: terrain
        );
        foreach (var startup in provider.GetServices<IRunStartup>())
        {
            startup.Start();
        }

        var game = new Game(provider);
        _games.Add(game);
        return game;
    }

    private sealed class Transports(FakeTransport transport) : ISessionTransports
    {
        public ISessionTransport Transport(SessionTransportKind kind) => transport;
    }

    private sealed class Game
    {
        public Game(ServiceProvider provider)
        {
            Provider = provider;
            Matches = provider.GetRequiredService<MatchControl>();
            Flow = new MatchFlow(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Matches,
                provider.GetRequiredService<SystemTimes>(),
                new MatchFlowHooks(new MatchGpu(_ => { }, () => { }), _ => WorldsStarted++, () => Ended++),
                () => Clock,
                _ => { },
                NullLogger.Instance
            );
        }

        public ServiceProvider Provider { get; }

        public MatchControl Matches { get; }

        public MatchFlow Flow { get; }

        public double Clock { get; set; }

        public int WorldsStarted { get; private set; }

        public int Ended { get; private set; }
    }
}
