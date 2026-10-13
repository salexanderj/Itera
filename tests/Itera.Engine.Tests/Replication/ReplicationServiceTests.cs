using System.Globalization;
using Itera.Engine.Commands;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Replication;

/// <summary>
/// <see cref="Itera.Engine.Replication.ReplicationService"/> between a host and a client in one process: what the host
/// moves appears on the client and follows it the interpolation delay behind (measured: 6.0 steps), as far as that
/// delay is set, and through 100 ms each way with jitter and loss the delay and the way behind (12.2 steps at the
/// median); a send larger than a packet goes in several; a destroyed entity goes, also through loss, and so does one the
/// host stops replicating; one out of a client's reach goes and comes back whole; a client gets no more snapshot bytes a
/// second than its budget, and every entity within two seconds; a client that replaces one that left is sent everything;
/// a client of other kinds is refused; a damaged snapshot or reply is refused or read, never thrown past; a reply for a
/// packet never sent drops the client.
/// </summary>
[RunsLong]
public sealed class ReplicationServiceTests(ITestOutputHelper output)
{
    /// <summary>Steps of 100 ms, the interpolation delay by default.</summary>
    private const double DelayTicks = 6;

    [Fact]
    public void What_the_host_moves_appears_on_the_client_and_follows_it_the_interpolation_delay_behind()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 3, Speed = 3 });
        harness.Step(30);

        var lags = Lags(harness, 120);

        Assert.Equal(3, harness.Shown().Count);
        Assert.Equal(3, harness.ClientPlan.Spawns);
        // Measured 5.99 to 6.02 steps: the delay alone, the host's tick known to within its rounding.
        Assert.All(lags, lag => Assert.InRange(lag, DelayTicks - 0.1, DelayTicks + 0.2));
    }

    /// <summary>
    /// The console's <c>replication</c> on a client prints what it shows (a command of a client's own, not refused as
    /// one only a host may run), and on the host what it sends.
    /// </summary>
    [Fact]
    public void The_replication_command_prints_what_a_client_shows_and_what_the_host_sends()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 2, Speed = 1 });
        harness.Step(30);
        var shown = new Output();
        var sent = new Output();

        Commands(harness.Client).Execute("replication", shown);
        Commands(harness.Host).Execute("replication", sent);

        Assert.Empty(shown.Errors);
        Assert.Contains(shown.Lines, line => line.StartsWith("Showing 2 entities", StringComparison.Ordinal));
        Assert.Empty(sent.Errors);
        Assert.Contains(sent.Lines, line => line.StartsWith("Replicating ", StringComparison.Ordinal));
        Assert.Contains(sent.Lines, line => line.StartsWith("  client 1: ", StringComparison.Ordinal));

        static ConsoleCommands Commands(Engine.Matches.HeadlessMatch match) =>
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ConsoleCommands>(
                match.Services
            );
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Errors.Add(text);
    }

    [Fact]
    public void A_longer_interpolation_delay_shows_entities_that_much_further_behind()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 2, Speed = 3 });
        harness.ClientReplication.InterpolationMilliseconds = 150;
        harness.Step(30);

        var lags = Lags(harness, 60);

        Assert.All(lags, lag => Assert.InRange(lag, 9 - 0.1, 9 + 0.2));
    }

    [Fact]
    public void Through_100_ms_each_way_with_jitter_and_loss_what_the_host_moves_still_follows()
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 5, Speed = 4 },
            hostConditions: new NetworkConditions(0, 0, 5),
            clientConditions: new NetworkConditions(100, 10, 5)
        );
        harness.Step(120);

        var lags = Lags(harness, 600);

        Assert.Equal(5, harness.Shown().Count);
        // The delay and the one-way delay, twelve steps; measured 12.2 at the median and 12.6 at nine in ten, with a lost
        // send or a late one adding a send or two (the most measured, 18).
        var oneWay = 6.0;
        Assert.All(lags, lag => Assert.InRange(lag, DelayTicks + oneWay - 0.1, DelayTicks + oneWay + 7));
        var ordered = lags.Order().ToList();
        var nineInTen = ordered[ordered.Count * 9 / 10];
        Assert.True(nineInTen < DelayTicks + oneWay + 1, $"Nine in ten under {nineInTen:F2}.");
    }

    [Fact]
    public void A_send_larger_than_a_packet_goes_in_several_each_within_the_limit()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 200, Speed = 2 });
        harness.HostTransport.Recorded = [];

        harness.Step(30);

        // The fake takes 1,024 bytes a packet (and throws past it): at 64 KB/s a send's share, about 2,180 bytes, takes
        // several.
        var snapshots = harness.HostTransport.Recorded.Where(message => message[0] == 8).ToList();
        Assert.True(snapshots.Count > 30 / harness.HostReplication.SendIntervalTicks, $"{snapshots.Count} snapshots.");
        Assert.All(snapshots, snapshot => Assert.True(snapshot.Length <= 1024));
        Assert.Equal(200, harness.Shown().Count);
    }

    [Fact]
    public void A_destroyed_entity_goes_from_the_client_also_through_loss()
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 6 },
            hostConditions: new NetworkConditions(0, 0, 30),
            clientConditions: new NetworkConditions(50, 0, 30)
        );
        harness.Step(120);
        Assert.Equal(6, harness.Shown().Count);
        var (first, firstId) = harness.HostPlan.Movers[1];
        var (second, secondId) = harness.HostPlan.Movers[4];

        harness.Host.World.Destroy(first);
        harness.Host.World.Destroy(second);
        harness.Step(120);

        var shown = harness.Shown();
        Assert.Equal(4, shown.Count);
        Assert.False(shown.ContainsKey(firstId));
        Assert.False(shown.ContainsKey(secondId));
        Assert.Equal(2, harness.ClientPlan.Despawns);
    }

    [Fact]
    public void An_entity_the_host_stops_replicating_goes_from_the_client_and_stays_on_the_host()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 3 });
        harness.Step(30);
        var (entity, id) = harness.HostPlan.Movers[0];
        var replicating = harness.HostReplication.Replicating;

        harness.HostReplication.Remove(id);
        harness.Step(30);

        Assert.False(harness.Shown().ContainsKey(id));
        Assert.Equal(2, harness.Shown().Count);
        Assert.True(harness.Host.World.IsAlive(entity));
        Assert.Equal(replicating - 1, harness.HostReplication.Replicating);
    }

    [Fact]
    public void An_id_added_again_under_another_kind_is_shown_afresh_and_the_client_stays()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 1 });
        harness.Step(30);
        var id = PlayerIds.Of(5);
        var first = harness.Host.World.Create(new TestMover { Position = new System.Numerics.Vector3(3, 0, 0) });
        harness.HostReplication.Add(first, harness.HostPlan.Mover, id);
        harness.Step(30);
        Assert.True(harness.Shown().ContainsKey(id));
        var spawns = harness.ClientPlan.Spawns;

        // Removed and added again between two sends, as another kind: the client shows the new one, made afresh.
        harness.HostReplication.Remove(id);
        harness.Host.World.Remove<Components.Replicated>(first);
        var second = harness.Host.World.Create(new TestPlayer { Feet = new System.Numerics.Vector3(-4, 0, 0) });
        harness.HostReplication.Add(second, harness.HostPlan.Player, id);
        harness.Step(30);

        Assert.Equal(1, harness.Host.Match.Session.JoinedPeers);
        Assert.Equal(spawns + 1, harness.ClientPlan.Spawns);
        var origin = harness.Host.Services.GetOrigin();
        Assert.Equal(origin.X - 4, harness.Shown()[id].At.X, 2);
    }

    [Fact]
    public void Offline_an_id_whose_entity_was_destroyed_is_free_for_the_next()
    {
        var plan = new MoverPlan { Count = 0 };
        using var provider = ReplicationHarness.Build(null, plan, new FakeTransport(new FakeNetwork()));
        using var offline = new Itera.Engine.Matches.HeadlessRun(provider).Play(seed: 3);
        var replication = offline.Match.Replication;
        var player = offline.World.Create(new TestPlayer());
        replication.Add(player, plan.Player, PlayerIds.Of(0));

        // A player dies and comes back under its slot's id in the same step, and again after a step.
        offline.World.Destroy(player);
        replication.Add(offline.World.Create(new TestPlayer()), plan.Player, PlayerIds.Of(0));
        offline.Step(1);
        Assert.Equal(1, replication.Replicating);
        offline.Step(1);
        Assert.Equal(1, replication.Replicating);
    }

    [Fact]
    public void An_entity_out_of_a_clients_reach_goes_and_comes_back_whole()
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan
            {
                Count = 1,
                Speed = 10,
                RelevanceMetres = 20,
                PlayerAt = default,
            }
        );
        harness.Step(30);
        var (entity, id) = harness.HostPlan.Movers[0];
        Assert.True(harness.Shown().ContainsKey(id));

        // Past 20 m from the client's player in two seconds: it goes.
        harness.Step(150);
        Assert.False(harness.Shown().ContainsKey(id));
        Assert.Equal(1, harness.ClientPlan.Despawns);

        // Back at 5 m: it comes back, sent whole (a change from a state the client let go of would not be read).
        harness.Host.World.Get<TestMover>(entity).Position.X = 5 - (float)harness.Host.Services.GetOrigin().X;
        harness.HostPlan.Speed = 0;
        harness.Step(30);
        var shown = harness.Shown();
        Assert.True(shown.ContainsKey(id));
        Assert.Equal(5, shown[id].At.X, 1);
        Assert.Equal(2, harness.ClientPlan.Spawns);
    }

    [Fact]
    public void A_client_gets_no_more_snapshot_bytes_a_second_than_its_budget_and_every_entity_within_two_seconds()
    {
        var options = new Dictionary<string, string?>
        {
            ["Engine:Replication:BytesPerSecond"] = "8192",
            ["Engine:Replication:PeakBytesPerSecond"] = "8192",
        };
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 400, Speed = 2 }, options: options);
        harness.Step(120);
        // Every mover; the client's own player is not sent to it.
        Assert.Equal(400, harness.Shown().Count);
        harness.HostTransport.Recorded = [];

        harness.Step(600);

        var bytes = harness.HostTransport.Recorded.Where(message => message[0] == 8).Sum(message => message.Length);
        var perSecond = bytes / 10.0;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{perSecond:F0} snapshot bytes a second."));
        Assert.True(perSecond <= 8192 * 1.02, $"{perSecond:F0} bytes a second.");
        // The movers want far more than the budget (400 a send, each a few bytes), so it is spent.
        Assert.True(perSecond > 8192 * 0.9, $"Only {perSecond:F0} bytes a second.");
    }

    [Fact]
    public void A_client_that_takes_the_place_of_one_that_left_is_sent_everything()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 4, Speed = 2 });
        harness.Step(60);

        harness.ReplaceClient();
        harness.Step(60);

        Assert.Equal(4, harness.Shown().Count);
        Assert.Equal(1, harness.Host.Match.Session.JoinedPeers);
    }

    [Fact]
    public void A_client_replicating_other_kinds_is_refused()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ReplicationHarness.Start(new MoverPlan(), clientPlan: new MoverPlan { ExtraKind = true })
        );

        Assert.Contains("replicate other kinds", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Damaged snapshots fed to one client one after another: each is read or refused with
    /// <see cref="InvalidDataException"/> (which drops the host in a real session), none hangs, and the client's next step
    /// runs. That what is read keeps to its fields' ranges is the codec's test.
    /// </summary>
    [Fact]
    public void A_damaged_snapshot_is_refused_or_read_and_the_next_step_runs()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 2 });
        harness.HostTransport.Recorded = [];
        harness.Step(10);
        var sample = harness.HostTransport.Recorded.Last(message => message[0] == 8 && message.Length > 12);
        IReplicationEndpoint endpoint = harness.ClientReplication;
        var rows = HostileValues.Damaged("snapshot", sample[1..]).ToList();

        Watchdog.Run(
            rows.Count,
            i => rows[i].Label,
            i =>
            {
                try
                {
                    endpoint.ReceiveSnapshot(rows[i].Body);
                }
                catch (InvalidDataException) { }
            }
        );

        harness.Client.Step(1);
    }

    [Fact]
    public void A_damaged_reply_is_refused_or_read()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 2 });
        harness.Step(10);
        var reply = new MessageWriter();
        reply.WriteVarInt(3);
        reply.WriteULong(5);
        IReplicationEndpoint endpoint = harness.HostReplication;
        var rows = HostileValues.Damaged("reply", reply.Written.ToArray()).ToList();

        Watchdog.Run(
            rows.Count,
            i => rows[i].Label,
            i =>
            {
                try
                {
                    endpoint.ReceiveReply(1, rows[i].Body);
                }
                catch (InvalidDataException) { }
            }
        );

        harness.Host.Step(1);
    }

    [Fact]
    public void A_reply_for_a_packet_never_sent_drops_the_client()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 1 });
        harness.Step(5);
        Assert.Equal(1, harness.Host.Match.Session.JoinedPeers);
        var reply = new MessageWriter();
        reply.WriteByte(9);
        reply.WriteVarInt(1_000_000);
        reply.WriteULong(0);

        harness.Client.Match.Session.SendEngineToHost(reply.Written);
        harness.Host.PollSession();

        Assert.Equal(0, harness.Host.Match.Session.JoinedPeers);
    }

    /// <summary>
    /// Steps <paramref name="ticks"/> times, and after each reads how far behind the host each mover is shown, in steps:
    /// its shown x against the host's speed (each mover starts 2 m apart and moves along x at the plan's speed). Every
    /// mover is shown at every step, or the test fails.
    /// </summary>
    private List<double> Lags(ReplicationHarness harness, int ticks)
    {
        var lags = new List<double>();
        var origin = harness.Host.Services.GetOrigin();
        for (var tick = 0; tick < ticks; tick++)
        {
            harness.Step(1);
            var shown = harness.Shown();
            foreach (var (entity, id) in harness.HostPlan.Movers)
            {
                Assert.True(shown.TryGetValue(id, out var state), $"Mover {id} is not shown at step {tick}.");
                var hostX = origin.X + harness.Host.World.Get<TestMover>(entity).Position.X;
                var behind = (hostX - state.At.X) / (harness.HostPlan.Speed / 60.0);
                lags.Add(behind);
            }
        }

        var sorted = lags.Order().ToList();
        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Lag in steps over {lags.Count}: least {sorted[0]:F3}, median {sorted[sorted.Count / 2]:F3}, 90% {sorted[sorted.Count * 9 / 10]:F3}, 99% {sorted[sorted.Count * 99 / 100]:F3}, most {sorted[^1]:F3}"
            )
        );
        return lags;
    }
}

internal static class OriginExtensions
{
    /// <summary>The match's origin, absolute.</summary>
    public static WorldPoint GetOrigin(this IServiceProvider services) =>
        Microsoft
            .Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<WorldOrigin>(services)
            .ToWorld(System.Numerics.Vector3.Zero);
}
