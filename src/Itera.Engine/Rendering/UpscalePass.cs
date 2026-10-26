using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// Upscales the picture drawn at a render scale below 1 (<see cref="RenderScale"/>) to the swapchain image with AMD's
/// FSR 1: <c>fsr-easu.frag</c> upscales it into an image the size of the screen, and <c>fsr-rcas.frag</c> sharpens that
/// into the swapchain image. The world, models, and debug lines draw into the source image
/// (<see cref="BeginSource"/>), which has the swapchain's format, so their pipelines serve both paths. FSR 1 wants
/// sRGB-encoded values, so EASU reads the source through a UNORM view, and the upscaled image holds them as they are.
/// Both images are the screen's size. The picture fills the source's top-left part, the drawn size, as the depth image
/// holds it, so a new render scale (<see cref="DynamicRenderScale"/>) needs no new image. EASU takes the drawn size as
/// its input viewport and the source's size as its input size (<see cref="EasuConstants.Create"/>), and holds the reads
/// of its pixels near the picture's right and bottom edges inside the picture. The images are made on the render thread
/// the first time a frame is drawn below scale 1, and again for a new screen. Nothing waits for the device: each frame
/// slot has its own descriptor sets, pointed at the images only after that slot's fence, and images replaced, or no
/// longer used because frames have been drawn at scale 1 for <see cref="FullSizeFramesBeforeFree"/> frames
/// (<see cref="DrawnAtFullSize"/>), are destroyed once the frame before has passed its fence.
/// </summary>
public sealed unsafe partial class UpscalePass : IDisposable, IReloadsShaders
{
    /// <summary>The format of the upscaled image: 10 bits a colour, so the sharpening adds no banding.</summary>
    public const Format UpscaledFormat = Format.A2B10G10R10UnormPack32;

    /// <summary>RCAS's sharpening in stops below the most, AMD's suggested 0.2.</summary>
    public const float Sharpness = 0.2f;

    /// <summary>
    /// Frames drawn at scale 1 in a row after which the images are freed: dynamic render scaling that moves between 1
    /// and just below it keeps them, and a game that stays at 1 frees them after a few seconds.
    /// </summary>
    public const int FullSizeFramesBeforeFree = 600;

    private static readonly ImageSubresourceRange ColorRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly ShaderCompiler _shaders;
    private readonly GpuMemoryAllocator _allocator;
    private readonly ILogger<UpscalePass> _logger;
    private byte[] _vertexSpirv = [];
    private byte[] _easuSpirv = [];
    private byte[] _rcasSpirv = [];
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private PipelineLayout _easuLayout;
    private PipelineLayout _rcasLayout;
    private Pipeline _easuPipeline;
    private Pipeline _rcasPipeline;
    private Format _format;
    private readonly DescriptorSet[] _easuSets = new DescriptorSet[FrameSync.FramesInFlight];
    private readonly DescriptorSet[] _rcasSets = new DescriptorSet[FrameSync.FramesInFlight];
    private readonly FrameImages<Images> _images = new();
    private Extent2D _drawn;
    private bool _disposed;

    public UpscalePass(
        VulkanContext context,
        VulkanSwapchain swapchain,
        ShaderCompiler shaders,
        GpuMemoryAllocator allocator,
        ILogger<UpscalePass> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _shaders = shaders;
        _allocator = allocator;
        _logger = logger;
    }

    /// <summary>
    /// The format that reads the same bytes as <paramref name="format"/> without sRGB decoding: the UNORM twin of an
    /// sRGB format, or the format itself.
    /// </summary>
    public static Format Unorm(Format format) =>
        format switch
        {
            Format.B8G8R8A8Srgb => Format.B8G8R8A8Unorm,
            Format.R8G8B8A8Srgb => Format.R8G8B8A8Unorm,
            Format.A8B8G8R8SrgbPack32 => Format.A8B8G8R8UnormPack32,
            _ => format,
        };

