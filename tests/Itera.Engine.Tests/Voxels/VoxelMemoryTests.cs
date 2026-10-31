using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed unsafe class VoxelMemoryTests
{
    private const long Megabyte = 1 << 20;

    [Fact]
    public void Blocks_are_distinct_and_writable()
    {
        using var memory = new VoxelMemory(64 * Megabyte);
        var handles = new int[1000];
        for (var i = 0; i < handles.Length; i++)
        {
            Assert.True(memory.TryAllocate(VoxelPool.Index1, false, out handles[i]));
            new Span<byte>(memory.Pointer(VoxelPool.Index1, handles[i]), 80).Fill((byte)i);
        }

        Assert.Equal(handles.Length, handles.Distinct().Count());
        for (var i = 0; i < handles.Length; i++)
        {
            Assert.True(new Span<byte>(memory.Pointer(VoxelPool.Index1, handles[i]), 80).IndexOfAnyExcept((byte)i) < 0);
        }
    }

    [Fact]
    public void Freed_blocks_are_reused_before_new_ones()
    {
        using var memory = new VoxelMemory(64 * Megabyte);
        memory.TryAllocate(VoxelPool.Nodes, false, out var a);
        memory.TryAllocate(VoxelPool.Nodes, false, out var b);

        memory.Free(VoxelPool.Nodes, a);
        memory.TryAllocate(VoxelPool.Nodes, false, out var c);

        Assert.Equal(a, c);
        Assert.NotEqual(b, c);
        Assert.Equal(2, memory.LiveBlocks(VoxelPool.Nodes));
    }

    [Fact]
    public void Bricks_have_a_mask_and_a_separate_record()
    {
        using var memory = new VoxelMemory(64 * Megabyte);
        memory.TryAllocate(VoxelPool.Bricks, false, out var brick);

        memory.BrickMask(brick)[7] = ulong.MaxValue;
        memory.Brick(brick)->Bits = 4;
        memory.Brick(brick)->Block = 12;

        Assert.Equal(ulong.MaxValue, memory.BrickMask(brick)[7]);
        Assert.Equal(4, memory.Brick(brick)->Bits);
        Assert.Equal(12, memory.Brick(brick)->Block);
        Assert.Equal(76, memory.UsedBytes);
    }

    [Fact]
    public void Pools_grow_past_one_slab()
    {
        using var memory = new VoxelMemory(64 * Megabyte);
        var count = 3 * (1 << 20) / 512;
        var last = -1;
        for (var i = 0; i < count; i++)
        {
            Assert.True(memory.TryAllocate(VoxelPool.Index8, false, out last));
        }

        memory.Pointer(VoxelPool.Index8, last)[511] = 42;

        Assert.Equal(3 * Megabyte, memory.ReservedBytes);
        Assert.Equal(count * 512L, memory.UsedBytes);
        Assert.Equal(42, memory.Pointer(VoxelPool.Index8, last)[511]);
    }

    /// <summary>
    /// Threads reading their blocks while the pool grows past 64 slabs (its slab arrays doubling from 16 to 128) always
    /// find what they wrote (the lock-free growth had no test with readers on other threads).
    /// </summary>
    [Fact]
    public void Readers_on_other_threads_keep_their_blocks_while_the_pool_grows_past_64_slabs()
    {
        using var memory = new VoxelMemory(16 * Megabyte);
        const int readers = 3;
        const int blocksEach = 64;
        var owned = new int[readers][];
        for (var r = 0; r < readers; r++)
        {
            owned[r] = new int[blocksEach];
            for (var i = 0; i < blocksEach; i++)
            {
                owned[r][i] = memory.Allocate(VoxelPool.Index8);
                *(int*)memory.Pointer(VoxelPool.Index8, owned[r][i]) = (r * 1000) + i;
            }
        }

        var stop = 0;
        var wrong = 0;
        var reads = new long[readers];
        var threads = new Thread[readers];
        for (var r = 0; r < readers; r++)
        {
            var reader = r;
            threads[r] = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    for (var i = 0; i < blocksEach; i++)
                    {
                        if (*(int*)memory.Pointer(VoxelPool.Index8, owned[reader][i]) != (reader * 1000) + i)
                        {
                            Interlocked.Increment(ref wrong);
                        }
                    }

                    reads[reader]++;
                }
            });
            threads[r].Start();
        }

        // Each reader reads its blocks once before the pool grows, so all of them read while it grows, however the
        // threads are scheduled (optimised, the growth below ended before a reader on a busy machine had started).
        Assert.True(SpinWait.SpinUntil(EveryReaderRead, TestTime.Limit(10)));

        // 512-byte blocks, 2,048 a 1 MB slab: 70 slabs' worth.
        var last = 0;
        for (var i = 0; i < 2048 * 70; i++)
        {
            last = memory.Allocate(VoxelPool.Index8);
        }

        Volatile.Write(ref stop, 1);
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TestTime.Limit(10)));
        }

        memory.Pointer(VoxelPool.Index8, last)[511] = 7;
        Assert.Equal(7, memory.Pointer(VoxelPool.Index8, last)[511]);
        Assert.Equal(0, wrong);
        Assert.True(memory.ReservedBytes >= 70L * Megabyte);

        bool EveryReaderRead()
        {
            for (var r = 0; r < readers; r++)
            {
                if (Volatile.Read(ref reads[r]) == 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>A block freed twice throws, in Debug builds (a double free handed one block to two owners).</summary>
    [Fact]
    public void A_block_freed_twice_throws_in_debug_builds()
    {
        using var memory = new VoxelMemory(16 * Megabyte);
        var handle = memory.Allocate(VoxelPool.Index8);
        memory.Free(VoxelPool.Index8, handle);
#if DEBUG
        Assert.Throws<InvalidOperationException>(() => memory.Free(VoxelPool.Index8, handle));
        Assert.Throws<InvalidOperationException>(() => memory.Free(VoxelPool.Index8, 1 << 30));
#endif
    }

    [Fact]
    public void The_budget_stops_new_slabs_unless_it_may_be_exceeded()
    {
        using var memory = new VoxelMemory(Megabyte);
        var perSlab = (int)(Megabyte / 512);
        for (var i = 0; i < perSlab; i++)
        {
            Assert.True(memory.TryAllocate(VoxelPool.Index8, false, out _));
        }

        Assert.False(memory.TryAllocate(VoxelPool.Index8, false, out _));
        Assert.True(memory.TryAllocate(VoxelPool.Index8, true, out _));
        Assert.Equal(2 * Megabyte, memory.ReservedBytes);
    }

    [Fact]
    public void Threads_allocating_at_once_never_share_a_block()
    {
        using var memory = new VoxelMemory(256 * Megabyte);
        var perThread = 20_000;
        var results = new int[4][];

        Parallel.For(
            0,
            4,
            t =>
            {
                var mine = new int[perThread];
                for (var i = 0; i < perThread; i++)
                {
                    memory.TryAllocate(VoxelPool.List8, false, out mine[i]);
                    if (i % 3 == 0)
                    {
                        memory.Free(VoxelPool.List8, mine[i]);
                        memory.TryAllocate(VoxelPool.List8, false, out mine[i]);
                    }
                }

                results[t] = mine;
            }
        );

        var all = results.SelectMany(r => r).ToArray();
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(all.Length, memory.LiveBlocks(VoxelPool.List8));
    }

    /// <summary>
    /// Each thread keeps free blocks in a cache of its own (2026-09-29): the counts leave out what the caches hold, so
    /// blocks allocated and freed on several threads count as none in use once every one is freed.
    /// </summary>
    [Fact]
    public void Counts_leave_out_what_the_threads_caches_hold()
    {
        using var memory = new VoxelMemory(256 * Megabyte);

        Parallel.For(
            0,
            4,
            t =>
            {
                var mine = new int[5_000];
                for (var i = 0; i < mine.Length; i++)
                {
                    memory.TryAllocate(VoxelPool.Index8, false, out mine[i]);
                }

                // Freed in another order, a few at a time, so caches fill, give back, and refill.
                for (var i = 0; i < mine.Length; i += 2)
                {
                    memory.Free(VoxelPool.Index8, mine[i]);
                }

                for (var i = 1; i < mine.Length; i += 2)
                {
                    memory.Free(VoxelPool.Index8, mine[i]);
                }
            }
        );

        Assert.Equal(0, memory.LiveBlocks(VoxelPool.Index8));
        Assert.Equal(0, memory.UsedBytes);
    }

    /// <summary>
    /// A thread that frees more than its cache holds gives the rest back to the pool, where another thread takes them
    /// before any new slab.
    /// </summary>
    [Fact]
    public void A_full_cache_gives_blocks_back_for_other_threads()
    {
        using var memory = new VoxelMemory(256 * Megabyte);
        var blocks = 4 * VoxelMemory.CacheBlocks;
        var taken = new int[blocks];
        for (var i = 0; i < blocks; i++)
        {
            memory.TryAllocate(VoxelPool.Nodes, false, out taken[i]);
        }

        foreach (var block in taken)
        {
            memory.Free(VoxelPool.Nodes, block);
        }

        var reserved = memory.ReservedBytes;
        var again = new HashSet<int>();
        var other = new Thread(() =>
        {
            for (var i = 0; i < blocks - VoxelMemory.CacheBlocks; i++)
            {
                memory.TryAllocate(VoxelPool.Nodes, false, out var block);
                again.Add(block);
            }
        });
        other.Start();
        other.Join();

        Assert.Equal(reserved, memory.ReservedBytes);
        Assert.Subset(taken.ToHashSet(), again);
        Assert.Equal(blocks - VoxelMemory.CacheBlocks, memory.LiveBlocks(VoxelPool.Nodes));
    }

    [Fact]
    public void Dispose_can_run_twice_and_blocks_cannot_be_taken_afterwards()
    {
        var memory = new VoxelMemory(Megabyte);
        memory.TryAllocate(VoxelPool.Nodes, false, out _);

        memory.Dispose();
        memory.Dispose();

        Assert.Equal(0, memory.ReservedBytes);
        Assert.Throws<ObjectDisposedException>(() => memory.TryAllocate(VoxelPool.Nodes, false, out _));
        Assert.Throws<ObjectDisposedException>(() => memory.Free(VoxelPool.Nodes, 0));
    }
}
