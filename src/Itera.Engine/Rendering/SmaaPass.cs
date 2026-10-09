using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>What SMAA's blend pass shows (the console's <c>aa</c>).</summary>
public enum SmaaPicture : uint
{
    /// <summary>The anti-aliased picture.</summary>
    Blended = 0,

    /// <summary>The edges the first pass found, red for left edges and green for top ones, over a dimmed picture.</summary>
    Edges = 1,

    /// <summary>The blend weights, red across horizontal edges and green across vertical ones, over a dimmed picture.</summary>
    Weights = 2,
}

/// <summary>
/// Anti-aliasing with SMAA 1x (<c>Engine:Renderer:AntiAliasing</c>, the settings'
/// Anti-aliasing), before FSR 1, as AMD asks of its input. The world, models, particles, and debug lines draw into the
/// scene image (<see cref="BeginScene"/>), the drawn size, in the swapchain's format. Then three passes
/// (<see cref="Record"/>): <c>smaa-edges.frag</c> finds the edges by luma into the edges image, <c>smaa-weights.frag</c>
/// works out each edge pixel's blend weights from the edges' shapes and SMAA's lookup textures
/// (<see cref="SmaaTextures"/>), and <c>smaa-blend.frag</c> blends each pixel with its neighbours into the swapchain image
/// (render scale 1) or FSR 1's source image (below 1). The edges are found in the sRGB-encoded values,
/// through a UNORM view of the scene, and the blend happens in linear light, through the scene's own view, as SMAA asks.
/// The images are made on the render thread the first time a frame is anti-aliased and again when the drawn size or the
/// screen changes, as <see cref="UpscalePass"/>'s are, and freed after <see cref="UnusedFramesBeforeFree"/> frames
/// without anti-aliasing. The lookup textures are copied to the GPU in the first anti-aliased frame.
/// </summary>
public sealed unsafe partial class SmaaPass : IDisposable, IReloadsShaders
{
    /// <summary>The format of the edges image: a left and a top edge per pixel.</summary>
    public const Format EdgesFormat = Format.R8G8Unorm;

    /// <summary>The format of the weights image: four blend weights per pixel.</summary>
    public const Format WeightsFormat = Format.R8G8B8A8Unorm;

    /// <summary>Frames without anti-aliasing in a row after which the images are freed.</summary>
    public const int UnusedFramesBeforeFree = 600;

    private static readonly ImageSubresourceRange ColorRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly ShaderCompiler _shaders;
    private readonly GpuMemoryAllocator _allocator;
    private readonly ILogger<SmaaPass> _logger;
    private byte[] _vertexSpirv = [];
    private byte[] _edgesSpirv = [];
    private byte[] _weightsSpirv = [];
    private byte[] _blendSpirv = [];
    private Sampler _sampler;
    private DescriptorSetLayout _edgesSetLayout;
    private DescriptorSetLayout _weightsSetLayout;
    private DescriptorSetLayout _blendSetLayout;
    private DescriptorPool _pool;
    private PipelineLayout _edgesLayout;
    private PipelineLayout _weightsLayout;
    private PipelineLayout _blendLayout;
    private Pipeline _edgesPipeline;
    private Pipeline _weightsPipeline;
    private Pipeline _blendPipeline;
    private Format _format;
    private readonly DescriptorSet[] _edgesSets = new DescriptorSet[FrameSync.FramesInFlight];
    private readonly DescriptorSet[] _weightsSets = new DescriptorSet[FrameSync.FramesInFlight];
    private readonly DescriptorSet[] _blendSets = new DescriptorSet[FrameSync.FramesInFlight];
    private readonly FrameImages<Images> _images = new();
    private GpuImage? _area;
    private GpuImage? _search;
    private GpuBuffer? _lookupUpload;
    private bool _lookupCopied;
    private bool _disposed;

