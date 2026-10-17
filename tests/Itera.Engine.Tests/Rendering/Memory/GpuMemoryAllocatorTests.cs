using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering.Memory;

/// <summary>
/// <see cref="GpuMemoryAllocator"/>'s decisions without a device: the range a flush or invalidate covers,
/// rounded to whole atoms and running to the end of a dedicated allocation that rounds past it; where a resource goes
/// (dedicated or a block, whole atoms for non-coherent memory); the block size for a heap; a small resource taking
/// memory of its own where its type has no room for a new block; and the fall back to the next acceptable memory type
/// when one is full, with no block kept for the full one. This machine's host-visible memory is
/// all coherent and its heaps never fill, so none of these paths run in the game here.
/// </summary>
public sealed unsafe class GpuMemoryAllocatorTests
{
    [Theory]
    // Atom 1 (coherent memory never flushes, but the arithmetic holds): the range as asked.
    [InlineData(128UL, 1000UL, false, 1UL, 10UL, 20UL, 138UL, 20UL)]
    // Atom 64, in a block: rounded out to atoms, at the allocation's start, middle, and end, never past it.
    [InlineData(128UL, 256UL, false, 64UL, 0UL, 1UL, 128UL, 64UL)]
    [InlineData(128UL, 256UL, false, 64UL, 10UL, 100UL, 128UL, 128UL)]
    [InlineData(128UL, 256UL, false, 64UL, 250UL, 6UL, 320UL, 64UL)]
    // Atom 256.
    [InlineData(512UL, 512UL, false, 256UL, 0UL, 1UL, 512UL, 256UL)]
    [InlineData(512UL, 512UL, false, 256UL, 300UL, 212UL, 768UL, 256UL)]
    // Dedicated, 1000 bytes: a range that rounds past the end runs to the end instead.
    [InlineData(0UL, 1000UL, true, 64UL, 0UL, 64UL, 0UL, 64UL)]
    [InlineData(0UL, 1000UL, true, 64UL, 990UL, 10UL, 960UL, Vk.WholeSize)]
    public void A_range_is_rounded_to_whole_atoms_and_a_dedicated_ones_end_runs_to_the_end(
        ulong allocationOffset,
        ulong allocationSize,
        bool dedicated,
        ulong atom,
        ulong offset,
        ulong size,
        ulong expectedOffset,
        ulong expectedSize
    )
    {
        var (rangeOffset, rangeSize) = GpuMemoryAllocator.RangeFor(
            allocationOffset,
            allocationSize,
            dedicated,
            atom,
            offset,
            size
        );

        Assert.Equal(expectedOffset, rangeOffset);
        Assert.Equal(expectedSize, rangeSize);
        if (!dedicated)
        {
            Assert.True(
                rangeOffset >= allocationOffset && rangeOffset + rangeSize <= allocationOffset + allocationSize
            );
        }
    }

    [Fact]
    public void A_resource_goes_to_a_block_unless_it_is_large_or_the_driver_asks()
    {
        const ulong block = 64UL << 20;
        var small = new MemoryRequirements { Size = 1000, Alignment = 16 };

        Assert.Equal(new(false, 1000, 16), GpuMemoryAllocator.PlacementFor(small, true, 64, block, false));
        // Non-coherent memory: whole atoms, in size and alignment.
        Assert.Equal(new(false, 1024, 64), GpuMemoryAllocator.PlacementFor(small, false, 64, block, false));
        Assert.Equal(new(false, 1024, 256), GpuMemoryAllocator.PlacementFor(small, false, 256, block, false));
        // No alignment given is one byte.
        Assert.Equal(
            new(false, 1000, 1),
            GpuMemoryAllocator.PlacementFor(new MemoryRequirements { Size = 1000 }, true, 1, block, false)
        );
        Assert.True(GpuMemoryAllocator.PlacementFor(small, true, 64, block, driverWantsDedicated: true).Dedicated);
        var half = new MemoryRequirements { Size = block / 2, Alignment = 256 };
        Assert.True(GpuMemoryAllocator.PlacementFor(half, true, 64, block, false).Dedicated);
        var under = new MemoryRequirements { Size = block / 2 - 256, Alignment = 256 };
        Assert.False(GpuMemoryAllocator.PlacementFor(under, true, 64, block, false).Dedicated);
    }

