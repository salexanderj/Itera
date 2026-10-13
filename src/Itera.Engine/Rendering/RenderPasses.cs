using Itera.Engine.Diagnostics;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Pipelines;
using Itera.Engine.Rendering.Shaders;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>Where in the frame a game's pass (<see cref="IRenderPass"/>) records.</summary>
public enum RenderStage
{
    /// <summary>
    /// Before the shadow maps, outside any rendering: compute work, after the mesh pass's light clusters and skinning.
    /// </summary>
    Compute,

    /// <summary>
    /// Inside the world's rendering (the mesh pass's samples and colour format, its depth, compared Greater: depth is
    /// reversed), after the engine's opaque draws (the world, the models, the mesh assets) and before the sky: surfaces
    /// that write depth.
    /// </summary>
    Opaque,

    /// <summary>
    /// Inside the world's rendering, after the sky and the engine's blended mesh assets: surfaces that blend and write no
    /// depth. They draw after the engine's blended surfaces, not sorted among them.
    /// </summary>
    Transparent,

    /// <summary>
    /// After the world's rendering is resolved and the particles and debug lines are drawn: one sample a pixel at the
    /// drawn size (the render scale's), the colour and the resolved depth, before anti-aliasing and the upscale.
    /// </summary>
    Scene,

    /// <summary>After the upscale: one sample a pixel at the screen's size, the colour alone, before the UI.</summary>
    Screen,
}

/// <summary>
/// A game's own GPU pass, registered in its composition (<c>services.AddSingleton&lt;IRenderPass, MyPass&gt;()</c>; at
/// most <see cref="GpuTimings.MostGamePasses"/>, each named apart). The engine calls <see cref="Initialize"/> once
/// before the render thread starts (the run's "render passes" step, after the mesh assets reach the GPU), then each
/// frame <see cref="Write"/> on the main thread after the frame systems, and <see cref="Record"/> on the render thread
/// at its <see cref="Stage"/>: inside a rendering the engine began, its viewport and scissor set (Compute: outside
/// any). A pass keeps one region of its host-visible memory per frame slot (<see cref="FrameSync.FramesInFlight"/>),
/// writes the slot it is given in <see cref="Write"/> and flushes it in <see cref="Record"/> (non-coherent memory).
/// <see cref="OnTargetsChanged"/> comes when the formats or samples may have changed (the swapchain made again, the
/// device idle); shaders reload with the engine's (<see cref="IReloadsShaders"/>). Its GPU time is measured on its own
/// (the <c>gpu</c> request, a Debug line) and in <see cref="GpuPass.Game"/>. <see cref="IDisposable.Dispose"/> may come
/// twice (the engine's shutdown, then the container's), and without <see cref="Initialize"/> (a run without graphics, or
/// a step before it failed), so a pass's constructor makes no Vulkan object; and when the engine gave up on a stuck
/// device (<see cref="VulkanContext.IsAbandoned"/>), a pass destroys none of its objects, as the engine's passes do.
/// </summary>
public interface IRenderPass : IReloadsShaders, IDisposable
{
    /// <summary>Its name in timings and log lines.</summary>
    string Name { get; }

    /// <summary>Where in the frame it records.</summary>
    RenderStage Stage { get; }

    /// <summary>Makes its buffers, layouts, and pipelines for its stage's targets. Main thread, once.</summary>
    void Initialize(RenderPassSetup setup);

    /// <summary>
    /// Writes the frame's data into frame slot <paramref name="slot"/>'s region of its memory (write only, in address
    /// order). Main thread, after the frame systems; allocates nothing.
    /// </summary>
    void Write(int slot, in RenderPassFrame frame);

    /// <summary>Records its work at its stage. Render thread; allocates nothing.</summary>
    void Record(in RenderPassContext context);

    /// <summary>Makes its pipelines again when <paramref name="setup"/>'s formats or samples differ from its own. Render thread, device idle.</summary>
    void OnTargetsChanged(RenderPassSetup setup);

