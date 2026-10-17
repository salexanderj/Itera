using System.Globalization;
using System.Numerics;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Replication;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Replication;

/// <summary>
/// A client's own player, predicted, and the host's correction (<c>ReplicationService.Prediction.cs</c>), over the test
/// floor in one process: the client moves its player at once and the host moves its copy from the same commands to the
/// same place, without a correction; through 100 ms each way with jitter and loss too; a host that moves the player
/// elsewhere corrects the client, whose drawn jump eases out; commands go as the host reads them; a damaged reply with
/// commands is refused or read; and a predicted step allocates nothing.
/// </summary>
[RunsLong]
public sealed class PredictionTests(ITestOutputHelper output)
{
    /// <summary>Two seconds of walking ahead, one sprinting to the side, one walking back with a jump a second; then still.</summary>
    private static PlayerCommand Walk(ulong tick, ulong stopAt)
    {
        if (tick >= stopAt)
        {
            return new PlayerCommand { Yaw = 0.5f };
        }

        return (tick % 240) switch
        {
            < 120 => new PlayerCommand { Move = new Vector2(0, 1), Yaw = 0.5f },
            < 180 => new PlayerCommand
            {
                Move = new Vector2(1, 0),
                Yaw = 2f,
                Buttons = PlayerButtons.Sprint,
            },
            _ => new PlayerCommand
            {
                Move = new Vector2(0, -1),
                Yaw = -1f,
                Buttons = tick % 60 == 0 ? PlayerButtons.Jump : PlayerButtons.None,
            },
        };
    }

    [Fact]
    public void A_client_moves_its_player_at_once_and_the_host_moves_its_copy_from_the_same_commands_alike()
    {
        var stopAt = ulong.MaxValue;
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            clientPlan: new MoverPlan { Characters = true, Script = tick => Walk(tick, stopAt) }
        );
        var start = Feet(harness.Client, harness.ClientPlan.Own);
        harness.Step(6);
        // At once: the client's player has moved before the host has had its first command back.
        Assert.True(Feet(harness.Client, harness.ClientPlan.Own).DistanceTo(start) > 0.05);
        // The host's copy took steps before the first command came (it fell onto the floor): the first owner's state
        // brings the two together, and from then on they move alike.
        harness.Step(54);
        var startup = harness.ClientReplication.Corrections;
        Assert.True(startup <= 1, $"{startup} corrections in the first second.");

        harness.Step(480);
        stopAt = harness.Client.Tick;
        harness.Step(90);