    [Theory]
    [InlineData(8UL << 30, 64UL << 20)]
    [InlineData(256UL << 20, 32UL << 20)]
    [InlineData(100UL << 20, 12UL << 20)]
    [InlineData(4UL << 20, 1UL << 20)]
    public void A_block_is_an_eighth_of_its_heap_in_whole_megabytes_from_1_to_64(ulong heap, ulong expected)
    {
        Assert.Equal(expected, GpuMemoryAllocator.BlockSize(heap));
    }

    [Fact]
    public void A_full_memory_type_falls_back_to_the_next_and_keeps_no_block()
    {
        var memory = new FakeMemory(failingType: 0);
        var allocator = Allocator(memory);

        var allocation = allocator.Allocate(
            new MemoryRequirements
            {
                Size = 4096,
                Alignment = 256,
                MemoryTypeBits = 0b11,
            },
            MemoryUsage.DeviceLocal,
            ResourceKind.Linear,
            driverWantsDedicated: false,
            default,
            default,
            "test buffer"
        );

        Assert.Equal(1, allocation.TypeIndex);
        // The first type's block, then the buffer alone there, then the next type's block.
        Assert.Equal([0, 0, 1], memory.Tried);
        Assert.Equal(0, allocator.BlockCount(0, ResourceKind.Linear));
        Assert.Equal(1, allocator.BlockCount(1, ResourceKind.Linear));
        Assert.Equal(GpuMemoryAllocator.BlockSize(1UL << 30), allocator.ReservedBytes);
    }

    /// <summary>
    /// A small resource whose type has no room for a new block takes memory of its own in that type, where it went to the
    /// next type or failed.
    /// </summary>
    [Fact]
    public void A_type_with_no_room_for_a_block_still_takes_a_small_resource_alone()
    {
        var memory = new FakeMemory(failingType: 1, mostBytes: 1 << 20);
        var allocator = Allocator(memory);

        var allocation = allocator.Allocate(
            new MemoryRequirements
            {
                Size = 4096,
                Alignment = 256,
                MemoryTypeBits = 0b11,
            },
            MemoryUsage.DeviceLocal,
            ResourceKind.Linear,
            driverWantsDedicated: false,
            default,
            default,
            "test buffer"
        );

        Assert.Equal(0, allocation.TypeIndex);
        Assert.Equal([0, 0], memory.Tried);
        Assert.Equal(0, allocator.BlockCount(0, ResourceKind.Linear));
        Assert.Equal(4096UL, allocator.ReservedBytes);
    }

    [Fact]
    public void Every_type_full_throws_with_the_last_result()
    {
        var allocator = Allocator(new FakeMemory(failingType: -1));

        var failure = Assert.Throws<VulkanException>(() =>
            allocator.Allocate(
                new MemoryRequirements
                {
                    Size = 4096,
                    Alignment = 256,
                    MemoryTypeBits = 0b11,
                },
                MemoryUsage.DeviceLocal,
                ResourceKind.Linear,
                driverWantsDedicated: false,
                default,
                default,
                "test buffer"
            )
        );

        Assert.Contains("test buffer", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Two device-local types on one 1 GB heap, the first tried first.</summary>
    private static GpuMemoryAllocator Allocator(FakeMemory memory) =>
        new(
            NullLogger<GpuMemoryAllocator>.Instance,
            [MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.DeviceLocalBit],
            [0, 0],
            [1UL << 30],
            64,
            memory.Allocate
        );

    /// <summary>
    /// Stands for vkAllocateMemory: <c>failingType</c> is out of memory (-1: every type), and every type past
    /// <c>mostBytes</c> in one allocation.
    /// </summary>
    private sealed class FakeMemory(int failingType, ulong mostBytes = ulong.MaxValue)
    {
        private ulong _next = 1;

        public List<int> Tried { get; } = [];

        public Result Allocate(int type, ulong size, void* next, out DeviceMemory memory, out byte* mapped)
        {
            Tried.Add(type);
            mapped = null;
            if (failingType < 0 || type == failingType || size > mostBytes)
            {
                memory = default;
                return Result.ErrorOutOfDeviceMemory;
            }

            memory = new DeviceMemory(_next++);
            return Result.Success;
        }
    }
}
