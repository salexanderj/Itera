using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Memory;

/// <summary>
/// Host-visible memory the CPU writes data into for the GPU to copy into device-local buffers: one region per frame
/// slot (<see cref="FrameSync.FramesInFlight"/>). The main thread writes the region of <c>request.Slot</c> between
/// <see cref="FrameExchange.TryTakeRequest"/> and <see cref="FrameExchange.Publish"/>, after the render thread's fence
/// wait for that slot, so the GPU has finished every copy that read it. Writes only, in address order; the memory is
/// often uncached. Size from <c>Engine:Renderer:UploadMegabytesPerFrame</c>.
/// </summary>
public sealed unsafe partial class StagingBuffer : IDisposable, Meshes.IStagingRegions
{
    private readonly GpuMemoryAllocator _allocator;
    private readonly ILogger<StagingBuffer> _logger;
    private readonly ulong _regionBytes;
    private GpuBuffer? _buffer;

    public StagingBuffer(GpuMemoryAllocator allocator, IOptions<EngineOptions> options, ILogger<StagingBuffer> logger)
    {
        _allocator = allocator;
        _logger = logger;
        _regionBytes = (ulong)options.Value.Renderer.UploadMegabytesPerFrame << 20;
    }

    /// <summary>Bytes in each frame slot's region.</summary>
    public ulong RegionBytes => _regionBytes;

    /// <summary>The buffer; the source of the frame's copy commands.</summary>
    public GpuBuffer Buffer =>
        _buffer ?? throw new InvalidOperationException("The staging buffer has not been initialised.");

    /// <summary>Creates the buffer. Call after the allocator is initialised and before the render thread starts.</summary>
    public void Initialize()
    {
        if (_buffer is not null)
        {
            throw new InvalidOperationException("The staging buffer is already initialised.");
        }

        _buffer = _allocator.CreateBuffer(
            _regionBytes * FrameSync.FramesInFlight,
            BufferUsageFlags.TransferSrcBit,
            MemoryUsage.Upload,
            "staging"
        );
        var megabytes = _regionBytes >> 20;
        LogCreated(FrameSync.FramesInFlight, megabytes, _buffer.MemoryTypeIndex, _buffer.IsCoherent);
    }

    /// <summary>Offset of a slot's region in <see cref="Buffer"/>.</summary>
    public ulong RegionOffset(int slot)
    {
        CheckSlot(slot);
        return (ulong)slot * _regionBytes;
    }

    /// <summary>Start of a slot's region in mapped memory. Write only.</summary>
    public byte* Region(int slot) => Buffer.Mapped + RegionOffset(slot);

    /// <summary>
    /// Makes the first <paramref name="bytes"/> of a slot's region visible to the GPU. Needed only for non-coherent
    /// memory; call it after writing and before publishing the frame.
    /// </summary>
    public void Flush(int slot, ulong bytes)
    {
        if (bytes > _regionBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "More bytes than the region holds.");
        }

        Buffer.Flush(RegionOffset(slot), bytes);
    }

    /// <summary>Destroys the buffer. The GPU must be done with it. Safe to call more than once.</summary>
    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }

    private static void CheckSlot(int slot)
    {
        if ((uint)slot >= FrameSync.FramesInFlight)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "No such frame slot.");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Staging: {Slots} regions of {Megabytes} MB in memory type {Type} (coherent {Coherent})"
    )]
    private partial void LogCreated(int slots, ulong megabytes, int type, bool coherent);
}
