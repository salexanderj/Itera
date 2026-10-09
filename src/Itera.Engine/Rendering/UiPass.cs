using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// Draws the UI (the pixels of <c>UISurface</c>, one per screen pixel) over the finished picture. The pixels live in
/// a device-local storage buffer the size of the swapchain image, which <c>ui.frag</c> reads at each fragment's
/// position; no image, layout change, or sampler is needed. When the UI changes, the main thread writes the changed rows
/// into the upload region of the frame slot it builds (<see cref="Write"/>, between taking the request and publishing
/// the frame), and the render thread copies them into the storage buffer before the frame's passes
/// (<see cref="RecordUpload"/>). The buffers are made again, on the render thread with the device idle, only when the
/// swapchain's size changes; the frame request that follows carries the new size, and the main thread writes rows only
/// when its surface has that size, so the two always agree. Each remake raises <see cref="Generation"/>, which the
/// surface follows by sending every row (a size that changed and changed back between
/// two requests would leave the new buffer empty). On a screen larger than the device's largest storage buffer there are no
/// UI pixels, only a placeholder buffer, so the screen's dim still draws. Created before the render thread starts;
/// recorded by the render thread.
/// </summary>
public sealed unsafe partial class UiPass : IDisposable, IReloadsShaders
{
    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly ShaderCompiler _shaders;
    private readonly GpuMemoryAllocator _allocator;
    private readonly ILogger<UiPass> _logger;
    private byte[] _vertexSpirv = [];
    private byte[] _fragmentSpirv = [];
    private GpuBuffer? _pixels;
    private GpuBuffer? _upload;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private Format _format;

    // Written by the render thread when the buffers are made, before the frame request that carries the new size; the
    // main thread reads them after taking that request, which FrameExchange orders after the writes.
    private uint _width;
    private uint _height;

    private int _generation;

    /// <summary>True once rows have been copied into the pixel buffer since it was made. Render thread.</summary>
    private bool _uploaded;
    private bool _disposed;

    public UiPass(
        VulkanContext context,
        VulkanSwapchain swapchain,
        ShaderCompiler shaders,
        GpuMemoryAllocator allocator,
        ILogger<UiPass> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _shaders = shaders;
        _allocator = allocator;
        _logger = logger;
    }

    /// <summary>
    /// Raised each time the buffers are made; the main thread reads it after taking a frame request, which orders it after
    /// the remake, as the size.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    private ulong RowBytes => _width * 4ul;

    private ulong FrameBytes => RowBytes * _height;

