using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Itera.Engine.Rendering.Memory;

/// <summary>
/// Itera's GPU memory allocator (no VMA; ARCHITECTURE.md section 6). It takes memory from Vulkan in blocks of up to
/// 64 MB per memory type and hands out ranges of them (<see cref="BlockSuballocator"/>), because a device may allow as
/// few as 4096 <c>vkAllocateMemory</c> calls. Large resources, and resources the driver asks for, get a dedicated
/// allocation. Buffers and optimal-tiling images use separate blocks, so <c>bufferImageGranularity</c> never applies.
/// Host-visible memory is mapped once, when it is allocated, and stays mapped. One thread at a time: the main thread
/// before the render thread starts, the render thread while it runs (the lock guards the bookkeeping only), but for the
/// main thread's flush of the staging region and invalidate of the probe on the frame slot it builds.
/// </summary>
public sealed unsafe partial class GpuMemoryAllocator : IDisposable
{
    /// <summary>Largest block taken from Vulkan for sub-allocation.</summary>
    public const ulong MaxBlockSize = 64UL << 20;

    private readonly VulkanContext _context;
    private readonly ILogger<GpuMemoryAllocator> _logger;
    private readonly MemoryAllocation _allocateAndMap;
    private readonly object _lock = new();
    private MemoryPropertyFlags[] _typeFlags = [];
    private uint[] _typeHeaps = [];
    private ulong[] _heapSizes = [];
    private List<MemoryBlock>[] _blocks = [];
    private ulong _nonCoherentAtomSize = 1;
    private int _liveAllocations;
    private ulong _reservedBytes;
    private ulong _usedBytes;
    private bool _disposed;

    public GpuMemoryAllocator(VulkanContext context, ILogger<GpuMemoryAllocator> logger)
    {
        _context = context;
        _logger = logger;
        _allocateAndMap = AllocateAndMap;
    }

    /// <summary>
    /// For tests: an allocator over the given memory types with no device, where <paramref name="allocate"/> stands for
    /// <c>vkAllocateMemory</c> and <c>vkMapMemory</c>. Only <see cref="Allocate"/> may be used: nothing else has a device.
    /// </summary>
    internal GpuMemoryAllocator(
        ILogger<GpuMemoryAllocator> logger,
        MemoryPropertyFlags[] typeFlags,
        uint[] typeHeaps,
        ulong[] heapSizes,
        ulong nonCoherentAtomSize,
        MemoryAllocation allocate
    )
    {
        _context = null!;
        _logger = logger;
        _allocateAndMap = allocate;
        Initialize(typeFlags, typeHeaps, heapSizes, nonCoherentAtomSize);
    }

    /// <summary><c>vkAllocateMemory</c> of <paramref name="size"/> bytes of <paramref name="type"/>, mapped if host-visible.</summary>
    internal delegate Result MemoryAllocation(
        int type,
        ulong size,
        void* next,
        out DeviceMemory memory,
        out byte* mapped
    );

    /// <summary>Where one allocation goes: a dedicated allocation, or a block, with its size and alignment there.</summary>
    internal readonly record struct Placement(bool Dedicated, ulong Size, ulong Alignment);

    /// <summary>Bytes taken from Vulkan: every block and dedicated allocation.</summary>
    public ulong ReservedBytes
    {
        get
        {
            lock (_lock)
            {
                return _reservedBytes;
            }
        }
    }

    /// <summary>Bytes handed out to resources, including alignment padding inside each range.</summary>
    public ulong UsedBytes
    {
        get
        {
            lock (_lock)
            {
                return _usedBytes;
            }
        }
    }

    /// <summary>Reads the device's memory types. Call after the Vulkan context is initialised.</summary>
    public void Initialize()
    {
        var memory = _context.MemoryProperties;
        var typeFlags = new MemoryPropertyFlags[memory.MemoryTypeCount];
        var typeHeaps = new uint[memory.MemoryTypeCount];
        for (var i = 0; i < typeFlags.Length; i++)
        {
            typeFlags[i] = memory.MemoryTypes[i].PropertyFlags;
            typeHeaps[i] = memory.MemoryTypes[i].HeapIndex;
        }

        var heapSizes = new ulong[memory.MemoryHeapCount];
        for (var i = 0; i < heapSizes.Length; i++)
        {
            heapSizes[i] = memory.MemoryHeaps[i].Size;
        }

        Initialize(typeFlags, typeHeaps, heapSizes, _context.Limits.NonCoherentAtomSize);
    }

