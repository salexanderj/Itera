using Arch.Core;
using Arch.System;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Ecs;

/// <summary>
/// <see cref="EcsWorld"/> runs the fixed systems as <see cref="Group{T}"/> did: every system's before-update, then every
/// update in order, then every after-update; it times each update by name, and running a step allocates nothing.
/// </summary>
public sealed class EcsWorldTests
{
    [Fact]
    public void A_fixed_step_runs_each_phase_in_order_and_times_each_system()
    {
        var calls = new List<string>();
        var times = new SystemTimes();
        using var ecs = new EcsWorld(NullLogger<EcsWorld>.Instance, times);
        ecs.Initialize([new Recorder(ecs.World, "a", calls, 0), new Recorder(ecs.World, "b", calls, 3)], []);

        ecs.FixedUpdate(new FixedStepTime(1f / 60, 1));

        Assert.Equal(["a before", "b before", "a update", "b update", "a after", "b after"], calls);
        Assert.Equal(2, times.Count);
        // The system that sleeps 3 ms is the slowest (both are named by their type).
        var slowest = times.TakeSlowest(1, 1);
        Assert.StartsWith("Recorder ", slowest, StringComparison.Ordinal);
        Assert.InRange(
            double.Parse(slowest.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture),
            2.9,
            1000
        );
    }

    [Fact]
    public void A_fixed_step_allocates_nothing()
    {
        using var ecs = new EcsWorld(NullLogger<EcsWorld>.Instance, new SystemTimes());
        ecs.Initialize([new Recorder(ecs.World, "a", null, 0), new Recorder(ecs.World, "b", null, 0)], []);
        ecs.FixedUpdate(new FixedStepTime(1f / 60, 1));

        var before = SystemAllocation.StartMeasurement();
        for (var tick = 2UL; tick < 100; tick++)
        {
            ecs.FixedUpdate(new FixedStepTime(1f / 60, tick));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class Recorder(World world, string name, List<string>? calls, int sleepMs)
        : BaseSystem<World, FixedStepTime>(world),
            IFixedUpdateSystem
    {
        public override void BeforeUpdate(in FixedStepTime t) => calls?.Add(name + " before");

        public override void Update(in FixedStepTime t)
        {
            calls?.Add(name + " update");
            if (sleepMs > 0)
            {
                Thread.Sleep(sleepMs);
            }
        }

        public override void AfterUpdate(in FixedStepTime t) => calls?.Add(name + " after");
    }
}
