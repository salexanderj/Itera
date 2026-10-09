namespace Itera.Engine.Rendering.Memory;

/// <summary>
/// Hands out ranges of one block of GPU memory: first fit over free ranges kept sorted by offset, merged with their
/// neighbours on free. It knows nothing about Vulkan, so it is tested without a device. The caller keeps each range's
/// offset and size and passes both back to <see cref="Free"/>. Not thread safe; <see cref="GpuMemoryAllocator"/> locks
/// around it.
/// </summary>
internal sealed class BlockSuballocator
{
    private readonly List<FreeRange> _free = [];

    /// <param name="size">Bytes in the block.</param>
    public BlockSuballocator(ulong size)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        Size = size;
        FreeBytes = size;
        _free.Add(new FreeRange(0, size));
    }

    /// <summary>Bytes in the block.</summary>
    public ulong Size { get; }

    /// <summary>Bytes not handed out, including gaps left by alignment.</summary>
    public ulong FreeBytes { get; private set; }

    /// <summary>True when nothing is handed out.</summary>
    public bool IsEmpty => FreeBytes == Size;

    /// <summary>Separate free ranges; 1 when the block is empty or has one hole.</summary>
    public int FreeRangeCount => _free.Count;

    /// <summary>
    /// Takes <paramref name="size"/> bytes at an offset that is a multiple of <paramref name="alignment"/> (a power of
    /// two). Returns false when no free range is large enough. Bytes skipped for alignment stay free.
    /// </summary>
    public bool TryAllocate(ulong size, ulong alignment, out ulong offset)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "Alignment must be a power of two.");
        }

        for (var i = 0; i < _free.Count; i++)
        {
            var range = _free[i];
            var start = AlignUp(range.Offset, alignment);
            if (start < range.Offset || start > range.End || range.End - start < size)
            {
                continue;
            }

            var end = start + size;
            var before = new FreeRange(range.Offset, start - range.Offset);
            var after = new FreeRange(end, range.End - end);
            if (before.Size > 0 && after.Size > 0)
            {
                _free[i] = before;
                _free.Insert(i + 1, after);
            }
            else if (before.Size > 0)
            {
                _free[i] = before;
            }
            else if (after.Size > 0)
            {
                _free[i] = after;
            }
            else
            {
                _free.RemoveAt(i);
            }

            FreeBytes -= size;
            offset = start;
            return true;
        }

        offset = 0;
        return false;
    }

    /// <summary>
    /// Returns a range from <see cref="TryAllocate"/>. Throws <see cref="InvalidOperationException"/> when it overlaps
    /// free space (a double free) or lies outside the block.
    /// </summary>
    public void Free(ulong offset, ulong size)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        var end = offset + size;
        if (end < offset || end > Size)
        {
            throw new InvalidOperationException($"Range {offset}+{size} lies outside the block of {Size} bytes.");
        }

        // The first free range that starts at or after the freed one.
        var index = LowerBound(offset);
        var hasPrevious = index > 0;
        var hasNext = index < _free.Count;
        if ((hasPrevious && _free[index - 1].End > offset) || (hasNext && _free[index].Offset < end))
        {
            throw new InvalidOperationException($"Range {offset}+{size} is already free, in part or in full.");
        }

        var joinsPrevious = hasPrevious && _free[index - 1].End == offset;
        var joinsNext = hasNext && _free[index].Offset == end;
        if (joinsPrevious && joinsNext)
        {
            var previous = _free[index - 1];
            _free[index - 1] = new FreeRange(previous.Offset, _free[index].End - previous.Offset);
            _free.RemoveAt(index);
        }
        else if (joinsPrevious)
        {
            var previous = _free[index - 1];
            _free[index - 1] = new FreeRange(previous.Offset, end - previous.Offset);
        }
        else if (joinsNext)
        {
            _free[index] = new FreeRange(offset, _free[index].End - offset);
        }
        else
        {
            _free.Insert(index, new FreeRange(offset, size));
        }

        FreeBytes += size;
    }

    /// <summary><paramref name="value"/> rounded up to a multiple of <paramref name="alignment"/> (a power of two).</summary>
    public static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    private int LowerBound(ulong offset)
    {
        int low = 0,
            high = _free.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (_free[middle].Offset < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private readonly record struct FreeRange(ulong Offset, ulong Size)
    {
        public ulong End => Offset + Size;
    }
}
