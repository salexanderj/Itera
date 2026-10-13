using Itera.Engine.Rendering.Memory;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// The picture under the Screen stage's passes, for those that read it (<see cref="IRenderPass.ReadsPicture"/>): in a
/// frame where one asks, the swapchain image is copied, before the stage's rendering begins, into an image of the
/// screen's size and the swapchain's format, which the passes sample through <see cref="RenderPassContext.Picture"/>
/// (binding 0, a combined image sampler: linear, clamped to the edge; set layout
/// <see cref="RenderPassSetup.PictureSetLayout"/>). The image is made on the render thread the first time it is asked
/// for, made again for a new screen size, and freed once no pass has read it for <see cref="PictureFramesBeforeFree"/>
/// frames, each slot's set pointed at it after that slot's fence, as the upscale's images are (<see cref="FrameImages{T}"/>).
/// Nothing is copied or kept while no pass reads it, nor where the surface offers no copy
/// (<see cref="VulkanSwapchain.CanCopyImages"/>).
/// </summary>
public sealed unsafe partial class RenderPasses
{
    /// <summary>Frames in a row with no pass reading the picture after which its image is freed.</summary>
    public const int PictureFramesBeforeFree = 600;

    private static readonly ImageSubresourceRange PictureRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

    private readonly DescriptorSet[] _pictureSets = new DescriptorSet[FrameSync.FramesInFlight];
    private readonly FrameImages<GpuImage> _pictures = new();
    private readonly bool[] _written = new bool[FrameSync.FramesInFlight];
    private Sampler _pictureSampler;
    private DescriptorSetLayout _pictureSetLayout;
    private DescriptorPool _picturePool;
    private bool _passesWritten;