    private void Initialize(
        MemoryPropertyFlags[] typeFlags,
        uint[] typeHeaps,
        ulong[] heapSizes,
        ulong nonCoherentAtomSize
    )
    {
        _typeFlags = typeFlags;
        _typeHeaps = typeHeaps;
        _heapSizes = heapSizes;
        _blocks = new List<MemoryBlock>[_typeFlags.Length * 2];
        for (var i = 0; i < _blocks.Length; i++)
        {
            _blocks[i] = [];
        }

        _nonCoherentAtomSize = Math.Max(1, nonCoherentAtomSize);
    }

    /// <summary>Blocks kept for <paramref name="type"/> and <paramref name="kind"/>. For tests.</summary>
    internal int BlockCount(int type, ResourceKind kind) => _blocks[ListIndex(type, kind)].Count;

    /// <summary>
    /// Creates a buffer of <paramref name="size"/> bytes with its memory bound. <paramref name="name"/> appears in
    /// validation messages and in the log.
    /// </summary>
    public GpuBuffer CreateBuffer(ulong size, BufferUsageFlags usage, MemoryUsage memoryUsage, string name)
    {
        ArgumentOutOfRangeException.ThrowIfZero(size);
        var vk = _context.Vk;
        var device = _context.Device;
        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        vk.CreateBuffer(device, &createInfo, null, out var buffer).ThrowIfFailed("vkCreateBuffer");

        GpuAllocation? allocation = null;
        try
        {
            _context.SetObjectName(ObjectType.Buffer, buffer.Handle, name);

            var dedicated = new MemoryDedicatedRequirements { SType = StructureType.MemoryDedicatedRequirements };
            var requirements = new MemoryRequirements2
            {
                SType = StructureType.MemoryRequirements2,
                PNext = &dedicated,
            };
            var info = new BufferMemoryRequirementsInfo2
            {
                SType = StructureType.BufferMemoryRequirementsInfo2,
                Buffer = buffer,
            };
            vk.GetBufferMemoryRequirements2(device, &info, &requirements);

            allocation = Allocate(
                requirements.MemoryRequirements,
                memoryUsage,
                ResourceKind.Linear,
                dedicated.PrefersDedicatedAllocation || dedicated.RequiresDedicatedAllocation,
                buffer,
                default,
                name
            );
            vk.BindBufferMemory(device, buffer, allocation.Memory, allocation.Offset)
                .ThrowIfFailed("vkBindBufferMemory");
            return new GpuBuffer(this, _context, buffer, size, allocation, name);
        }
        catch
        {
            vk.DestroyBuffer(device, buffer, null);
            if (allocation is not null)
            {
                Free(allocation);
            }

            throw;
        }
    }