    /// <summary>
    /// True when the pass reads the picture under it in the frame of slot <paramref name="slot"/> (the Screen stage
    /// alone): the engine then copies the screen's picture for it, which <see cref="RenderPassContext.Picture"/> samples
    /// (a blur, a change of colour over the whole screen). A pass sets what it answers in <see cref="Write"/>; the engine
    /// asks on the render thread, only in frames whose passes were written. None by default: nothing is copied.
    /// </summary>
    bool ReadsPicture(int slot) => false;
}

/// <summary>What a game's pass is given at <see cref="IRenderPass.Write"/>: the frame's time and view.</summary>
/// <param name="Time">The frame's time.</param>
/// <param name="View">The frame's view constants (the drawn size).</param>
public readonly record struct RenderPassFrame(FrameTime Time, ViewConstants View);

/// <summary>What a game's pass records with (<see cref="IRenderPass.Record"/>).</summary>
/// <param name="CommandBuffer">The frame's command buffer.</param>
/// <param name="Slot">The frame slot whose memory <see cref="IRenderPass.Write"/> wrote.</param>
/// <param name="Fresh">False for a frame presented again: its compute work is still in its slot.</param>
/// <param name="View">The view constants, their width and height the stage's (the drawn or the screen's size).</param>
/// <param name="Extent">The size drawn to.</param>
/// <param name="MeshSet">The mesh pass's descriptor set (its lighting, shadow maps, sky field, lights, and look).</param>
/// <param name="Picture">
/// At the Screen stage, in a frame where a pass reads the picture (<see cref="IRenderPass.ReadsPicture"/>), the set that
/// samples a copy of it (<see cref="RenderPassSetup.PictureSetLayout"/>); otherwise none (its handle 0): a pass that
/// needs it then draws nothing.
/// </param>
public readonly record struct RenderPassContext(
    CommandBuffer CommandBuffer,
    int Slot,
    bool Fresh,
    ViewConstants View,
    Extent2D Extent,
    DescriptorSet MeshSet,
    DescriptorSet Picture = default
);

/// <summary>
/// What a game's pass makes its objects with: the device, the allocator, the shader compiler (whose folder holds the
/// engine's shaders, and a game's in a folder of its own beside them, including the engine's as <c>../view.glsl</c>),
/// its stage's targets (formats and samples), and the mesh pass's descriptor set and layout (<c>mesh-data.glsl</c>,
/// <c>look.glsl</c>), with helpers that make layouts and pipelines for the stage.
/// </summary>
public sealed class RenderPassSetup
{
    internal RenderPassSetup(
        VulkanContext context,
        GpuMemoryAllocator allocator,
        ShaderCompiler shaders,
        RenderStage stage,
        Format colorFormat,
        SampleCountFlags samples,
        Format depthFormat,
        DescriptorSetLayout meshSetLayout,
        DescriptorSet meshSet,
        DescriptorSetLayout pictureSetLayout
    )
    {
        Context = context;
        Allocator = allocator;
        Shaders = shaders;
        Stage = stage;
        ColorFormat = colorFormat;
        Samples = samples;
        DepthFormat = depthFormat;
        MeshSetLayout = meshSetLayout;
        MeshSet = meshSet;
        PictureSetLayout = pictureSetLayout;
    }

    public VulkanContext Context { get; }

    public GpuMemoryAllocator Allocator { get; }

    public ShaderCompiler Shaders { get; }

    public RenderStage Stage { get; }

    /// <summary>The colour target's format (<see cref="Format.Undefined"/> for <see cref="RenderStage.Compute"/>).</summary>
    public Format ColorFormat { get; }

    /// <summary>The targets' samples a pixel: the mesh pass's inside the world's rendering, else one.</summary>
    public SampleCountFlags Samples { get; }

    /// <summary>The depth target's format (<see cref="Format.Undefined"/> where the stage has none).</summary>
    public Format DepthFormat { get; }

    /// <summary>The mesh pass's descriptor set layout, for a pipeline layout whose set 0 it is.</summary>
    public DescriptorSetLayout MeshSetLayout { get; }

    /// <summary>The mesh pass's descriptor set (also in each frame's <see cref="RenderPassContext.MeshSet"/>).</summary>
    public DescriptorSet MeshSet { get; }

