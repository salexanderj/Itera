using Itera.Engine.Rendering;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// <see cref="GpuTimer"/>'s parse of a slot's query results: a pass whose begin or end was not written
/// gives no time, and a timestamp counter that wrapped between the two still gives the time between.
/// </summary>
public sealed class GpuTimerParseTests
{
    [Fact]
    public void Unwritten_queries_give_no_time_and_a_wrapped_counter_the_time_between()
    {
        const ulong mask = (1UL << 36) - 1;
        // Pass 0: 1,000 ticks; pass 1: its end not written; pass 2: the counter wrapped past 36 bits.
        ulong[] results = [5_000, 1, 6_000, 1, 7_000, 1, 0, 0, mask - 9, 1, 5, 1];
        var nanoseconds = new long[3];

        GpuTimer.Parse(results, mask, 2.5, nanoseconds);

        Assert.Equal([2_500L, -1L, 37L], nanoseconds);
    }
}
