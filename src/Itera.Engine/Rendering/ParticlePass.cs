using System.Numerics;
using Itera.Engine.Particles;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>What <see cref="ParticlePass.Write"/> put in a frame slot's regions, for the render thread.</summary>
/// <param name="Constants">The compute passes' push constants: the counts, the pool, the camera.</param>
/// <param name="Active">
/// True while particles may be alive (<see cref="ParticleFrame.Active"/>): only then are they simulated and drawn, and
/// the world's depth copied for them.
/// </param>
/// <param name="Clear">
/// True when the emitter was cleared (<see cref="ParticleFrame.Clear"/>): the pool is emptied before the next particles
/// are made.
/// </param>
/// <param name="Origin">The frame's origin in absolute centimetres, which its bursts and attractors are local to.</param>
/// <param name="LiveCount">
/// The pool's places from <see cref="ParticleConstants.LiveFirst"/> that may hold a living particle
/// (<see cref="ParticleFrame.LiveCount"/>): the simulate dispatch covers only these.
/// </param>
public readonly record struct ParticleWrite(
    ParticleConstants Constants,
    bool Active,
    bool Clear = false,
    VoxelCoord Origin = default,
    int LiveCount = 0
);

/// <summary>
/// Where the particle pool's positions are measured from: the origin of the last frame that simulated them (render
/// thread). A frame that simulates in another origin moves every particle made before it by the difference
/// (<see cref="ParticleConstants.Shift"/>); frames that do not simulate (presented again, under the loading screen, or
/// with no particle alive) leave it, so the next one that does takes the whole move. An emptied pool takes the frame's.
/// </summary>
internal sealed class ParticlePoolOrigin
{
    private VoxelCoord _origin;

    /// <summary>The pool was emptied in a frame of <paramref name="frame"/>'s origin: nothing in it to move.</summary>
    public void Emptied(VoxelCoord frame) => _origin = frame;

    /// <summary>
    /// A frame of <paramref name="frame"/>'s origin simulates the pool: how far the particles made before it move, in
    /// metres (to take off), after which the pool is in its origin.
    /// </summary>
    public Vector3 Simulated(VoxelCoord frame)
    {
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        var shift = new Vector3(
            (float)(((double)frame.X - _origin.X) / perMetre),
            (float)(((double)frame.Y - _origin.Y) / perMetre),
            (float)(((double)frame.Z - _origin.Z) / perMetre)
        );
        _origin = frame;
        return shift;
    }
}

/// <summary>
/// The particles on the GPU (they move and meet the world on the GPU, and the game only
/// asks for bursts). The pool (<see cref="GpuParticle"/>, <see cref="ParticleEmitter.Capacity"/> of them), the lists of
/// cubes and sprites to draw, and the counters with the two indirect draws are device-local and outlive frames; the
/// recipes, bursts, and attractors go in host-visible buffers with one region per frame slot, written by the main thread
/// (<see cref="Write"/>) and flushed by the render thread when the memory is not coherent. Each frame with particles
/// alive that draws the world, after the world's pass, the render thread resets the counters, makes the frame's new
/// particles (<c>particle-spawn.comp</c>), moves every particle against the world's depth image, lights each lit one
/// from the shadow cascades and the sky field, and lists those to draw (<c>particle-simulate.comp</c>), sorts the
/// sprites back to front by bins of depth (<c>particle-sort-scan.comp</c> and <c>particle-sort-scatter.comp</c>), and
/// copies the counters to the slot's readback region (<see cref="Collect"/> reads
/// them after the slot's fence into <see cref="ParticleStats"/>). After the models it draws the cubes
/// (<c>particle-cubes.vert</c> and <c>.frag</c>, writing depth) and then the sprites (<c>particle-sprites.vert</c> and
/// <c>.frag</c>, premultiplied, testing depth only), each with one <c>vkCmdDrawIndirect</c> whose instance count the
/// simulate pass wrote. Set 0 is the particles' (the buffers, the depth image, <see cref="DepthTarget"/>, and the mesh
/// pass's look buffer for the draws); the
/// compute passes also bind <see cref="Meshes.MeshPass"/>'s set as set 1, for the cascades, the lighting record, and the sky
/// field. While the loading screen covers the picture the world is not drawn, and the particles wait. Created before
/// the render thread starts; recorded by the render thread.
/// </summary>
public sealed unsafe partial class ParticlePass : IDisposable, IReloadsShaders
{
    /// <summary>The set's storage buffers, bindings 0 to 6 (particle-data.glsl).</summary>
    private const int StorageBindings = 7;