    /// <summary>
    /// At the Screen stage, the layout of <see cref="RenderPassContext.Picture"/>'s set: binding 0 a combined image
    /// sampler (linear, clamped to the edge) of the screen's picture, read by the fragment stage, the swapchain's format,
    /// so it reads what the stage's target holds. None (its handle 0) at the other stages.
    /// </summary>
    public DescriptorSetLayout PictureSetLayout { get; }

    /// <summary>The frame slots: a pass keeps a region of its per-frame memory for each.</summary>
    public static int FramesInFlight => FrameSync.FramesInFlight;

    /// <summary>A pipeline layout of <paramref name="sets"/> and one push constant range of <paramref name="pushBytes"/> at 0.</summary>
    public PipelineLayout CreateLayout(
        ReadOnlySpan<DescriptorSetLayout> sets,
        ShaderStageFlags pushStages,
        uint pushBytes,
        string name
    ) => PipelineFactory.CreateLayout(Context, sets, pushStages, pushBytes, name);

    /// <summary>
    /// A triangle pipeline for the stage's targets (its colour format, samples, and depth format): the vertex shader finds
    /// its corners itself (no vertex input), <paramref name="cull"/> faces culled (front faces wind counter-clockwise on
    /// screen), depth compared Greater (reversed) where the stage has depth and <paramref name="depthTest"/> asks, written
    /// when <paramref name="depthWrite"/>, the colour blended as <paramref name="blend"/> says.
    /// </summary>
    public Pipeline CreateTriangles(
        byte[] vertexSpirv,
        byte[] fragmentSpirv,
        PipelineLayout layout,
        CullModeFlags cull,
        bool depthTest,
        bool depthWrite,
        ColorBlend blend,
        string name
    )
    {
        var depth =
            DepthFormat != Format.Undefined && depthTest ? new DepthState(DepthFormat, CompareOp.Greater, depthWrite)
            : DepthFormat != Format.Undefined ? new DepthState(DepthFormat, CompareOp.Always, false)
            : default;
        return PipelineFactory.CreateTriangles(
            Context,
            vertexSpirv,
            fragmentSpirv,
            layout,
            ColorFormat,
            cull,
            in depth,
            name,
            blend,
            Samples
        );
    }

    /// <summary>A compute pipeline.</summary>
    public Pipeline CreateCompute(byte[] spirv, PipelineLayout layout, string name) =>
        PipelineFactory.CreateCompute(Context, spirv, layout, name);

    /// <summary>
    /// A descriptor set of storage buffers: binding i the whole of <paramref name="buffers"/>[i], read by
    /// <paramref name="stages"/>, with its layout (for <see cref="CreateLayout"/>) and its own pool.
    /// </summary>
    public RenderPassBufferSet CreateBufferSet(ReadOnlySpan<GpuBuffer> buffers, ShaderStageFlags stages, string name) =>
        RenderPassBufferSet.Create(Context, buffers, stages, name);
}

/// <summary>
/// A game pass's descriptor set of storage buffers (<see cref="RenderPassSetup.CreateBufferSet"/>): its layout and set.
/// <see cref="Dispose"/> destroys the pool (and so the set) and the layout, not the buffers; safe to call more than once.
/// </summary>
public sealed unsafe class RenderPassBufferSet : IDisposable
{
    private readonly VulkanContext _context;
    private DescriptorPool _pool;
    private bool _disposed;

    private RenderPassBufferSet(VulkanContext context) => _context = context;

    /// <summary>The set's layout, for the pipeline layout.</summary>
    public DescriptorSetLayout Layout { get; private set; }

    /// <summary>The set, to bind.</summary>
    public DescriptorSet Set { get; private set; }

    internal static RenderPassBufferSet Create(
        VulkanContext context,
        ReadOnlySpan<GpuBuffer> buffers,
        ShaderStageFlags stages,
        string name
    )
    {
        if (buffers.IsEmpty)
        {
            throw new ArgumentException("A buffer set needs a buffer.", nameof(buffers));
        }

        var vk = context.Vk;
        var device = context.Device;
        var count = (uint)buffers.Length;
        var set = new RenderPassBufferSet(context);
        try
        {
            var bindings = new DescriptorSetLayoutBinding[buffers.Length];
            for (var i = 0; i < bindings.Length; i++)
            {
                bindings[i] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)i,
                    DescriptorType = DescriptorType.StorageBuffer,
                    DescriptorCount = 1,
                    StageFlags = stages,
                };
            }

