using Itera.Engine.Diagnostics;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The raster pipeline's world pass (<c>Engine:Renderer:Pipeline</c> <c>Raster</c>; docs/rendering-redesign.md). It owns
/// the mesh heap, one device-local buffer of words that <see cref="WorldMeshes"/> fills through staging copies: the
/// materials and the tint map at fixed places at its start, then each block's vertices (<see cref="MeshVertex"/>) and
/// 16-bit indices in a range of its own; the heap is also the index buffer. It owns the frame's draws too, one region per
/// frame slot in host-visible buffers that the main thread writes: a <see cref="MeshDraw"/> per block drawn, four lists
/// of indexed indirect commands (the camera's and the near, far, and distant shadow cascades'; a command's first
/// instance names its draw), the cascades' record (<see cref="MeshLighting"/>), and the frame's look (<see cref="LookConstants"/>);
/// and the models' too, in the first three lists: a
/// <see cref="MeshModelDraw"/> per block of each model drawn, the draws of one block together, and one command per
/// block whose instances are those draws.
/// <para>
/// Each frame it draws the cascades into the layers of a depth map array, two, or three with
/// <c>Engine:Renderer:DistantShadowMetres</c> (<c>mesh-shadow.vert</c>, timed as
/// <see cref="GpuPass.ShadowMaps"/>), then the world: with <c>Engine:Renderer:MsaaSamples</c> above 1 into multisampled
/// colour and depth images of its own, resolved at the end into the target and the frame's depth image (sample 0),
/// else into those directly. Every block in view with one <c>vkCmdDrawIndexedIndirect</c> (<c>mesh.vert</c>,
/// <c>mesh.frag</c>, which reads the cascades through a comparison sampler), the models with another (the same shaders
/// compiled with <c>MESH_MODEL</c>), then the sky where nothing drew
/// (<c>world-sky.frag</c>, depth equal to the cleared 0 of the reverse form of <c>view.glsl</c>). Particles and debug
/// lines are drawn after it, tested against the resolved depth. Created before the render thread starts; recorded by
/// the render thread.
/// </para>
/// </summary>
public sealed unsafe partial class MeshPass : IDisposable, IReloadsShaders, IMeshDrawTarget
{
    /// <summary>Blocks one frame may draw in each list.</summary>
    public const int MaxDraws = 32_768;

    /// <summary>
    /// The models' and the mesh assets' command lists of a slot: the camera's, the near cascade's, the far cascade's.
    /// </summary>
    public const int Lists = 3;

    /// <summary>The world's command lists of a slot: those three, then the distant cascade's (only the terrain casts into it).</summary>
    public const int WorldLists = 4;

    /// <summary>The world's list of the distant cascade.</summary>
    public const int DistantList = 3;

    /// <summary>The distant cascade's layer of the shadow map (its push constant's cascade, <c>mesh-shadow.vert</c>).</summary>
    private const int DistantCascade = 2;

    /// <summary>Draws (<see cref="MeshDraw"/>) one frame slot holds: enough for every list full, each block apart.</summary>
    public const int MaxRecords = WorldLists * MaxDraws;

    /// <summary>Model draws (<see cref="MeshModelDraw"/>, one per block of a model drawn) one list of a slot holds.</summary>
    public const int MaxModelRecords = 65_536;

    /// <summary>Commands (one per block of a model level drawn) one model list of a slot holds.</summary>
    public const int MaxModelCommands = 16_384;

    /// <summary>
    /// Point lights a frame slot holds (<c>mesh-data.glsl</c>'s MESH_MOST_LIGHTS): the ceiling of the light budget
    /// (<c>Engine:Renderer:MostLights</c>).
    /// </summary>
    public const int MostLights = 256;

    /// <summary>Materials, one <see cref="MeshMaterial"/> each, at <see cref="MaterialsOffset"/> in the heap.</summary>
    public const int MaterialCount = 256;

    /// <summary>Words of the tint map: its header, then 256 x 256 texels four to a word.</summary>
    public const int TintMapWords = 4 + Voxels.IVoxelTintMap.Size * Voxels.IVoxelTintMap.Size / 4;

    /// <summary>Bytes in a granule of the heap (<see cref="GpuRangeHeap"/>).</summary>
    public const ulong GranuleBytes = GpuRangeHeap.GranuleWords * sizeof(uint);

    /// <summary>
    /// Byte offset of the materials in the heap: its second granule (the range allocator never hands out the first), so
    /// <see cref="WorldMeshes"/> takes it with the heap's first allocation.
    /// </summary>
    public const ulong MaterialsOffset = GranuleBytes;

    /// <summary>The cascades at most: near, far, and distant.</summary>
    private const int MostCascades = 3;

    /// <summary>The wireframe's depth bias, in the depth format's smallest steps at the face's depth.</summary>
    private const float WireBiasConstant = 64;

    /// <summary>The wireframe's depth bias as a share of the face's depth slope: more for faces seen edge-on.</summary>
    private const float WireBiasSlope = 1.5f;
    private const int Bindings = 18;
    private const int ShadowMapBinding = 5;
    private const int SkyFieldBinding = 8;

    /// <summary>The cascades' maps again, read as depths (the blocker search of contact-hardening shadows).</summary>
    private const int ShadowDepthBinding = 9;

    /// <summary>The frame's look, one <see cref="LookConstants"/> per slot (<c>look.glsl</c>), after the texture arrays.</summary>
    private const int LookBinding = 17;

    /// <summary>
    /// The view's clusters of lights (<c>mesh-data.glsl</c>'s MESH_CLUSTER_*): 16 x 9 tiles by 24 slices, each a count and
    /// up to 32 indices into the slot's lights, one slot's after another.
    /// </summary>
    internal const int ClusterCount = 16 * 9 * 24;

    internal const int ClusterLights = 32;

    internal const int ClusterSlotWords = ClusterCount * (1 + ClusterLights);

    /// <summary>Invocations in a workgroup of <c>mesh-clusters.comp</c>, one a cluster.</summary>
    private const int ClusterGroup = 64;
    private const string ModelDefine = "MESH_MODEL";
    private const string FadeDefine = "MESH_FADE";
    private const ShaderStageFlags Stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    /// <summary>
    /// The stages the set's bindings serve: the mesh shaders, and the particles' simulate pass, which reads the cascades,
    /// the lighting record, and the sky field (<see cref="ParticlePass"/>).
    /// </summary>
    private const ShaderStageFlags SetStages = Stages | ShaderStageFlags.ComputeBit;

    /// <summary>The stages that read the cascades and the sky field: <c>mesh.frag</c> and the particles' simulate pass.</summary>
    private const PipelineStageFlags2 LightReaders =
        PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
    private const PipelineStageFlags2 DepthTests =
        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly ShaderCompiler _shaders;
    private readonly GpuMemoryAllocator _allocator;
    private readonly StagingBuffer _staging;
    private readonly GpuTimer _timer;
    private readonly ILogger<MeshPass> _logger;
    private volatile int _requestedSamples;
    private readonly ulong _requestedHeapBytes;
    private readonly ImageView[] _cascadeViews = new ImageView[MostCascades];