    /// <summary>Creates the buffers for the swapchain's size, their descriptor set, and the pipeline.</summary>
    public void Initialize()
    {
        CreateDescriptorLayout();
        TakeShaders((byte[][])CompileShaders());
        var setLayout = _setLayout;
        _layout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            ShaderStageFlags.FragmentBit,
            (uint)sizeof(UiConstants),
            "ui layout"
        );
        CreatePipeline(_swapchain.ImageFormat);
        CreateBuffers(_swapchain.Extent);
    }

    /// <summary>
    /// Makes new buffers when the swapchain's size changed, and a new pipeline when its format changed. Render thread,
    /// device idle.
    /// </summary>
    public void OnSwapchainRecreated()
    {
        if (_swapchain.ImageFormat != _format)
        {
            _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
            _pipeline = default;
            CreatePipeline(_swapchain.ImageFormat);
        }

        var extent = _swapchain.Extent;
        if (extent.Width != _width || extent.Height != _height)
        {
            CreateBuffers(extent);
        }
    }

    /// <summary>
    /// Writes rows <paramref name="rowStart"/> to <paramref name="rowStart"/> + <paramref name="rows"/> of
    /// <paramref name="pixels"/> (<paramref name="width"/> x <paramref name="height"/>, four bytes a pixel, rows one
    /// after another) into the upload region of <paramref name="slot"/>, at the place they take in the pixel buffer.
    /// Returns the rows written: 0 when there are none, or when the pixels are not the size of the pass's buffers (the
    /// frame's request had another size). Main thread, between taking the frame's request and publishing the frame.
    /// Writes only, in address order.
    /// </summary>
    public int Write(int slot, ReadOnlySpan<byte> pixels, int width, int height, int rowStart, int rows)
    {
        if ((uint)slot >= FrameSync.FramesInFlight)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "No such frame slot.");
        }

        if (rows <= 0 || _upload is null || (uint)width != _width || (uint)height != _height)
        {
            return 0;
        }

        rowStart = Math.Clamp(rowStart, 0, height);
        rows = Math.Min(rows, height - rowStart);
        var offset = (int)RowBytes * rowStart;
        var bytes = (int)RowBytes * rows;
        if (rows <= 0 || pixels.Length < offset + bytes)
        {
            return 0;
        }

        pixels
            .Slice(offset, bytes)
            .CopyTo(new Span<byte>(_upload.Mapped + FrameBytes * (ulong)slot + (ulong)offset, bytes));
        return rows;
    }

    /// <summary>
    /// Records the copy of the rows <see cref="Write"/> put in the region of <paramref name="slot"/> into the pixel
    /// buffer, between two barriers: the first waits for earlier frames' reads of the buffer, the second makes the copy
    /// visible to this frame's draw. Outside rendering. First makes the main thread's writes visible to the GPU when the
    /// memory is not coherent. Render thread.
    /// </summary>
    public void RecordUpload(CommandBuffer commandBuffer, int slot, int rowStart, int rows)
    {
        if (rows <= 0 || _upload is null || _pixels is null)
        {
            return;
        }

        var offset = RowBytes * (ulong)rowStart;
        var bytes = RowBytes * (ulong)rows;
        var source = FrameBytes * (ulong)slot + offset;
        _upload.Flush(source, bytes);
        Barrier(
            commandBuffer,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.None,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit
        );
        var region = new BufferCopy(source, offset, bytes);
        _context.Vk.CmdCopyBuffer(commandBuffer, _upload.Buffer, _pixels.Buffer, 1, &region);
        Barrier(
            commandBuffer,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit
        );
        _uploaded = true;
    }

    /// <summary>
    /// Records the UI over <paramref name="target"/>, which the earlier passes drew, with the picture under it darkened
    /// by <paramref name="dim"/> (0 to 1, black at 1): the barrier waits for their colour writes, and the attachment loads
    /// them. The UI's pixels are read only where <paramref name="ui"/> and once rows have been copied into the current
    /// pixel buffer; with neither those nor a dim it draws nothing. Render thread.
    /// </summary>
    public void Record(CommandBuffer commandBuffer, ImageView target, Extent2D extent, bool ui, float dim)
    {
        var pixels = ui && _uploaded;
        if (_pixels is null || (!pixels && !(dim > 0)))
        {
            return;
        }

        var vk = _context.Vk;
        Barrier(
            commandBuffer,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit
        );

        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
        };
        vk.CmdBeginRendering(commandBuffer, &renderingInfo);
        // A negative height flips Y (core since Vulkan 1.1), as in every pass; ui.frag reads gl_FragCoord, which counts
        // from the top-left corner either way.
        var viewport = new Viewport(0, extent.Height, extent.Width, -(float)extent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        var set = _set;
        var constants = new UiConstants
        {
            Width = _width,
            Height = _height,
            Dim = Math.Clamp(dim, 0, 1),
            Pixels = pixels ? 1u : 0u,
        };
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        vk.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(UiConstants),
            &constants
        );
        vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        vk.CmdEndRendering(commandBuffer);
    }

    /// <summary>Destroys everything the pass made. The GPU must be done with it. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_context.IsAbandoned)
        {
            var vk = _context.Vk;
            var device = _context.Device;
            if (_pipeline.Handle != 0)
            {
                vk.DestroyPipeline(device, _pipeline, null);
            }

            if (_layout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _layout, null);
            }

            // Destroying the pool frees the set allocated from it.
            if (_pool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device, _pool, null);
            }

            if (_setLayout.Handle != 0)
            {
                vk.DestroyDescriptorSetLayout(device, _setLayout, null);
            }
        }

        _pipeline = default;
        _layout = default;
        _pool = default;
        _setLayout = default;
        _pixels?.Dispose();
        _pixels = null;
        _upload?.Dispose();
        _upload = null;
    }

    public object CompileShaders() => new[] { _shaders.Compile("fullscreen.vert"), _shaders.Compile("ui.frag") };

    public int ReplacePipelines(object compiled)
    {
        if (_pipeline.Handle != 0)
        {
            _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
        }

        _pipeline = default;
        TakeShaders((byte[][])compiled);
        CreatePipeline(_format);
        return 1;
    }

    private void TakeShaders(byte[][] compiled)
    {
        _vertexSpirv = compiled[0];
        _fragmentSpirv = compiled[1];
    }

    private void CreatePipeline(Format format)
    {
        var noDepth = new DepthState(Format.Undefined, CompareOp.Always, Write: false);
        _pipeline = PipelineFactory.CreateFullscreen(
            _context,
            _vertexSpirv,
            _fragmentSpirv,
            _layout,
            format,
            in noDepth,
            "ui",
            ColorBlend.Premultiplied
        );
        _format = format;
    }

    private void CreateBuffers(Extent2D extent)
    {
        _pixels?.Dispose();
        _pixels = null;
        _upload?.Dispose();
        _upload = null;
        _uploaded = false;
        _width = 0;
        _height = 0;

        var bytes = extent.Width * 4ul * extent.Height;
        if (bytes == 0)
        {
            return;
        }

        if (bytes > _context.Limits.MaxStorageBufferRange)
        {
            // No UI pixels, but a buffer the descriptor can name, so the dim still draws.
            LogTooLarge(extent.Width, extent.Height, _context.Limits.MaxStorageBufferRange);
            _pixels = _allocator.CreateBuffer(
                4,
                BufferUsageFlags.StorageBufferBit,
                MemoryUsage.DeviceLocal,
                "ui pixels placeholder"
            );
            WriteDescriptor();
            Interlocked.Increment(ref _generation);
            return;
        }

        _pixels = _allocator.CreateBuffer(
            bytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "ui pixels"
        );
        _upload = _allocator.CreateBuffer(
            bytes * FrameSync.FramesInFlight,
            BufferUsageFlags.TransferSrcBit,
            MemoryUsage.Upload,
            "ui upload"
        );
        _width = extent.Width;
        _height = extent.Height;
        WriteDescriptor();
        Interlocked.Increment(ref _generation);
        LogCreated(_width, _height, bytes / (1024.0 * 1024.0), _upload.MemoryTypeIndex);
    }

    private void CreateDescriptorLayout()
    {
        var vk = _context.Vk;
        var device = _context.Device;
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _setLayout)
            .ThrowIfFailed("vkCreateDescriptorSetLayout");

        var poolSize = new DescriptorPoolSize(DescriptorType.StorageBuffer, 1);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        vk.CreateDescriptorPool(device, &poolInfo, null, out _pool).ThrowIfFailed("vkCreateDescriptorPool");

        var setLayout = _setLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        DescriptorSet set;
        vk.AllocateDescriptorSets(device, &allocateInfo, &set).ThrowIfFailed("vkAllocateDescriptorSets");
        _set = set;
        _context.SetObjectName(ObjectType.DescriptorSet, _set.Handle, "ui");
    }

    /// <summary>Points the set at the current pixel buffer. No submitted frame may use the set.</summary>
    private void WriteDescriptor()
    {
        var pixels = _pixels ?? throw new InvalidOperationException("The UI pixel buffer does not exist.");
        var info = new DescriptorBufferInfo
        {
            Buffer = pixels.Buffer,
            Offset = 0,
            Range = pixels.Size,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &info,
        };
        _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    private void Barrier(
        CommandBuffer commandBuffer,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess
    )
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "UI pass: {Width}x{Height}, {Megabytes:F1} MB of pixels, upload memory type {Type}"
    )]
    private partial void LogCreated(uint width, uint height, double megabytes, int type);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "UI pass: {Width}x{Height} needs more than the device's largest storage buffer ({Limit} bytes); the UI is not drawn, only the screen's dim"
    )]
    private partial void LogTooLarge(uint width, uint height, uint limit);
}
