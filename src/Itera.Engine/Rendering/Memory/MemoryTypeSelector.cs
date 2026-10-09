using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Memory;

/// <summary>What GPU memory is used for; it decides which memory types are acceptable and which are preferred.</summary>
public enum MemoryUsage
{
    /// <summary>Only the GPU reads and writes it: voxel data, render targets. Must be device-local.</summary>
    DeviceLocal = 0,

    /// <summary>
    /// The CPU writes it once per use and the GPU reads it: staging for copies. Must be host-visible. Prefers
    /// host-coherent, uncached (write-combined) system memory, so it does not take VRAM.
    /// </summary>
    Upload = 1,

    /// <summary>The GPU writes it and the CPU reads it. Must be host-visible. Prefers host-cached memory.</summary>
    Readback = 2,
}

/// <summary>
/// Ranks memory types for a <see cref="MemoryUsage"/>. It works on the property flags of each type only, so it can be
/// tested with made-up devices: resizable BAR (all VRAM host-visible), a 256 MB BAR, unified memory, or no coherent
/// type.
/// </summary>
internal static class MemoryTypeSelector
{
    /// <summary>
    /// Flags that need a device feature or a special allocation Itera does not make. A type with any of them is never
    /// chosen: allocating AMD device-coherent memory without the <c>deviceCoherentMemory</c> feature is invalid.
    /// </summary>
    private const MemoryPropertyFlags Unusable =
        MemoryPropertyFlags.ProtectedBit
        | MemoryPropertyFlags.LazilyAllocatedBit
        | MemoryPropertyFlags.DeviceCoherentBitAmd
        | MemoryPropertyFlags.DeviceUncachedBitAmd
        | MemoryPropertyFlags.RdmaCapableBitNV;

    /// <summary>
    /// Writes the acceptable memory types for <paramref name="usage"/> into <paramref name="order"/>, best first, and
    /// returns how many there are. A type is acceptable when its bit is set in <paramref name="typeBits"/> (from
    /// <c>VkMemoryRequirements</c>), it has the usage's required flags, and none of the unusable ones. Types with equal
    /// scores keep the device's order, which the spec arranges from most to least suitable.
    /// </summary>
    /// <param name="typeFlags">Property flags of each memory type, indexed by memory type index.</param>
    /// <param name="typeBits">Bit i set when memory type i may hold the resource.</param>
    /// <param name="usage">What the memory is for.</param>
    /// <param name="order">Receives type indices; needs room for <paramref name="typeFlags"/>.Length entries.</param>
    public static int Rank(
        ReadOnlySpan<MemoryPropertyFlags> typeFlags,
        uint typeBits,
        MemoryUsage usage,
        Span<int> order
    )
    {
        var required = Required(usage);
        Span<int> scores = stackalloc int[typeFlags.Length];
        var count = 0;
        for (var i = 0; i < typeFlags.Length; i++)
        {
            var flags = typeFlags[i];
            if ((typeBits & (1u << i)) == 0 || (flags & required) != required || (flags & Unusable) != 0)
            {
                continue;
            }

            // Insertion sort by score, highest first; a later type goes after earlier ones with the same score.
            var score = Score(flags, usage);
            var at = count;
            while (at > 0 && scores[at - 1] < score)
            {
                scores[at] = scores[at - 1];
                order[at] = order[at - 1];
                at--;
            }

            scores[at] = score;
            order[at] = i;
            count++;
        }

        return count;
    }

    /// <summary>Flags every type for <paramref name="usage"/> must have.</summary>
    public static MemoryPropertyFlags Required(MemoryUsage usage) =>
        usage switch
        {
            MemoryUsage.DeviceLocal => MemoryPropertyFlags.DeviceLocalBit,
            MemoryUsage.Upload => MemoryPropertyFlags.HostVisibleBit,
            MemoryUsage.Readback => MemoryPropertyFlags.HostVisibleBit,
            _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "Unknown memory usage."),
        };

    private static int Score(MemoryPropertyFlags flags, MemoryUsage usage)
    {
        var deviceLocal = (flags & MemoryPropertyFlags.DeviceLocalBit) != 0;
        var hostVisible = (flags & MemoryPropertyFlags.HostVisibleBit) != 0;
        var coherent = (flags & MemoryPropertyFlags.HostCoherentBit) != 0;
        var cached = (flags & MemoryPropertyFlags.HostCachedBit) != 0;
        return usage switch
        {
            // VRAM the CPU cannot see first, so host-visible VRAM (a small BAR) stays free for other uses.
            MemoryUsage.DeviceLocal => hostVisible ? 0 : 1,
            // Coherent matters most (no flushes); then system memory over VRAM, then write-combined over cached.
            MemoryUsage.Upload => (coherent ? 4 : 0) + (deviceLocal ? 0 : 2) + (cached ? 0 : 1),
            // Cached matters most (reads of uncached memory are very slow); then coherent.
            MemoryUsage.Readback => (cached ? 4 : 0) + (coherent ? 2 : 0),
            _ => 0,
        };
    }
}