            DescriptorSetLayout layout;
            fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
            {
                var layoutInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = count,
                    PBindings = bindingsPtr,
                };
                vk.CreateDescriptorSetLayout(device, &layoutInfo, null, &layout)
                    .ThrowIfFailed("vkCreateDescriptorSetLayout");
            }

            set.Layout = layout;
            var poolSize = new DescriptorPoolSize(DescriptorType.StorageBuffer, count);
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
            };
            vk.CreateDescriptorPool(device, &poolInfo, null, out set._pool).ThrowIfFailed("vkCreateDescriptorPool");

            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = set._pool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            DescriptorSet handle;
            vk.AllocateDescriptorSets(device, &allocateInfo, &handle).ThrowIfFailed("vkAllocateDescriptorSets");
            set.Set = handle;

            var infos = new DescriptorBufferInfo[buffers.Length];
            var writes = new WriteDescriptorSet[buffers.Length];
            fixed (DescriptorBufferInfo* infosPtr = infos)
            {
                for (var i = 0; i < infos.Length; i++)
                {
                    infos[i] = new DescriptorBufferInfo
                    {
                        Buffer = buffers[i].Buffer,
                        Offset = 0,
                        Range = buffers[i].Size,
                    };
                    writes[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = handle,
                        DstBinding = (uint)i,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.StorageBuffer,
                        PBufferInfo = infosPtr + i,
                    };
                }

                fixed (WriteDescriptorSet* writesPtr = writes)
                {
                    vk.UpdateDescriptorSets(device, count, writesPtr, 0, null);
                }
            }

            context.SetObjectName(ObjectType.DescriptorSet, handle.Handle, name);
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_context.IsAbandoned)
        {
            // Destroying the pool frees the set allocated from it.
            if (_pool.Handle != 0)
            {
                _context.Vk.DestroyDescriptorPool(_context.Device, _pool, null);
            }

            if (Layout.Handle != 0)
            {
                _context.Vk.DestroyDescriptorSetLayout(_context.Device, Layout, null);
            }
        }

        _pool = default;
        Layout = default;
        Set = default;
    }
}

/// <summary>
/// The game's passes (<see cref="IRenderPass"/>), run at their stages: made at the run's "render passes" step (at most
/// <see cref="GpuTimings.MostGamePasses"/>, named apart), written each frame after the frame systems, recorded by the
/// mesh pass (Compute, Opaque, Transparent) and the frame renderer (Scene, Screen: the rendering begun here, after a
/// barrier from the frame's earlier writes, with the viewport set), each timed on its own, made again with the
/// swapchain, and reloaded with the engine's shaders; the Screen stage's passes may read the picture under them
/// (<c>RenderPasses.Picture.cs</c>). With none registered, it records nothing. Created before the render thread starts.
/// </summary>
public sealed unsafe partial class RenderPasses : IDisposable
{
    private readonly IRenderPass[] _passes;
    private readonly VulkanContext _context;
    private readonly GpuMemoryAllocator _allocator;
    private readonly ShaderCompiler _shaders;
    private readonly MeshPass _meshPass;
    private readonly VulkanSwapchain _swapchain;
    private readonly GpuTimer _timer;
    private readonly GpuTimings _timings;
    private readonly ILogger<RenderPasses> _logger;
    private readonly bool[] _atStage = new bool[5];
    private bool _initialized;
    private bool _disposed;
    private int _slot;
    private int _timerSlot;
    private bool _fresh;

    public RenderPasses(
        IEnumerable<IRenderPass> passes,
        VulkanContext context,
        GpuMemoryAllocator allocator,
        ShaderCompiler shaders,
        MeshPass meshPass,
        VulkanSwapchain swapchain,
        GpuTimer timer,
        GpuTimings timings,
        ILogger<RenderPasses> logger
    )
    {
        _passes = [.. passes];
        _context = context;
        _allocator = allocator;
        _shaders = shaders;
        _meshPass = meshPass;
        _swapchain = swapchain;
        _timer = timer;
        _timings = timings;
        _logger = logger;
    }

    /// <summary>The game's passes.</summary>
    public IReadOnlyList<IRenderPass> Passes => _passes;