        var client = Feet(harness.Client, harness.ClientPlan.Own);
        var host = Feet(harness.Host, harness.HostPlan.Remote[1]);
        output.WriteLine(
            Text(
                $"Client {client}, host {host}, {harness.ClientReplication.Corrections - startup} corrections after the first second."
            )
        );
        Assert.True(client.DistanceTo(start) > 5, $"The player walked only to {client}.");
        Assert.True(client.DistanceTo(host) < 0.001, $"The client's player at {client}, the host's copy at {host}.");
        Assert.Equal(startup, harness.ClientReplication.Corrections);
    }

    [Fact]
    public void Through_100_ms_each_way_with_jitter_and_loss_the_host_follows_and_corrections_are_rare()
    {
        var stopAt = ulong.MaxValue;
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            hostConditions: new NetworkConditions(0, 0, 5),
            clientConditions: new NetworkConditions(100, 10, 5),
            clientPlan: new MoverPlan { Characters = true, Script = tick => Walk(tick, stopAt) }
        );

        harness.Step(60);
        var startup = harness.ClientReplication.Corrections;
        harness.Step(720);
        stopAt = harness.Client.Tick;
        harness.Step(120);

        var client = Feet(harness.Client, harness.ClientPlan.Own);
        var host = Feet(harness.Host, harness.HostPlan.Remote[1]);
        var corrections = harness.ClientReplication.Corrections - startup;
        output.WriteLine(Text($"Client {client}, host {host}, {corrections} corrections after the first second."));
        Assert.True(client.DistanceTo(host) < 0.01, $"The client's player at {client}, the host's copy at {host}.");
        // A command is missed only when the eight replies that carry it are all lost, or come later than the host's buffer.
        Assert.True(corrections <= 3, $"{corrections} corrections.");
    }

    [Fact]
    public void A_host_that_puts_the_player_elsewhere_corrects_the_client_and_the_drawn_jump_eases_out()
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            clientPlan: new MoverPlan { Characters = true, Script = _ => new PlayerCommand() }
        );
        harness.Step(60);
        var startup = harness.ClientReplication.Corrections;

        // The host moves its copy a metre along x (a push, a knockdown, a respawn).
        harness.Host.World.Get<CharacterPosition>(harness.HostPlan.Remote[1]).Feet.X += 1;
        harness.Step(20);

        Assert.True(harness.ClientReplication.Corrections > startup);
        var client = Feet(harness.Client, harness.ClientPlan.Own);
        var host = Feet(harness.Host, harness.HostPlan.Remote[1]);
        Assert.True(client.DistanceTo(host) < 0.01, $"The client's player at {client}, the host's copy at {host}.");
        // The drawn body started where it was and eases to where it is.
        harness.Step(40);
        var eye = harness.Client.World.Get<EyeSmoothing>(harness.ClientPlan.Own);
        Assert.True(eye.Correction.Length() < 0.01, $"Still drawn {eye.Correction.Length():F3} m off.");
    }

    [Fact]
    public void Through_100_ms_each_way_a_host_that_pushes_the_walking_player_corrects_the_client_where_it_is()
    {
        var stopAt = ulong.MaxValue;
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            clientConditions: new NetworkConditions(100, 0, 0),
            clientPlan: new MoverPlan { Characters = true, Script = tick => Walk(tick, stopAt) }
        );
        harness.Step(120);
        var before = harness.ClientReplication.Corrections;

        // While it walks, the host pushes its copy a metre along x: the client is corrected and runs its later commands
        // (walking, a turn, jumps) again from there.
        harness.Host.World.Get<CharacterPosition>(harness.HostPlan.Remote[1]).Feet.X += 1;
        harness.Step(180);
        stopAt = harness.Client.Tick;
        harness.Step(90);

        var client = Feet(harness.Client, harness.ClientPlan.Own);
        var host = Feet(harness.Host, harness.HostPlan.Remote[1]);
        var corrections = harness.ClientReplication.Corrections - before;
        output.WriteLine(Text($"Client {client}, host {host}, {corrections} corrections after the push."));
        Assert.True(corrections >= 1);
        Assert.True(client.DistanceTo(host) < 0.01, $"The client's player at {client}, the host's copy at {host}.");
    }

    [Fact]
    public void After_a_client_stall_the_host_starts_again_from_its_newest_commands_and_corrections_stop()
    {
        var stopAt = ulong.MaxValue;
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            clientPlan: new MoverPlan { Characters = true, Script = tick => Walk(tick, stopAt) }
        );
        harness.Step(120);

        // The client stalls for a third of a second and drops those steps: its ticks fall behind the host's for good.
        harness.StepHost(20);
        harness.Step(60);
        var settled = harness.ClientReplication.Corrections;
        harness.Step(300);
        stopAt = harness.Client.Tick;
        harness.Step(90);

        var client = Feet(harness.Client, harness.ClientPlan.Own);
        var host = Feet(harness.Host, harness.HostPlan.Remote[1]);
        var corrections = harness.ClientReplication.Corrections - settled;
        output.WriteLine(
            Text($"Client {client}, host {host}, {corrections} corrections once settled after the stall.")
        );
        Assert.Equal(0, corrections);
        Assert.True(client.DistanceTo(host) < 0.001, $"The client's player at {client}, the host's copy at {host}.");
    }

    [Fact]
    public void The_drawn_jump_of_a_correction_starts_where_the_player_was_drawn()
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            clientPlan: new MoverPlan { Characters = true, Script = _ => new PlayerCommand() }
        );
        harness.Step(60);
        var drawnBefore = Drawn(harness);

        harness.Host.World.Get<CharacterPosition>(harness.HostPlan.Remote[1]).Feet.X += 1;
        // Step until the correction is made, reading what is drawn at the step's start each time.
        var corrections = harness.ClientReplication.Corrections;
        var steps = 0;
        while (harness.ClientReplication.Corrections == corrections && steps++ < 60)
        {
            drawnBefore = Drawn(harness);
            harness.Step(1);
        }

        // At the step the correction came, what is drawn at its start is where it was drawn before: no jump.
        var origin = harness.Client.Services.GetRequiredService<WorldOrigin>();
        var position = harness.Client.World.Get<CharacterPosition>(harness.ClientPlan.Own);
        var eye = harness.Client.World.Get<EyeSmoothing>(harness.ClientPlan.Own);
        var drawnAfter = origin.ToWorld(position.PreviousFeet + eye.PreviousCorrection);
        Assert.True(drawnAfter.DistanceTo(drawnBefore) < 0.01, $"Drawn at {drawnBefore}, then at {drawnAfter}.");
    }

    [Fact]
    public void Commands_go_as_the_host_reads_them()
    {
        var random = new Random(7);
        var writer = new MessageWriter();
        for (var i = 0; i < 10_000; i++)
        {
            var command = new PlayerCommand
            {
                Move = new Vector2((float)(random.NextDouble() * 2.4 - 1.2), (float)(random.NextDouble() * 2.4 - 1.2)),
                Yaw = (float)((random.NextDouble() - 0.5) * 30),
                Pitch = (float)((random.NextDouble() - 0.5) * 4),
                Buttons = (PlayerButtons)random.Next(256),
                Actions = (uint)random.NextInt64(uint.MaxValue + 1L),
            };
            writer.Reset();
            ReplicationService.WriteCommand(writer, in command);
            var reader = new MessageReader(writer.Written);
            var read = ReplicationService.ReadCommand(ref reader);
            reader.EnsureEnd();
            var quantised = command;
            ReplicationService.Quantise(ref quantised);

            Assert.Equal(read, quantised);
            var again = quantised;
            ReplicationService.Quantise(ref again);
            Assert.Equal(quantised, again);
        }

        // Numbers that are not finite, or far past any range, read as the host reads them too.
        foreach (
            var value in (float[])
                [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e30f, -1e30f, float.MaxValue]
        )
        {
            var command = new PlayerCommand
            {
                Move = new Vector2(value, -value),
                Yaw = value,
                Pitch = value,
            };
            writer.Reset();
            ReplicationService.WriteCommand(writer, in command);
            var reader = new MessageReader(writer.Written);
            var read = ReplicationService.ReadCommand(ref reader);
            var quantised = command;
            ReplicationService.Quantise(ref quantised);

            Assert.Equal(read, quantised);
            Assert.True(float.IsFinite(read.Move.X) && float.IsFinite(read.Yaw) && float.IsFinite(read.Pitch));
        }
    }

    [Fact]
    public void A_damaged_reply_with_commands_is_refused_or_read()
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            clientPlan: new MoverPlan { Characters = true, Script = tick => Walk(tick, ulong.MaxValue) }
        );
        harness.ClientTransport.Recorded = [];
        harness.Step(30);
        var sample = harness.ClientTransport.Recorded.Last(message => message[0] == 9 && message.Length > 40);
        IReplicationEndpoint endpoint = harness.HostReplication;
        var rows = HostileValues.Damaged("reply", sample[1..]).ToList();

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

    /// <summary>
    /// A reply whose shown tick is the most negative long (ten bytes no client writes) is refused as damaged, where it
    /// threw an overflow that ended the host's match; and one whose newest command tick lies past any tick is refused too.
    /// </summary>
    [Fact]
    public void A_reply_with_the_most_negative_shown_tick_or_a_command_past_any_tick_is_refused()
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            clientPlan: new MoverPlan { Characters = true, Script = tick => Walk(tick, ulong.MaxValue) }
        );
        harness.Step(30);
        IReplicationEndpoint endpoint = harness.HostReplication;
        var mostNegative = Reply(10, long.MinValue);
        var pastAnyTick = Reply(int.MaxValue - 1, 0);

        Assert.Throws<InvalidDataException>(() => endpoint.ReceiveReply(1, mostNegative));
        Assert.Throws<InvalidDataException>(() => endpoint.ReceiveReply(1, pastAnyTick));
        harness.Host.Step(10);
    }

    /// <summary>A reply's body: nothing acknowledged, then one command at <paramref name="newest"/> made at <paramref name="shown"/>.</summary>
    private static byte[] Reply(int newest, long shown)
    {
        var writer = new MessageWriter();
        writer.WriteVarInt(0);
        writer.WriteULong(0);
        writer.WriteVarInt(1);
        writer.WriteVarInt(newest);
        ReplicationService.WriteCommand(writer, new PlayerCommand());
        writer.WriteVarLong(shown);
        return writer.Written.ToArray();
    }

    /// <summary>
    /// A game's own movement state (the test's boost, kept in a component of the test's) registered as a predicted part
    /// is put back with the host's state when the client is corrected, so the client runs its later commands again as
    /// the host ran them: one correction each time the host pushes the player in the middle of a boost. Not registered,
    /// the client runs them again with the boost it has now, and is corrected again and again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_games_movement_state_as_a_predicted_part_is_put_back_by_a_correction(bool predicted)
    {
        var stopAt = ulong.MaxValue;
        using var harness = ReplicationHarness.Start(
            new MoverPlan
            {
                Count = 0,
                Characters = true,
                Boost = true,
                BoostPredicted = predicted,
            },
            clientPlan: new MoverPlan
            {
                Characters = true,
                Boost = true,
                BoostPredicted = predicted,
                Script = tick =>
                    tick >= stopAt
                        ? new PlayerCommand { Yaw = 0.5f }
                        : new PlayerCommand
                        {
                            Move = new Vector2(0, 1),
                            Yaw = 0.5f + tick % 240 / 120f,
                            Actions = tick % 60 == 0 ? 1u : 0,
                        },
            }
        );
        harness.Step(90);
        var before = harness.ClientReplication.Corrections;
        var copy = harness.HostPlan.Remote[1];
        var pushed = -1;
        var pushes = 0;
        for (var step = 0; step < 480; step++)
        {
            // Half way through each boost on the host, it pushes its copy aside: the client's state at that command
            // differs, inside the boost.
            var boost = harness.Host.World.Get<TestBoost>(copy);
            if (boost.Started != pushed && boost.Seconds is > 0 and < 0.25f)
            {
                harness.Host.World.Get<CharacterPosition>(copy).Feet.X += 0.5f;
                pushed = boost.Started;
                pushes++;
            }

            harness.Step(1);
        }

        stopAt = harness.Client.Tick;
        harness.Step(90);
        var corrections = harness.ClientReplication.Corrections - before;
        output.WriteLine(Text($"{pushes} pushes, {corrections} corrections, the part predicted: {predicted}."));
        Assert.True(pushes >= 6, $"Only {pushes} pushes.");
        if (predicted)
        {
            Assert.Equal(pushes, corrections);
            var client = Feet(harness.Client, harness.ClientPlan.Own);
            var host = Feet(harness.Host, copy);
            Assert.True(client.DistanceTo(host) < 0.01, $"The client's player at {client}, the host's copy at {host}.");
        }
        else
        {
            Assert.True(corrections > pushes, $"{corrections} corrections for {pushes} pushes.");
        }
    }

    /// <summary>
    /// The host knows the tick each client showed what it replicates at when it made the command its player moves by:
    /// about the interpolation delay, the way there and back, and the command buffer behind the host's step, within the
    /// limit; none for the host's own slot.
    /// </summary>
    [Theory]
    [InlineData(0, 7, 14)]
    [InlineData(100, 18, 28)]
    public void The_host_knows_the_tick_each_client_showed_when_it_made_its_command(
        int delayMilliseconds,
        double least,
        double most
    )
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 2, Characters = true },
            clientConditions: delayMilliseconds > 0 ? new NetworkConditions(delayMilliseconds, 0, 0) : default,
            clientPlan: new MoverPlan { Characters = true, Script = tick => Walk(tick, ulong.MaxValue) }
        );
        harness.Step(180);
        var behind = new List<double>();
        for (var step = 0; step < 60; step++)
        {
            harness.Step(1);
            behind.Add(harness.Host.Tick - 1 - harness.HostReplication.ShownTickOf(1));
        }

        output.WriteLine(Text($"Behind the host's step by {behind.Min():F2} to {behind.Max():F2} steps."));
        Assert.All(behind, steps => Assert.InRange(steps, least, most));
        Assert.Equal(-1, harness.HostReplication.ShownTickOf(0));
        Assert.Equal(-1, harness.ClientReplication.ShownTickOf(1));
    }

    [Fact]
    public void A_predicted_players_reply_and_check_allocate_nothing()
    {
        using var harness = ReplicationHarness.Start(
            new MoverPlan { Count = 0, Characters = true },
            clientPlan: new MoverPlan { Characters = true, Script = tick => Walk(tick, ulong.MaxValue) }
        );
        harness.Step(120);
        harness.ClientTransport.CountSendsOnly = true;
        var systems = harness.Client.Services.GetServices<IFixedUpdateSystem>().ToList();
        var reply = systems.OfType<ReplicationReplySystem>().Single();
        var receive = systems.OfType<ReplicationReceiveSystem>().Single();
        var start = harness.Client.Tick;

        var replyBytes = SystemAllocation.BytesAllocated(reply, i => new FixedStepTime(1f / 60, start + (ulong)i));
        var receiveBytes = SystemAllocation.BytesAllocated(receive, i => new FixedStepTime(1f / 60, start + (ulong)i));

        Assert.Equal(0, replyBytes);
        Assert.Equal(0, receiveBytes);
    }

    /// <summary>Where <paramref name="entity"/>'s feet are, absolute.</summary>
    private static WorldPoint Feet(Itera.Engine.Matches.HeadlessMatch match, Arch.Core.Entity entity) =>
        match.Services.GetRequiredService<WorldOrigin>().ToWorld(match.World.Get<CharacterPosition>(entity).Feet);

    /// <summary>Where the client's player is drawn at the end of the last step (its feet and its correction), absolute.</summary>
    private static WorldPoint Drawn(ReplicationHarness harness)
    {
        var origin = harness.Client.Services.GetRequiredService<WorldOrigin>();
        var position = harness.Client.World.Get<CharacterPosition>(harness.ClientPlan.Own);
        var eye = harness.Client.World.Get<EyeSmoothing>(harness.ClientPlan.Own);
        return origin.ToWorld(position.Feet + eye.Correction);
    }

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