    /// <summary>The depth image's binding (particle-simulate.comp's sceneDepth).</summary>
    private const int DepthBinding = 7;

    /// <summary>
    /// The frame's look, the mesh pass's buffer of every slot's (<see cref="Meshes.MeshPass.LookOf"/>), which the draws'
    /// lighting, fog, tone curve, and grade read (<c>look.glsl</c> compiled with <see cref="LookDefine"/>).
    /// </summary>
    private const int LookBinding = 8;

    /// <summary>The sprites' sort keys and its bins (particle-sort.glsl), read by the compute passes alone.</summary>
    private const int SpriteKeysBinding = 9;

    private const int SortBinsBinding = 10;

    private const int Bindings = 11;

    /// <summary>The sprites' sort's bins of depth (particle-sort.glsl's <c>PARTICLE_SORT_BINS</c>).</summary>
    internal const int SortBins = 1024;

    /// <summary>Where the draw shaders find the look: this set's <see cref="LookBinding"/>.</summary>
    private const string LookDefine = "LOOK_BINDING 8";
    private const ShaderStageFlags SetStages =
        ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
    private const ShaderStageFlags DrawStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
    private const uint GroupSize = 64;

    /// <summary>Seconds between the Debug lines of particle counts, while particles live.</summary>
    private const double StatsLineSeconds = 10;

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly ShaderCompiler _shaders;
    private readonly GpuMemoryAllocator _allocator;
    private readonly DepthTarget _depth;
    private readonly Meshes.MeshPass _meshPass;
    private readonly ParticleEmitter _emitter;
    private readonly ParticleStats _stats;
    private readonly ILogger<ParticlePass> _logger;
    private readonly bool[] _simulated = new bool[FrameSync.FramesInFlight];
    private byte[] _cubeVertexSpirv = [];
    private byte[] _cubeFragmentSpirv = [];
    private byte[] _spriteVertexSpirv = [];
    private byte[] _spriteFragmentSpirv = [];
    private GpuBuffer? _pool;
    private GpuBuffer? _cubes;
    private GpuBuffer? _sprites;
    private GpuBuffer? _spriteKeys;
    private GpuBuffer? _sortBins;
    private GpuBuffer? _counters;
    private GpuBuffer? _recipes;
    private GpuBuffer? _bursts;
    private GpuBuffer? _attractors;
    private GpuBuffer? _readback;
    private DescriptorSetLayout _setLayout;
    private Sampler _depthSampler;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _set;
    private PipelineLayout _computeLayout;
    private PipelineLayout _drawLayout;
    private Pipeline _spawnPipeline;
    private Pipeline _simulatePipeline;
    private Pipeline _sortScanPipeline;
    private Pipeline _sortScatterPipeline;
    private Pipeline _cubePipeline;
    private Pipeline _spritePipeline;
    private Format _format;
    private bool _poolCleared;
    private readonly ParticlePoolOrigin _poolOrigin = new();
    private double _nextStatsLine = StatsLineSeconds;
    private bool _disposed;

    public ParticlePass(
        VulkanContext context,
        VulkanSwapchain swapchain,
        ShaderCompiler shaders,
        GpuMemoryAllocator allocator,
        DepthTarget depth,
        Meshes.MeshPass meshPass,
        ParticleEmitter emitter,
        ParticleStats stats,
        ILogger<ParticlePass> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _shaders = shaders;
        _allocator = allocator;
        _depth = depth;
        _meshPass = meshPass;
        _emitter = emitter;
        _stats = stats;
        _logger = logger;
    }

    private static ulong RecipeSlotBytes => (ulong)(ParticleRecipes.MaxRecipes * sizeof(GpuParticleRecipe));

    private static ulong AttractorSlotBytes => (ulong)(ParticleEmitter.MaxAttractors * sizeof(System.Numerics.Vector4));

    private ulong BurstSlotBytes => (ulong)(_emitter.MaxBursts * sizeof(GpuParticleBurst));

