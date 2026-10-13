using Itera.Engine.Ecs;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Replication;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="ReplicationReplySystem"/>: a client's replies tell the host which states it holds, so entities that stop
/// moving stop being sent; a step allocates nothing.
/// </summary>
public sealed class ReplicationReplySystemTests
{
    [Fact]
    public void Once_the_client_says_it_holds_their_states_entities_that_stand_still_are_not_sent_again()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 20, Speed = 0 });
        harness.Step(60);
        // The client has them all, so each was sent.
        Assert.Equal(20, harness.Shown().Count);
        harness.HostTransport.Recorded = [];

        harness.Step(20);

        // Only the packets' headers: the kind, a number and a tick of up to three bytes each, and no removals.
        var snapshots = harness.HostTransport.Recorded.Where(message => message[0] == 8).ToList();
        Assert.Equal(10, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.True(snapshot.Length <= 7, $"A snapshot of {snapshot.Length} bytes."));
    }

    [Fact]
    public void A_clients_reply_allocates_nothing()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 5 });
        harness.Step(30);
        harness.ClientTransport.CountSendsOnly = true;
        var system = harness
            .Client.Services.GetServices<IFixedUpdateSystem>()
            .OfType<ReplicationReplySystem>()
            .Single();

        var bytes = SystemAllocation.BytesAllocated(system, i => new FixedStepTime(1f / 60, (ulong)i));

        Assert.Equal(0, bytes);
    }
}
