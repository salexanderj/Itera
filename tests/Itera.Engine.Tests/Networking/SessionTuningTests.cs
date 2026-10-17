using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Networking;

/// <summary>
/// Tunables through a session (<see cref="Tunables"/>, <see cref="SessionService"/>), a host and a client without a world
/// over <see cref="FakeTransport"/>: a joining client takes every one of the host's match tunables, those at their
/// defaults too, and each change after its step; local ones stay each game's; a name the client has not got is passed over and a value past its
/// range left out; and a client's go back to their defaults when its session ends.
/// </summary>
public sealed class SessionTuningTests : IDisposable
{
    private readonly FakeNetwork _network = new();
    private readonly List<IDisposable> _owned = [];
    private double _clock;

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    [Fact]
    public void A_client_takes_the_hosts_match_tunables_as_it_joins_and_as_they_change_and_its_own_go_back_at_the_end()
    {
        var (hostTunables, hostSpeed, hostFeel, _) = Make(mostSpeed: 20);
        var (clientTunables, clientSpeed, clientFeel, clientOnly) = Make(mostSpeed: 20);
        var host = Host(hostTunables);
        hostTunables.TrySet(hostSpeed, 6);
        hostTunables.TrySet(hostFeel, 0.9f);

        var client = Join(host, clientTunables);

        Assert.Equal(6f, clientSpeed.Value);
        Assert.Equal(0.5f, clientFeel.Value);
        hostTunables.TrySet(hostSpeed, 7);
        hostTunables.TrySet(hostFeel, 0.1f);
        host.AfterFixedStep();
        client.Poll();
        Assert.Equal(7f, clientSpeed.Value);
        Assert.Equal(0.5f, clientFeel.Value);
        Assert.Equal(1f, clientOnly.Value);

        client.Stop();
        Assert.Equal(4.5f, clientSpeed.Value);
    }

    [Fact]
    public void A_value_past_the_clients_range_is_left_out_and_a_name_it_has_not_got_passed_over()
    {
        var (hostTunables, hostSpeed, _, hostOnly) = Make(mostSpeed: 20);
        var (clientTunables, clientSpeed, _, _) = Make(mostSpeed: 10);
        var host = Host(hostTunables);
        var client = Join(host, clientTunables);

        hostTunables.TrySet(hostSpeed, 15);
        hostTunables.TrySet(hostOnly, 3);
        host.AfterFixedStep();
        client.Poll();

        Assert.Equal(4.5f, clientSpeed.Value);
        Assert.Equal(SessionEnd.None, client.End);
    }

    /// <summary>
    /// A client takes the host's match tunables at their defaults too: one it changed earlier in its run (as a host of
    /// another match) goes back to the host's, and one whose default differs in its build takes the host's.
    /// </summary>
    [Fact]
    public void A_client_takes_the_hosts_match_tunables_at_their_defaults_too()
    {
        var (hostTunables, _, _, _) = Make(mostSpeed: 20);
        var clientTunables = new Tunables();
        var clientSpeed = clientTunables.Register("move.walk", 5f, 0, 20, "");
        var clientExtra = clientTunables.Register("test.extra", 1, 0, 5, "");
        clientTunables.TrySet(clientExtra, 4);
        var host = Host(hostTunables);

        Join(host, clientTunables);

        Assert.Equal(4.5f, clientSpeed.Value);
        Assert.Equal(1f, clientExtra.Value);
    }

    /// <summary>A walking speed and an extra (match), and a camera's ease (local).</summary>
    private static (Tunables Tunables, Tunable Speed, Tunable Feel, Tunable Extra) Make(float mostSpeed)
    {
        var tunables = new Tunables();
        var speed = tunables.Register("move.walk", 4.5f, 0, mostSpeed, "");
        var feel = tunables.Register("camera.ease", 0.5f, 0, 1, "", TuningScope.Local);
        var extra = tunables.Register("test.extra", 1, 0, 5, "");
        return (tunables, speed, feel, extra);
    }

    private SessionService Host(Tunables tunables)
    {
        var host = Session(new SessionSettings { Mode = SessionMode.Host, PlayerName = "Host" }, () => { }, tunables);
        host.Initialize();
        return host;
    }

    private SessionService Join(SessionService host, Tunables tunables)
    {
        var client = Session(
            new SessionSettings
            {
                Mode = SessionMode.Join,
                Address = "host:47600",
                PlayerName = "Client",
            },
            host.Poll,
            tunables
        );
        client.Initialize();
        client.LoadJoinedWorld();
        return client;
    }

    private SessionService Session(SessionSettings settings, Action wait, Tunables tunables)
    {
        var session = new SessionService(
            settings,
            60,
            5,
            0,
            null,
            new FakeTransport(_network),
            NullLogger<SessionService>.Instance,
            () => _clock,
            () =>
            {
                _clock += 0.01;
                wait();
            },
            identity: new Identity(),
            tunables: tunables
        );
        _owned.Add(session);
        return session;
    }

    private sealed class Identity : IMatchIdentity
    {
        public string Id => "tuning.test";

        public int Version => 1;
    }
}