    // The shadow map's layers: 2, or 3 with Engine:Renderer:DistantShadowMetres.
    private readonly int _cascades;
    private byte[] _skyVertexSpirv = [];
    private byte[] _skySpirv = [];
    private byte[] _meshVertexSpirv = [];
    private byte[] _meshFragmentSpirv = [];
    private byte[] _shadowVertexSpirv = [];
    private byte[] _depthOnlySpirv = [];
    private byte[] _modelVertexSpirv = [];
    private byte[] _modelFadeVertexSpirv = [];
    private byte[] _modelFadeFragmentSpirv = [];
    private byte[] _modelFadeWireSpirv = [];
    private byte[] _modelShadowVertexSpirv = [];
    private byte[] _wireSpirv = [];
    private byte[] _clustersSpirv = [];
    private GpuBuffer? _heap;
    private GpuBuffer? _draws;
    private GpuBuffer? _commands;
    private GpuBuffer? _lighting;
    private GpuBuffer? _modelDraws;
    private GpuBuffer? _modelCommands;
    private GpuBuffer? _lights;
    private GpuBuffer? _clusters;
    private GpuBuffer? _look;
    private GpuImage? _shadowMap;
    private GpuImage? _skyImage;
    private Sampler _skySampler;
    private bool _skyReady;
    private GpuImage? _msaaColor;
    private GpuImage? _msaaDepth;
    private Sampler _shadowSampler;
    private Sampler _shadowDepthSampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private PipelineLayout _shadowLayout;
    private PipelineLayout _clusterLayout;
    private Pipeline _clusterPipeline;
    private Pipeline _meshPipeline;
    private Pipeline _skyPipeline;
    private Pipeline _shadowPipeline;
    private Pipeline _modelPipeline;
    private Pipeline _modelFadePipeline;
    private Pipeline _modelShadowPipeline;
    private Pipeline _wirePipeline;
    private Pipeline _modelWirePipeline;
    private Pipeline _viewMeshPipeline;
    private Pipeline _viewModelPipeline;
    private Pipeline _viewSkyPipeline;
    private bool _viewPipelinesWanted;
    private SampleCountFlags _samples = SampleCountFlags.Count1Bit;
    private Format _format;
    private bool _disposed;

    public MeshPass(
        VulkanContext context,
        VulkanSwapchain swapchain,
        ShaderCompiler shaders,
        GpuMemoryAllocator allocator,
        StagingBuffer staging,
        GpuTimer timer,
        IOptions<EngineOptions> options,
        ILogger<MeshPass> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _shaders = shaders;
        _allocator = allocator;
        _staging = staging;
        _timer = timer;
        _logger = logger;
        _requestedSamples = options.Value.Renderer.MsaaSamples;
        _requestedHeapBytes = (ulong)options.Value.Renderer.MeshMegabytes << 20;
        ShadowMapTexels = (uint)options.Value.Renderer.ShadowMapTexels;
        _cascades = options.Value.Renderer.DistantShadowMetres > 0 ? MostCascades : MostCascades - 1;
        _bakeFolder = Path.Combine(options.Value.Application.DataDirectory(), "Bakes");
    }

    /// <summary>Bytes of the materials in the heap.</summary>
    public static ulong MaterialsBytes => (ulong)(MaterialCount * sizeof(MeshMaterial));

    /// <summary>Byte offset of the tint map in the heap: the granule after the materials.</summary>
    public static ulong TintMapOffset => MaterialsOffset + RoundUp(MaterialsBytes);

    /// <summary>Bytes of the tint map in the heap.</summary>
    public static ulong TintMapBytes => TintMapWords * sizeof(uint);

    /// <summary>Texels along each side of each cascade's depth map (<c>Engine:Renderer:ShadowMapTexels</c>).</summary>
    public uint ShadowMapTexels { get; }

    /// <summary>The heap's size in bytes, set by <see cref="Initialize"/>: a whole number of granules.</summary>
    public ulong HeapBytes { get; private set; }

    /// <summary>The layout of the pass's descriptor set, which <see cref="ParticlePass"/> binds as its set 1.</summary>
    internal DescriptorSetLayout SetLayout => _setLayout;

    /// <summary>The pass's descriptor set (<c>mesh-data.glsl</c>), which <see cref="ParticlePass"/> binds as its set 1.</summary>
    internal DescriptorSet Set => _set;

    /// <summary>True when the world is drawn with more than one sample a pixel, which smooths its edges without SMAA.</summary>
    public bool Multisampled => _samples != SampleCountFlags.Count1Bit;

    /// <summary>The samples a pixel the main rendering's attachments have, for pipelines that draw in it.</summary>
    internal SampleCountFlags Samples => _samples;

    /// <summary>The colour format the main rendering draws in, for pipelines that draw in it.</summary>
    internal Format ColorFormat => _format;

    /// <summary>
    /// The world's samples a pixel the player's setting asks for (1, 2, 4, or 8; <c>Engine:Renderer:MsaaSamples</c> until
    /// then): taken by <see cref="Initialize"/>, and after it by the next <see cref="OnSwapchainRecreated"/>, which the
    /// frame renderer asks for. Any thread.
    /// </summary>
    public void RequestSamples(int samples) => _requestedSamples = samples;