    /// <summary>
    /// Creates a device-local 2D image of <paramref name="format"/> and <paramref name="extent"/> with optimal tiling
    /// (3D, <paramref name="depth"/> deep, when that is more than 1, with a 3D view),
    /// <paramref name="mips"/> mip levels (one by default), <paramref name="layers"/> layers (one by default; more, or
    /// <paramref name="arrayView"/>, make the view a 2D array) of
    /// <paramref name="samples"/> samples (one by default), its memory bound, and a view of <paramref name="aspect"/>. <paramref name="name"/>
    /// appears in validation messages and in the log. When <paramref name="alsoViewedAs"/> is another format of the same
    /// size, such as the UNORM twin of an sRGB format, the image may also be viewed as that format: it is made mutable,
    /// with a list of the two formats, which lets drivers keep compressing it.
    /// </summary>
    public GpuImage CreateImage(
        Format format,
        Extent2D extent,
        ImageUsageFlags usage,
        ImageAspectFlags aspect,
        string name,
        Format alsoViewedAs = Format.Undefined,
        SampleCountFlags samples = SampleCountFlags.Count1Bit,
        uint layers = 1,
        uint depth = 1,
        uint mips = 1,
        bool arrayView = false
    )
    {
        ArgumentOutOfRangeException.ThrowIfZero(extent.Width);
        ArgumentOutOfRangeException.ThrowIfZero(mips);
        ArgumentOutOfRangeException.ThrowIfZero(layers);
        ArgumentOutOfRangeException.ThrowIfZero(depth);
        if (depth > 1 && layers > 1)
        {
            throw new ArgumentException("A 3D image has one layer.", nameof(layers));
        }

        ArgumentOutOfRangeException.ThrowIfZero(extent.Height);
        var vk = _context.Vk;
        var device = _context.Device;
        var createInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = depth > 1 ? ImageType.Type3D : ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(extent.Width, extent.Height, depth),
            MipLevels = mips,
            ArrayLayers = layers,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        var viewFormats = stackalloc Format[2] { format, alsoViewedAs };
        var formatList = new ImageFormatListCreateInfo
        {
            SType = StructureType.ImageFormatListCreateInfo,
            ViewFormatCount = 2,
            PViewFormats = viewFormats,
        };
        if (alsoViewedAs != Format.Undefined && alsoViewedAs != format)
        {
            createInfo.Flags = ImageCreateFlags.CreateMutableFormatBit;
            createInfo.PNext = &formatList;
        }

        vk.CreateImage(device, &createInfo, null, out var image).ThrowIfFailed("vkCreateImage");

        GpuAllocation? allocation = null;
        ImageView view = default;
        try
        {
            _context.SetObjectName(ObjectType.Image, image.Handle, name);

            var dedicated = new MemoryDedicatedRequirements { SType = StructureType.MemoryDedicatedRequirements };
            var requirements = new MemoryRequirements2
            {
                SType = StructureType.MemoryRequirements2,
                PNext = &dedicated,
            };
            var info = new ImageMemoryRequirementsInfo2
            {
                SType = StructureType.ImageMemoryRequirementsInfo2,
                Image = image,
            };
            vk.GetImageMemoryRequirements2(device, &info, &requirements);

            allocation = Allocate(
                requirements.MemoryRequirements,
                MemoryUsage.DeviceLocal,
                ResourceKind.Optimal,
                dedicated.PrefersDedicatedAllocation || dedicated.RequiresDedicatedAllocation,
                default,
                image,
                name
            );
            vk.BindImageMemory(device, image, allocation.Memory, allocation.Offset).ThrowIfFailed("vkBindImageMemory");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType =
                    depth > 1 ? ImageViewType.Type3D
                    : layers > 1 || arrayView ? ImageViewType.Type2DArray
                    : ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(aspect, 0, mips, 0, layers),
            };
            vk.CreateImageView(device, &viewInfo, null, out view).ThrowIfFailed("vkCreateImageView");
            _context.SetObjectName(ObjectType.ImageView, view.Handle, name);
            return new GpuImage(this, _context, image, view, format, extent, allocation, name, mips, layers);
        }
        catch
        {
            if (view.Handle != 0)
            {
                vk.DestroyImageView(device, view, null);
            }

            vk.DestroyImage(device, image, null);
            if (allocation is not null)
            {
                Free(allocation);
            }

            throw;
        }
    }

    /// <summary>
    /// Makes CPU writes to a range of host-visible, non-coherent memory visible to the GPU. Does nothing for coherent
    /// memory. <paramref name="offset"/> and <paramref name="size"/> are relative to the allocation.
    /// </summary>
    public void Flush(GpuAllocation allocation, ulong offset, ulong size)
    {
        if (allocation.IsCoherent || size == 0)
        {
            return;
        }

        var range = MappedRange(allocation, offset, size);
        _context.Vk.FlushMappedMemoryRanges(_context.Device, 1, &range).ThrowIfFailed("vkFlushMappedMemoryRanges");
    }

    /// <summary>
    /// Makes GPU writes to a range of host-visible, non-coherent memory visible to the CPU, after the fence of the
    /// submit that wrote them. Does nothing for coherent memory.
    /// </summary>
    public void Invalidate(GpuAllocation allocation, ulong offset, ulong size)
    {
        if (allocation.IsCoherent || size == 0)
        {
            return;
        }

        var range = MappedRange(allocation, offset, size);
        _context
            .Vk.InvalidateMappedMemoryRanges(_context.Device, 1, &range)
            .ThrowIfFailed("vkInvalidateMappedMemoryRanges");
    }

    /// <summary>Returns an allocation's memory. The resource bound to it must already be destroyed.</summary>
    public void Free(GpuAllocation allocation)
    {
        lock (_lock)
        {
            if (allocation.IsFreed)
            {
                throw new InvalidOperationException($"GPU memory for {allocation.Name} was freed twice.");
            }

            allocation.IsFreed = true;
            _liveAllocations--;
            _usedBytes -= allocation.Size;
            if (_context.IsAbandoned || _disposed)
            {
                // After Dispose every block is gone already; the device may be too.
                return;
            }

            var block = allocation.Block;
            if (block is null)
            {
                _context.Vk.FreeMemory(_context.Device, allocation.Memory, null);
                _reservedBytes -= allocation.Size;
                LogDedicatedFreed(allocation.Name, allocation.Size / (1024.0 * 1024.0));
                return;
            }

            block.Suballocator.Free(allocation.Offset, allocation.Size);
            var list = _blocks[ListIndex(block.TypeIndex, block.Kind)];
            // One empty block per list stays, so a resource that comes and goes does not allocate each time.
            if (block.Suballocator.IsEmpty && list.Count > 1)
            {
                list.Remove(block);
                FreeBlock(block);
            }
        }
    }

    /// <summary>
    /// Frees every block. Resources should be disposed first; any allocation still live is reported. Does nothing to
    /// Vulkan after <see cref="VulkanContext.Abandon"/>. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_liveAllocations > 0)
            {
                LogLiveAtShutdown(_liveAllocations);
            }

            if (_context.IsAbandoned)
            {
                return;
            }

            foreach (var list in _blocks)
            {
                foreach (var block in list)
                {
                    FreeBlock(block);
                }

                list.Clear();
            }
        }
    }

    internal GpuAllocation Allocate(
        MemoryRequirements requirements,
        MemoryUsage usage,
        ResourceKind kind,
        bool driverWantsDedicated,
        VkBuffer dedicatedBuffer,
        Image dedicatedImage,
        string name
    )
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Span<int> order = stackalloc int[_typeFlags.Length];
            var count = MemoryTypeSelector.Rank(_typeFlags, requirements.MemoryTypeBits, usage, order);
            if (count == 0)
            {
                throw new VulkanException(
                    $"No memory type suits {name} ({usage}, type bits 0x{requirements.MemoryTypeBits:X})."
                );
            }

            // Try each acceptable type in turn: a heap that is full (a small BAR, for example) is not fatal.
            var lastResult = Result.ErrorOutOfDeviceMemory;
            for (var i = 0; i < count; i++)
            {
                var type = order[i];
                var blockSize = BlockSizeFor(type);
                var placement = PlacementFor(
                    requirements,
                    IsCoherent(type),
                    _nonCoherentAtomSize,
                    blockSize,
                    driverWantsDedicated
                );
                var result = placement.Dedicated
                    ? TryAllocateDedicated(
                        type,
                        requirements.Size,
                        dedicatedBuffer,
                        dedicatedImage,
                        name,
                        out var allocation
                    )
                    : TryAllocateInBlock(
                        type,
                        kind,
                        placement.Size,
                        placement.Alignment,
                        blockSize,
                        name,
                        out allocation
                    );
                if (result == Result.Success)
                {
                    _liveAllocations++;
                    _usedBytes += allocation!.Size;
                    return allocation;
                }

                lastResult = result;
            }

            throw new VulkanException($"vkAllocateMemory for {name}", lastResult);
        }
    }

    private Result TryAllocateDedicated(
        int type,
        ulong size,
        VkBuffer buffer,
        Image image,
        string name,
        out GpuAllocation? allocation
    )
    {
        allocation = null;
        // One of the two is set, the other is null (spec, VkMemoryDedicatedAllocateInfo).
        var dedicatedInfo = new MemoryDedicatedAllocateInfo
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Buffer = buffer,
            Image = image,
        };
        var result = _allocateAndMap(type, size, &dedicatedInfo, out var memory, out var mapped);
        if (result != Result.Success)
        {
            return result;
        }

        _reservedBytes += size;
        allocation = new GpuAllocation(memory, 0, size, type, IsCoherent(type), mapped, null, name);
        LogDedicated(name, size / (1024.0 * 1024.0), type, _typeFlags[type]);
        return Result.Success;
    }

    private Result TryAllocateInBlock(
        int type,
        ResourceKind kind,
        ulong size,
        ulong alignment,
        ulong blockSize,
        string name,
        out GpuAllocation? allocation
    )
    {
        allocation = null;
        var list = _blocks[ListIndex(type, kind)];
        ulong offset;
        foreach (var existing in list)
        {
            if (existing.Suballocator.TryAllocate(size, alignment, out offset))
            {
                allocation = InBlock(existing, offset, size, name);
                return Result.Success;
            }
        }

        var result = _allocateAndMap(type, blockSize, null, out var memory, out var mapped);
        if (result != Result.Success)
        {
            return result;
        }

        var block = new MemoryBlock(memory, type, kind, new BlockSuballocator(blockSize), mapped);
        list.Add(block);
        _reservedBytes += blockSize;
        LogBlockCreated(blockSize / (1024.0 * 1024.0), type, _typeFlags[type], kind, list.Count, name);
        if (!block.Suballocator.TryAllocate(size, alignment, out offset))
        {
            // Only an alignment near the block size could cause this; the block stays for later requests.
            throw new InvalidOperationException(
                $"{name} ({size} bytes, alignment {alignment}) does not fit a new {blockSize}-byte block."
            );
        }

        allocation = InBlock(block, offset, size, name);
        return Result.Success;
    }

    private GpuAllocation InBlock(MemoryBlock block, ulong offset, ulong size, string name)
    {
        var mapped = block.Mapped == null ? null : block.Mapped + offset;
        return new GpuAllocation(
            block.Memory,
            offset,
            size,
            block.TypeIndex,
            IsCoherent(block.TypeIndex),
            mapped,
            block,
            name
        );
    }

    private Result AllocateAndMap(int type, ulong size, void* next, out DeviceMemory memory, out byte* mapped)
    {
        var vk = _context.Vk;
        var device = _context.Device;
        mapped = null;
        var info = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = next,
            AllocationSize = size,
            MemoryTypeIndex = (uint)type,
        };
        var result = vk.AllocateMemory(device, &info, null, out memory);
        if (result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory)
        {
            return result;
        }

        result.ThrowIfFailed("vkAllocateMemory");
        if ((_typeFlags[type] & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            void* pointer;
            var mapResult = vk.MapMemory(device, memory, 0, Vk.WholeSize, MemoryMapFlags.None, &pointer);
            if (mapResult != Result.Success)
            {
                vk.FreeMemory(device, memory, null);
                memory = default;
                mapResult.ThrowIfFailed("vkMapMemory");
            }

            mapped = (byte*)pointer;
        }

        return Result.Success;
    }

    private void FreeBlock(MemoryBlock block)
    {
        // Freeing mapped memory unmaps it (spec, vkFreeMemory).
        _context.Vk.FreeMemory(_context.Device, block.Memory, null);
        _reservedBytes -= block.Suballocator.Size;
        LogBlockFreed(block.Suballocator.Size / (1024.0 * 1024.0), block.TypeIndex, block.Kind);
    }

    private MappedMemoryRange MappedRange(GpuAllocation allocation, ulong offset, ulong size)
    {
        if (allocation.Mapped == null)
        {
            throw new InvalidOperationException($"GPU memory for {allocation.Name} is not host-visible.");
        }

        if (offset > allocation.Size || size > allocation.Size - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The range lies outside the allocation.");
        }

        var (rangeOffset, rangeSize) = RangeFor(
            allocation.Offset,
            allocation.Size,
            allocation.Block is null,
            _nonCoherentAtomSize,
            offset,
            size
        );
        return new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = allocation.Memory,
            Offset = rangeOffset,
            Size = rangeSize,
        };
    }

    /// <summary>
    /// The memory range to flush or invalidate for bytes <paramref name="offset"/> to <paramref name="offset"/> +
    /// <paramref name="size"/> of an allocation at <paramref name="allocationOffset"/> in its memory, rounded out to
    /// whole atoms. A range in a block starts and ends on whole atoms (<see cref="PlacementFor"/>), so rounding out stays
    /// inside it. A dedicated allocation has the resource's exact size; a range that rounds past its end runs to the end
    /// instead, which the spec allows (<c>VK_WHOLE_SIZE</c>, VUID-VkMappedMemoryRange-size-01389).
    /// </summary>
    internal static (ulong Offset, ulong Size) RangeFor(
        ulong allocationOffset,
        ulong allocationSize,
        bool dedicated,
        ulong atom,
        ulong offset,
        ulong size
    )
    {
        var start = allocationOffset + offset;
        var alignedStart = start & ~(atom - 1);
        var alignedEnd = BlockSuballocator.AlignUp(start + size, atom);
        var toEnd = dedicated && alignedEnd > allocationSize;
        return (alignedStart, toEnd ? Vk.WholeSize : alignedEnd - alignedStart);
    }

    /// <summary>
    /// Where a resource with <paramref name="requirements"/> goes in a memory type: a dedicated allocation when the
    /// driver asks for one or it takes half a block or more; otherwise a block, where non-coherent memory gets whole
    /// atoms (flushes are rounded out to atoms, and whole atoms keep them off neighbours).
    /// </summary>
    internal static Placement PlacementFor(
        MemoryRequirements requirements,
        bool coherent,
        ulong atom,
        ulong blockSize,
        bool driverWantsDedicated
    )
    {
        var size = requirements.Size;
        var alignment = Math.Max(1, requirements.Alignment);
        if (!coherent)
        {
            size = BlockSuballocator.AlignUp(size, atom);
            alignment = Math.Max(alignment, atom);
        }

        return new Placement(driverWantsDedicated || size >= blockSize / 2, size, alignment);
    }

    /// <summary>
    /// 64 MB, or an eighth of a heap of <paramref name="heapSize"/> when that is smaller (a 256 MB BAR heap gets 32 MB
    /// blocks), in whole megabytes and never below 1 MB.
    /// </summary>
    internal static ulong BlockSize(ulong heapSize)
    {
        var size = Math.Min(MaxBlockSize, heapSize / 8) & ~((1UL << 20) - 1);
        return Math.Max(size, 1UL << 20);
    }

    private ulong BlockSizeFor(int type) => BlockSize(_heapSizes[_typeHeaps[type]]);

    private bool IsCoherent(int type) => (_typeFlags[type] & MemoryPropertyFlags.HostCoherentBit) != 0;

    private static int ListIndex(int type, ResourceKind kind) => type * 2 + (int)kind;

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "GPU memory: new {SizeMb:F0} MB block in type {Type} ({Flags}) for {Kind} resources, block {Count} of that kind, first used by {Name}"
    )]
    private partial void LogBlockCreated(
        double sizeMb,
        int type,
        MemoryPropertyFlags flags,
        ResourceKind kind,
        int count,
        string name
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "GPU memory: freed a {SizeMb:F0} MB block in type {Type} ({Kind})"
    )]
    private partial void LogBlockFreed(double sizeMb, int type, ResourceKind kind);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "GPU memory: dedicated {SizeMb:F2} MB in type {Type} ({Flags}) for {Name}"
    )]
    private partial void LogDedicated(string name, double sizeMb, int type, MemoryPropertyFlags flags);

    [LoggerMessage(Level = LogLevel.Debug, Message = "GPU memory: freed dedicated {SizeMb:F2} MB of {Name}")]
    private partial void LogDedicatedFreed(string name, double sizeMb);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "GPU memory: {Count} allocation(s) still live at shutdown; a resource was not disposed"
    )]
    private partial void LogLiveAtShutdown(int count);
}

