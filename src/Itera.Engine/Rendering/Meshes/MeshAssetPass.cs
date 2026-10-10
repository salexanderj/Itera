using System.Numerics;
using Itera.Engine.Assets;
using Itera.Engine.Diagnostics;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The mesh assets on the GPU (<see cref="MeshAssets"/>: models read from glTF files, meshes a game builds), drawn inside
/// <see cref="MeshPass"/>'s rendering so they share its depth, samples, shadow maps, lighting, and fog (it calls
/// <see cref="IMeshPassDraws"/>): their shadows into each cascade after the world's and the models', and their surfaces
/// after the models, before the sky. At the run's "mesh assets on the gpu" step it lays every asset out
/// (<see cref="MeshAssetLayout"/>) in one vertex buffer read by index (<see cref="MeshAssetVertex"/>), one buffer of
/// 32-bit indices, one material table (<see cref="MeshAssetMaterial"/>), and one image per texture with every level (a
/// white stand-in first), all copied by the first frame from an upload buffer that goes once no frame in flight can read
/// it. Set 1 of its pipelines holds those, six samplers, and every frame slot's draws (<see cref="MeshAssetDraw"/>),
/// which <see cref="MeshAssetDraws"/> writes with the indirect commands, grouped into four pipeline ranges
/// (<see cref="MeshAssetRange"/>); blended materials' draws come in a fourth list of the slot (<see cref="BlendedList"/>),
/// sorted back to front, drawn after the sky (<see cref="RecordTransparent"/>) with one pipeline that culls nothing,
/// tests depth without writing it, and blends by alpha, each draw showing the faces it names. Skinned assets also have their joints and weights (<see cref="MeshSkinVertex"/>), and
/// each frame slot a region of the vertex buffer (<c>Engine:Renderer:SkinMegabytes</c>) that <c>mesh-skin.comp</c> fills
/// with the frame's posed instances (<see cref="RecordSkinning"/>, before the cascades), from the slot's skin groups
/// (<see cref="MeshSkinGroup"/>) and skinning matrices. Created before the render thread starts; recorded by the render
/// thread.
/// </summary>
public sealed unsafe partial class MeshAssetPass : IDisposable, IReloadsShaders, IMeshAssetTarget, IMeshPassDraws
{
    /// <summary>Draws (one per instance and primitive) one list of a frame slot holds.</summary>
    public const int MaxDraws = 16_384;

    /// <summary>Commands (one per primitive drawn) one list of a frame slot holds.</summary>
    public const int MaxCommands = 4_096;

    /// <summary>The samplers of <see cref="MeshSampler"/>, in its order.</summary>
    public const int Samplers = 6;

    /// <summary>Posed instances a frame slot skins (one <see cref="MeshSkinGroup"/> each).</summary>
    public const int MaxSkinGroups = MeshInstances.MaxInstances;

    /// <summary>Vertices one invocation group of <c>mesh-skin.comp</c> skins (its <c>local_size_x</c>).</summary>
    private const int SkinGroupSize = 64;

    /// <summary>
    /// The most vertices one posed asset may have: a dispatch row of invocation groups every device takes
    /// (<c>maxComputeWorkGroupCount</c>'s least, 65,535). Larger assets are drawn unposed.
    /// </summary>
    public const int MostSkinnedVertices = 65_535 * SkinGroupSize;

    /// <summary>The slot's list of blended draws, after the mesh pass's three (the camera's and the two cascades').</summary>
    public const int BlendedList = MeshPass.Lists;

    /// <summary>The lists of draws and commands a frame slot holds: the mesh pass's three and the blended.</summary>
    private const int SlotLists = MeshPass.Lists + 1;

    private const int Bindings = 8;
    private const string MaskDefine = "MESH_ASSET_MASK";
    private const string BlendDefine = "MESH_ASSET_BLEND";