    /// <summary>True when a pass records at <paramref name="stage"/>.</summary>
    public bool Has(RenderStage stage) => _atStage[(int)stage];

    /// <summary>
    /// Checks the passes (at most <see cref="GpuTimings.MostGamePasses"/>, named apart) and makes each one's objects for
    /// its stage. The run's "render passes" step: after the mesh pass and the mesh assets, before the render thread.
    /// </summary>
    public void Initialize()
    {
        Check(_passes);
        foreach (var pass in _passes)
        {
            _atStage[(int)pass.Stage] = true;
        }

        if (_atStage[(int)RenderStage.Screen])
        {
            CreatePictureObjects();
        }

        foreach (var pass in _passes)
        {
            pass.Initialize(SetupFor(pass.Stage));
        }

        var names = new string[_passes.Length];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = _passes[i].Name;
            LogPass(_passes[i].Name, _passes[i].Stage);
        }

        _timings.GamePassNames = names;
        _initialized = true;
    }

    /// <summary>Throws for more passes than can be timed, or two of one name or none.</summary>
    internal static void Check(IReadOnlyList<IRenderPass> passes)
    {
        if (passes.Count > GpuTimings.MostGamePasses)
        {
            throw new InvalidOperationException(
                $"The game registers {passes.Count} render passes; at most {GpuTimings.MostGamePasses} are run."
            );
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pass in passes)
        {
            if (string.IsNullOrWhiteSpace(pass.Name) || !seen.Add(pass.Name))
            {
                throw new InvalidOperationException(
                    $"Each render pass needs a name of its own; '{pass.Name}' is empty or taken."
                );
            }
        }
    }

    /// <summary>Each pass writes the frame's data into the slot. Main thread, after the frame systems.</summary>
    public void Write(int slot, in FrameTime time, in ViewConstants view)
    {
        if (!_initialized)
        {
            return;
        }

        var frame = new RenderPassFrame(time, view);
        foreach (var pass in _passes)
        {
            pass.Write(slot, in frame);
        }

        // Read by the render thread with the slot's frame, which the exchange hands over after this.
        _written[slot] = true;
    }

    /// <summary>The frame's slot (for memory), its timer slot, and whether it is fresh, for the stages' records. Render thread.</summary>
    public void BeginFrame(int slot, int timerSlot, bool fresh)
    {
        _slot = slot;
        _timerSlot = timerSlot;
        _fresh = fresh;
        // Whether the passes wrote this frame's data (a frame presented again as the render thread stops has none); the
        // slot's next frame is written only after this one was recorded.
        _passesWritten = _written[slot];
        _written[slot] = false;
    }

    /// <summary>
    /// Records the passes of <paramref name="stage"/> where the caller stands (Compute outside rendering, Opaque and
    /// Transparent inside the world's): each timed on its own. Render thread.
    /// </summary>
    public void Record(RenderStage stage, CommandBuffer commandBuffer, in ViewConstants view, Extent2D extent)
    {
        if (!_atStage[(int)stage])
        {
            return;
        }

        var context = new RenderPassContext(commandBuffer, _slot, _fresh, view, extent, _meshPass.Set);
        for (var i = 0; i < _passes.Length; i++)
        {
            if (_passes[i].Stage != stage)
            {
                continue;
            }

            _timer.BeginGame(commandBuffer, _timerSlot, i);
            _passes[i].Record(in context);
            _timer.EndGame(commandBuffer, _timerSlot, i);
        }
    }

    /// <summary>
    /// The Scene stage's passes, in a rendering of <paramref name="target"/> and <paramref name="depth"/> at
    /// <paramref name="extent"/> begun here. Render thread, after the particles and the debug lines.
    /// </summary>
    public void RecordScene(
        CommandBuffer commandBuffer,
        ImageView target,
        ImageView depth,
        Extent2D extent,
        in ViewConstants view
    )
    {
        if (!_atStage[(int)RenderStage.Scene])
        {
            return;
        }

        BeginRendering(commandBuffer, target, depth, extent);
        Record(RenderStage.Scene, commandBuffer, in view, extent);
        _context.Vk.CmdEndRendering(commandBuffer);
    }

    /// <summary>
    /// The Screen stage's passes, in a rendering of the screen's <paramref name="target"/> (the view of the swapchain's
    /// <paramref name="image"/>; no depth) begun here, the view's size the screen's; first, when one reads the picture,
    /// its copy, timed with the stage's first pass. Render thread, after the upscale.
    /// </summary>
    public void RecordScreen(
        CommandBuffer commandBuffer,
        Image image,
        ImageView target,
        Extent2D screen,
        in ViewConstants view
    )
    {
        if (!_atStage[(int)RenderStage.Screen])
        {
            return;
        }

        var constants = view;
        constants.Width = screen.Width;
        constants.Height = screen.Height;
        // The picture's copy is timed with the stage's first pass, whose time it then includes.
        var first = _firstScreenPass;
        var picture = default(DescriptorSet);
        if (_pictureCopyable && FirstPictureReader() >= 0)
        {
            _timer.BeginGame(commandBuffer, _timerSlot, first);
            picture = CopyPicture(commandBuffer, image, screen);
        }
        else
        {
            first = -1;
            _pictures.NotUsed(_timerSlot, PictureFramesBeforeFree);
        }

        BeginRendering(commandBuffer, target, default, screen);
        var context = new RenderPassContext(commandBuffer, _slot, _fresh, constants, screen, _meshPass.Set, picture);
        for (var i = 0; i < _passes.Length; i++)
        {
            if (_passes[i].Stage != RenderStage.Screen)
            {
                continue;
            }

            if (i != first)
            {
                _timer.BeginGame(commandBuffer, _timerSlot, i);
            }

            _passes[i].Record(in context);
            _timer.EndGame(commandBuffer, _timerSlot, i);
        }

        _context.Vk.CmdEndRendering(commandBuffer);
    }

    /// <summary>The swapchain was made again: each pass makes its pipelines again if its targets changed. Render thread, device idle.</summary>
    public void OnSwapchainRecreated()
    {
        if (!_initialized)
        {
            return;
        }

        _pictures.Clear();
        UpdatePictureSupport(warn: false);
        foreach (var pass in _passes)
        {
            pass.OnTargetsChanged(SetupFor(pass.Stage));
        }
    }

    /// <summary>Disposes every pass. The GPU must be done with them. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialized = false;
        foreach (var pass in _passes)
        {
            pass.Dispose();
        }

        DestroyPictureObjects();
    }

    /// <summary>The setup of a stage: its targets' formats and samples now.</summary>
    private RenderPassSetup SetupFor(RenderStage stage)
    {
        var inWorld = stage is RenderStage.Opaque or RenderStage.Transparent;
        var color = stage == RenderStage.Compute ? Format.Undefined : _swapchain.ImageFormat;
        var depth = stage is RenderStage.Opaque or RenderStage.Transparent or RenderStage.Scene
            ? DeviceRequirements.DepthFormat
            : Format.Undefined;
        return new RenderPassSetup(
            _context,
            _allocator,
            _shaders,
            stage,
            inWorld ? _meshPass.ColorFormat : color,
            inWorld ? _meshPass.Samples : SampleCountFlags.Count1Bit,
            depth,
            _meshPass.SetLayout,
            _meshPass.Set,
            stage == RenderStage.Screen ? _pictureSetLayout : default
        );
    }

    /// <summary>
    /// Begins a rendering of the colour target (and the depth, when given) with what they hold, after a barrier from the
    /// frame's earlier writes to them, with the viewport (+y up, as every pass) and scissor set.
    /// </summary>
    private void BeginRendering(CommandBuffer commandBuffer, ImageView target, ImageView depth, Extent2D extent)
    {
        var vk = _context.Vk;
        var hasDepth = depth.Handle != 0;
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
                | AccessFlags2.DepthStencilAttachmentReadBit
                | AccessFlags2.DepthStencilAttachmentWriteBit,
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
            PDepthAttachment = hasDepth ? &depthAttachment : null,
        };
        vk.CmdBeginRendering(commandBuffer, &renderingInfo);
        var viewport = new Viewport(0, extent.Height, extent.Width, -(float)extent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Render pass of the game's: {Name} at the {Stage} stage")]
    private partial void LogPass(string name, RenderStage stage);
}