    public SmaaPass(
        VulkanContext context,
        VulkanSwapchain swapchain,
        ShaderCompiler shaders,
        GpuMemoryAllocator allocator,
        ILogger<SmaaPass> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _shaders = shaders;
        _allocator = allocator;
        _logger = logger;
    }

    /// <summary>
    /// Compiles the shaders; makes the sampler, descriptor sets, pipelines, and lookup textures; and writes the lookup
    /// textures' bytes into a buffer the first anti-aliased frame copies from. Main thread, before the render thread
    /// starts. No drawn-size images yet.
    /// </summary>
    public void Initialize()
    {
        TakeShaders((byte[][])CompileShaders());
        CreateSampler();
        _edgesSetLayout = CreateSetLayout(1, "smaa edges");
        _weightsSetLayout = CreateSetLayout(3, "smaa weights");
        _blendSetLayout = CreateSetLayout(3, "smaa blend");
        CreateDescriptorSets();
        _edgesLayout = CreatePipelineLayout(_edgesSetLayout, "smaa edges layout");
        _weightsLayout = CreatePipelineLayout(_weightsSetLayout, "smaa weights layout");
        _blendLayout = CreatePipelineLayout(_blendSetLayout, "smaa blend layout");
        CreatePipelines(_swapchain.ImageFormat);
        CreateLookupTextures();
    }

    public object CompileShaders() =>
        new[]
        {
            _shaders.Compile("fullscreen.vert"),
            _shaders.Compile("smaa-edges.frag"),
            _shaders.Compile("smaa-weights.frag"),
            _shaders.Compile("smaa-blend.frag"),
        };

    public int ReplacePipelines(object compiled)
    {
        DestroyPipelines();
        TakeShaders((byte[][])compiled);
        CreatePipelines(_format);
        return 3;
    }

    /// <summary>True while drawn-size images exist, for this frame or for frames still in flight.</summary>
    public bool HasImages => _images.HasImages;

    /// <summary>
    /// Drops the drawn-size images, which the next anti-aliased frame makes again at the new sizes, and makes the blend's
    /// pipeline again when the swapchain's format changed. The lookup textures stay. Render thread, device idle.
    /// </summary>
    public void OnSwapchainRecreated()
    {
        _images.Clear();
        if (_swapchain.ImageFormat != _format)
        {
            DestroyPipelines();
            CreatePipelines(_swapchain.ImageFormat);
        }
    }

    /// <summary>
    /// For a frame of <paramref name="slot"/> drawn without anti-aliasing, after the slot's fence: frees the images the
    /// slot's fence has released, and after <see cref="UnusedFramesBeforeFree"/> such frames in a row retires the
    /// images in use. Render thread.
    /// </summary>
    public void NotUsed(int slot) => _images.NotUsed(slot, UnusedFramesBeforeFree);

    /// <summary>
    /// For a frame of <paramref name="slot"/> that is anti-aliased, after the slot's fence and outside rendering: copies
    /// the lookup textures the first time, makes the images when there are none or they have another size, points the
    /// slot's descriptor sets at them, and records the barrier before the frame's first write to the scene image
    /// (earlier frames' reads of it finish first; its old contents are dropped, as the world's pass covers it). Returns the
    /// view the world, models, particles, and lines draw into, <paramref name="drawn"/> in size, in the colour attachment
    /// layout. Render thread.
    /// </summary>
    public ImageView BeginScene(CommandBuffer commandBuffer, int slot, Extent2D drawn)
    {
        _images.Use(slot);
        CopyLookupTextures(commandBuffer);
        var images = EnsureImages(slot, drawn);
        if (_images.SetsNeedWriting(slot))
        {
            // The slot's fence has passed, so no submitted frame uses its sets.
            WriteDescriptorSets(slot, images);
            _images.SetsWritten(slot);
        }

        TransitionImage(
            commandBuffer,
            images.Scene.Image,
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit
        );
        return images.Scene.View;
    }