    private readonly VulkanContext _context;
    private readonly ShaderCompiler _shaders;
    private readonly GpuMemoryAllocator _allocator;
    private readonly MeshPass _meshPass;
    private readonly GpuTimer _timer;
    private readonly MeshAssets _assets;
    private readonly RenderViews _views;
    private readonly IOptions<EngineOptions> _options;
    private readonly ILogger<MeshAssetPass> _logger;
    private readonly Sampler[] _samplers = new Sampler[Samplers];
    private readonly Pipeline[] _pipelines = new Pipeline[MeshAssetLayout.ListRanges];
    private readonly Pipeline[] _shadowPipelines = new Pipeline[MeshAssetLayout.ListRanges];
    private Pipeline _blendPipeline;
    private readonly Pipeline[] _viewPipelines = new Pipeline[MeshAssetLayout.ListRanges];
    private Pipeline _viewBlendPipeline;
    private bool _viewPipelinesWanted;
    private ImageView[] _viewImages = [];
    private MeshAssetLayout? _assetLayout;
    private GpuBuffer? _upload;
    private GpuBuffer? _vertices;
    private GpuBuffer? _indices;
    private GpuBuffer? _materials;
    private GpuBuffer? _draws;
    private GpuBuffer? _commands;
    private GpuBuffer? _skins;
    private GpuBuffer? _skinGroups;
    private GpuBuffer? _palettes;
    private BufferCopy _skinCopy;
    private int _skinBase;
    private int _skinCapacity;
    private PipelineLayout _skinLayout;
    private Pipeline _skinPipeline;
    private byte[] _skinSpirv = [];
    private bool _fresh;
    private GpuImage[]? _textures;
    private BufferImageCopy[][]? _textureCopies;
    private BufferCopy _vertexCopy;
    private BufferCopy _indexCopy;
    private BufferCopy _materialCopy;
    private ulong _textureBytes;
    private bool _uploaded;
    private int _uploadFramesLeft;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private PipelineLayout _shadowLayout;
    private byte[] _vertexSpirv = [];
    private byte[] _fragmentSpirv = [];
    private byte[] _maskedFragmentSpirv = [];
    private byte[] _blendVertexSpirv = [];
    private byte[] _blendFragmentSpirv = [];
    private byte[] _shadowVertexSpirv = [];
    private byte[] _maskedShadowVertexSpirv = [];
    private byte[] _shadowFragmentSpirv = [];
    private byte[] _depthOnlySpirv = [];
    private Format _format;
    private SampleCountFlags _samples;
    private MeshAssetFrame _frame;
    private bool _disposed;

    public MeshAssetPass(
        VulkanContext context,
        ShaderCompiler shaders,
        GpuMemoryAllocator allocator,
        MeshPass meshPass,
        GpuTimer timer,
        MeshAssets assets,
        RenderViews views,
        IOptions<EngineOptions> options,
        ILogger<MeshAssetPass> logger
    )
    {
        _context = context;
        _shaders = shaders;
        _allocator = allocator;
        _meshPass = meshPass;
        _timer = timer;
        _assets = assets;
        _views = views;
        _options = options;
        _logger = logger;
    }

    /// <summary>Where the run's assets lie in the pass's buffers; set by <see cref="Initialize"/>.</summary>
    public MeshAssetLayout Layout => _assetLayout ?? throw NotInitialised();

    /// <summary>
    /// Lays out the run's assets, makes the buffers, the textures, the samplers, the descriptor set, and the pipelines,
    /// and fills the upload buffer. After the run has read its assets and the mesh pass is made; before the render thread
    /// starts.
    /// </summary>
    public void Initialize()
    {
        var layout = MeshAssetLayout.From(_assets);
        _assetLayout = layout;
        CreateBuffersAndTextures(layout);
        CreateSamplers();
        CreateDescriptors();
        TakeShaders((byte[][])CompileShaders());
        CreatePipelines(_meshPass.ColorFormat, _meshPass.Samples);
        var drawMegabytes = (_draws!.Size + _commands!.Size) / (1024.0 * 1024.0) / FrameSync.FramesInFlight;
        LogCreated(
            layout.VertexCount,
            layout.IndexCount / 3,
            layout.MaterialCount,
            _textures!.Length - 1,
            _textureBytes / (1024.0 * 1024.0),
            drawMegabytes
        );
        if (_skinCapacity > 0)
        {
            var skinMegabytes = (double)_skinCapacity * sizeof(MeshAssetVertex) / (1024.0 * 1024.0);
            LogSkinning(layout.SkinVertexCount, _skinCapacity, skinMegabytes);
        }

        // A skinned asset that no frame slot's room can take is always drawn unposed: said once, here.
        foreach (var place in layout.Assets)
        {
            if (place.FirstSkin >= 0 && place.VertexCount > Math.Min(_skinCapacity, MostSkinnedVertices))
            {
                LogSkinTooSmall(place.VertexCount, _skinCapacity);
                break;
            }
        }
    }