    /// <summary>The Screen stage's first pass that reads the picture in this frame, or -1 for none.</summary>
    private int FirstPictureReader()
    {
        if (!_passesWritten || _pictureSetLayout.Handle == 0)
        {
            return -1;
        }

        for (var i = 0; i < _passes.Length; i++)
        {
            if (_passes[i].Stage == RenderStage.Screen && _passes[i].ReadsPicture(_slot))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Copies the swapchain's <paramref name="image"/> (<paramref name="screen"/> in size, in the colour attachment
    /// layout, left in it) into the picture and returns the frame slot's set that samples it. Render thread, outside any
    /// rendering.
    /// </summary>
    private DescriptorSet CopyPicture(CommandBuffer commandBuffer, Image image, Extent2D screen)
    {
        _pictures.Use(_slot);
        var picture = EnsurePicture(screen);
        if (_pictures.SetsNeedWriting(_slot))
        {
            // The slot's fence has passed, so no submitted frame uses its set.
            WritePictureSet(_pictureSets[_slot], picture.View);
            _pictures.SetsWritten(_slot);
        }

        var barriers = stackalloc ImageMemoryBarrier2[2];
        // The frame's colour writes to the swapchain image finish before the copy reads it; earlier frames' reads of the
        // picture finish before the copy writes it, its old contents dropped.
        barriers[0] = PictureBarrier(
            image,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferReadBit
        );
        barriers[1] = PictureBarrier(
            picture.Image,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.None,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit
        );
        PictureBarriers(commandBuffer, barriers);
        var layers = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
        var region = new ImageCopy
        {
            SrcSubresource = layers,
            DstSubresource = layers,
            Extent = new Extent3D(screen.Width, screen.Height, 1),
        };
        _context.Vk.CmdCopyImage(
            commandBuffer,
            image,
            ImageLayout.TransferSrcOptimal,
            picture.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &region
        );

        // The copy's writes before the passes sample the picture; the copy's read of the swapchain image before they draw
        // over it again.
        barriers[0] = PictureBarrier(
            picture.Image,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit
        );
        barriers[1] = PictureBarrier(
            image,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit
        );
        PictureBarriers(commandBuffer, barriers);
        return _pictureSets[_slot];
    }

    /// <summary>The picture's image, made when there is none or it has another size or format; the old one retired.</summary>
    private GpuImage EnsurePicture(Extent2D screen)
    {
        var picture = _pictures.Current;
        var format = _swapchain.ImageFormat;
        if (
            picture is not null
            && picture.Extent.Width == screen.Width
            && picture.Extent.Height == screen.Height
            && picture.Format == format
        )
        {
            return picture;
        }

        var made = _allocator.CreateImage(
            format,
            screen,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.ColorBit,
            "screen picture"
        );
        _pictures.Replace(made, _slot);
        LogPicture(screen.Width, screen.Height);
        return made;
    }

    /// <summary>The picture's sampler, set layout, and a set for each frame slot. At the run's "render passes" step.</summary>
    private void CreatePictureObjects()
    {
        var vk = _context.Vk;
        var device = _context.Device;
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0,
        };
        vk.CreateSampler(device, &samplerInfo, null, out _pictureSampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _pictureSampler.Handle, "screen picture");

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _pictureSetLayout)
            .ThrowIfFailed("vkCreateDescriptorSetLayout");

        const int SetCount = FrameSync.FramesInFlight;
        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, SetCount);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = SetCount,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        vk.CreateDescriptorPool(device, &poolInfo, null, out _picturePool).ThrowIfFailed("vkCreateDescriptorPool");
        var setLayouts = stackalloc DescriptorSetLayout[SetCount];
        for (var i = 0; i < SetCount; i++)
        {
            setLayouts[i] = _pictureSetLayout;
        }

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _picturePool,
            DescriptorSetCount = SetCount,
            PSetLayouts = setLayouts,
        };
        var sets = stackalloc DescriptorSet[SetCount];
        vk.AllocateDescriptorSets(device, &allocateInfo, sets).ThrowIfFailed("vkAllocateDescriptorSets");
        for (var slot = 0; slot < SetCount; slot++)
        {
            _pictureSets[slot] = sets[slot];
            _context.SetObjectName(ObjectType.DescriptorSet, sets[slot].Handle, "screen picture");
        }

        if (!_swapchain.CanCopyImages)
        {
            LogNoPicture();
        }
    }

    /// <summary>Destroys the picture's image and objects. The GPU must be done with them.</summary>
    private void DestroyPictureObjects()
    {
        _pictures.Clear();
        if (!_context.IsAbandoned)
        {
            var vk = _context.Vk;
            var device = _context.Device;
            // Destroying the pool frees the sets allocated from it.
            if (_picturePool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device, _picturePool, null);
            }

            if (_pictureSetLayout.Handle != 0)
            {
                vk.DestroyDescriptorSetLayout(device, _pictureSetLayout, null);
            }

            if (_pictureSampler.Handle != 0)
            {
                vk.DestroySampler(device, _pictureSampler, null);
            }
        }

        _picturePool = default;
        _pictureSetLayout = default;
        _pictureSampler = default;
    }

    /// <summary>Points <paramref name="set"/> at <paramref name="view"/>. No submitted frame may use the set.</summary>
    private void WritePictureSet(DescriptorSet set, ImageView view)
    {
        var info = new DescriptorImageInfo(_pictureSampler, view, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info,
        };
        _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    private static ImageMemoryBarrier2 PictureBarrier(
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        PipelineStageFlags2 srcStage,
        AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage,
        AccessFlags2 dstAccess
    ) =>
        new()
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = PictureRange,
        };

    private void PictureBarriers(CommandBuffer commandBuffer, ImageMemoryBarrier2* barriers)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 2,
            PImageMemoryBarriers = barriers,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Render passes: the screen's picture copied for them at {Width}x{Height}"
    )]
    private partial void LogPicture(uint width, uint height);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Render passes: the surface offers no copy of its images, so passes that read the screen's picture get none"
    )]
    private partial void LogNoPicture();
}
