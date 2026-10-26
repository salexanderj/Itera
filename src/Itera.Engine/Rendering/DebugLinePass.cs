using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// Draws the frame's <see cref="DebugLines"/> over the picture, after the world's pass, hidden where the depth image
/// holds something nearer (no depth writes). The line ends go in a host-visible buffer with one region per frame slot:
/// the main thread writes the region of the frame it builds (<see cref="Write"/>, between taking the request and
/// publishing the frame, so the GPU is done with that slot), the render thread flushes it when the memory is not
/// coherent (so the main thread makes no Vulkan call here), and the draw's first vertex picks the region
/// (<c>debug-lines.vert</c> reads the buffer by <c>gl_VertexIndex</c>). Created before the render thread starts;
/// recorded by the render thread.
/// </summary>
public sealed unsafe partial class DebugLinePass : IDisposable, IReloadsShaders
{
    private const int VerticesPerSlot = 2 * DebugLines.MaxLines;

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly ShaderCompiler _shaders;
    private readonly GpuMemoryAllocator _allocator;
    private readonly ILogger<DebugLinePass> _logger;
    private byte[] _vertexSpirv = [];
    private byte[] _fragmentSpirv = [];
    private GpuBuffer? _buffer;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private Format _format;
    private bool _disposed;

    public DebugLinePass(
        VulkanContext context,
        VulkanSwapchain swapchain,
        ShaderCompiler shaders,
        GpuMemoryAllocator allocator,
        ILogger<DebugLinePass> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _shaders = shaders;
        _allocator = allocator;
        _logger = logger;
    }

    private static ulong SlotBytes => (ulong)(VerticesPerSlot * sizeof(DebugLineVertex));

    /// <summary>Creates the line buffer, its descriptor set, and the pipeline.</summary>
    public void Initialize()
    {
        _buffer = _allocator.CreateBuffer(
            SlotBytes * FrameSync.FramesInFlight,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "debug lines"
        );
        CreateDescriptors();
        TakeShaders((byte[][])CompileShaders());
        var setLayout = _setLayout;
        _layout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            ShaderStageFlags.VertexBit,
            (uint)sizeof(ViewConstants),
            "debug line layout"
        );
        CreatePipeline(_swapchain.ImageFormat);
        LogCreated(DebugLines.MaxLines, FrameSync.FramesInFlight, _buffer.MemoryTypeIndex);
    }

    /// <summary>Makes a new pipeline when the swapchain's format changed. Render thread, device idle.</summary>
    public void OnSwapchainRecreated()
    {
        if (_swapchain.ImageFormat != _format)
        {
            _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
            _pipeline = default;
            CreatePipeline(_swapchain.ImageFormat);
        }
    }

    /// <summary>
    /// Writes the line ends into the region of <paramref name="slot"/> and returns how many there are. Main thread,
    /// between taking the frame's request and publishing the frame. Writes only, in address order.
    /// </summary>
    public int Write(int slot, ReadOnlySpan<DebugLineVertex> vertices)
    {
        if ((uint)slot >= FrameSync.FramesInFlight)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "No such frame slot.");
        }

        var count = Math.Min(vertices.Length, VerticesPerSlot) & ~1;
        if (count == 0)
        {
            return 0;
        }

        vertices[..count].CopyTo(new Span<DebugLineVertex>(Buffer.Mapped + SlotBytes * (ulong)slot, count));
        return count;
    }

    /// <summary>
    /// Records the lines of the frame in <paramref name="slot"/> over <paramref name="target"/>, which the world's pass
    /// drew, tested against <paramref name="depth"/>: the barrier waits for the colour and depth writes before it, and
    /// the attachments load them. First makes the main thread's writes to the slot's region visible to the GPU when the
    /// memory is not coherent. Render thread.
    /// </summary>
    public void Record(
        CommandBuffer commandBuffer,
        ImageView target,
        ImageView depth,
        Extent2D extent,
        in ViewConstants view,
        int slot,
        int vertexCount
    )
    {
        if (vertexCount == 0)
        {
            return;
        }

        Buffer.Flush(SlotBytes * (ulong)slot, (ulong)(vertexCount * sizeof(DebugLineVertex)));
        var vk = _context.Vk;
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask =
                PipelineStageFlags2.ColorAttachmentOutputBit
                | PipelineStageFlags2.EarlyFragmentTestsBit
                | PipelineStageFlags2.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            DstStageMask =
                PipelineStageFlags2.ColorAttachmentOutputBit
                | PipelineStageFlags2.EarlyFragmentTestsBit
                | PipelineStageFlags2.LateFragmentTestsBit,
            DstAccessMask =
                AccessFlags2.ColorAttachmentReadBit
                | AccessFlags2.ColorAttachmentWriteBit
                | AccessFlags2.DepthStencilAttachmentReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        vk.CmdPipelineBarrier2(commandBuffer, &dependency);

        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depth,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
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
            PDepthAttachment = &depthAttachment,
        };
        vk.CmdBeginRendering(commandBuffer, &renderingInfo);
        // A negative height flips Y (core since Vulkan 1.1), so +y in clip space is up on screen.
        var viewport = new Viewport(0, extent.Height, extent.Width, -(float)extent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        var set = _set;
        var constants = view;
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        vk.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.VertexBit,
            0,
            (uint)sizeof(ViewConstants),
            &constants
        );
        vk.CmdDraw(commandBuffer, (uint)vertexCount, 1, (uint)(slot * VerticesPerSlot), 0);
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
        _buffer?.Dispose();
        _buffer = null;
    }

    private GpuBuffer Buffer =>
        _buffer ?? throw new InvalidOperationException("The debug line pass has not been initialised.");

    public object CompileShaders() =>
        new[] { _shaders.Compile("debug-lines.vert"), _shaders.Compile("debug-lines.frag") };

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
        // Hidden behind whatever is nearer (reverse depth: nearer is greater); lines write no depth.
        var depth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.GreaterOrEqual, Write: false);
        _pipeline = PipelineFactory.CreateLines(
            _context,
            _vertexSpirv,
            _fragmentSpirv,
            _layout,
            format,
            in depth,
            "debug lines"
        );
        _format = format;
    }

    private void CreateDescriptors()
    {
        var vk = _context.Vk;
        var device = _context.Device;
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit,
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

        // The whole buffer: every slot's region, picked by the draw's first vertex.
        var info = new DescriptorBufferInfo
        {
            Buffer = Buffer.Buffer,
            Offset = 0,
            Range = Buffer.Size,
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
        vk.UpdateDescriptorSets(device, 1, &write, 0, null);
        _context.SetObjectName(ObjectType.DescriptorSet, _set.Handle, "debug lines");
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Debug lines: up to {Lines} a frame in {Slots} slot regions, memory type {Type}"
    )]
    private partial void LogCreated(int lines, int slots, int type);
}
