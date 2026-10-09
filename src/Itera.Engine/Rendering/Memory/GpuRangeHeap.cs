using System.Numerics;

namespace Itera.Engine.Rendering.Memory;

/// <summary>
/// Bookkeeping for a GPU buffer of words that the CPU never reads (the mesh heap): contiguous ranges in granules of
/// <see cref="GranuleWords"/> words, tracked by a bitmap, so each upload is one copy region however long the game runs
/// (fixed-size blocks from per-class free lists scattered over the raymarcher's heap and reached 1,000 regions per MB
/// uploaded after 10 minutes). A range goes
/// in the first free run long enough, lowest address first, which keeps free space in long runs: in a churn test at two
/// thirds full, the longest free run never fell below 59 % of the free space, against 1 % when the search went on from
/// where the last range ended (best fit kept 69 % but must scan the whole heap). Granule 0 is never handed out, so no
/// range starts at word 0. Main thread
/// only; allocates nothing after construction.
/// </summary>
internal sealed class GpuRangeHeap
{
    /// <summary>Words in a granule: 256 bytes.</summary>
    public const int GranuleWords = 64;

    private readonly ulong[] _used;
    private readonly long _granules;

    // No granule below this one is free: where the search for a free run starts.
    private long _firstFree;
    private long _usedGranules;

    /// <param name="words">Words in the heap; whole granules are used.</param>
    public GpuRangeHeap(long words)
    {
        _granules = words / GranuleWords;
        if (_granules < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(words), words, "The heap needs at least two granules.");
        }

        _used = new ulong[(_granules + 63) / 64];
        Reset();
    }

    /// <summary>Bytes in live ranges, rounded up to whole granules.</summary>
    public long UsedBytes => _usedGranules * GranuleWords * sizeof(uint);

    /// <summary>Bytes that may be handed out.</summary>
    public long CapacityBytes => (_granules - 1) * GranuleWords * sizeof(uint);

    /// <summary>
    /// Bytes in the longest free run: the largest range that can be handed out now. It falls below the free bytes as
    /// the heap fragments. Scans the bitmap a word at a time (about 50,000 words for 768 MB); for stats, not for every
    /// frame.
    /// </summary>
    public long LargestFreeBytes()
    {
        long longest = 0;
        long run = 0;
        foreach (var word in _used)
        {
            if (word == 0)
            {
                run += 64;
                continue;
            }

            if (word == ulong.MaxValue)
            {
                longest = Math.Max(longest, run);
                run = 0;
                continue;
            }

            // Walk the word's free and used stretches, low bits first.
            var rest = word;
            var left = 64;
            while (left > 0)
            {
                var free = Math.Min(BitOperations.TrailingZeroCount(rest), left);
                run += free;
                rest = free == 64 ? 0 : rest >> free;
                left -= free;
                if (left == 0)
                {
                    break;
                }

                longest = Math.Max(longest, run);
                run = 0;
                var used = BitOperations.TrailingZeroCount(~rest);
                rest = used == 64 ? 0 : rest >> used;
                left -= used;
            }
        }

        return Math.Max(longest, run) * GranuleWords * sizeof(uint);
    }

    /// <summary>Granules a range of <paramref name="words"/> words takes.</summary>
    public static long GranulesOf(int words) => (words + GranuleWords - 1) / GranuleWords;

    /// <summary>Takes a range of at least <paramref name="words"/> words. False when no free run is long enough.</summary>
    public bool TryAllocate(int words, out uint offset)
    {
        offset = 0;
        var count = GranulesOf(words);
        if (count <= 0)
        {
            return false;
        }

        var start = FindRun(count, _firstFree);
        if (start < 0)
        {
            return false;
        }

        Mark(start, count, used: true);
        _usedGranules += count;
        if (start == _firstFree)
        {
            _firstFree = start + count;
        }

        offset = (uint)(start * GranuleWords);
        return true;
    }

    /// <summary>Returns a range that <see cref="TryAllocate"/> handed out for <paramref name="words"/> words.</summary>
    public void Free(uint offset, int words)
    {
        var count = GranulesOf(words);
        var start = offset / GranuleWords;
        Mark(start, count, used: false);
        _usedGranules -= count;
        _firstFree = Math.Min(_firstFree, start);
    }

    /// <summary>Frees everything but granule 0.</summary>
    public void Reset()
    {
        Array.Clear(_used);
        // Granule 0 and the bits past the last granule are never free.
        _used[0] = 1;
        var tail = (int)(_granules & 63);
        if (tail != 0)
        {
            _used[^1] |= ulong.MaxValue << tail;
        }

        _usedGranules = 0;
        _firstFree = 1;
    }

    /// <summary>The first granule of a free run of <paramref name="count"/> granules from <paramref name="from"/> on, or -1.</summary>
    private long FindRun(long count, long from)
    {
        var g = from;
        while (g + count <= _granules)
        {
            var word = _used[g >> 6] >> (int)(g & 63);
            if ((word & 1) != 0)
            {
                // Skip the used granules here.
                g += BitOperations.TrailingZeroCount(~word);
                continue;
            }

            var runStart = g;
            var end = runStart + count;
            while (g < end)
            {
                word = _used[g >> 6] >> (int)(g & 63);
                if (word == 0)
                {
                    g += 64 - (g & 63);
                    continue;
                }

                g += BitOperations.TrailingZeroCount(word);
                break;
            }

            if (g >= end)
            {
                return runStart;
            }
        }

        return -1;
    }

    private void Mark(long start, long count, bool used)
    {
        var g = start;
        var end = start + count;
        while (g < end)
        {
            var bit = (int)(g & 63);
            var bits = (int)Math.Min(64 - bit, end - g);
            var mask = (bits == 64 ? ulong.MaxValue : (1UL << bits) - 1) << bit;
            if (used)
            {
                _used[g >> 6] |= mask;
            }
            else
            {
                _used[g >> 6] &= ~mask;
            }

            g += bits;
        }
    }
}