    /// <summary>Makes the camera's pipelines again when the mesh pass's colour format or samples changed. Render thread, device idle; after the mesh pass's.</summary>
    public void OnSwapchainRecreated()
    {
        if (_meshPass.ColorFormat != _format || _meshPass.Samples != _samples)
        {
            DestroyPipelines();
            CreatePipelines(_meshPass.ColorFormat, _meshPass.Samples);
        }
    }

    public Span<MeshAssetDraw> DrawsOf(int slot, int list) =>
        new(Draws.Mapped + (ulong)DrawBase(slot, list) * (ulong)sizeof(MeshAssetDraw), MaxDraws);

    public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) =>
        new(Commands.Mapped + CommandsOffset(slot, list, 0), MaxCommands);

    /// <summary>
    /// The index in the shaders' draws of list <paramref name="list"/> of <paramref name="slot"/>'s first (the camera's,
    /// the cascades', or <see cref="BlendedList"/>).
    /// </summary>
    public static int DrawBase(int slot, int list) => (slot * SlotLists + list) * MaxDraws;

    /// <summary>The index in the slot's command buffer of list <paramref name="list"/> of <paramref name="slot"/>'s first command.</summary>
    public static int CommandBase(int slot, int list) => (slot * SlotLists + list) * MaxCommands;

    /// <summary>The index in the shaders' skin groups of <paramref name="slot"/>'s first.</summary>
    public static int GroupBase(int slot) => slot * MaxSkinGroups;

    /// <summary>The index in the shaders' skinning matrices of <paramref name="slot"/>'s first.</summary>
    public static int PaletteBase(int slot) => slot * MeshInstances.MaxPaletteJoints;

    public Span<MeshSkinGroup> SkinGroupsOf(int slot) =>
        new(SkinGroups.Mapped + (ulong)GroupBase(slot) * (ulong)sizeof(MeshSkinGroup), MaxSkinGroups);

    public Span<Matrix4x4> PalettesOf(int slot) =>
        new(Palettes.Mapped + (ulong)PaletteBase(slot) * (ulong)sizeof(Matrix4x4), MeshInstances.MaxPaletteJoints);

    /// <summary>The skinned vertices a frame slot's region holds; 0 when the run read no skinned asset.</summary>
    public int SkinVertexCapacity => _skinCapacity;

    public int SkinVertexBase(int slot) => _skinBase + slot * _skinCapacity;

    /// <summary>
    /// The frame's counts (<see cref="MeshAssetFrame"/>), kept for this frame's recording, and what the main thread wrote
    /// into <paramref name="slot"/> made visible to the GPU on memory that is not coherent. A frame presented again
    /// (<paramref name="fresh"/> false, on the way to stopping) skins nothing: its skinned vertices are still in its slot.
    /// Render thread, before the mesh pass records.
    /// </summary>
    public void BeginFrame(int slot, in MeshAssetFrame frame, bool fresh = true)
    {
        _frame = frame;
        _fresh = fresh;
        if (fresh && frame.SkinGroups > 0)
        {
            SkinGroups.Flush(
                (ulong)GroupBase(slot) * (ulong)sizeof(MeshSkinGroup),
                (ulong)Math.Min(frame.SkinGroups, MaxSkinGroups) * (ulong)sizeof(MeshSkinGroup)
            );
            Palettes.Flush(
                (ulong)PaletteBase(slot) * (ulong)sizeof(Matrix4x4),
                (ulong)Math.Clamp(frame.SkinJoints, 0, MeshInstances.MaxPaletteJoints) * (ulong)sizeof(Matrix4x4)
            );
        }

        var blended = Math.Clamp(frame.Blended, 0, MaxCommands);
        if (blended > 0)
        {
            Draws.Flush(
                (ulong)DrawBase(slot, BlendedList) * (ulong)sizeof(MeshAssetDraw),
                (ulong)blended * (ulong)sizeof(MeshAssetDraw)
            );
            Commands.Flush(
                CommandsOffset(slot, BlendedList, 0),
                (ulong)blended * (ulong)sizeof(DrawIndexedIndirectCommand)
            );
        }

        for (var list = 0; list < MeshPass.Lists; list++)
        {
            var counts = frame.List(list);
            var draws = Math.Clamp(counts.Draws, 0, MaxDraws);
            if (draws > 0)
            {
                Draws.Flush(
                    (ulong)DrawBase(slot, list) * (ulong)sizeof(MeshAssetDraw),
                    (ulong)draws * (ulong)sizeof(MeshAssetDraw)
                );
            }

            var commands = Math.Clamp(counts.Commands, 0, MaxCommands);
            if (commands > 0)
            {
                Commands.Flush(
                    CommandsOffset(slot, list, 0),
                    (ulong)commands * (ulong)sizeof(DrawIndexedIndirectCommand)
                );
            }
        }
    }

    /// <summary>
    /// The first time only: copies the vertices, indices, materials, and every texture's levels from the upload buffer
    /// and leaves them for the shaders to read; <see cref="FrameSync.FramesInFlight"/> frames later no frame in flight can
    /// read the buffer, and it goes. Host writes need no barrier: the queue submit makes them visible. Outside rendering,
    /// every frame (under the loading screen too). Render thread.
    /// </summary>
    public void RecordUploads(CommandBuffer commandBuffer)
    {
        if (_uploaded)
        {
            if (_upload is not null && --_uploadFramesLeft < 0)
            {
                _upload.Dispose();
                _upload = null;
            }

            return;
        }

        _uploaded = true;
        _uploadFramesLeft = FrameSync.FramesInFlight;
        var vk = _context.Vk;
        var upload = Upload.Buffer;
        CopyBuffer(commandBuffer, upload, Vertices.Buffer, _vertexCopy);
        CopyBuffer(commandBuffer, upload, Indices.Buffer, _indexCopy);
        CopyBuffer(commandBuffer, upload, Materials.Buffer, _materialCopy);
        CopyBuffer(commandBuffer, upload, Skins.Buffer, _skinCopy);
        for (var t = 0; t < _textures!.Length; t++)
        {
            var image = _textures[t];
            TextureBarrier(commandBuffer, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var copies = _textureCopies![t];
            fixed (BufferImageCopy* regions = copies)
            {
                vk.CmdCopyBufferToImage(
                    commandBuffer,
                    upload,
                    image.Image,
                    ImageLayout.TransferDstOptimal,
                    (uint)copies.Length,
                    regions
                );
            }

            TextureBarrier(commandBuffer, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            _textureCopies[t] = [];
        }

        // The buffers' copies before every later read: vertices, materials, and skins in the shaders, indices as indices.
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask =
                PipelineStageFlags2.IndexInputBit
                | PipelineStageFlags2.VertexShaderBit
                | PipelineStageFlags2.FragmentShaderBit
                | PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.IndexReadBit | AccessFlags2.ShaderStorageReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    /// <summary>
    /// Skins the frame's posed instances (<see cref="MeshSkinGroup"/>) into the slot's region of the vertex buffer, before
    /// the cascades and the camera read them: one row of invocation groups an instance. Only on a fresh frame. Outside
    /// rendering. Timed as <see cref="GpuPass.Skinning"/>.
    /// </summary>
    public void RecordSkinning(CommandBuffer commandBuffer, int slot, int timerSlot)
    {
        var groups = Math.Min(_frame.SkinGroups, MaxSkinGroups);
        if (!_fresh || groups <= 0 || _frame.SkinVertices <= 0)
        {
            return;
        }

        var vk = _context.Vk;
        _timer.Begin(commandBuffer, timerSlot, GpuPass.Skinning);
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _skinPipeline);
        var set = _set;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _skinLayout, 1, 1, &set, 0, null);
        var constants = new MeshSkinConstants { FirstGroup = (uint)GroupBase(slot), Groups = (uint)groups };
        vk.CmdPushConstants(
            commandBuffer,
            _skinLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)sizeof(MeshSkinConstants),
            &constants
        );
        vk.CmdDispatch(
            commandBuffer,
            (uint)((_frame.SkinVertices + SkinGroupSize - 1) / SkinGroupSize),
            (uint)groups,
            1
        );

        // The skinned vertices before the cascades' and the camera's vertex shaders read them. Their last readers, this
        // slot's frame before, finished before its fence.
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.VertexShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        vk.CmdPipelineBarrier2(commandBuffer, &dependency);
        _timer.End(commandBuffer, timerSlot, GpuPass.Skinning);
    }

    /// <summary>
    /// Draws the cascade's list of this frame into the cascade's depth map, inside its rendering (after the world's and the
    /// models'): its viewport and scissor, both sets, the cascade's push constants, and each range's commands.
    /// </summary>
    public void RecordShadows(CommandBuffer commandBuffer, int slot, int cascade)
    {
        var list = _frame.List(1 + cascade);
        if (list.Commands <= 0)
        {
            return;
        }

        var vk = _context.Vk;
        var texels = _meshPass.ShadowMapTexels;
        var viewport = new Viewport(0, 0, texels, texels, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(texels, texels));
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        BindSets(commandBuffer, _shadowLayout);
        var constants = new MeshShadowConstants { Slot = (uint)slot, Cascade = (uint)cascade };
        vk.CmdPushConstants(
            commandBuffer,
            _shadowLayout,
            ShaderStageFlags.VertexBit,
            0,
            (uint)sizeof(MeshShadowConstants),
            &constants
        );
        DrawRanges(commandBuffer, slot, 1 + cascade, in list, _shadowPipelines);
    }

    /// <summary>
    /// Draws the camera's list of this frame inside the mesh pass's main rendering (after the models, before the sky;
    /// its viewport and scissor are set): both sets, the view's push constants, and each range's commands. Timed as
    /// <see cref="GpuPass.Meshes"/>.
    /// </summary>
    public void RecordCamera(CommandBuffer commandBuffer, int slot, in ViewConstants view, int timerSlot)
    {
        var list = _frame.Camera;
        if (list.Commands <= 0)
        {
            return;
        }

        _timer.Begin(commandBuffer, timerSlot, GpuPass.Meshes);
        BindSets(commandBuffer, _layout);
        var constants = view;
        _context.Vk.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(ViewConstants),
            &constants
        );
        DrawRanges(commandBuffer, slot, 0, in list, _pipelines);
        _timer.End(commandBuffer, timerSlot, GpuPass.Meshes);
    }

    /// <summary>
    /// Draws the frame's blended draws, sorted back to front (<see cref="MeshAssetDraws"/>), in the mesh pass's main
    /// rendering after the sky: both sets, the view's push constants, and one indirect draw of every command in order
    /// (the device draws them in that order, each blended over the ones before). Timed with the mesh pass's world.
    /// </summary>
    public void RecordTransparent(CommandBuffer commandBuffer, int slot, in ViewConstants view)
    {
        var commands = Math.Clamp(_frame.Blended, 0, MaxCommands);
        if (commands <= 0)
        {
            return;
        }

        var vk = _context.Vk;
        BindSets(commandBuffer, _layout);
        var constants = view;
        vk.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(ViewConstants),
            &constants
        );
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _blendPipeline);
        vk.CmdDrawIndexedIndirect(
            commandBuffer,
            Commands.Buffer,
            CommandsOffset(slot, BlendedList, 0),
            (uint)commands,
            (uint)sizeof(DrawIndexedIndirectCommand)
        );
    }

    public object CompileShaders()
    {
        var count = $"MESH_TEXTURE_COUNT {Math.Max(_textures is null ? 1 : SlotCount, 1)}";
        return new[]
        {
            _shaders.Compile("mesh-asset.vert", [count]),
            _shaders.Compile("mesh-asset.frag", [count]),
            _shaders.Compile("mesh-asset.frag", [count, MaskDefine]),
            _shaders.Compile("mesh-asset.frag", [count, BlendDefine]),
            _shaders.Compile("mesh-asset.vert", [count, BlendDefine]),
            _shaders.Compile("mesh-asset-shadow.vert", [count]),
            _shaders.Compile("mesh-asset-shadow.vert", [count, MaskDefine]),
            _shaders.Compile("mesh-asset-shadow.frag", [count]),
            _shaders.Compile("depth-only.frag"),
            _shaders.Compile("mesh-skin.comp", [count]),
        };
    }

    public int ReplacePipelines(object compiled)
    {
        DestroyPipelines();
        TakeShaders((byte[][])compiled);
        CreatePipelines(_format, _samples);
        return (2 * MeshAssetLayout.ListRanges)
            + 2
            + (_viewBlendPipeline.Handle != 0 ? MeshAssetLayout.ListRanges + 1 : 0);
    }

    /// <summary>Destroys everything the pass made. The GPU must be done with it. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyPipelines();
        if (!_context.IsAbandoned)
        {
            var vk = _context.Vk;
            var device = _context.Device;
            if (_layout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _layout, null);
            }

            if (_shadowLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _shadowLayout, null);
            }

            if (_skinLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _skinLayout, null);
            }

            if (_descriptorPool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device, _descriptorPool, null);
            }

            if (_setLayout.Handle != 0)
            {
                vk.DestroyDescriptorSetLayout(device, _setLayout, null);
            }

            foreach (var sampler in _samplers)
            {
                if (sampler.Handle != 0)
                {
                    vk.DestroySampler(device, sampler, null);
                }
            }
        }

        _layout = default;
        _shadowLayout = default;
        _skinLayout = default;
        _descriptorPool = default;
        _setLayout = default;
        Array.Clear(_samplers);
        foreach (var texture in _textures ?? [])
        {
            texture?.Dispose();
        }

        _textures = null;
        _upload?.Dispose();
        _vertices?.Dispose();
        _indices?.Dispose();
        _materials?.Dispose();
        _draws?.Dispose();
        _commands?.Dispose();
        _skins?.Dispose();
        _skinGroups?.Dispose();
        _palettes?.Dispose();
        _skins = null;
        _skinGroups = null;
        _palettes = null;
        _upload = null;
        _vertices = null;
        _indices = null;
        _materials = null;
        _draws = null;
        _commands = null;
    }

    private void BindSets(CommandBuffer commandBuffer, PipelineLayout layout) =>
        BindSets(commandBuffer, layout, _meshPass.Set, _set);

    /// <summary>Binds set 0 (the mesh pass's layout) and set 1 (this pass's), and the indices.</summary>
    private void BindSets(CommandBuffer commandBuffer, PipelineLayout layout, DescriptorSet meshSet, DescriptorSet set)
    {
        var sets = stackalloc DescriptorSet[2] { meshSet, set };
        _context.Vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, layout, 0, 2, sets, 0, null);
        _context.Vk.CmdBindIndexBuffer(commandBuffer, Indices.Buffer, 0, IndexType.Uint32);
    }

    /// <summary>Each range's commands of the list, with its pipeline.</summary>
    private void DrawRanges(
        CommandBuffer commandBuffer,
        int slot,
        int list,
        in MeshAssetList counts,
        Pipeline[] pipelines
    ) => DrawRanges(commandBuffer, Commands.Buffer, CommandsOffset(slot, list, 0), MaxCommands, in counts, pipelines);

    /// <summary>Each range's commands of a list starting at <paramref name="offset"/> in <paramref name="buffer"/>, with its pipeline.</summary>
    private void DrawRanges(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer buffer,
        ulong offset,
        int most,
        in MeshAssetList counts,
        Pipeline[] pipelines
    )
    {
        var vk = _context.Vk;
        for (var range = 0; range < MeshAssetLayout.ListRanges; range++)
        {
            var first = counts.FirstOf((MeshAssetRange)range);
            var commands = Math.Clamp(counts.Of((MeshAssetRange)range), 0, Math.Max(most - first, 0));
            if (commands == 0)
            {
                continue;
            }

            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipelines[range]);
            vk.CmdDrawIndexedIndirect(
                commandBuffer,
                buffer,
                offset + (ulong)first * (ulong)sizeof(DrawIndexedIndirectCommand),
                (uint)commands,
                (uint)sizeof(DrawIndexedIndirectCommand)
            );
        }
    }

    private void CopyBuffer(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Silk.NET.Vulkan.Buffer target,
        BufferCopy region
    )
    {
        if (region.Size == 0)
        {
            return;
        }

        _context.Vk.CmdCopyBuffer(commandBuffer, source, target, 1, &region);
    }

    /// <summary>Moves every level of <paramref name="image"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private void TextureBarrier(CommandBuffer commandBuffer, GpuImage image, ImageLayout from, ImageLayout to)
    {
        var toShader = to == ImageLayout.ShaderReadOnlyOptimal;
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = from == ImageLayout.Undefined ? PipelineStageFlags2.None : PipelineStageFlags2.CopyBit,
            SrcAccessMask = from == ImageLayout.Undefined ? AccessFlags2.None : AccessFlags2.TransferWriteBit,
            DstStageMask = toShader ? PipelineStageFlags2.FragmentShaderBit : PipelineStageFlags2.CopyBit,
            DstAccessMask = toShader ? AccessFlags2.ShaderSampledReadBit : AccessFlags2.TransferWriteBit,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, image.Mips, 0, image.Layers),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void TakeShaders(byte[][] compiled)
    {
        _vertexSpirv = compiled[0];
        _fragmentSpirv = compiled[1];
        _maskedFragmentSpirv = compiled[2];
        _blendFragmentSpirv = compiled[3];
        _blendVertexSpirv = compiled[4];
        _shadowVertexSpirv = compiled[5];
        _maskedShadowVertexSpirv = compiled[6];
        _shadowFragmentSpirv = compiled[7];
        _depthOnlySpirv = compiled[8];
        _skinSpirv = compiled[9];
    }

    private static ulong CommandsOffset(int slot, int list, int first) =>
        ((ulong)CommandBase(slot, list) + (ulong)first) * (ulong)sizeof(DrawIndexedIndirectCommand);

    /// <summary>The texture array's slots: the asset textures kept, then one an image view.</summary>
    private int SlotCount => _textures!.Length + _views.Count;

    /// <summary>The image view <paramref name="view"/>'s shown image, or the white stand-in until it is made.</summary>
    private ImageView ViewImage(int view) =>
        view < _viewImages.Length && _viewImages[view].Handle != 0 ? _viewImages[view] : _textures![0].View;

    private GpuBuffer Upload => _upload ?? throw NotInitialised();

    private GpuBuffer Vertices => _vertices ?? throw NotInitialised();

    private GpuBuffer Indices => _indices ?? throw NotInitialised();

    private GpuBuffer Materials => _materials ?? throw NotInitialised();

    private GpuBuffer Draws => _draws ?? throw NotInitialised();

    private GpuBuffer Commands => _commands ?? throw NotInitialised();

    private GpuBuffer Skins => _skins ?? throw NotInitialised();

    private GpuBuffer SkinGroups => _skinGroups ?? throw NotInitialised();

    private GpuBuffer Palettes => _palettes ?? throw NotInitialised();

    private static InvalidOperationException NotInitialised() => new("The mesh asset pass has not been initialised.");

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Mesh assets on the GPU: {Vertices} vertices, {Triangles} triangles, {Materials} material(s), {Textures} texture(s) of {Megabytes:F1} MB; draws {DrawMegabytes:F1} MB a frame slot"
    )]
    private partial void LogCreated(
        int vertices,
        int triangles,
        int materials,
        int textures,
        double megabytes,
        double drawMegabytes
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets on the GPU: an image view is shown on material {Material} of {Asset}, which no mesh asset of the run has"
    )]
    private partial void LogUnknownViewMaterial(string asset, string material);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets on the GPU: {LeftOut} texture(s) left out past the device's room for {Kept}; their materials are drawn with their factors alone"
    )]
    private partial void LogTexturesLeftOut(int leftOut, int kept);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Mesh assets on the GPU: {SkinVertices} skinned vertices; posed instances skinned into {Capacity} vertices ({Megabytes:F1} MB) a frame slot"
    )]
    private partial void LogSkinning(int skinVertices, int capacity, double megabytes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets on the GPU: a skinned asset of {Vertices} vertices does not fit a frame slot's room for skinned vertices ({Capacity}; Engine:Renderer:SkinMegabytes and the device's storage buffer range) or one skinning: it is drawn unposed"
    )]
    private partial void LogSkinTooSmall(int vertices, int capacity);
}

/// <summary>
/// What draws inside <see cref="MeshPass"/>'s renderings besides the world and the models (<see cref="MeshAssetPass"/>):
/// its compute work before the cascades, its shadows into each cascade, its surfaces in the main view after the models,
/// and its blended surfaces after the sky. Render thread.
/// </summary>
public interface IMeshPassDraws
{
    /// <summary>Work on the GPU before the cascades, outside rendering: the posed instances' skinning.</summary>
    void RecordSkinning(CommandBuffer commandBuffer, int slot, int timerSlot);

    /// <summary>Draws into cascade <paramref name="cascade"/>'s depth map, inside its rendering; sets its own viewport.</summary>
    void RecordShadows(CommandBuffer commandBuffer, int slot, int cascade);

    /// <summary>Draws in the main view, inside its rendering, after the models; the viewport is set.</summary>
    void RecordCamera(CommandBuffer commandBuffer, int slot, in ViewConstants view, int timerSlot);

    /// <summary>Draws blended surfaces in the main view, inside its rendering, after the sky; the viewport is set.</summary>
    void RecordTransparent(CommandBuffer commandBuffer, int slot, in ViewConstants view);
}