    /// <summary>Creates the buffers, the descriptor set, and the pipelines. After the depth target and the mesh pass.</summary>
    public void Initialize()
    {
        var capacity = (ulong)_emitter.Capacity;
        var slots = (ulong)FrameSync.FramesInFlight;
        _pool = _allocator.CreateBuffer(
            capacity * (ulong)sizeof(GpuParticle),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "particles"
        );
        _cubes = _allocator.CreateBuffer(
            capacity * sizeof(uint),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.DeviceLocal,
            "particle cubes"
        );
        _sprites = _allocator.CreateBuffer(
            capacity * sizeof(uint),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.DeviceLocal,
            "particle sprites"
        );
        _spriteKeys = _allocator.CreateBuffer(
            capacity * sizeof(uint),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.DeviceLocal,
            "particle sprite keys"
        );
        _sortBins = _allocator.CreateBuffer(
            SortBins * sizeof(uint),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "particle sort bins"
        );
        _counters = _allocator.CreateBuffer(
            (ulong)sizeof(ParticleCounters),
            BufferUsageFlags.StorageBufferBit
                | BufferUsageFlags.IndirectBufferBit
                | BufferUsageFlags.TransferDstBit
                | BufferUsageFlags.TransferSrcBit,
            MemoryUsage.DeviceLocal,
            "particle counters"
        );
        _recipes = _allocator.CreateBuffer(
            RecipeSlotBytes * slots,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "particle recipes"
        );
        _bursts = _allocator.CreateBuffer(
            BurstSlotBytes * slots,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "particle bursts"
        );
        _attractors = _allocator.CreateBuffer(
            AttractorSlotBytes * slots,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "particle attractors"
        );
        _readback = _allocator.CreateBuffer(
            (ulong)sizeof(ParticleCounters) * slots,
            BufferUsageFlags.TransferDstBit,
            MemoryUsage.Readback,
            "particle counters readback"
        );
        CreateDepthSampler();
        CreateDescriptors();
        WriteDepthDescriptor();

        var setLayout = _setLayout;
        var computeSets = stackalloc DescriptorSetLayout[2] { _setLayout, _meshPass.SetLayout };
        _computeLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(computeSets, 2),
            ShaderStageFlags.ComputeBit,
            (uint)sizeof(ParticleConstants),
            "particle compute layout"
        );
        _drawLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            DrawStages,
            (uint)sizeof(ViewConstants),
            "particle draw layout"
        );
        var compiled = (byte[][])CompileShaders();
        CreateComputePipelines(compiled);
        TakeShaders(compiled);
        CreateDrawPipelines(_swapchain.ImageFormat);
        var megabytes = (capacity * (ulong)sizeof(GpuParticle)) >> 20;
        LogCreated(_emitter.Capacity, megabytes, _emitter.MaxBursts, _pool.MemoryTypeIndex);
    }

    /// <summary>Makes new draw pipelines when the swapchain's format changed. Render thread, device idle.</summary>
    public void OnSwapchainRecreated()
    {
        if (_swapchain.ImageFormat != _format)
        {
            DestroyDrawPipelines();
            CreateDrawPipelines(_swapchain.ImageFormat);
        }

        // The depth target made its image again for the new size (FrameRenderer recreates it first).
        WriteDepthDescriptor();
    }

    /// <summary>
    /// Writes the frame's recipes, the bursts the emitter queued (<see cref="ParticleEmitter.Drain"/>), and the
    /// attractors into the regions of <paramref name="slot"/>, keeping the camera's attractor at the camera, and
    /// returns the compute passes' constants; each recipe's gravity a multiple of the match's <paramref name="gravity"/>
    /// (the Earth's with no match). <paramref name="worldDrawn"/> is false when the loading screen covers the world
    /// (<see cref="LoadingDraw.Covers"/>): the render thread then skips the particles (<see cref="SkipSimulate"/>), so
    /// the emitter's clock does not advance. Main thread, between taking the frame's request and publishing the frame.
    /// Writes only, in address order.
    /// </summary>
    public ParticleWrite Write(
        int slot,
        in FrameTime time,
        in ViewConstants view,
        System.Numerics.Vector3 gravity,
        bool worldDrawn
    )
    {
        if ((uint)slot >= FrameSync.FramesInFlight)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "No such frame slot.");
        }

        _emitter.SetAttractor(ParticleEmitter.CameraAttractor, view.Position - new System.Numerics.Vector3(0, 0.3f, 0));
        var bursts = new Span<GpuParticleBurst>(Bursts.Mapped + BurstSlotBytes * (ulong)slot, _emitter.MaxBursts);
        // The simulate pass ages the particles by the frame's delta (the constants' Delta below), so the emitter's
        // clock takes the same.
        var frame = _emitter.Drain(bursts, time.DeltaSeconds, worldDrawn);
        if (!frame.Active)
        {
            return new ParticleWrite(default, false, frame.Clear, view.Origin);
        }

        if (time.TotalSeconds >= _nextStatsLine)
        {
            _nextStatsLine = time.TotalSeconds + StatsLineSeconds;
            LogStats(_stats.Living, _stats.Cubes, _stats.Sprites, _stats.Overwritten, _emitter.DroppedBursts);
        }

        var recipes = _emitter.Recipes.All;
        var recipeTarget = new Span<GpuParticleRecipe>(
            Recipes.Mapped + RecipeSlotBytes * (ulong)slot,
            ParticleRecipes.MaxRecipes
        );
        for (var i = 0; i < recipes.Length; i++)
        {
            recipeTarget[i] = GpuParticleRecipe.From(in recipes[i], gravity);
        }

        _emitter.Attractors.CopyTo(
            new Span<System.Numerics.Vector4>(
                Attractors.Mapped + AttractorSlotBytes * (ulong)slot,
                ParticleEmitter.MaxAttractors
            )
        );

        // The view's right and up reach the image's edges at unit length along forward, so the corner's tangent is their
        // combined length.
        var cornerTangent2 = view.Right.LengthSquared() + view.Up.LengthSquared();
        var constants = new ParticleConstants
        {
            Delta = time.DeltaSeconds,
            Slot = (uint)slot,
            Bursts = (uint)frame.Bursts,
            Spawns = (uint)frame.Spawns,
            Capacity = (uint)_emitter.Capacity,
            SunDirection = view.SunDirection,
            MaxDistance = view.MaxDistance,
            Camera = view.Position,
            LiveFirst = (uint)frame.LiveFirst,
            Forward = view.Forward,
            ViewCosine = 1 / MathF.Sqrt(1 + cornerTangent2),
            BurstStart = (uint)(slot * _emitter.MaxBursts),
            Right = view.Right,
            Up = view.Up,
            Width = view.Width,
            Height = view.Height,
            Near = view.Near,
            Far = view.Far,
            Orthographic = view.Orthographic ? 1u : 0u,
        };
        return new ParticleWrite(constants, true, frame.Clear, view.Origin, frame.LiveCount);
    }

    /// <summary>
    /// Reads the counters of the frame last simulated in <paramref name="slot"/> into <see cref="ParticleStats"/>.
    /// Render thread, right after the slot's fence wait.
    /// </summary>
    public void Collect(int slot)
    {
        if (_readback is null)
        {
            return;
        }

        if (!_simulated[slot])
        {
            _stats.Clear();
            return;
        }

        var offset = (ulong)(slot * sizeof(ParticleCounters));
        _readback.Invalidate(offset, (ulong)sizeof(ParticleCounters));
        var counters = *(ParticleCounters*)(_readback.Mapped + offset);
        _stats.Record(in counters);
    }

    /// <summary>
    /// A frame that does not draw the world (the loading screen covers it): the particles wait, and its slot counts as
    /// not simulated; a cleared emitter still has the next frame that records empty the pool. Render thread.
    /// </summary>
    public void SkipSimulate(int slot, in ParticleWrite write)
    {
        _simulated[slot] = false;
        if (write.Clear)
        {
            _poolCleared = false;
        }
    }

    /// <summary>
    /// Records the frame's particle work after the world's pass: the counters reset, the new particles made, every
    /// particle moved against the world's depth image and listed for drawing, and the counters copied for
    /// <see cref="Collect"/>; the depth image is handed to the compute pass and back around it
    /// (<see cref="DepthTarget.RecordToSampled"/>). Records nothing while no particles can be alive, and for a frame
    /// presented a second time (<paramref name="fresh"/> false, on the way to
    /// stopping): its bursts must not be made twice, and its slot's regions may already belong to a later frame; its
    /// draws show the particles as the last simulation left them. A cleared emitter (<see cref="ParticleWrite.Clear"/>)
    /// has the next frame that records empty the pool first, as the first one does. Render thread.
    /// </summary>
    public void RecordSimulate(CommandBuffer commandBuffer, int slot, in ParticleWrite write, bool fresh)
    {
        _simulated[slot] = false;
        if (write.Clear)
        {
            _poolCleared = false;
        }

        if (!write.Active || !fresh)
        {
            return;
        }

        var constants = write.Constants;

        Recipes.Flush(RecipeSlotBytes * (ulong)slot, RecipeSlotBytes);
        Attractors.Flush(AttractorSlotBytes * (ulong)slot, AttractorSlotBytes);
        if (constants.Bursts > 0)
        {
            Bursts.Flush(BurstSlotBytes * (ulong)slot, constants.Bursts * (ulong)sizeof(GpuParticleBurst));
        }

        var vk = _context.Vk;
        // The last frame's draws, indirect reads, and counter copy finish before this frame writes what they read.
        Barrier(
            commandBuffer,
            PipelineStageFlags2.VertexShaderBit
                | PipelineStageFlags2.DrawIndirectBit
                | PipelineStageFlags2.CopyBit
                | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.CopyBit | PipelineStageFlags2.ClearBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
        );
        if (!_poolCleared)
        {
            // Every slot starts free (lifetime 0), and again after the emitter was cleared.
            vk.CmdFillBuffer(commandBuffer, Pool.Buffer, 0, Vk.WholeSize, 0);
            _poolCleared = true;
            _poolOrigin.Emptied(write.Origin);
        }

        // The particles made before this frame move with the origin, if it moved since they last did.
        constants.Shift = _poolOrigin.Simulated(write.Origin);

        var start = ParticleCounters.Start;
        vk.CmdUpdateBuffer(commandBuffer, Counters.Buffer, 0, (ulong)sizeof(ParticleCounters), &start);
        vk.CmdFillBuffer(commandBuffer, SortBinsBuffer.Buffer, 0, Vk.WholeSize, 0);
        Barrier(
            commandBuffer,
            PipelineStageFlags2.CopyBit | PipelineStageFlags2.ClearBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
        );

        // The world's depth, which the simulate pass reads; the cascades and the sky field are ready for it already
        // (MeshPass's barriers name the compute stage).
        _depth.RecordToSampled(commandBuffer, _meshPass.Multisampled);
        BindComputeSets(commandBuffer);
        vk.CmdPushConstants(
            commandBuffer,
            _computeLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)sizeof(ParticleConstants),
            &constants
        );
        if (constants.Spawns > 0)
        {
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _spawnPipeline);
            vk.CmdDispatch(commandBuffer, (constants.Spawns + GroupSize - 1) / GroupSize, 1, 1);
            ComputeBarrier(commandBuffer);
        }

        // Only the ring's slots that may hold a living particle (ParticleFrame's span): the groups' last few run past it,
        // onto slots no living particle holds.
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _simulatePipeline);
        var live = (uint)Math.Clamp(write.LiveCount, 0, (int)constants.Capacity);
        var groups = (live + GroupSize - 1) / GroupSize;
        vk.CmdDispatch(commandBuffer, groups, 1, 1);

        // The sprites back to front: each bin's first place from the counts, then each sprite to its place. The scatter
        // runs over the simulate pass's span, which holds every sprite listed.
        ComputeBarrier(commandBuffer);
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _sortScanPipeline);
        vk.CmdDispatch(commandBuffer, 1, 1, 1);
        ComputeBarrier(commandBuffer);
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _sortScatterPipeline);
        vk.CmdDispatch(commandBuffer, groups, 1, 1);
        _depth.RecordToAttachment(commandBuffer);

        // The pool, lists, and counters are read by this frame's draws, the indirect commands, and the copy below.
        Barrier(
            commandBuffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.CopyBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.IndirectCommandReadBit | AccessFlags2.TransferReadBit
        );
        var copy = new BufferCopy
        {
            SrcOffset = 0,
            DstOffset = (ulong)(slot * sizeof(ParticleCounters)),
            Size = (ulong)sizeof(ParticleCounters),
        };
        vk.CmdCopyBuffer(commandBuffer, Counters.Buffer, Readback.Buffer, 1, &copy);
        Barrier(
            commandBuffer,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit
        );
        _simulated[slot] = true;
    }

    /// <summary>
    /// Records the cubes and then the sprites over <paramref name="target"/>, tested against <paramref name="depth"/>
    /// (the world's depth, and the models'), after the passes before them: the barrier waits for their colour and depth
    /// writes, and the attachments load them. Nothing while no particles can be alive. Render thread.
    /// </summary>
    public void RecordDraw(
        CommandBuffer commandBuffer,
        ImageView target,
        ImageView depth,
        Extent2D extent,
        in ViewConstants view,
        bool active
    )
    {
        if (!active)
        {
            return;
        }

        var vk = _context.Vk;
        const PipelineStageFlags2 stages =
            PipelineStageFlags2.ColorAttachmentOutputBit
            | PipelineStageFlags2.EarlyFragmentTestsBit
            | PipelineStageFlags2.LateFragmentTestsBit;
        Barrier(
            commandBuffer,
            stages,
            AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            stages,
            AccessFlags2.ColorAttachmentReadBit
                | AccessFlags2.ColorAttachmentWriteBit
                | AccessFlags2.DepthStencilAttachmentReadBit
                | AccessFlags2.DepthStencilAttachmentWriteBit
        );

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
        // The view's size is the image drawn to, as the world's pass has it.
        var constants = view;
        constants.Width = extent.Width;
        constants.Height = extent.Height;
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        var set = _set;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _drawLayout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(commandBuffer, _drawLayout, DrawStages, 0, (uint)sizeof(ViewConstants), &constants);
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _cubePipeline);
        vk.CmdDrawIndirect(commandBuffer, Counters.Buffer, 0, 1, (uint)sizeof(DrawIndirectCommand));
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _spritePipeline);
        vk.CmdDrawIndirect(
            commandBuffer,
            Counters.Buffer,
            (ulong)sizeof(DrawIndirectCommand),
            1,
            (uint)sizeof(DrawIndirectCommand)
        );
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
            DestroyDrawPipelines();
            if (_spawnPipeline.Handle != 0)
            {
                vk.DestroyPipeline(device, _spawnPipeline, null);
            }

            if (_simulatePipeline.Handle != 0)
            {
                vk.DestroyPipeline(device, _simulatePipeline, null);
            }

            if (_sortScanPipeline.Handle != 0)
            {
                vk.DestroyPipeline(device, _sortScanPipeline, null);
            }

            if (_sortScatterPipeline.Handle != 0)
            {
                vk.DestroyPipeline(device, _sortScatterPipeline, null);
            }

            if (_computeLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _computeLayout, null);
            }

            if (_drawLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _drawLayout, null);
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

            if (_depthSampler.Handle != 0)
            {
                vk.DestroySampler(device, _depthSampler, null);
            }
        }

        _depthSampler = default;
        _spawnPipeline = default;
        _simulatePipeline = default;
        _sortScanPipeline = default;
        _sortScatterPipeline = default;
        _computeLayout = default;
        _drawLayout = default;
        _descriptorPool = default;
        _setLayout = default;
        _readback?.Dispose();
        _attractors?.Dispose();
        _bursts?.Dispose();
        _recipes?.Dispose();
        _counters?.Dispose();
        _sortBins?.Dispose();
        _spriteKeys?.Dispose();
        _sprites?.Dispose();
        _cubes?.Dispose();
        _pool?.Dispose();
        _readback = null;
        _attractors = null;
        _bursts = null;
        _recipes = null;
        _counters = null;
        _sortBins = null;
        _spriteKeys = null;
        _sprites = null;
        _cubes = null;
        _pool = null;
    }

    private GpuBuffer Pool => _pool ?? throw NotInitialised();

    private GpuBuffer Counters => _counters ?? throw NotInitialised();

    private GpuBuffer Recipes => _recipes ?? throw NotInitialised();

    private GpuBuffer Bursts => _bursts ?? throw NotInitialised();

    private GpuBuffer Attractors => _attractors ?? throw NotInitialised();

    private GpuBuffer Readback => _readback ?? throw NotInitialised();

    private GpuBuffer SortBinsBuffer => _sortBins ?? throw NotInitialised();

    private static InvalidOperationException NotInitialised() => new("The particle pass has not been initialised.");

    /// <summary>One compute pass's storage writes, before the next one reads and writes them.</summary>
    private void ComputeBarrier(CommandBuffer commandBuffer) =>
        Barrier(
            commandBuffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
        );

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

    public object CompileShaders()
    {
        return new[]
        {
            _shaders.Compile("particle-spawn.comp"),
            _shaders.Compile("particle-simulate.comp"),
            _shaders.Compile("particle-cubes.vert", [LookDefine]),
            _shaders.Compile("particle-cubes.frag", [LookDefine]),
            _shaders.Compile("particle-sprites.vert", [LookDefine]),
            _shaders.Compile("particle-sprites.frag", [LookDefine]),
            _shaders.Compile("particle-sort-scan.comp"),
            _shaders.Compile("particle-sort-scatter.comp"),
        };
    }

    /// <summary>Binds the particles' set as set 0 and the mesh pass's as set 1, for the compute passes.</summary>
    private void BindComputeSets(CommandBuffer commandBuffer)
    {
        var sets = stackalloc DescriptorSet[2] { _set, _meshPass.Set };
        _context.Vk.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _computeLayout,
            0,
            2,
            sets,
            0,
            null
        );
    }

    public int ReplacePipelines(object compiled)
    {
        var shaders = (byte[][])compiled;
        _context.Vk.DestroyPipeline(_context.Device, _spawnPipeline, null);
        _context.Vk.DestroyPipeline(_context.Device, _simulatePipeline, null);
        _context.Vk.DestroyPipeline(_context.Device, _sortScanPipeline, null);
        _context.Vk.DestroyPipeline(_context.Device, _sortScatterPipeline, null);
        // Cleared at once: should making a new one fail, Dispose finds no handle destroyed already.
        _spawnPipeline = default;
        _simulatePipeline = default;
        _sortScanPipeline = default;
        _sortScatterPipeline = default;
        DestroyDrawPipelines();
        CreateComputePipelines(shaders);
        TakeShaders(shaders);
        CreateDrawPipelines(_format);
        return 6;
    }

    private void CreateComputePipelines(byte[][] compiled)
    {
        _spawnPipeline = PipelineFactory.CreateCompute(_context, compiled[0], _computeLayout, "particle spawn");
        _simulatePipeline = PipelineFactory.CreateCompute(_context, compiled[1], _computeLayout, "particle simulate");
        _sortScanPipeline = PipelineFactory.CreateCompute(_context, compiled[6], _computeLayout, "particle sort scan");
        _sortScatterPipeline = PipelineFactory.CreateCompute(
            _context,
            compiled[7],
            _computeLayout,
            "particle sort scatter"
        );
    }

    private void TakeShaders(byte[][] compiled)
    {
        _cubeVertexSpirv = compiled[2];
        _cubeFragmentSpirv = compiled[3];
        _spriteVertexSpirv = compiled[4];
        _spriteFragmentSpirv = compiled[5];
    }

    private void CreateDrawPipelines(Format format)
    {
        // Reverse depth: nearer is greater. Cubes write their depth; sprites only test it.
        var cubeDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Greater, Write: true);
        _cubePipeline = PipelineFactory.CreateTriangles(
            _context,
            _cubeVertexSpirv,
            _cubeFragmentSpirv,
            _drawLayout,
            format,
            CullModeFlags.BackBit,
            in cubeDepth,
            "particle cubes"
        );
        var spriteDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.GreaterOrEqual, Write: false);
        _spritePipeline = PipelineFactory.CreateTriangles(
            _context,
            _spriteVertexSpirv,
            _spriteFragmentSpirv,
            _drawLayout,
            format,
            CullModeFlags.None,
            in spriteDepth,
            "particle sprites",
            ColorBlend.Premultiplied
        );
        _format = format;
    }

    private void DestroyDrawPipelines()
    {
        var vk = _context.Vk;
        if (_cubePipeline.Handle != 0)
        {
            vk.DestroyPipeline(_context.Device, _cubePipeline, null);
        }

        if (_spritePipeline.Handle != 0)
        {
            vk.DestroyPipeline(_context.Device, _spritePipeline, null);
        }

        _cubePipeline = default;
        _spritePipeline = default;
    }

    private void CreateDescriptors()
    {
        var vk = _context.Vk;
        var device = _context.Device;
        var bindings = stackalloc DescriptorSetLayoutBinding[Bindings];
        for (var i = 0; i < StorageBindings; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = SetStages,
            };
        }

        bindings[DepthBinding] = new DescriptorSetLayoutBinding
        {
            Binding = DepthBinding,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };
        bindings[LookBinding] = new DescriptorSetLayoutBinding
        {
            Binding = LookBinding,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = DrawStages,
        };
        for (var i = SpriteKeysBinding; i <= SortBinsBinding; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
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

        var poolSizes = stackalloc DescriptorPoolSize[2]
        {
            new(DescriptorType.StorageBuffer, StorageBindings + 3),
            new(DescriptorType.CombinedImageSampler, 1),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        vk.CreateDescriptorPool(device, &poolInfo, null, out _descriptorPool).ThrowIfFailed("vkCreateDescriptorPool");

        var setLayout = _setLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        DescriptorSet set;
        vk.AllocateDescriptorSets(device, &allocateInfo, &set).ThrowIfFailed("vkAllocateDescriptorSets");
        _set = set;

        // In binding order (particle-data.glsl): each buffer whole; the per-slot buffers hold every slot's region, which
        // the shaders pick by the frame's slot.
        var buffers = stackalloc Silk.NET.Vulkan.Buffer[StorageBindings]
        {
            Pool.Buffer,
            _cubes!.Buffer,
            Counters.Buffer,
            Recipes.Buffer,
            Bursts.Buffer,
            Attractors.Buffer,
            _sprites!.Buffer,
        };
        var infos = stackalloc DescriptorBufferInfo[StorageBindings];
        var writes = stackalloc WriteDescriptorSet[StorageBindings];
        for (var i = 0; i < StorageBindings; i++)
        {
            infos[i] = new DescriptorBufferInfo
            {
                Buffer = buffers[i],
                Offset = 0,
                Range = Vk.WholeSize,
            };
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &infos[i],
            };
        }

        vk.UpdateDescriptorSets(device, StorageBindings, writes, 0, null);
        var look = new DescriptorBufferInfo(_meshPass.LookBuffer.Buffer, 0, Vk.WholeSize);
        var lookWrite = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = LookBinding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &look,
        };
        vk.UpdateDescriptorSets(device, 1, &lookWrite, 0, null);
        var sortInfos = stackalloc DescriptorBufferInfo[2]
        {
            new(_spriteKeys!.Buffer, 0, Vk.WholeSize),
            new(SortBinsBuffer.Buffer, 0, Vk.WholeSize),
        };
        var sortWrites = stackalloc WriteDescriptorSet[2];
        for (var i = 0; i < 2; i++)
        {
            sortWrites[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)(SpriteKeysBinding + i),
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &sortInfos[i],
            };
        }

        vk.UpdateDescriptorSets(device, 2, sortWrites, 0, null);
        _context.SetObjectName(ObjectType.DescriptorSet, _set.Handle, "particles");
    }

    /// <summary>Points the set's depth binding at the depth target's image. No frame may be using the set.</summary>
    private void WriteDepthDescriptor()
    {
        var image = new DescriptorImageInfo
        {
            Sampler = _depthSampler,
            ImageView = _depth.View,
            ImageLayout = ImageLayout.DepthReadOnlyOptimal,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = DepthBinding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };
        _context.Vk.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    /// <summary>The depth image's sampler: nearest, clamped; the shader reads whole texels (texelFetch).</summary>
    private void CreateDepthSampler()
    {
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
        _context.Vk.CreateSampler(_context.Device, &info, null, out _depthSampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _depthSampler.Handle, "particle depth");
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Particle pass: a pool of {Capacity} particles ({Megabytes} MB), up to {Bursts} bursts a frame, memory type {Type}"
    )]
    private partial void LogCreated(int capacity, ulong megabytes, int bursts, int type);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Particles: {Living} living, {Cubes} cubes and {Sprites} sprites drawn; since the start {Overwritten} living overwritten, {Dropped} bursts dropped"
    )]
    private partial void LogStats(int living, int cubes, int sprites, long overwritten, long dropped);
}
