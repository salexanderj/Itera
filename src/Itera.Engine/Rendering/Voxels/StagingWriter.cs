using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Voxels;

/// <summary>
/// Writes one frame's upload data into a staging region, front to back, and records where each piece goes as copy
/// regions. A write whose destination and source both follow the previous region extends it, so blocks allocated
/// one after another become one copy. Callers check <see cref="HasRoom"/> before writing. Main thread; tests point it
/// at native memory.
/// </summary>
internal sealed unsafe class StagingWriter
{
    private byte* _staging;
    private ulong _bufferOffset;
    private ulong _capacity;
    private ulong _reservedBytes;
    private int _reservedRegions;
    private BufferCopy[] _regions = [];

    /// <summary>Bytes written so far this frame.</summary>
    public ulong Used { get; private set; }

    /// <summary>Copy regions recorded so far this frame.</summary>
    public int RegionCount { get; private set; }

    /// <summary>The regions recorded so far.</summary>
    public ReadOnlySpan<BufferCopy> Regions => _regions.AsSpan(0, RegionCount);

    /// <summary>Starts a frame's writing.</summary>
    /// <param name="staging">Mapped start of the frame slot's staging region.</param>
    /// <param name="bufferOffset">Offset of that region in the staging buffer, for the copy regions.</param>
    /// <param name="capacity">Bytes in the region.</param>
    /// <param name="regions">Array the copy regions go into; its length caps them.</param>
    public void Begin(byte* staging, ulong bufferOffset, ulong capacity, BufferCopy[] regions)
    {
        _staging = staging;
        _bufferOffset = bufferOffset;
        _capacity = capacity;
        _regions = regions;
        _reservedBytes = 0;
        _reservedRegions = 0;
        Used = 0;
        RegionCount = 0;
    }

    /// <summary>Holds back room for writes that must happen later in the frame (the window slots).</summary>
    public void Reserve(ulong bytes, int regions)
    {
        _reservedBytes = bytes;
        _reservedRegions = regions;
    }

    /// <summary>Gives the reserved room back, just before the writes it was held for.</summary>
    public void ReleaseReserve() => Reserve(0, 0);

    /// <summary>True when <paramref name="bytes"/> more bytes in at most <paramref name="regions"/> regions fit.</summary>
    public bool HasRoom(ulong bytes, int regions) =>
        Used + _reservedBytes + bytes <= _capacity && RegionCount + _reservedRegions + regions <= _regions.Length;

    /// <summary>
    /// Returns where to put <paramref name="words"/> words that the GPU copies to byte <paramref name="destination"/>
    /// of the voxel buffer. Write them all, in order; the memory may be uncached, so never read it.
    /// </summary>
    public uint* Write(ulong destination, int words)
    {
        var bytes = (ulong)words * sizeof(uint);
        if (Used + bytes > _capacity || (RegionCount == _regions.Length && !Extends(destination)))
        {
            throw new InvalidOperationException("The staging region is full; check HasRoom before writing.");
        }

        var source = _bufferOffset + Used;
        if (Extends(destination))
        {
            _regions[RegionCount - 1].Size += bytes;
        }
        else
        {
            _regions[RegionCount++] = new BufferCopy(source, destination, bytes);
        }

        var pointer = (uint*)(_staging + Used);
        Used += bytes;
        return pointer;
    }

    /// <summary>
    /// Returns where to put <paramref name="bytes"/> bytes (rounded up to whole words) that the caller copies on its own
    /// (to an image), and their offset in the staging buffer. No copy region is recorded. Check <see cref="HasRoom"/>
    /// first; write them all, in order, and never read them.
    /// </summary>
    public byte* Stage(ulong bytes, out ulong sourceOffset)
    {
        bytes = (bytes + sizeof(uint) - 1) / sizeof(uint) * sizeof(uint);
        if (Used + bytes > _capacity)
        {
            throw new InvalidOperationException("The staging region is full; check HasRoom before writing.");
        }

        sourceOffset = _bufferOffset + Used;
        var pointer = _staging + Used;
        Used += bytes;
        return pointer;
    }

    private bool Extends(ulong destination)
    {
        if (RegionCount == 0)
        {
            return false;
        }

        ref readonly var last = ref _regions[RegionCount - 1];
        return last.SrcOffset + last.Size == _bufferOffset + Used && last.DstOffset + last.Size == destination;
    }
}
