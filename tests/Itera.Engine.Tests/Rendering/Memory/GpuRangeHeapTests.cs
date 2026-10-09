using Itera.Engine.Rendering.Memory;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Rendering.Memory;

public sealed class GpuRangeHeapTests(ITestOutputHelper output)
{
    private const int Granule = GpuRangeHeap.GranuleWords;

    [Fact]
    public void Ranges_never_start_in_granule_0_and_never_overlap()
    {
        var heap = new GpuRangeHeap(1 << 20);
        var ranges = new List<(uint Offset, int Words)>();
        var random = new Random(3);
        while (true)
        {
            var words = random.Next(1, 5000);
            if (!heap.TryAllocate(words, out var offset))
            {
                break;
            }

            ranges.Add((offset, words));
        }

        Assert.True(ranges.Count > 100);
        Assert.DoesNotContain(ranges, r => r.Offset < Granule);
        ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        for (var i = 1; i < ranges.Count; i++)
        {
            Assert.True(ranges[i - 1].Offset + ranges[i - 1].Words <= ranges[i].Offset);
        }

        Assert.Equal(ranges.Sum(r => GpuRangeHeap.GranulesOf(r.Words)) * Granule * 4, heap.UsedBytes);
    }

    [Fact]
    public void A_full_heap_refuses_until_a_free_makes_room()
    {
        var heap = new GpuRangeHeap(64L * Granule);
        var offsets = new List<uint>();
        while (heap.TryAllocate(Granule, out var offset))
        {
            offsets.Add(offset);
        }

        // Granule 0 is never handed out.
        Assert.Equal(63, offsets.Count);
        Assert.False(heap.TryAllocate(1, out _));

        heap.Free(offsets[40], Granule);

        Assert.True(heap.TryAllocate(Granule, out var again));
        Assert.Equal(offsets[40], again);
        Assert.False(heap.TryAllocate(1, out _));
    }

    [Fact]
    public void A_range_goes_in_the_lowest_free_run_that_is_long_enough()
    {
        var heap = new GpuRangeHeap(64L * Granule);
        heap.TryAllocate(10 * Granule, out var a);
        heap.TryAllocate(5 * Granule, out var b);
        heap.TryAllocate(10 * Granule, out var c);
        heap.Free(a, 10 * Granule);
        heap.Free(c, 10 * Granule);

        // a's run (granules 1-10) comes before c's, and is long enough.
        Assert.True(heap.TryAllocate(8 * Granule, out var d));
        Assert.Equal(a, d);

        // The 2 granules left of a's run are too short for 3; c's run holds them.
        Assert.True(heap.TryAllocate(3 * Granule, out var e));
        Assert.Equal(c, e);

        Assert.True(heap.TryAllocate(2 * Granule, out var f));
        Assert.Equal(a + 8u * Granule, f);
        Assert.Equal(Granule * 1u, a);
        Assert.Equal(a + 10u * Granule, b);
    }

    [Fact]
    public void Largest_free_run_and_reset()
    {
        var heap = new GpuRangeHeap(64L * Granule);
        heap.TryAllocate(10 * Granule, out var a);
        heap.TryAllocate(20 * Granule, out _);
        heap.Free(a, 10 * Granule);

        Assert.Equal(20L * Granule * 4, heap.UsedBytes);
        // Granules 1-10 are free, and 31-63 after the second range.
        Assert.Equal(33L * Granule * 4, heap.LargestFreeBytes());

        heap.Reset();

        Assert.Equal(0, heap.UsedBytes);
        Assert.Equal(63L * Granule * 4, heap.LargestFreeBytes());
    }

    /// <summary>
    /// Columns of a few kilobytes to a megabyte (as a column's chunks are) loaded and unloaded at random for a long time,
    /// with the heap about two thirds full, as the game runs it (500 MB of 768). Without compaction, variable-size ranges
    /// can fragment; this measures how often a range finds no run long enough while there is room in total.
    /// </summary>
    [Fact]
    public void Long_churn_at_two_thirds_full_keeps_free_space_in_long_runs()
    {
        const long words = 16L << 20;
        var heap = new GpuRangeHeap(words);
        var random = new Random(5);
        var live = new List<(uint Offset, int Words)>();
        int failures = 0,
            allocations = 0;
        double lowestLargestShare = 1;
        for (var step = 0; step < 200_000; step++)
        {
            var load = (double)heap.UsedBytes / (words * 4);
            if (load < 0.67 || live.Count == 0)
            {
                // Column sizes: mostly 10-100 K words, some up to 250 K (a column of 1 MB).
                var size = random.Next(100) < 90 ? random.Next(2_500, 25_000) : random.Next(25_000, 65_000);
                allocations++;
                if (heap.TryAllocate(size, out var offset))
                {
                    live.Add((offset, size));
                }
                else
                {
                    failures++;
                }
            }
            else
            {
                var index = random.Next(live.Count);
                heap.Free(live[index].Offset, live[index].Words);
                live[index] = live[^1];
                live.RemoveAt(live.Count - 1);
            }

            if (step % 1000 == 0)
            {
                var free = words * 4 - heap.UsedBytes;
                var largest = heap.LargestFreeBytes();
                Assert.Equal(LargestGap(live, words), largest);
                lowestLargestShare = Math.Min(lowestLargestShare, (double)largest / free);
            }
        }

        output.WriteLine(
            $"{allocations} ranges, {failures} found no run; the largest free run was at least {lowestLargestShare:P0} of the free space"
        );
        Assert.Equal(0, failures);
        // First fit keeps at least 59 % here; searching on from the last range fell to 1 %.
        Assert.True(
            lowestLargestShare > 0.25,
            $"The longest free run fell to {lowestLargestShare:P0} of the free space."
        );
    }

    /// <summary>The longest gap between live ranges, in bytes of whole granules, granule 0 counting as used.</summary>
    private static long LargestGap(List<(uint Offset, int Words)> live, long words)
    {
        var granules = words / Granule;
        var sorted = live.Select(r =>
                (Start: (long)r.Offset / Granule, End: r.Offset / Granule + GpuRangeHeap.GranulesOf(r.Words))
            )
            .OrderBy(r => r.Start)
            .ToList();
        long longest = 0;
        long end = 1;
        foreach (var (start, rangeEnd) in sorted)
        {
            longest = Math.Max(longest, start - end);
            end = rangeEnd;
        }

        longest = Math.Max(longest, granules - end);
        return longest * Granule * 4;
    }
}
