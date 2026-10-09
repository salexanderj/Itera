using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Itera.Engine.Rendering.Memory;

/// <summary>
/// A Vulkan buffer and the memory bound to it, made by <see cref="GpuMemoryAllocator.CreateBuffer"/>. Host-visible
/// buffers stay mapped for their whole life (<see cref="Mapped"/>). The CPU only writes mapped upload memory, never
/// reads it (ecs-vulkan skill).
/// </summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly GpuMemoryAllocator _allocator;
    private readonly VulkanContext _context;
    private readonly GpuAllocation _allocation;
    private bool _disposed;

    internal GpuBuffer(
        GpuMemoryAllocator allocator,
        VulkanContext context,
        VkBuffer buffer,
        ulong size,
        GpuAllocation allocation,
        string name
    )
    {
        _allocator = allocator;
        _context = context;
        Buffer = buffer;
        Size = size;
        _allocation = allocation;
        Name = name;
    }

    /// <summary>The buffer handle.</summary>
    public VkBuffer Buffer { get; private set; }

    /// <summary>Bytes the buffer was created with.</summary>
    public ulong Size { get; }

    /// <summary>The name given at creation, for messages.</summary>
    public string Name { get; }

    /// <summary>Start of the buffer's mapped memory, or null when the memory is not host-visible.</summary>
    public byte* Mapped => _allocation.Mapped;

    /// <summary>True when CPU writes need no <see cref="Flush"/>.</summary>
    public bool IsCoherent => _allocation.IsCoherent;

    /// <summary>The memory type the buffer lives in.</summary>
    public int MemoryTypeIndex => _allocation.TypeIndex;

    /// <summary>Makes CPU writes to a range visible to the GPU when the memory is not coherent.</summary>
    public void Flush(ulong offset, ulong size) => _allocator.Flush(_allocation, offset, size);

    /// <summary>Makes GPU writes to a range visible to the CPU when the memory is not coherent.</summary>
    public void Invalidate(ulong offset, ulong size) => _allocator.Invalidate(_allocation, offset, size);

    /// <summary>
    /// Destroys the buffer, then returns its memory. The GPU must be done with it. Does nothing to Vulkan after
    /// <see cref="VulkanContext.Abandon"/>. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_context.IsAbandoned && Buffer.Handle != 0)
        {
            _context.Vk.DestroyBuffer(_context.Device, Buffer, null);
        }

        Buffer = default;
        _allocator.Free(_allocation);
    }
}