    /// <summary>
    /// Records SMAA's three passes over the scene image, which the frame drew after <see cref="BeginScene"/>, into
    /// <paramref name="target"/>: an image of the swapchain's format and the drawn size, in the colour attachment layout,
    /// which the blend covers completely. <paramref name="picture"/> shows the edges or the weights instead. Render
    /// thread.
    /// </summary>
    public void Record(CommandBuffer commandBuffer, int slot, ImageView target, SmaaPicture picture)
    {
        var images = _images.Current ?? throw new InvalidOperationException("BeginScene was not recorded this frame.");
        var drawn = images.Scene.Extent;
        var constants = new SmaaConstants
        {
            RtMetrics = new(1f / drawn.Width, 1f / drawn.Height, drawn.Width, drawn.Height),
            Picture = (uint)picture,
        };

        // The frame's colour writes to the scene finish before the edges pass samples it; earlier frames' reads of the
        // edges image finish before it is cleared.
        TransitionImage(
            commandBuffer,
            images.Scene.Image,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit
        );
        BeginTarget(commandBuffer, images.Edges.Image);
        Draw(commandBuffer, images.Edges.View, drawn, _edgesPipeline, _edgesLayout, _edgesSets[slot], &constants, true);

        EndTarget(commandBuffer, images.Edges.Image);
        BeginTarget(commandBuffer, images.Weights.Image);
        Draw(
            commandBuffer,
            images.Weights.View,
            drawn,
            _weightsPipeline,
            _weightsLayout,
            _weightsSets[slot],
            &constants,
            true
        );

        EndTarget(commandBuffer, images.Weights.Image);
        Draw(commandBuffer, target, drawn, _blendPipeline, _blendLayout, _blendSets[slot], &constants, false);
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
        _area?.Dispose();
        _search?.Dispose();
        _lookupUpload?.Dispose();
        _area = null;
        _search = null;
        _lookupUpload = null;
        if (!_context.IsAbandoned)
        {
            var vk = _context.Vk;
            var device = _context.Device;
            DestroyPipelines();
            foreach (var layout in (ReadOnlySpan<PipelineLayout>)[_edgesLayout, _weightsLayout, _blendLayout])
            {
                if (layout.Handle != 0)
                {
                    vk.DestroyPipelineLayout(device, layout, null);
                }
            }

            // Destroying the pool frees the sets allocated from it.
            if (_pool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device, _pool, null);
            }

            foreach (
                var layout in (ReadOnlySpan<DescriptorSetLayout>)[_edgesSetLayout, _weightsSetLayout, _blendSetLayout]
            )
            {
                if (layout.Handle != 0)
                {
                    vk.DestroyDescriptorSetLayout(device, layout, null);
                }
            }

            if (_sampler.Handle != 0)
            {
                vk.DestroySampler(device, _sampler, null);
            }
        }

