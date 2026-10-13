using Itera.Engine.Ecs;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Replication;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="ReplicationReceiveSystem"/>: a client makes what the host replicates and sets it every step, while the
/// host's own receive does nothing; a step allocates nothing.
/// </summary>
public sealed class ReplicationReceiveSystemTests
{
    [Fact]
    public void A_client_makes_and_sets_what_the_host_replicates_every_step_and_the_hosts_receive_does_nothing()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 4 });
        harness.Step(30);
        var before = harness.Shown().Values.Sum(shown => shown.Applied);

        harness.Step(10);

        var shown = harness.Shown();
        Assert.Equal(4, shown.Count);
        // Each of the four is set at each of the ten steps.
        Assert.Equal(before + 40, shown.Values.Sum(state => state.Applied));
        Assert.Equal(0, harness.HostPlan.Spawns);
    }

    [Fact]
    public void A_clients_receive_allocates_nothing()
    {
        using var harness = ReplicationHarness.Start(new MoverPlan { Count = 50 });
        harness.Step(120);
        var system = harness
            .Client.Services.GetServices<IFixedUpdateSystem>()
            .OfType<ReplicationReceiveSystem>()
            .Single();
        var start = harness.Client.Tick;

        var bytes = SystemAllocation.BytesAllocated(system, i => new FixedStepTime(1f / 60, start + (ulong)(i % 4)));

        Assert.Equal(0, bytes);
    }
}