/// <summary>Whether a resource's memory layout is linear (buffers) or optimal (images with optimal tiling).</summary>
internal enum ResourceKind
{
    Linear = 0,
    Optimal = 1,
}

/// <summary>One <c>vkAllocateMemory</c> block that <see cref="GpuMemoryAllocator"/> hands out ranges of.</summary>
internal sealed unsafe class MemoryBlock(
    DeviceMemory memory,
    int typeIndex,
    ResourceKind kind,
    BlockSuballocator suballocator,
    byte* mapped
)
{
    public DeviceMemory Memory { get; } = memory;

    public int TypeIndex { get; } = typeIndex;

    public ResourceKind Kind { get; } = kind;

    public BlockSuballocator Suballocator { get; } = suballocator;

    /// <summary>Start of the mapped block, or null when the memory is not host-visible.</summary>
    public byte* Mapped { get; } = mapped;
}

/// <summary>A range of GPU memory bound to one resource.</summary>
public sealed unsafe class GpuAllocation
{
    internal GpuAllocation(
        DeviceMemory memory,
        ulong offset,
        ulong size,
        int typeIndex,
        bool isCoherent,
        byte* mapped,
        MemoryBlock? block,
        string name
    )
    {
        Memory = memory;
        Offset = offset;
        Size = size;
        TypeIndex = typeIndex;
        IsCoherent = isCoherent;
        Mapped = mapped;
        Block = block;
        Name = name;
    }

    /// <summary>The memory object; shared with other allocations unless this one is dedicated.</summary>
    public DeviceMemory Memory { get; }

    /// <summary>Offset of this range in <see cref="Memory"/>.</summary>
    public ulong Offset { get; }

    /// <summary>Bytes in this range.</summary>
    public ulong Size { get; }

    /// <summary>Memory type index.</summary>
    public int TypeIndex { get; }

    /// <summary>True when CPU writes need no flush and GPU writes no invalidate.</summary>
    public bool IsCoherent { get; }

    /// <summary>Where the range is mapped, or null when the memory is not host-visible.</summary>
    public byte* Mapped { get; }

    /// <summary>The resource's name, for messages.</summary>
    public string Name { get; }

    internal MemoryBlock? Block { get; }

    internal bool IsFreed { get; set; }
}
