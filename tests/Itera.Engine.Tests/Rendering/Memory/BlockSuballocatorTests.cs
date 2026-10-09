using Itera.Engine.Rendering.Memory;

namespace Itera.Engine.Tests.Rendering.Memory;

public sealed class BlockSuballocatorTests
{
    [Fact]
    public void Ranges_are_aligned_and_do_not_overlap()
    {
        var block = new BlockSuballocator(4096);

        Assert.True(block.TryAllocate(100, 1, out var a));
        Assert.True(block.TryAllocate(100, 256, out var b));
        Assert.True(block.TryAllocate(8, 64, out var c));

        Assert.Equal(0ul, a);
        Assert.Equal(256ul, b);
        // The gap between a and b is free and aligned 64 at 128.
        Assert.Equal(128ul, c);
        Assert.Equal(4096ul - 208, block.FreeBytes);
    }

    [Fact]
    public void Allocation_fails_when_no_range_is_large_enough()
    {
        var block = new BlockSuballocator(1024);
        Assert.True(block.TryAllocate(512, 1, out _));
        Assert.True(block.TryAllocate(256, 1, out var middle));
        Assert.True(block.TryAllocate(256, 1, out _));
        block.Free(middle, 256);

        Assert.False(block.TryAllocate(257, 1, out _));
        Assert.True(block.TryAllocate(256, 1, out var again));
        Assert.Equal(middle, again);
    }

    [Fact]
    public void Freed_neighbours_merge_back_into_one_range()
    {
        var block = new BlockSuballocator(1000);
        var offsets = new ulong[10];
        for (var i = 0; i < offsets.Length; i++)
        {
            Assert.True(block.TryAllocate(100, 1, out offsets[i]));
        }

        // Free every other range, then the rest, so each free joins a range on one side, the other, or both.
        for (var i = 0; i < offsets.Length; i += 2)
        {
            block.Free(offsets[i], 100);
        }

        Assert.Equal(5, block.FreeRangeCount);
        for (var i = 1; i < offsets.Length; i += 2)
        {
            block.Free(offsets[i], 100);
        }

        Assert.True(block.IsEmpty);
        Assert.Equal(1, block.FreeRangeCount);
        Assert.True(block.TryAllocate(1000, 1, out var whole));
        Assert.Equal(0ul, whole);
    }

    [Fact]
    public void A_double_free_throws()
    {
        var block = new BlockSuballocator(1024);
        Assert.True(block.TryAllocate(128, 1, out var a));
        Assert.True(block.TryAllocate(128, 1, out _));
        block.Free(a, 128);

        Assert.Throws<InvalidOperationException>(() => block.Free(a, 128));
        Assert.Throws<InvalidOperationException>(() => block.Free(64, 128));
        Assert.Throws<InvalidOperationException>(() => block.Free(1000, 128));
    }

    [Fact]
    public void Alignment_must_be_a_power_of_two()
    {
        var block = new BlockSuballocator(1024);

        Assert.Throws<ArgumentOutOfRangeException>(() => block.TryAllocate(16, 3, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => block.TryAllocate(16, 0, out _));
    }

    [Fact]
    public void Random_allocations_and_frees_keep_the_books_straight()
    {
        const ulong size = 1 << 20;
        var block = new BlockSuballocator(size);
        var live = new List<(ulong Offset, ulong Size)>();
        var random = new Random(1234);

        for (var step = 0; step < 5000; step++)
        {
            if (live.Count > 0 && random.Next(3) == 0)
            {
                var index = random.Next(live.Count);
                block.Free(live[index].Offset, live[index].Size);
                live.RemoveAt(index);
            }
            else
            {
                var bytes = (ulong)random.Next(1, 20_000);
                var alignment = 1ul << random.Next(0, 9);
                if (block.TryAllocate(bytes, alignment, out var offset))
                {
                    Assert.Equal(0ul, offset % alignment);
                    Assert.True(offset + bytes <= size);
                    Assert.DoesNotContain(live, r => offset < r.Offset + r.Size && r.Offset < offset + bytes);
                    live.Add((offset, bytes));
                }
            }

            var used = live.Aggregate(0ul, (sum, r) => sum + r.Size);
            Assert.Equal(size - used, block.FreeBytes);
        }

        foreach (var (offset, bytes) in live)
        {
            block.Free(offset, bytes);
        }

        Assert.True(block.IsEmpty);
        Assert.Equal(1, block.FreeRangeCount);
    }
}