        _edgesPipeline = default;
        _weightsPipeline = default;
        _blendPipeline = default;
        _edgesLayout = default;
        _weightsLayout = default;
        _blendLayout = default;
        _pool = default;
        _edgesSetLayout = default;
        _weightsSetLayout = default;
        _blendSetLayout = default;
        _sampler = default;
    }

    private void TakeShaders(byte[][] compiled)
    {
        _vertexSpirv = compiled[0];
        _edgesSpirv = compiled[1];
        _weightsSpirv = compiled[2];
        _blendSpirv = compiled[3];
    }

    /// <summary>Makes the three pipelines; the blend's draws into images of <paramref name="format"/>.</summary>
    private void CreatePipelines(Format format)
    {
        var noDepth = new DepthState(Format.Undefined, CompareOp.Always, Write: false);
        _edgesPipeline = PipelineFactory.CreateFullscreen(
            _context,
            _vertexSpirv,
            _edgesSpirv,
            _edgesLayout,
            EdgesFormat,
            in noDepth,
            "smaa edges"
        );
        _weightsPipeline = PipelineFactory.CreateFullscreen(
            _context,
            _vertexSpirv,
            _weightsSpirv,
            _weightsLayout,
            WeightsFormat,
            in noDepth,
            "smaa weights"
        );
        _blendPipeline = PipelineFactory.CreateFullscreen(
            _context,
            _vertexSpirv,
            _blendSpirv,
            _blendLayout,
            format,
            in noDepth,
            "smaa blend"
        );
        _format = format;
    }

    private void DestroyPipelines()
    {
        // Cleared at once: should making a new one fail, Dispose finds no handle destroyed already.
        foreach (var pipeline in (ReadOnlySpan<Pipeline>)[_edgesPipeline, _weightsPipeline, _blendPipeline])
        {
            if (pipeline.Handle != 0)
            {
                _context.Vk.DestroyPipeline(_context.Device, pipeline, null);
            }
        }

        _edgesPipeline = default;
        _weightsPipeline = default;
        _blendPipeline = default;
    }

    /// <summary>
    /// Makes the two lookup textures and an upload buffer holding their bytes, which the first anti-aliased frame copies
    /// (<see cref="CopyLookupTextures"/>). The buffer is 180 KB and stays until <see cref="Dispose"/>.
    /// </summary>
    private void CreateLookupTextures()
    {
        var area = SmaaTextures.ReadArea();
        var search = SmaaTextures.ReadSearch();
        _lookupUpload = _allocator.CreateBuffer(
            (ulong)(area.Length + search.Length),
            BufferUsageFlags.TransferSrcBit,
            MemoryUsage.Upload,
            "smaa lookup upload"
        );
        area.CopyTo(new Span<byte>(_lookupUpload.Mapped, area.Length));
        search.CopyTo(new Span<byte>(_lookupUpload.Mapped + area.Length, search.Length));
        _lookupUpload.Flush(0, _lookupUpload.Size);
        const ImageUsageFlags usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit;
        _area = _allocator.CreateImage(
            SmaaTextures.AreaFormat,
            new Extent2D(SmaaTextures.AreaWidth, SmaaTextures.AreaHeight),
            usage,
            ImageAspectFlags.ColorBit,
            "smaa area"
        );
        _search = _allocator.CreateImage(
            SmaaTextures.SearchFormat,
            new Extent2D(SmaaTextures.SearchWidth, SmaaTextures.SearchHeight),
            usage,
            ImageAspectFlags.ColorBit,
            "smaa search"
        );
    }

    /// <summary>
    /// Records the copy of the lookup textures from the upload buffer, the first time only (not tied to a frame being
    /// fresh: a frame presented again records nothing here). Host writes to the buffer need no barrier: the queue submit
    /// makes them visible. Outside rendering.
    /// </summary>
    private void CopyLookupTextures(CommandBuffer commandBuffer)
    {
        if (_lookupCopied || _lookupUpload is null || _area is null || _search is null)
        {
            return;
        }

        _lookupCopied = true;
        foreach (var image in (ReadOnlySpan<Image>)[_area.Image, _search.Image])
        {
            TransitionImage(
                commandBuffer,
                image,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                PipelineStageFlags2.None,
                AccessFlags2.None,
                PipelineStageFlags2.CopyBit,
                AccessFlags2.TransferWriteBit
            );
        }

        CopyToImage(commandBuffer, 0, _area);
        CopyToImage(commandBuffer, SmaaTextures.AreaBytes, _search);
        foreach (var image in (ReadOnlySpan<Image>)[_area.Image, _search.Image])
        {
            TransitionImage(
                commandBuffer,
                image,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.CopyBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderSampledReadBit
            );
        }
    }

    private void CopyToImage(CommandBuffer commandBuffer, int offset, GpuImage image)
    {
        // Rows packed one after another, as the files hold them.
        var region = new BufferImageCopy
        {
            BufferOffset = (ulong)offset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = default,
            ImageExtent = new Extent3D(image.Extent.Width, image.Extent.Height, 1),
        };
        _context.Vk.CmdCopyBufferToImage(
            commandBuffer,
            _lookupUpload!.Buffer,
            image.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &region
        );
    }

    /// <summary>The frame's images, made when there are none or they have another size; the old ones are retired.</summary>
    private Images EnsureImages(int slot, Extent2D drawn)
    {
        var images = _images.Current;
        if (
            images is not null
            && images.Scene.Extent.Width == drawn.Width
            && images.Scene.Extent.Height == drawn.Height
        )
        {
            return images;
        }

        var format = _swapchain.ImageFormat;
        var unorm = UpscalePass.Unorm(format);
        const ImageUsageFlags usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
        GpuImage? scene = null;
        ImageView sceneUnorm = default;
        GpuImage? edges = null;
        try
        {
            scene = _allocator.CreateImage(format, drawn, usage, ImageAspectFlags.ColorBit, "smaa scene", unorm);
            sceneUnorm = scene.CreateView(unorm, "smaa scene unorm");
            edges = _allocator.CreateImage(EdgesFormat, drawn, usage, ImageAspectFlags.ColorBit, "smaa edges");
            var weights = _allocator.CreateImage(
                WeightsFormat,
                drawn,
                usage,
                ImageAspectFlags.ColorBit,
                "smaa weights"
            );
            var made = new Images(_context, scene, sceneUnorm, edges, weights);
            _images.Replace(made, slot);
            if (images is null)
            {
                LogImages(drawn.Width, drawn.Height);
            }
            else
            {
                LogResized(drawn.Width, drawn.Height);
            }

            return made;
        }
        catch
        {
            if (sceneUnorm.Handle != 0)
            {
                _context.Vk.DestroyImageView(_context.Device, sceneUnorm, null);
            }

            edges?.Dispose();
            scene?.Dispose();
            throw;
        }
    }

    /// <summary>Points <paramref name="slot"/>'s three sets at <paramref name="images"/>. No submitted frame may use them.</summary>
    private void WriteDescriptorSets(int slot, Images images)
    {
        // The edges pass reads the scene's sRGB-encoded values; the weights pass the edges and the lookup textures; the
        // blend the scene in linear light, the weights, and (for its edges picture) the edges.
        var infos = stackalloc DescriptorImageInfo[7]
        {
            new(_sampler, images.SceneUnorm, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, images.Edges.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _area!.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, _search!.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, images.Scene.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, images.Weights.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, images.Edges.View, ImageLayout.ShaderReadOnlyOptimal),
        };
        var writes = stackalloc WriteDescriptorSet[3]
        {
            Write(_edgesSets[slot], &infos[0], 1),
            Write(_weightsSets[slot], &infos[1], 3),
            Write(_blendSets[slot], &infos[4], 3),
        };
        _context.Vk.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
    }

    private static WriteDescriptorSet Write(DescriptorSet set, DescriptorImageInfo* infos, uint count) =>
        new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = count,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = infos,
        };

    /// <summary>Earlier frames' reads of <paramref name="image"/> finish before this frame clears it.</summary>
    private void BeginTarget(CommandBuffer commandBuffer, Image image) =>
        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit
        );

    /// <summary>The pass's writes to <paramref name="image"/> finish before the next pass samples it.</summary>
    private void EndTarget(CommandBuffer commandBuffer, Image image) =>
        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit
        );

    private void Draw(
        CommandBuffer commandBuffer,
        ImageView target,
        Extent2D extent,
        Pipeline pipeline,
        PipelineLayout layout,
        DescriptorSet set,
        SmaaConstants* constants,
        bool clear
    )
    {
        var vk = _context.Vk;
        // The edges and weights images start at 0 each frame (SMAA writes only edge pixels); the blend covers its target.
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = clear ? AttachmentLoadOp.Clear : AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = default,
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
        vk.CmdPushConstants(
            commandBuffer,
            layout,
            ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(SmaaConstants),
            constants
        );
        vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        vk.CmdEndRendering(commandBuffer);
    }

    private void CreateSampler()
    {
        // SMAA: every sampler filters linearly and clamps. The weights pass reads two edges in one bilinear fetch.
        var info = new SamplerCreateInfo
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
        _context.Vk.CreateSampler(_context.Device, &info, null, out _sampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _sampler.Handle, "smaa");
    }

    /// <summary>A set layout of <paramref name="count"/> sampled images, bindings 0 on, read by the fragment shader.</summary>
    private DescriptorSetLayout CreateSetLayout(int count, string name)
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (var i = 0; i < count; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)count,
            PBindings = bindings,
        };
        _context
            .Vk.CreateDescriptorSetLayout(_context.Device, &info, null, out var layout)
            .ThrowIfFailed("vkCreateDescriptorSetLayout");
        _context.SetObjectName(ObjectType.DescriptorSetLayout, layout.Handle, name);
        return layout;
    }

    private PipelineLayout CreatePipelineLayout(DescriptorSetLayout setLayout, string name) =>
        PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            ShaderStageFlags.FragmentBit,
            (uint)sizeof(SmaaConstants),
            name
        );

    private void CreateDescriptorSets()
    {
        var vk = _context.Vk;
        var device = _context.Device;

        // Three sets (the edges', weights', and blend's) for each frame slot: 1 + 3 + 3 sampled images.
        const int SetCount = 3 * FrameSync.FramesInFlight;
        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 7 * FrameSync.FramesInFlight);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = SetCount,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        vk.CreateDescriptorPool(device, &poolInfo, null, out _pool).ThrowIfFailed("vkCreateDescriptorPool");

        var setLayouts = stackalloc DescriptorSetLayout[SetCount];
        for (var slot = 0; slot < FrameSync.FramesInFlight; slot++)
        {
            setLayouts[3 * slot] = _edgesSetLayout;
            setLayouts[3 * slot + 1] = _weightsSetLayout;
            setLayouts[3 * slot + 2] = _blendSetLayout;
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
            _edgesSets[slot] = sets[3 * slot];
            _weightsSets[slot] = sets[3 * slot + 1];
            _blendSets[slot] = sets[3 * slot + 2];
            _context.SetObjectName(ObjectType.DescriptorSet, _edgesSets[slot].Handle, "smaa edges");
            _context.SetObjectName(ObjectType.DescriptorSet, _weightsSets[slot].Handle, "smaa weights");
            _context.SetObjectName(ObjectType.DescriptorSet, _blendSets[slot].Handle, "smaa blend");
        }
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
        Message = "Anti-aliasing: SMAA 1x at {Width}x{Height} (edges, weights, blend)"
    )]
    private partial void LogImages(uint width, uint height);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Anti-aliasing: SMAA 1x at {Width}x{Height}")]
    private partial void LogResized(uint width, uint height);

    /// <summary>
    /// The images of one drawn size: the scene, its UNORM view that the edges pass reads, and the edges and weights.
    /// </summary>
    private sealed class Images(
        VulkanContext context,
        GpuImage scene,
        ImageView sceneUnorm,
        GpuImage edges,
        GpuImage weights
    ) : IDisposable
    {
        private bool _disposed;

        public GpuImage Scene { get; } = scene;

        public ImageView SceneUnorm { get; } = sceneUnorm;

        public GpuImage Edges { get; } = edges;

        public GpuImage Weights { get; } = weights;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (SceneUnorm.Handle != 0 && !context.IsAbandoned)
            {
                context.Vk.DestroyImageView(context.Device, SceneUnorm, null);
            }

            Scene.Dispose();
            Edges.Dispose();
            Weights.Dispose();
        }
    }
}
