using Itera.Engine.Rendering.Memory;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// The depth image the passes of a frame share, the size of the swapchain
/// (<see cref="DeviceRequirements.DepthFormat"/>). The world's pass (<c>MeshPass</c>) writes every pixel's depth in reverse form
/// (<c>view.glsl</c>: the near distance over the distance along the view direction, 0 for the sky), the particles'
/// simulate pass reads it (<see cref="RecordToSampled"/>), and later passes test against it. One image serves every frame in flight: <see cref="RecordFrameStart"/> waits for the depth tests of
/// earlier frames before this frame's first write. Created before the render thread starts, made again by the render
/// thread when the swapchain's size changes.
/// </summary>
public sealed unsafe partial class DepthTarget : IDisposable
{
    private static readonly ImageSubresourceRange DepthRange = new(ImageAspectFlags.DepthBit, 0, 1, 0, 1);

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly GpuMemoryAllocator _allocator;
    private readonly ILogger<DepthTarget> _logger;
    private GpuImage? _image;
    private bool _disposed;

    public DepthTarget(
        VulkanContext context,
        VulkanSwapchain swapchain,
        GpuMemoryAllocator allocator,
        ILogger<DepthTarget> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _allocator = allocator;
        _logger = logger;
    }

    /// <summary>The view of the depth image. Throws before <see cref="Initialize"/>.</summary>
    public ImageView View =>
        (_image ?? throw new InvalidOperationException("The depth target has not been initialised.")).View;

    /// <summary>Makes the image for the swapchain's size. After the swapchain.</summary>
    public void Initialize() => Create(_swapchain.Extent);

    /// <summary>Makes the image again when the swapchain's size changed. Render thread, device idle.</summary>
    public void OnSwapchainRecreated()
    {
        var extent = _swapchain.Extent;
        if (_image is not null && _image.Extent.Width == extent.Width && _image.Extent.Height == extent.Height)
        {
            return;
        }

        _image?.Dispose();
        _image = null;
        Create(extent);
    }

    /// <summary>
    /// Records the barrier before the frame's first depth write: the depth tests of earlier frames finish first, and
    /// the old contents are dropped (the world's pass writes or clears every pixel). A multisample resolve into the
    /// image (<c>MeshPass</c> with more than one sample a pixel, <paramref name="resolvedInto"/>) counts as a colour attachment
    /// write, so the last frame's is waited for too, and a second barrier, chained to the first, makes the new layout
    /// visible to this frame's (a depth layout's barrier takes depth accesses only). Render thread.
    /// </summary>
    public void RecordFrameStart(CommandBuffer commandBuffer, bool resolvedInto = false)
    {
        const PipelineStageFlags2 tests =
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        var stages = resolvedInto ? tests | PipelineStageFlags2.ColorAttachmentOutputBit : tests;
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = stages,
            SrcAccessMask = resolvedInto
                ? AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.ColorAttachmentWriteBit
                : AccessFlags2.DepthStencilAttachmentWriteBit,
            DstStageMask = stages,
            DstAccessMask = AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.DepthAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _image!.Image,
            SubresourceRange = DepthRange,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
        if (!resolvedInto)
        {
            return;
        }

        var resolve = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags2.ColorAttachmentWriteBit,
        };
        var chained = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &resolve,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &chained);
    }

    /// <summary>
    /// Records the barrier that hands the world pass's depth to a compute shader's sampled reads (the particles' simulate
    /// pass), contents kept: after the world's depth writes, and after its multisample resolve when
    /// <paramref name="resolvedInto"/> (a colour attachment write). Render thread; <see cref="RecordToAttachment"/>
    /// follows before the passes that test against it.
    /// </summary>
    public void RecordToSampled(CommandBuffer commandBuffer, bool resolvedInto)
    {
        const PipelineStageFlags2 tests =
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
        if (resolvedInto)
        {
            // The resolve's write is a colour attachment write, which a depth layout's image barrier may not name: a
            // memory barrier makes it available first, and the transition below waits on the same stage.
            var resolve = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags2.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags2.None,
            };
            var first = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &resolve,
            };
            _context.Vk.CmdPipelineBarrier2(commandBuffer, &first);
        }

        Transition(
            commandBuffer,
            resolvedInto ? tests | PipelineStageFlags2.ColorAttachmentOutputBit : tests,
            AccessFlags2.DepthStencilAttachmentWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.DepthAttachmentOptimal,
            ImageLayout.DepthReadOnlyOptimal
        );
    }

    /// <summary>
    /// Records the barrier that gives the depth back to the passes that test against it and write it (particle cubes),
    /// contents kept, after the compute shader's reads. Render thread.
    /// </summary>
    public void RecordToAttachment(CommandBuffer commandBuffer) =>
        Transition(
            commandBuffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.None,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            ImageLayout.DepthReadOnlyOptimal,
            ImageLayout.DepthAttachmentOptimal
        );

    private void Transition(
        CommandBuffer commandBuffer,
        PipelineStageFlags2 sourceStages,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStages,
        AccessFlags2 destinationAccess,
        ImageLayout oldLayout,
        ImageLayout newLayout
    )
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sourceStages,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStages,
            DstAccessMask = destinationAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _image!.Image,
            SubresourceRange = DepthRange,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    /// <summary>Destroys the image. The GPU must be done with it. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _image?.Dispose();
        _image = null;
    }

    private void Create(Extent2D extent)
    {
        if (extent.Width == 0 || extent.Height == 0)
        {
            return;
        }

        _image = _allocator.CreateImage(
            DeviceRequirements.DepthFormat,
            extent,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.DepthBit,
            "depth"
        );
        LogCreated(extent.Width, extent.Height, _image.MemoryTypeIndex);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Depth image: {Width}x{Height}, memory type {Type}")]
    private partial void LogCreated(uint width, uint height, int type);
}