    /// <summary>
    /// Creates the buffers, the shadow maps and their sampler, the multisampled images, the descriptor set, and the
    /// pipelines. After the staging buffer and the GPU timer.
    /// </summary>
    public void Initialize()
    {
        HeapBytes = ChooseHeapBytes();
        _samples = ChooseSamples();
        _heap = _allocator.CreateBuffer(
            HeapBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "mesh heap"
        );
        var slots = (ulong)FrameSync.FramesInFlight;
        _draws = _allocator.CreateBuffer(
            slots * MaxRecords * (ulong)sizeof(MeshDraw),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "mesh draws"
        );
        _commands = _allocator.CreateBuffer(
            slots * WorldLists * MaxDraws * (ulong)sizeof(DrawIndexedIndirectCommand),
            BufferUsageFlags.IndirectBufferBit,
            MemoryUsage.Stream,
            "mesh draw commands"
        );
        _lighting = _allocator.CreateBuffer(
            slots * (ulong)sizeof(MeshLighting),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "mesh lighting"
        );
        _modelDraws = _allocator.CreateBuffer(
            slots * Lists * MaxModelRecords * (ulong)sizeof(MeshModelDraw),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "mesh model draws"
        );
        _modelCommands = _allocator.CreateBuffer(
            slots * Lists * MaxModelCommands * (ulong)sizeof(DrawIndexedIndirectCommand),
            BufferUsageFlags.IndirectBufferBit,
            MemoryUsage.Stream,
            "mesh model commands"
        );
        _lights = _allocator.CreateBuffer(
            slots * MostLights * (ulong)sizeof(MeshLight),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "mesh lights"
        );
        _clusters = _allocator.CreateBuffer(
            slots * ClusterSlotWords * sizeof(uint),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.DeviceLocal,
            "mesh light clusters"
        );
        _look = _allocator.CreateBuffer(
            slots * (ulong)sizeof(LookConstants),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "look"
        );
        _shadowMap = _allocator.CreateImage(
            DeviceRequirements.DepthFormat,
            new Extent2D(ShadowMapTexels, ShadowMapTexels),
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.DepthBit,
            "shadow cascades",
            layers: (uint)_cascades
        );
        for (var i = 0; i < _cascades; i++)
        {
            _cascadeViews[i] = _shadowMap.CreateLayerView((uint)i, ImageAspectFlags.DepthBit, $"shadow cascade {i}");
        }

        // The sky field's cells, x by y by z (SkyField's order), as one byte of 0 to 1 each.
        _skyImage = _allocator.CreateImage(
            Format.R8Unorm,
            new Extent2D(SkyField.SizeXZ, SkyField.SizeY),
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit,
            "sky field",
            depth: SkyField.SizeXZ
        );
        CreateShadowSampler();
        CreateSkySampler();
        CreateTextures();
        CreateDescriptors();
        var setLayout = _setLayout;
        _layout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            Stages,
            (uint)sizeof(ViewConstants),
            "mesh layout"
        );
        _shadowLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            ShaderStageFlags.VertexBit,
            (uint)sizeof(MeshShadowConstants),
            "mesh shadow layout"
        );
        _clusterLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            ShaderStageFlags.ComputeBit,
            (uint)sizeof(ViewConstants),
            "mesh cluster layout"
        );
        TakeShaders((byte[][])CompileShaders());
        CreatePipelines(_swapchain.ImageFormat);
        CreateTargets();
        LogCreated(HeapBytes >> 20, MaxDraws, (int)_samples, _heap.MemoryTypeIndex);
    }

    /// <summary>
    /// Makes new pipelines when the swapchain's format or the samples asked for (<see cref="RequestSamples"/>) changed, and
    /// new multisampled images when the samples, or the swapchain's size or format, did. Render thread, device idle.
    /// </summary>
    public void OnSwapchainRecreated()
    {
        var samples = ChooseSamples();
        var samplesChanged = samples != _samples;
        if (_swapchain.ImageFormat != _format || samplesChanged)
        {
            _samples = samples;
            DestroyPipelines();
            CreatePipelines(_swapchain.ImageFormat);
        }

        var extent = _swapchain.Extent;
        if (
            samplesChanged
            || (
                Multisampled
                && (
                    _msaaColor is null
                    || _msaaColor.Extent.Width != extent.Width
                    || _msaaColor.Extent.Height != extent.Height
                    || _msaaColor.Format != _swapchain.ImageFormat
                )
            )
        )
        {
            DestroyTargets();
            CreateTargets();
        }

        if (samplesChanged)
        {
            LogSamplesChanged((int)_samples);
        }
    }

    /// <summary>
    /// The draws of <paramref name="slot"/>, which the main thread writes between taking that slot's frame request and
    /// publishing the frame: write only, in address order.
    /// </summary>
    public Span<MeshDraw> DrawsOf(int slot) =>
        new(Draws.Mapped + (ulong)slot * MaxRecords * (ulong)sizeof(MeshDraw), MaxRecords);

    /// <summary>
    /// The indirect commands of list <paramref name="list"/> (0 the camera's, 1 to 3 the cascades') of
    /// <paramref name="slot"/>, written as <see cref="DrawsOf"/> is.
    /// </summary>
    public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) =>
        new(Commands.Mapped + CommandsOffset(slot, list), MaxDraws);

    /// <summary>The point lights of <paramref name="slot"/>, written as <see cref="DrawsOf"/> is.</summary>
    public Span<MeshLight> LightsOf(int slot) =>
        new(Lights.Mapped + (ulong)slot * MostLights * (ulong)sizeof(MeshLight), MostLights);

    /// <summary>
    /// The model draws of list <paramref name="list"/> of <paramref name="slot"/>, written as <see cref="DrawsOf"/> is; a
    /// model command's first instance is <see cref="ModelRecordBase"/> plus its first draw's place here.
    /// </summary>
    public Span<MeshModelDraw> ModelDrawsOf(int slot, int list) =>
        new(ModelDraws.Mapped + (ulong)ModelRecordBase(slot, list) * (ulong)sizeof(MeshModelDraw), MaxModelRecords);

    /// <summary>The model commands of list <paramref name="list"/> of <paramref name="slot"/>, written as <see cref="DrawsOf"/> is.</summary>
    public Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list) =>
        new(ModelCommands.Mapped + ModelCommandsOffset(slot, list), MaxModelCommands);

    /// <summary>The index in the shaders' model draws of list <paramref name="list"/> of <paramref name="slot"/>'s first.</summary>
    public static int ModelRecordBase(int slot, int list) => (slot * Lists + list) * MaxModelRecords;

    /// <summary>The cascades' record of <paramref name="slot"/>, written as <see cref="DrawsOf"/> is.</summary>
    public ref MeshLighting LightingOf(int slot) =>
        ref *(MeshLighting*)(Lighting.Mapped + (ulong)slot * (ulong)sizeof(MeshLighting));

    /// <summary>The look of <paramref name="slot"/> (<see cref="RenderLook"/>), written as <see cref="DrawsOf"/> is.</summary>
    public ref LookConstants LookOf(int slot) =>
        ref *(LookConstants*)(Look.Mapped + (ulong)slot * (ulong)sizeof(LookConstants));

    /// <summary>The looks of every slot, which the particles' draws bind too (<see cref="ParticlePass"/>).</summary>
    internal GpuBuffer LookBuffer => Look;

    /// <summary>
    /// Records the frame's copies from the staging region of the slot into the heap, between two barriers: the first
    /// waits for earlier frames' reads of the heap (as vertices, indices, materials, and the tint map), so ranges freed
    /// and taken again are safe to overwrite; the second makes the copies visible to this frame's draws, and to a game's
    /// compute pass that reads the camera's blocks (<see cref="WorldMeshes.CameraCommands"/>). Outside rendering. Render
    /// thread.
    /// </summary>
    public void RecordUploads(CommandBuffer commandBuffer, ReadOnlySpan<BufferCopy> regions)
    {
        if (regions.IsEmpty)
        {
            return;
        }

        const PipelineStageFlags2 readers =
            PipelineStageFlags2.ComputeShaderBit
            | PipelineStageFlags2.IndexInputBit
            | PipelineStageFlags2.VertexShaderBit
            | PipelineStageFlags2.FragmentShaderBit;
        Barrier(
            commandBuffer,
            readers | PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit
        );
        fixed (BufferCopy* pointer = regions)
        {
            _context.Vk.CmdCopyBuffer(
                commandBuffer,
                _staging.Buffer.Buffer,
                Heap.Buffer,
                (uint)regions.Length,
                pointer
            );
        }

        Barrier(
            commandBuffer,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            readers,
            AccessFlags2.IndexReadBit | AccessFlags2.ShaderStorageReadBit
        );
    }

    /// <summary>
    /// Records the shadow cascades, then the world into <paramref name="target"/>, which it covers completely: the blocks
    /// the main thread wrote for <paramref name="slot"/> (<paramref name="frame"/> counts them), then the sky. Writes
    /// <paramref name="depth"/>, which must be in the depth attachment layout (<see cref="DepthTarget.RecordFrameStart"/>).
    /// Times the cascades with the <see cref="GpuTimer"/> queries of <paramref name="timerSlot"/>. What else draws in its
    /// renderings (<paramref name="draws"/>: the mesh assets) draws into each cascade after the world's and the models'
    /// shadows, and in the main view after the models. Render thread.
    /// </summary>
    public void Record(
        CommandBuffer commandBuffer,
        ImageView target,
        ImageView depth,
        Extent2D extent,
        in ViewConstants view,
        int slot,
        in MeshFrame frame,
        int timerSlot,
        IMeshPassDraws? draws = null,
        RenderPasses? passes = null
    )
    {
        var vk = _context.Vk;
        FlushSlot(slot, in frame);
        RecordTextures(commandBuffer);
        RecordBake(commandBuffer, slot);
        RecordClusters(commandBuffer, in view, _set);
        draws?.RecordSkinning(commandBuffer, slot, timerSlot);
        passes?.Record(RenderStage.Compute, commandBuffer, in view, extent);

        _timer.Begin(commandBuffer, timerSlot, GpuPass.ShadowMaps);
        RecordCascades(commandBuffer, slot, in frame, draws);
        _timer.End(commandBuffer, timerSlot, GpuPass.ShadowMaps);

        // The image size the shaders find a pixel's cluster of lights by (mesh.frag's pointLight) is the image drawn to.
        var constants = view;
        constants.Width = extent.Width;
        constants.Height = extent.Height;
        var multisampled = Multisampled;
        if (multisampled)
        {
            // The last frame's writes to the multisampled images finish before this frame's; their contents go.
            ImageBarrier(
                commandBuffer,
                MsaaColor.Image,
                ImageAspectFlags.ColorBit,
                1,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit,
                ImageLayout.ColorAttachmentOptimal
            );
            ImageBarrier(
                commandBuffer,
                MsaaDepth.Image,
                ImageAspectFlags.DepthBit,
                1,
                // The last frame's resolve read the samples at the colour output stage.
                DepthTests | PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                DepthTests,
                AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
                ImageLayout.DepthAttachmentOptimal
            );
        }

        // Multisampled, the samples are resolved into the target (their mean) and the depth image (sample 0, the depth
        // the particles and lines test against) as rendering ends, and are not kept.
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = multisampled ? MsaaColor.View : target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = multisampled ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
            ResolveMode = multisampled ? ResolveModeFlags.AverageBit : ResolveModeFlags.None,
            ResolveImageView = multisampled ? target : default,
            ResolveImageLayout = ImageLayout.ColorAttachmentOptimal,
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = multisampled ? MsaaDepth.View : depth,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = multisampled ? AttachmentStoreOp.DontCare : AttachmentStoreOp.Store,
            ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(0, 0) },
            ResolveMode = multisampled ? ResolveModeFlags.SampleZeroBit : ResolveModeFlags.None,
            ResolveImageView = multisampled ? depth : default,
            ResolveImageLayout = ImageLayout.DepthAttachmentOptimal,
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
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(commandBuffer, _layout, Stages, 0, (uint)sizeof(ViewConstants), &constants);
        var assets = draws;
        var worldDraws = Math.Clamp(frame.Draws, 0, MaxDraws);
        var models = Math.Clamp(frame.ModelCamera.Commands, 0, MaxModelCommands);
        var fading = Math.Clamp(frame.ModelCamera.Faded, 0, models);
        vk.CmdBindIndexBuffer(commandBuffer, Heap.Buffer, 0, IndexType.Uint16);
        // The models shown whole before the ground, so the ground behind them (a wall, a building) fails the depth test
        // before it is shaded; their time is part of the world's, their shadows part of the shadow maps'.
        if (models > fading)
        {
            _timer.Begin(commandBuffer, timerSlot, GpuPass.Models);
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _modelPipeline);
            vk.CmdDrawIndexedIndirect(
                commandBuffer,
                ModelCommands.Buffer,
                ModelCommandsOffset(slot, 0),
                (uint)(models - fading),
                (uint)sizeof(DrawIndexedIndirectCommand)
            );
            _timer.End(commandBuffer, timerSlot, GpuPass.Models);
        }

        if (worldDraws > 0)
        {
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _meshPipeline);
            vk.CmdDrawIndexedIndirect(
                commandBuffer,
                Commands.Buffer,
                CommandsOffset(slot, 0),
                (uint)worldDraws,
                (uint)sizeof(DrawIndexedIndirectCommand)
            );
        }

        // Those that fade after the ground: their shader may discard, so only what is drawn before can reject them.
        if (fading > 0)
        {
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _modelFadePipeline);
            vk.CmdDrawIndexedIndirect(
                commandBuffer,
                ModelCommands.Buffer,
                ModelCommandsOffset(slot, 0) + (ulong)((models - fading) * sizeof(DrawIndexedIndirectCommand)),
                (uint)fading,
                (uint)sizeof(DrawIndexedIndirectCommand)
            );
        }

        assets?.RecordCamera(commandBuffer, slot, in constants, timerSlot);
        passes?.Record(RenderStage.Opaque, commandBuffer, in constants, extent);
        if (assets is not null || passes?.Has(RenderStage.Opaque) == true)
        {
            // The wireframe and the sky with this pass's set and constants again, whatever the draws bound.
            vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
            vk.CmdPushConstants(commandBuffer, _layout, Stages, 0, (uint)sizeof(ViewConstants), &constants);
        }

        if (passes?.Has(RenderStage.Opaque) == true)
        {
            // And the view's viewport and scissor, which a game's pass may have changed.
            vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
            vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        }

        if (view.Has(ViewSwitches.Wireframe) && _wirePipeline.Handle != 0)
        {
            RecordWireframe(commandBuffer, slot, worldDraws, models);
        }

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _skyPipeline);
        vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        // Blended surfaces over everything drawn, the sky included, which would paint over them if it came after.
        assets?.RecordTransparent(commandBuffer, slot, in constants);
        passes?.Record(RenderStage.Transparent, commandBuffer, in constants, extent);
        vk.CmdEndRendering(commandBuffer);
    }

    /// <summary>
    /// Sorts the slot's lights into the view's clusters (<c>mesh-clusters.comp</c>, one invocation a cluster), for the
    /// world's fragments to read: the slot's region of the cluster buffer, which the slot's last frame read before its
    /// fence.
    /// </summary>
    private void RecordClusters(CommandBuffer commandBuffer, in ViewConstants view, DescriptorSet set)
    {
        var vk = _context.Vk;
        var constants = view;
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _clusterPipeline);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _clusterLayout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(
            commandBuffer,
            _clusterLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)sizeof(ViewConstants),
            &constants
        );
        vk.CmdDispatch(commandBuffer, (ClusterCount + ClusterGroup - 1) / ClusterGroup, 1, 1);
        Barrier(
            commandBuffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit
        );
    }

    /// <summary>The wireframe debug view: the camera's world and model commands again, as lines over what they drew.</summary>
    private void RecordWireframe(CommandBuffer commandBuffer, int slot, int draws, int models)
    {
        var vk = _context.Vk;
        vk.CmdBindIndexBuffer(commandBuffer, Heap.Buffer, 0, IndexType.Uint16);
        if (draws > 0)
        {
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _wirePipeline);
            vk.CmdDrawIndexedIndirect(
                commandBuffer,
                Commands.Buffer,
                CommandsOffset(slot, 0),
                (uint)draws,
                (uint)sizeof(DrawIndexedIndirectCommand)
            );
        }

        if (models > 0)
        {
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _modelWirePipeline);
            vk.CmdDrawIndexedIndirect(
                commandBuffer,
                ModelCommands.Buffer,
                ModelCommandsOffset(slot, 0),
                (uint)models,
                (uint)sizeof(DrawIndexedIndirectCommand)
            );
        }
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
            DestroyPipelines();
            if (_layout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _layout, null);
            }

            if (_shadowLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _shadowLayout, null);
            }

            if (_clusterLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _clusterLayout, null);
            }

            // Destroying the pool frees the set allocated from it.
            if (_descriptorPool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device, _descriptorPool, null);
            }

            if (_setLayout.Handle != 0)
            {
                vk.DestroyDescriptorSetLayout(device, _setLayout, null);
            }

            if (_shadowSampler.Handle != 0)
            {
                vk.DestroySampler(device, _shadowSampler, null);
            }

            if (_shadowDepthSampler.Handle != 0)
            {
                vk.DestroySampler(device, _shadowDepthSampler, null);
            }

            if (_skySampler.Handle != 0)
            {
                vk.DestroySampler(device, _skySampler, null);
            }

            foreach (var view in _cascadeViews)
            {
                if (view.Handle != 0)
                {
                    vk.DestroyImageView(device, view, null);
                }
            }
        }

        Array.Clear(_cascadeViews);
        _layout = default;
        _shadowLayout = default;
        _clusterLayout = default;
        _descriptorPool = default;
        _setLayout = default;
        _shadowSampler = default;
        _skySampler = default;
        DestroyTargets();
        DestroyTextures();
        DestroyBake();
        _shadowMap?.Dispose();
        _skyImage?.Dispose();
        _skyImage = null;
        _lighting?.Dispose();
        _modelDraws?.Dispose();
        _modelCommands?.Dispose();
        _lights?.Dispose();
        _clusters?.Dispose();
        _look?.Dispose();
        _commands?.Dispose();
        _draws?.Dispose();
        _heap?.Dispose();
        _shadowMap = null;
        _lighting = null;
        _modelDraws = null;
        _modelCommands = null;
        _lights = null;
        _clusters = null;
        _look = null;
        _commands = null;
        _draws = null;
        _heap = null;
    }

    public object CompileShaders() =>
        new[]
        {
            _shaders.Compile("fullscreen.vert"),
            _shaders.Compile("world-sky.frag"),
            _shaders.Compile("mesh.vert"),
            _shaders.Compile("mesh.frag"),
            _shaders.Compile("mesh-shadow.vert"),
            _shaders.Compile("depth-only.frag"),
            _shaders.Compile("mesh.vert", [ModelDefine]),
            _shaders.Compile("mesh-shadow.vert", [ModelDefine]),
            _shaders.Compile("mesh-wire.frag"),
            _shaders.Compile("mesh-clusters.comp"),
            // Only a bake asks for this one (EnsureBake); a reload compiles it again once a bake has.
            _bakeSpirv.Length > 0
                ? _shaders.Compile("material-bake.comp")
                : null,
            _shaders.Compile("mesh.vert", [ModelDefine, FadeDefine]),
            _shaders.Compile("mesh.frag", [FadeDefine]),
            _shaders.Compile("mesh-wire.frag", [FadeDefine]),
        };

    public int ReplacePipelines(object compiled)
    {
        DestroyPipelines();
        TakeShaders((byte[][])compiled);
        CreatePipelines(_format);
        ReplaceBakePipeline();
        return (_wirePipeline.Handle != 0 ? 9 : 7) + (_viewSkyPipeline.Handle != 0 ? 3 : 0);
    }

    private GpuBuffer Heap => _heap ?? throw NotInitialised();

    private GpuBuffer Draws => _draws ?? throw NotInitialised();

    private GpuBuffer Commands => _commands ?? throw NotInitialised();

    private GpuBuffer Lighting => _lighting ?? throw NotInitialised();

    private GpuBuffer ModelDraws => _modelDraws ?? throw NotInitialised();

    private GpuBuffer ModelCommands => _modelCommands ?? throw NotInitialised();

    private GpuBuffer Lights => _lights ?? throw NotInitialised();

    private GpuBuffer Clusters => _clusters ?? throw NotInitialised();

    private GpuBuffer Look => _look ?? throw NotInitialised();

    private GpuImage ShadowMap => _shadowMap ?? throw NotInitialised();

    private GpuImage SkyImage => _skyImage ?? throw NotInitialised();

    private GpuImage MsaaColor => _msaaColor ?? throw NotInitialised();

    private GpuImage MsaaDepth => _msaaDepth ?? throw NotInitialised();

    private static InvalidOperationException NotInitialised() => new("The mesh pass has not been initialised.");

    private static ulong RoundUp(ulong bytes) => (bytes + GranuleBytes - 1) / GranuleBytes * GranuleBytes;

    private static ulong CommandsOffset(int slot, int list) =>
        ((ulong)slot * WorldLists + (ulong)list) * MaxDraws * (ulong)sizeof(DrawIndexedIndirectCommand);

    private static ulong ModelCommandsOffset(int slot, int list) =>
        ((ulong)slot * Lists + (ulong)list) * MaxModelCommands * (ulong)sizeof(DrawIndexedIndirectCommand);

    /// <summary>
    /// Makes what the main thread wrote into <paramref name="slot"/> for this frame visible to the GPU, on memory that
    /// is not coherent.
    /// </summary>
    private void FlushSlot(int slot, in MeshFrame frame)
    {
        var records = Math.Clamp(frame.Records, 0, MaxRecords);
        if (records > 0)
        {
            Draws.Flush((ulong)slot * MaxRecords * (ulong)sizeof(MeshDraw), (ulong)records * (ulong)sizeof(MeshDraw));
        }

        FlushCommands(slot, 0, frame.Draws);
        FlushCommands(slot, 1, frame.NearShadows);
        FlushCommands(slot, 2, frame.FarShadows);
        FlushCommands(slot, DistantList, frame.DistantShadows);
        Lighting.Flush((ulong)slot * (ulong)sizeof(MeshLighting), (ulong)sizeof(MeshLighting));
        Look.Flush((ulong)slot * (ulong)sizeof(LookConstants), (ulong)sizeof(LookConstants));
        Lights.Flush((ulong)slot * MostLights * (ulong)sizeof(MeshLight), MostLights * (ulong)sizeof(MeshLight));
        for (var list = 0; list < Lists; list++)
        {
            var models = frame.ModelList(list);
            var modelRecords = Math.Clamp(models.Records, 0, MaxModelRecords);
            if (modelRecords > 0)
            {
                ModelDraws.Flush(
                    (ulong)ModelRecordBase(slot, list) * (ulong)sizeof(MeshModelDraw),
                    (ulong)modelRecords * (ulong)sizeof(MeshModelDraw)
                );
            }

            var commands = Math.Clamp(models.Commands, 0, MaxModelCommands);
            if (commands > 0)
            {
                ModelCommands.Flush(
                    ModelCommandsOffset(slot, list),
                    (ulong)commands * (ulong)sizeof(DrawIndexedIndirectCommand)
                );
            }
        }
    }

    private void FlushCommands(int slot, int list, int count)
    {
        count = Math.Clamp(count, 0, MaxDraws);
        if (count > 0)
        {
            Commands.Flush(CommandsOffset(slot, list), (ulong)count * (ulong)sizeof(DrawIndexedIndirectCommand));
        }
    }

    /// <summary>
    /// Copies a new sky field from the slot's staging region into its image, after the frames before have read it, and
    /// leaves it in the layout <c>mesh.frag</c> and the particles' simulate pass read it in; the first frame without one clears it to open sky. With the
    /// frame's other copies, outside rendering, for every fresh frame (a field staged under the loading screen must
    /// reach the image as the record naming it does). Render thread.
    /// </summary>
    public void RecordSkyField(CommandBuffer commandBuffer, in MeshFrame frame)
    {
        if (!frame.SkyUploaded && _skyReady)
        {
            return;
        }

        ImageBarrier(
            commandBuffer,
            SkyImage.Image,
            ImageAspectFlags.ColorBit,
            1,
            LightReaders,
            AccessFlags2.None,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit,
            ImageLayout.TransferDstOptimal
        );
        if (frame.SkyUploaded)
        {
            var region = new BufferImageCopy
            {
                BufferOffset = frame.SkyUploadOffset,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(SkyField.SizeXZ, SkyField.SizeY, SkyField.SizeXZ),
            };
            _context.Vk.CmdCopyBufferToImage(
                commandBuffer,
                _staging.Buffer.Buffer,
                SkyImage.Image,
                ImageLayout.TransferDstOptimal,
                1,
                &region
            );
        }
        else
        {
            var open = new ClearColorValue(1f, 1f, 1f, 1f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _context.Vk.CmdClearColorImage(
                commandBuffer,
                SkyImage.Image,
                ImageLayout.TransferDstOptimal,
                &open,
                1,
                &range
            );
        }

        ImageBarrier(
            commandBuffer,
            SkyImage.Image,
            ImageAspectFlags.ColorBit,
            1,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit,
            LightReaders,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferDstOptimal
        );
        _skyReady = true;
    }

    /// <summary>
    /// Draws each cascade's list into its layer of the shadow map, cleared to 1 (farthest from the sun), then leaves the
    /// map in the layout <c>mesh.frag</c> and the particles' simulate pass read it in. With shadows off the lists are empty and the layers only cleared:
    /// <c>mesh.frag</c> does not read the map then. The distant cascade's layer is drawn only in a frame that draws it
    /// (<see cref="MeshFrame.Distant"/>; its record's strength is 0 otherwise, and nothing reads the layer), from the
    /// world's list alone.
    /// </summary>
    private void RecordCascades(CommandBuffer commandBuffer, int slot, in MeshFrame frame, IMeshPassDraws? draws)
    {
        var vk = _context.Vk;
        // The last frame's lookups finish before this frame's draws; the old contents go.
        ImageBarrier(
            commandBuffer,
            ShadowMap.Image,
            ImageAspectFlags.DepthBit,
            (uint)_cascades,
            LightReaders,
            AccessFlags2.None,
            DepthTests,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            ImageLayout.DepthAttachmentOptimal
        );
        // A positive height: clip y -1 is the map's first row, as mesh.frag reads it (uv = clip.xy * 0.5 + 0.5).
        var viewport = new Viewport(0, 0, ShadowMapTexels, ShadowMapTexels, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(ShadowMapTexels, ShadowMapTexels));
        var set = _set;
        for (var cascade = 0; cascade < _cascades; cascade++)
        {
            var distant = cascade == DistantCascade;
            if (distant && !frame.Distant)
            {
                continue;
            }

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _cascadeViews[cascade],
                ImageLayout = ImageLayout.DepthAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(1, 0) },
            };
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = scissor,
                LayerCount = 1,
                PDepthAttachment = &depthAttachment,
            };
            vk.CmdBeginRendering(commandBuffer, &renderingInfo);
            var count = Math.Clamp(
                cascade switch
                {
                    0 => frame.NearShadows,
                    1 => frame.FarShadows,
                    _ => frame.DistantShadows,
                },
                0,
                MaxDraws
            );
            var models = distant ? 0 : Math.Clamp(frame.ModelList(1 + cascade).Commands, 0, MaxModelCommands);
            if (count + models > 0)
            {
                var constants = new MeshShadowConstants { Slot = (uint)slot, Cascade = (uint)cascade };
                vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
                vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
                vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _shadowLayout, 0, 1, &set, 0, null);
                vk.CmdPushConstants(
                    commandBuffer,
                    _shadowLayout,
                    ShaderStageFlags.VertexBit,
                    0,
                    (uint)sizeof(MeshShadowConstants),
                    &constants
                );
                vk.CmdBindIndexBuffer(commandBuffer, Heap.Buffer, 0, IndexType.Uint16);
            }

            if (count > 0)
            {
                vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _shadowPipeline);
                vk.CmdDrawIndexedIndirect(
                    commandBuffer,
                    Commands.Buffer,
                    CommandsOffset(slot, 1 + cascade),
                    (uint)count,
                    (uint)sizeof(DrawIndexedIndirectCommand)
                );
            }

            if (models > 0)
            {
                vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _modelShadowPipeline);
                vk.CmdDrawIndexedIndirect(
                    commandBuffer,
                    ModelCommands.Buffer,
                    ModelCommandsOffset(slot, 1 + cascade),
                    (uint)models,
                    (uint)sizeof(DrawIndexedIndirectCommand)
                );
            }

            // The mesh assets cast into the near and far cascades only.
            if (!distant)
            {
                draws?.RecordShadows(commandBuffer, slot, cascade);
            }

            vk.CmdEndRendering(commandBuffer);
        }

        ImageBarrier(
            commandBuffer,
            ShadowMap.Image,
            ImageAspectFlags.DepthBit,
            (uint)_cascades,
            DepthTests,
            AccessFlags2.DepthStencilAttachmentWriteBit,
            LightReaders,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.DepthAttachmentOptimal
        );
    }

    /// <summary>
    /// The heap's size: <c>Engine:Renderer:MeshMegabytes</c> within the device's storage buffer range, its largest
    /// buffer, and half its largest device-local memory heap, in whole granules.
    /// </summary>
    private ulong ChooseHeapBytes()
    {
        var bytes = Math.Min(_requestedHeapBytes, _context.Limits.MaxStorageBufferRange);
        bytes = Math.Min(bytes, _context.MaxBufferSize);
        var memory = _context.MemoryProperties;
        ulong largest = 0;
        for (var i = 0; i < memory.MemoryHeapCount; i++)
        {
            if ((memory.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
            {
                largest = Math.Max(largest, memory.MemoryHeaps[i].Size);
            }
        }

        bytes = Math.Min(bytes, largest / 2);
        return bytes / GranuleBytes * GranuleBytes;
    }

    /// <summary>
    /// The most samples a pixel, up to <c>Engine:Renderer:MsaaSamples</c>, that the device offers for colour and depth
    /// attachments together.
    /// </summary>
    private SampleCountFlags ChooseSamples()
    {
        var limits = _context.Limits;
        var offered = limits.FramebufferColorSampleCounts & limits.FramebufferDepthSampleCounts;
        for (var samples = _requestedSamples; samples > 1; samples >>= 1)
        {
            if ((offered & (SampleCountFlags)samples) != 0)
            {
                return (SampleCountFlags)samples;
            }
        }

        return SampleCountFlags.Count1Bit;
    }

    /// <summary>
    /// The multisampled colour and depth images, at the swapchain's size and in its format, when the world is
    /// multisampled. A render scale below 1 uses their top-left part, as it does the depth image's.
    /// </summary>
    private void CreateTargets()
    {
        var extent = _swapchain.Extent;
        if (!Multisampled || extent.Width == 0 || extent.Height == 0)
        {
            return;
        }

        _msaaColor = _allocator.CreateImage(
            _swapchain.ImageFormat,
            extent,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
            ImageAspectFlags.ColorBit,
            "mesh colour samples",
            samples: _samples
        );
        // Sampled, though no shader reads it: RADV keeps depth compressed for the resolve only in an image a shader could
        // read ("TC-compatible" metadata), and otherwise expands every sample first (0.15 ms at 1440p with 4 samples).
        // Where the device cannot sample depth at this count, a plain transient image.
        var sampled = (_context.Limits.SampledImageDepthSampleCounts & _samples) != 0;
        _msaaDepth = _allocator.CreateImage(
            DeviceRequirements.DepthFormat,
            extent,
            ImageUsageFlags.DepthStencilAttachmentBit
                | (sampled ? ImageUsageFlags.SampledBit : ImageUsageFlags.TransientAttachmentBit),
            ImageAspectFlags.DepthBit,
            "mesh depth samples",
            samples: _samples
        );
    }

    private void DestroyTargets()
    {
        _msaaColor?.Dispose();
        _msaaDepth?.Dispose();
        _msaaColor = null;
        _msaaDepth = null;
    }

    /// <summary>Takes the compiled shaders. The bake's (index 10) is null until a bake has compiled it.</summary>
    private void TakeShaders(byte[][] compiled)
    {
        _skyVertexSpirv = compiled[0];
        _skySpirv = compiled[1];
        _meshVertexSpirv = compiled[2];
        _meshFragmentSpirv = compiled[3];
        _shadowVertexSpirv = compiled[4];
        _depthOnlySpirv = compiled[5];
        _modelVertexSpirv = compiled[6];
        _modelShadowVertexSpirv = compiled[7];
        _wireSpirv = compiled[8];
        _clustersSpirv = compiled[9];
        _bakeSpirv = compiled[10] ?? _bakeSpirv;
        _modelFadeVertexSpirv = compiled[11];
        _modelFadeFragmentSpirv = compiled[12];
        _modelFadeWireSpirv = compiled[13];
    }

    private void CreatePipelines(Format format)
    {
        _clusterPipeline = PipelineFactory.CreateCompute(_context, _clustersSpirv, _clusterLayout, "mesh clusters");
        // Reverse depth: nearer is greater. The meshes test and write it; front faces wind counter-clockwise, as the
        // mesher makes them seen from the air.
        var meshDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Greater, Write: true);
        _meshPipeline = PipelineFactory.CreateTriangles(
            _context,
            _meshVertexSpirv,
            _meshFragmentSpirv,
            _layout,
            format,
            CullModeFlags.BackBit,
            in meshDepth,
            "world meshes",
            samples: _samples
        );
        // The sky lies at depth 0, the cleared value: it covers only what no block drew.
        var skyDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Equal, Write: false);
        _skyPipeline = PipelineFactory.CreateFullscreen(
            _context,
            _skyVertexSpirv,
            _skySpirv,
            _layout,
            format,
            in skyDepth,
            "world sky",
            samples: _samples
        );
        // The cascades: nearer the sun is less. Only the faces turned away from the sun are drawn, so a lit surface
        // never meets its own depth in the map (the meshes are closed: a ray from a shaded point to the sun enters the
        // solid through such a face). The cascade's axes have the camera's handedness, but its viewport is not flipped,
        // so those faces wind as front faces do in the main pass: the culled "back" faces are the ones facing the sun.
        var shadowDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Less, Write: true);
        _shadowPipeline = PipelineFactory.CreateTriangles(
            _context,
            _shadowVertexSpirv,
            _depthOnlySpirv,
            _shadowLayout,
            Format.Undefined,
            CullModeFlags.BackBit,
            in shadowDepth,
            "shadow cascades"
        );
        // The models as the world's blocks, each instance placed and turned by its own draw; those that fade (the end of
        // the camera's list) with shaders that draw their share of the pixels alone.
        _modelPipeline = PipelineFactory.CreateTriangles(
            _context,
            _modelVertexSpirv,
            _meshFragmentSpirv,
            _layout,
            format,
            CullModeFlags.BackBit,
            in meshDepth,
            "model meshes",
            samples: _samples
        );
        _modelFadePipeline = PipelineFactory.CreateTriangles(
            _context,
            _modelFadeVertexSpirv,
            _modelFadeFragmentSpirv,
            _layout,
            format,
            CullModeFlags.BackBit,
            in meshDepth,
            "fading model meshes",
            samples: _samples
        );
        _modelShadowPipeline = PipelineFactory.CreateTriangles(
            _context,
            _modelShadowVertexSpirv,
            _depthOnlySpirv,
            _shadowLayout,
            Format.Undefined,
            CullModeFlags.BackBit,
            in shadowDepth,
            "model shadow cascades"
        );
        // The wireframe debug view, where the device draws triangles as lines: every face's edges over the world,
        // passing where they lie on or before the depth there, pulled towards the camera (greater, in reverse depth) by
        // a bias that grows on faces seen edge-on, so a face's edges are not hidden by the face itself.
        if (_context.SupportsWireframe)
        {
            var wireDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.GreaterOrEqual, Write: false);
            var bias = new DepthBias(WireBiasConstant, WireBiasSlope);
            _wirePipeline = PipelineFactory.CreateTriangles(
                _context,
                _meshVertexSpirv,
                _wireSpirv,
                _layout,
                format,
                CullModeFlags.BackBit,
                in wireDepth,
                "world wireframe",
                samples: _samples,
                lines: true,
                bias: bias
            );
            _modelWirePipeline = PipelineFactory.CreateTriangles(
                _context,
                _modelFadeVertexSpirv,
                _modelFadeWireSpirv,
                _layout,
                format,
                CullModeFlags.BackBit,
                in wireDepth,
                "model wireframe",
                samples: _samples,
                lines: true,
                bias: bias
            );
        }

        _format = format;
        CreateViewPipelines();
    }

    /// <summary>
    /// The world's, the models', and the sky's pipelines at one sample a pixel, for the image views
    /// (<see cref="WantViewPipelines"/>), when the world is multisampled; with one sample the world's serve.
    /// </summary>
    private void CreateViewPipelines()
    {
        if (!_viewPipelinesWanted || _samples == SampleCountFlags.Count1Bit || _viewSkyPipeline.Handle != 0)
        {
            return;
        }

        var meshDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Greater, Write: true);
        _viewMeshPipeline = PipelineFactory.CreateTriangles(
            _context,
            _meshVertexSpirv,
            _meshFragmentSpirv,
            _layout,
            _format,
            CullModeFlags.BackBit,
            in meshDepth,
            "image view world meshes"
        );
        _viewModelPipeline = PipelineFactory.CreateTriangles(
            _context,
            _modelVertexSpirv,
            _meshFragmentSpirv,
            _layout,
            _format,
            CullModeFlags.BackBit,
            in meshDepth,
            "image view model meshes"
        );
        var skyDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Equal, Write: false);
        _viewSkyPipeline = PipelineFactory.CreateFullscreen(
            _context,
            _skyVertexSpirv,
            _skySpirv,
            _layout,
            _format,
            in skyDepth,
            "image view sky"
        );
    }

    private void DestroyPipelines()
    {
        DestroyPipeline(ref _viewMeshPipeline);
        DestroyPipeline(ref _viewModelPipeline);
        DestroyPipeline(ref _viewSkyPipeline);
        DestroyPipeline(ref _clusterPipeline);
        DestroyPipeline(ref _meshPipeline);
        DestroyPipeline(ref _skyPipeline);
        DestroyPipeline(ref _shadowPipeline);
        DestroyPipeline(ref _modelPipeline);
        DestroyPipeline(ref _modelFadePipeline);
        DestroyPipeline(ref _modelShadowPipeline);
        DestroyPipeline(ref _wirePipeline);
        DestroyPipeline(ref _modelWirePipeline);
    }

    private void DestroyPipeline(ref Pipeline pipeline)
    {
        if (pipeline.Handle != 0)
        {
            _context.Vk.DestroyPipeline(_context.Device, pipeline, null);
        }

        pipeline = default;
    }

    /// <summary>
    /// The cascades' sampler: it compares as it reads (lit where the point is no farther from the sun than the map
    /// holds), blends the 2x2 comparisons round a point where the device filters the depth format linearly, and reads
    /// lit past the map's edges (the white border is depth 1).
    /// </summary>
    private void CreateShadowSampler()
    {
        _context.Vk.GetPhysicalDeviceFormatProperties(
            _context.PhysicalDevice,
            DeviceRequirements.DepthFormat,
            out var properties
        );
        var filter =
            (properties.OptimalTilingFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) != 0
                ? Filter.Linear
                : Filter.Nearest;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            BorderColor = BorderColor.FloatOpaqueWhite,
            CompareEnable = true,
            CompareOp = CompareOp.LessOrEqual,
            MaxLod = 0,
        };
        _context.Vk.CreateSampler(_context.Device, &info, null, out _shadowSampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _shadowSampler.Handle, "shadow cascades");

        // The same maps read as depths, texel by texel; past their edges 1, as far from the sun as can be: no blocker.
        var depthInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            BorderColor = BorderColor.FloatOpaqueWhite,
            MaxLod = 0,
        };
        _context
            .Vk.CreateSampler(_context.Device, &depthInfo, null, out _shadowDepthSampler)
            .ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _shadowDepthSampler.Handle, "shadow cascade depths");
    }

    /// <summary>The sky field's sampler: blended between cells, and open sky (white, 1) past the field's edges.</summary>
    private void CreateSkySampler()
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToBorder,
            BorderColor = BorderColor.FloatOpaqueWhite,
            MaxLod = 0,
        };
        _context.Vk.CreateSampler(_context.Device, &info, null, out _skySampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _skySampler.Handle, "sky field");
    }

    private void CreateDescriptors()
    {
        var vk = _context.Vk;
        var device = _context.Device;
        var bindings = stackalloc DescriptorSetLayoutBinding[Bindings];
        for (var i = 0; i < Bindings; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = TypeOf(i),
                DescriptorCount = 1,
                StageFlags = SetStages,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = Bindings,
            PBindings = bindings,
        };
        vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _setLayout)
            .ThrowIfFailed("vkCreateDescriptorSetLayout");

        _descriptorPool = CreatePool();
        _set = WriteSet(_descriptorPool, Draws, Lighting, ModelDraws, Lights, Clusters, "world meshes");
    }

    /// <summary>A pool for one set of the pass's layout.</summary>
    private DescriptorPool CreatePool()
    {
        var poolSizes = stackalloc DescriptorPoolSize[2]
        {
            new(DescriptorType.StorageBuffer, Bindings - 3 - TextureArrays),
            new(DescriptorType.CombinedImageSampler, 3 + TextureArrays),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        _context
            .Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, out var pool)
            .ThrowIfFailed("vkCreateDescriptorPool");
        return pool;
    }

    /// <summary>
    /// A set of the pass's layout from <paramref name="pool"/>, with the given frame records (the pass's own, or an image
    /// view's: <see cref="RenderViewTarget"/>) and the pass's heap, maps, sky field, textures, and looks.
    /// </summary>
    private DescriptorSet WriteSet(
        DescriptorPool pool,
        GpuBuffer draws,
        GpuBuffer lighting,
        GpuBuffer modelDraws,
        GpuBuffer lights,
        GpuBuffer clusters,
        string name
    )
    {
        var vk = _context.Vk;
        var device = _context.Device;
        var setLayout = _setLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        DescriptorSet set;
        vk.AllocateDescriptorSets(device, &allocateInfo, &set).ThrowIfFailed("vkAllocateDescriptorSets");

        // In binding order (mesh-data.glsl): the whole heap as words, the draws of every slot, the heap's materials and
        // tint map at their fixed places (multiples of 256 bytes, and so of any storage buffer alignment), the cascades'
        // records of every slot, the cascades' maps (an image), the model draws of every slot, the point lights of every
        // slot, the sky field (an image), the cascades' maps again, read as depths, the light clusters of every slot, the
        // six arrays of the materials' texture sets (images), and the looks of every slot.
        var infos = stackalloc DescriptorBufferInfo[FirstTextureBinding]
        {
            new(Heap.Buffer, 0, Vk.WholeSize),
            new(draws.Buffer, 0, Vk.WholeSize),
            new(Heap.Buffer, MaterialsOffset, MaterialsBytes),
            new(Heap.Buffer, TintMapOffset, TintMapBytes),
            new(lighting.Buffer, 0, Vk.WholeSize),
            default,
            new(modelDraws.Buffer, 0, Vk.WholeSize),
            new(lights.Buffer, 0, Vk.WholeSize),
            default,
            default,
            new(clusters.Buffer, 0, Vk.WholeSize),
        };
        var look = new DescriptorBufferInfo(Look.Buffer, 0, Vk.WholeSize);
        var images = stackalloc DescriptorImageInfo[3 + TextureArrays];
        images[0] = new(_shadowSampler, ShadowMap.View, ImageLayout.ShaderReadOnlyOptimal);
        images[1] = new(_skySampler, SkyImage.View, ImageLayout.ShaderReadOnlyOptimal);
        images[2] = new(_shadowDepthSampler, ShadowMap.View, ImageLayout.ShaderReadOnlyOptimal);
        for (var i = 0; i < TextureArrays; i++)
        {
            images[3 + i] = new(_textureSampler, TextureArray(i).View, ImageLayout.ShaderReadOnlyOptimal);
        }

        var writes = stackalloc WriteDescriptorSet[Bindings];
        for (var i = 0; i < Bindings; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = TypeOf(i),
                PBufferInfo =
                    i == LookBinding ? &look
                    : TypeOf(i) == DescriptorType.StorageBuffer ? &infos[i]
                    : null,
                PImageInfo =
                    i == ShadowMapBinding ? &images[0]
                    : i == SkyFieldBinding ? &images[1]
                    : i == ShadowDepthBinding ? &images[2]
                    : TypeOf(i) == DescriptorType.CombinedImageSampler ? &images[3 + i - FirstTextureBinding]
                    : null,
            };
        }

        vk.UpdateDescriptorSets(device, Bindings, writes, 0, null);
        _context.SetObjectName(ObjectType.DescriptorSet, set.Handle, name);
        return set;
    }

    private static DescriptorType TypeOf(int binding) =>
        binding is ShadowMapBinding or SkyFieldBinding or ShadowDepthBinding
        || (binding >= FirstTextureBinding && binding < FirstTextureBinding + TextureArrays)
            ? DescriptorType.CombinedImageSampler
            : DescriptorType.StorageBuffer;

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

    /// <summary>
    /// Moves the first <paramref name="layers"/> layers of <paramref name="image"/> from <paramref name="oldLayout"/>
    /// (undefined by default, which lets the contents go) into <paramref name="layout"/>, after the source stage's
    /// access and before the destination's.
    /// </summary>
    private void ImageBarrier(
        CommandBuffer commandBuffer,
        Image image,
        ImageAspectFlags aspect,
        uint layers,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess,
        ImageLayout layout,
        ImageLayout oldLayout = ImageLayout.Undefined
    )
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            OldLayout = oldLayout,
            NewLayout = layout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, layers),
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
        Level = LogLevel.Information,
        Message = "Meshes: heap {HeapMegabytes} MB, up to {MaxDraws} blocks drawn a frame, {Samples} sample(s) a pixel, memory type {Type}"
    )]
    private partial void LogCreated(ulong heapMegabytes, int maxDraws, int samples, int type);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Meshes: the world is now drawn with {Samples} sample(s) a pixel"
    )]
    private partial void LogSamplesChanged(int samples);
}
