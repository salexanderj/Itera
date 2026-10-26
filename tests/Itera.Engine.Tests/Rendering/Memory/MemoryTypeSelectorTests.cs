using Itera.Engine.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering.Memory;

public sealed class MemoryTypeSelectorTests
{
    private const MemoryPropertyFlags Local = MemoryPropertyFlags.DeviceLocalBit;
    private const MemoryPropertyFlags Visible = MemoryPropertyFlags.HostVisibleBit;
    private const MemoryPropertyFlags Coherent = MemoryPropertyFlags.HostCoherentBit;
    private const MemoryPropertyFlags Cached = MemoryPropertyFlags.HostCachedBit;
    private const uint AllTypes = uint.MaxValue;

    /// <summary>A discrete AMD card on RADV with resizable BAR: all VRAM is also host-visible.</summary>
    private static readonly MemoryPropertyFlags[] ResizableBar =
    [
        Local,
        Visible | Coherent,
        Local | Visible | Coherent,
        Visible | Coherent | Cached,
        Local | MemoryPropertyFlags.DeviceCoherentBitAmd | MemoryPropertyFlags.DeviceUncachedBitAmd,
    ];

    /// <summary>A discrete card with only a 256 MB window of VRAM visible to the CPU.</summary>
    private static readonly MemoryPropertyFlags[] SmallBar =
    [
        Local,
        Visible | Coherent,
        Visible | Coherent | Cached,
        Local | Visible | Coherent,
    ];

    /// <summary>Unified memory, as on Apple GPUs through MoltenVK: one heap, every type device-local.</summary>
    private static readonly MemoryPropertyFlags[] Unified =
    [
        Local,
        Local | Visible | Coherent,
        Local | Visible | Cached,
    ];

    /// <summary>A device whose only cached type is not coherent.</summary>
    private static readonly MemoryPropertyFlags[] CachedNotCoherent = [Local, Visible | Coherent, Visible | Cached];

    [Fact]
    public void Device_local_prefers_vram_the_cpu_cannot_see()
    {
        Assert.Equal(0, Best(ResizableBar, MemoryUsage.DeviceLocal));
        Assert.Equal(0, Best(SmallBar, MemoryUsage.DeviceLocal));
        Assert.Equal(0, Best(Unified, MemoryUsage.DeviceLocal));
    }

    [Fact]
    public void Upload_prefers_coherent_write_combined_system_memory()
    {
        Assert.Equal(1, Best(ResizableBar, MemoryUsage.Upload));
        Assert.Equal(1, Best(SmallBar, MemoryUsage.Upload));
        // Unified memory has no system-memory type; the coherent one wins over the cached, non-coherent one.
        Assert.Equal(1, Best(Unified, MemoryUsage.Upload));
    }

    [Fact]
    public void Stream_prefers_coherent_vram_the_cpu_can_map_then_system_memory()
    {
        Assert.Equal(2, Best(ResizableBar, MemoryUsage.Stream));
        Assert.Equal(3, Best(SmallBar, MemoryUsage.Stream));
        Assert.Equal(1, Best(Unified, MemoryUsage.Stream));
        // No VRAM the CPU can map: coherent system memory.
        Assert.Equal(1, Best(CachedNotCoherent, MemoryUsage.Stream));

        // A full window falls back to system memory next.
        Span<int> order = stackalloc int[SmallBar.Length];
        var count = MemoryTypeSelector.Rank(SmallBar, AllTypes, MemoryUsage.Stream, order);
        Assert.Equal([3, 1, 2], order[..count].ToArray());
    }

    [Fact]
    public void Readback_prefers_cached_memory_even_when_it_is_not_coherent()
    {
        Assert.Equal(3, Best(ResizableBar, MemoryUsage.Readback));
        Assert.Equal(2, Best(SmallBar, MemoryUsage.Readback));
        Assert.Equal(2, Best(Unified, MemoryUsage.Readback));
        Assert.Equal(2, Best(CachedNotCoherent, MemoryUsage.Readback));
    }

    [Fact]
    public void Types_that_need_a_device_feature_are_never_chosen()
    {
        Span<int> order = stackalloc int[ResizableBar.Length];
        var count = MemoryTypeSelector.Rank(ResizableBar, AllTypes, MemoryUsage.DeviceLocal, order);

        Assert.Equal(2, count);
        Assert.Equal([0, 2], order[..count].ToArray());
    }

    [Fact]
    public void Only_types_allowed_by_the_resource_are_ranked()
    {
        Span<int> order = stackalloc int[SmallBar.Length];

        // A resource that may live only in types 1 and 3 cannot be device-local unless it takes the BAR type.
        var count = MemoryTypeSelector.Rank(SmallBar, 0b1010, MemoryUsage.DeviceLocal, order);
        Assert.Equal(1, count);
        Assert.Equal(3, order[0]);

        // None of the allowed types is host-visible.
        count = MemoryTypeSelector.Rank(SmallBar, 0b0001, MemoryUsage.Upload, order);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Every_acceptable_type_is_ranked_so_a_failed_allocation_can_try_the_next()
    {
        Span<int> order = stackalloc int[SmallBar.Length];
        var count = MemoryTypeSelector.Rank(SmallBar, AllTypes, MemoryUsage.Upload, order);

        // Coherent write-combined system memory, then coherent cached system memory, then the small BAR last.
        Assert.Equal([1, 2, 3], order[..count].ToArray());
    }

    private static int Best(MemoryPropertyFlags[] types, MemoryUsage usage)
    {
        Span<int> order = stackalloc int[types.Length];
        var count = MemoryTypeSelector.Rank(types, AllTypes, usage, order);
        Assert.True(count > 0);
        return order[0];
    }
}
