using Itera.Engine.Diagnostics;

namespace Itera.Engine.Tests.Diagnostics;

/// <summary>
/// <see cref="SystemTimes"/>: the slowest systems first, only as many as asked for, every time cleared after, and
/// recording allocates nothing.
/// </summary>
public sealed class SystemTimesTests
{
    [Fact]
    public void The_slowest_come_first_and_the_times_start_again_after()
    {
        var times = new SystemTimes();
        var ticks = System.Diagnostics.Stopwatch.Frequency / 1000;
        foreach (var (name, ms) in (ReadOnlySpan<(string, int)>)[("A", 1), ("B", 7), ("C", 3), ("D", 5), ("E", 2)])
        {
            times.Record(times.Add(name), ms * ticks);
        }

        Assert.Equal("B 7.000 max 7.00, D 5.000 max 5.00, C 3.000 max 3.00", times.TakeSlowest(3, 1));
        Assert.Equal("A 0.000 max 0.00, B 0.000 max 0.00", times.TakeSlowest(2, 1));
        Assert.Equal("", times.TakeSlowest(2, 0));
    }

    [Fact]
    public void Recording_allocates_nothing()
    {
        var times = new SystemTimes();
        var index = times.Add("A");
        times.Record(index, 1);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            times.Record(index, i);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