    /// <summary>Compiles the shaders and makes the sampler, descriptor sets, and pipelines. No images yet.</summary>
    public void Initialize()
    {
        TakeShaders((byte[][])CompileShaders());
        CreateSampler();
        CreateDescriptors();
        var setLayout = _setLayout;
        _easuLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            ShaderStageFlags.FragmentBit,
            (uint)sizeof(EasuConstants),
            "fsr easu layout"
        );
        _rcasLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            ShaderStageFlags.FragmentBit,
            (uint)sizeof(RcasConstants),
            "fsr rcas layout"
        );
        CreateEasuPipeline();
        CreateRcasPipeline(_swapchain.ImageFormat);
    }

    public object CompileShaders() =>
        new[]
        {
            _shaders.Compile("fullscreen.vert"),
            _shaders.Compile("fsr-easu.frag"),
            _shaders.Compile("fsr-rcas.frag"),
        };

    public int ReplacePipelines(object compiled)
    {
        _context.Vk.DestroyPipeline(_context.Device, _easuPipeline, null);
        _context.Vk.DestroyPipeline(_context.Device, _rcasPipeline, null);
        // Cleared at once: should making a new one fail, Dispose finds no handle destroyed already.
        _easuPipeline = default;
        _rcasPipeline = default;
        TakeShaders((byte[][])compiled);
        CreateEasuPipeline();
        CreateRcasPipeline(_format);
        return 2;
    }

    private void TakeShaders(byte[][] compiled)
    {
        _vertexSpirv = compiled[0];
        _easuSpirv = compiled[1];
        _rcasSpirv = compiled[2];
    }

    private void CreateEasuPipeline()
    {
        var noDepth = new DepthState(Format.Undefined, CompareOp.Always, Write: false);
        _easuPipeline = PipelineFactory.CreateFullscreen(
            _context,
            _vertexSpirv,
            _easuSpirv,
            _easuLayout,
            UpscaledFormat,
            in noDepth,
            "fsr easu"
        );
    }

    /// <summary>True while images exist, for this frame or for frames still in flight.</summary>
    public bool HasImages => _images.HasImages;

    /// <summary>
    /// Drops the images, which the next frame below scale 1 makes again at the new sizes, and makes the sharpening's
    /// pipeline again when the swapchain's format changed. Render thread, device idle.
    /// </summary>
    public void OnSwapchainRecreated()
    {
        _images.Clear();
        if (_swapchain.ImageFormat != _format)
        {
            _context.Vk.DestroyPipeline(_context.Device, _rcasPipeline, null);
            _rcasPipeline = default;
            CreateRcasPipeline(_swapchain.ImageFormat);
        }
    }

    /// <summary>
    /// For a frame of <paramref name="slot"/> drawn at scale 1, after the slot's fence: frees the images the slot's
    /// fence has released, and after <see cref="FullSizeFramesBeforeFree"/> such frames in a row retires the images in
    /// use, which the frame before may still read. Render thread.
    /// </summary>
    public void DrawnAtFullSize(int slot) => _images.NotUsed(slot, FullSizeFramesBeforeFree);

    /// <summary>
    /// For a frame of <paramref name="slot"/> drawn below scale 1, after the slot's fence: frees the images the slot's
    /// fence has released, makes the images when they do not exist or are not the screen's size (retiring the old ones),
    /// points the slot's descriptor sets at them, then records the barrier before the frame's first write to the source
    /// image: earlier frames' reads of it finish first, and its old contents are dropped (the world's pass covers the
    /// drawn part, and EASU reads no other). Returns the view the passes draw into, in the colour attachment layout: the
    /// screen's size, of which they draw the top-left part, <paramref name="drawn"/> in size. Render thread.
    /// </summary>
    public ImageView BeginSource(CommandBuffer commandBuffer, int slot, Extent2D drawn, Extent2D screen)
    {
        _images.Use(slot);
        var images = EnsureImages(slot, drawn, screen);
        if (drawn.Width != _drawn.Width || drawn.Height != _drawn.Height)
        {
            // Trace: dynamic render scaling may ask for a new drawn size every few frames.
            LogResized(drawn.Width, drawn.Height);
        }

        _drawn = drawn;
        if (_images.SetsNeedWriting(slot))
        {
            // The slot's fence has passed, so no submitted frame uses its sets.
            WriteDescriptor(_easuSets[slot], images.SourceUnorm);
            WriteDescriptor(_rcasSets[slot], images.Upscaled.View);
            _images.SetsWritten(slot);
        }

        var source = images.Source;
        TransitionImage(
            commandBuffer,
            source.Image,
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit
        );
        return source.View;
    }

    /// <summary>
    /// Records the upscale of the picture in the source image's top-left part, which the frame's passes drew after
    /// <see cref="BeginSource"/> at its drawn size, into
    /// <paramref name="target"/> (<paramref name="screen"/> in size, in the colour attachment layout), which it covers
    /// completely. <paramref name="linearOutput"/> is true when the target is sRGB. Render thread.
    /// </summary>
    public void Record(CommandBuffer commandBuffer, int slot, ImageView target, Extent2D screen, bool linearOutput)
    {
        var images = _images.Current ?? throw new InvalidOperationException("BeginSource was not recorded this frame.");
        var source = images.Source;
        var upscaled = images.Upscaled;

        // The frame's colour writes to the source finish before EASU samples it; earlier frames' RCAS reads of the
        // upscaled image finish before EASU writes it again.
        TransitionImage(
            commandBuffer,
            source.Image,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit
        );
        TransitionImage(
            commandBuffer,
            upscaled.Image,
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit
        );
        var easu = EasuConstants.Create(
            new(_drawn.Width, _drawn.Height),
            new(source.Extent.Width, source.Extent.Height),
            new(screen.Width, screen.Height)
        );
        Draw(
            commandBuffer,
            upscaled.View,
            screen,
            _easuPipeline,
            _easuLayout,
            _easuSets[slot],
            &easu,
            sizeof(EasuConstants)
        );

        TransitionImage(
            commandBuffer,
            upscaled.Image,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit
        );
        var rcas = new RcasConstants
        {
            Width = screen.Width,
            Height = screen.Height,
            Sharpness = Sharpness,
            LinearOutput = linearOutput ? 1u : 0u,
        };
        Draw(commandBuffer, target, screen, _rcasPipeline, _rcasLayout, _rcasSets[slot], &rcas, sizeof(RcasConstants));
    }

    /// <summary>Destroys everything the pass made. The GPU must be done with it. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _images.Clear();
        if (!_context.IsAbandoned)
        {
            var vk = _context.Vk;
            var device = _context.Device;
            if (_rcasPipeline.Handle != 0)
            {
                vk.DestroyPipeline(device, _rcasPipeline, null);
            }

            if (_easuPipeline.Handle != 0)
            {
                vk.DestroyPipeline(device, _easuPipeline, null);
            }

            if (_rcasLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _rcasLayout, null);
            }

            if (_easuLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _easuLayout, null);
            }

            // Destroying the pool frees the sets allocated from it.
            if (_pool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device, _pool, null);
            }

            if (_setLayout.Handle != 0)
            {
                vk.DestroyDescriptorSetLayout(device, _setLayout, null);
            }

            if (_sampler.Handle != 0)
            {
                vk.DestroySampler(device, _sampler, null);
            }
        }

        _rcasPipeline = default;
        _easuPipeline = default;
        _rcasLayout = default;
        _easuLayout = default;
        _pool = default;
        _setLayout = default;
        _sampler = default;
    }

    private void Draw(
        CommandBuffer commandBuffer,
        ImageView target,
        Extent2D extent,
        Pipeline pipeline,
        PipelineLayout layout,
        DescriptorSet set,
        void* constants,
        int constantBytes
    )
    {
        var vk = _context.Vk;
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.DontCare,
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
        // A negative height flips Y, as in every pass; the shaders read gl_FragCoord, which counts from the top-left
        // corner either way.
        var viewport = new Viewport(0, extent.Height, extent.Width, -(float)extent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null);
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        vk.CmdPushConstants(commandBuffer, layout, ShaderStageFlags.FragmentBit, 0, (uint)constantBytes, constants);
        vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        vk.CmdEndRendering(commandBuffer);
    }

    /// <summary>
    /// True when images of <paramref name="images"/> serve a frame drawn at <paramref name="drawn"/> for a screen of
    /// <paramref name="screen"/>: they are the screen's size, and the drawn size fits in them. A new render scale keeps
    /// them; a new screen does not.
    /// </summary>
    internal static bool ImagesFit(Extent2D images, Extent2D drawn, Extent2D screen) =>
        images.Width == screen.Width
        && images.Height == screen.Height
        && drawn.Width <= images.Width
        && drawn.Height <= images.Height;

    /// <summary>
    /// The frame's images, made at the screen's size when there are none or they are another size; the old ones are
    /// retired.
    /// </summary>
    private Images EnsureImages(int slot, Extent2D drawn, Extent2D screen)
    {
        var images = _images.Current;
        if (images is not null && ImagesFit(images.Source.Extent, drawn, screen))
        {
            return images;
        }

        var format = _swapchain.ImageFormat;
        var unorm = Unorm(format);
        var source = _allocator.CreateImage(
            format,
            screen,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.ColorBit,
            "upscale source",
            unorm
        );
        var sourceUnorm = source.CreateView(unorm, "upscale source unorm");
        var upscaled = _allocator.CreateImage(
            UpscaledFormat,
            screen,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.ColorBit,
            "upscaled"
        );
        var made = new Images(_context, source, sourceUnorm, upscaled);
        _images.Replace(made, slot);
        LogImages(drawn.Width, drawn.Height, screen.Width, screen.Height);
        return made;
    }

    private void CreateRcasPipeline(Format format)
    {
        var noDepth = new DepthState(Format.Undefined, CompareOp.Always, Write: false);
        _rcasPipeline = PipelineFactory.CreateFullscreen(
            _context,
            _vertexSpirv,
            _rcasSpirv,
            _rcasLayout,
            format,
            in noDepth,
            "fsr rcas"
        );
        _format = format;
    }

    private void CreateSampler()
    {
        // EASU gathers four texels and RCAS fetches single ones, so filtering never applies; clamping keeps EASU's
        // gathers at the edges on the edge texels.
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0,
        };
        _context.Vk.CreateSampler(_context.Device, &info, null, out _sampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _sampler.Handle, "upscale");
    }

    private void CreateDescriptors()
    {
        var vk = _context.Vk;
        var device = _context.Device;
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
        vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _setLayout)
            .ThrowIfFailed("vkCreateDescriptorSetLayout");

        // Two sets (EASU's and RCAS's) for each frame slot.
        const int SetCount = 2 * FrameSync.FramesInFlight;
        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, SetCount);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = SetCount,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        vk.CreateDescriptorPool(device, &poolInfo, null, out _pool).ThrowIfFailed("vkCreateDescriptorPool");

        var setLayouts = stackalloc DescriptorSetLayout[SetCount];
        for (var i = 0; i < SetCount; i++)
        {
            setLayouts[i] = _setLayout;
        }

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = SetCount,
            PSetLayouts = setLayouts,
        };
        var sets = stackalloc DescriptorSet[SetCount];
        vk.AllocateDescriptorSets(device, &allocateInfo, sets).ThrowIfFailed("vkAllocateDescriptorSets");
        for (var slot = 0; slot < FrameSync.FramesInFlight; slot++)
        {
            _easuSets[slot] = sets[2 * slot];
            _rcasSets[slot] = sets[2 * slot + 1];
            _context.SetObjectName(ObjectType.DescriptorSet, _easuSets[slot].Handle, "fsr easu");
            _context.SetObjectName(ObjectType.DescriptorSet, _rcasSets[slot].Handle, "fsr rcas");
        }
    }

    /// <summary>Points <paramref name="set"/> at <paramref name="view"/>. No submitted frame may use the set.</summary>
    private void WriteDescriptor(DescriptorSet set, ImageView view)
    {
        var info = new DescriptorImageInfo(_sampler, view, ImageLayout.ShaderReadOnlyOptimal);
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

    private void TransitionImage(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        PipelineStageFlags2 srcStage,
        AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage,
        AccessFlags2 dstAccess
    )
    {
        var barrier = new ImageMemoryBarrier2
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
            SubresourceRange = ColorRange,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Upscale: drawn at {Width}x{Height}, upscaled to {ScreenWidth}x{ScreenHeight} with FSR 1"
    )]
    private partial void LogImages(uint width, uint height, uint screenWidth, uint screenHeight);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Upscale: drawn at {Width}x{Height}")]
    private partial void LogResized(uint width, uint height);

    /// <summary>The images of one screen: the source, its UNORM view that EASU reads, and the upscaled image.</summary>
    private sealed class Images(VulkanContext context, GpuImage source, ImageView sourceUnorm, GpuImage upscaled)
        : IDisposable
    {
        public GpuImage Source { get; } = source;

        public ImageView SourceUnorm { get; } = sourceUnorm;

        public GpuImage Upscaled { get; } = upscaled;

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (SourceUnorm.Handle != 0 && !context.IsAbandoned)
            {
                context.Vk.DestroyImageView(context.Device, SourceUnorm, null);
            }

            Source.Dispose();
            Upscaled.Dispose();
        }
    }
}
