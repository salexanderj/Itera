using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Replication;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="ReplicationSendSystem"/>: a host sends each client its snapshots every send's steps; offline the
/// replicated entities are kept and nothing is sent; a step allocates nothing once its packets have grown.
/// </summary>
public sealed class ReplicationSendSystemTests
{
    [Fact]
    public void A_host_sends_each_client_a_snapshot_every_sends_steps_and_offline_nothing_goes()
    {
        using (var harness = ReplicationHarness.Start(new MoverPlan { Count = 2 }))
        {
            harness.HostTransport.Recorded = [];
            harness.Step(60);
            var snapshots = harness.HostTransport.Recorded.Count(message => message[0] == 8);
            // 30 sends a second: one every second step.
            Assert.Equal(60 / harness.HostReplication.SendIntervalTicks, snapshots);
        }

        var plan = new MoverPlan { Count = 2 };
        var transport = new FakeTransport(new FakeNetwork());
        using var provider = ReplicationHarness.Build(null, plan, transport);
        using var offline = new HeadlessRun(provider).Play(seed: 3);
        offline.Step(30);

        Assert.Equal(2, offline.Match.Replication.Replicating);
        Assert.Equal(0, transport.Sends);
    }

    [Fact]
    public void A_hosts_send_allocates_nothing()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 50 });
        harness.Step(120);
        harness.HostTransport.CountSendsOnly = true;
        var system = harness.Host.Services.GetServices<IFixedUpdateSystem>().OfType<ReplicationSendSystem>().Single();
        var start = harness.Host.Tick + 1000;

        var bytes = SystemAllocation.BytesAllocated(system, i => new FixedStepTime(1f / 60, start + (ulong)i));

        Assert.Equal(0, bytes);
    }
}
