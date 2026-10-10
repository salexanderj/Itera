using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// What an image view (<see cref="RenderViews"/>) drew in a frame slot: whether it was drawn, its view's constants, and
/// its lists' counts (<see cref="RenderViewTarget"/>). Written by the main thread before the frame is published, read by
/// the render thread as it records that frame.
/// </summary>
internal struct RenderViewFrame
{
    /// <summary>True when the view is drawn in this frame.</summary>
    public bool Due;

    /// <summary>The view's constants: its camera at its image's size, the frame's sun and sky, no shadows.</summary>
    public ViewConstants View;

    /// <summary>The world's draws written (<see cref="MeshDraw"/>), one a block drawn.</summary>
    public int Records;

    /// <summary>The world's commands in the view's list.</summary>
    public int Draws;

    /// <summary>The models' list.</summary>
    public MeshModelList Models;

    /// <summary>The mesh assets' list, in its four pipeline ranges.</summary>
    public MeshAssetList Assets;

    /// <summary>The mesh assets' blended commands, back to front.</summary>
    public int Blended;
}

/// <summary>
/// One image view's side of the mesh pass: its own frame records, so it is drawn with <see cref="MeshPass"/>'s and
/// <see cref="MeshAssetPass"/>'s pipelines and shaders through sets of their layouts that name them (the world's draws
/// and commands, its lighting record (no shadows, the sky field's place), its point lights and their clusters, the
/// models' draws and commands, and the mesh assets' draws and commands, the seen and the blended), each a region per
/// frame slot; and its images: the colour and depth it is drawn into, one sample a pixel (the passes' pipelines for
/// views), and the image the shaders and the HUD read (<see cref="Shown"/>), which the drawn colour is copied into after
/// each update, so no rendering ever reads the image it draws into. Its <see cref="View"/> is its frustum and lights, as
/// <see cref="MeshView"/> is the frame's. Made at the run's "image views" step (<see cref="RenderViewPass"/>).
/// </summary>
internal sealed unsafe class RenderViewTarget : IRenderViewLists, IDisposable
{
    /// <summary>The world's blocks one frame slot of a view draws, at most.</summary>
    public const int MaxDraws = 8_192;

    /// <summary>The models' draws (one per block of a model drawn) one frame slot of a view holds.</summary>
    public const int MaxModelRecords = 16_384;

    /// <summary>The models' commands (one per block of a model level) one frame slot of a view holds.</summary>
    public const int MaxModelCommands = 4_096;

    /// <summary>The mesh assets' draws (one per instance and primitive) one list of a view's frame slot holds.</summary>
    public const int MaxAssetDraws = 4_096;

    /// <summary>The mesh assets' commands (one per primitive drawn) one list of a view's frame slot holds.</summary>
    public const int MaxAssetCommands = 1_024;

    /// <summary>The mesh assets' list of what the view sees, in its pipeline ranges.</summary>
    public const int AssetSeenList = 0;

    /// <summary>The mesh assets' list of blended draws, back to front.</summary>
    public const int AssetBlendedList = 1;

    private const int AssetLists = 2;

    private readonly VulkanContext _context;
    private readonly GpuMemoryAllocator _allocator;
    private readonly RenderViewFrame[] _frames = new RenderViewFrame[FrameSync.FramesInFlight];
    private DescriptorPool _meshPool;
    private DescriptorPool _assetPool;
    private bool _disposed;

    public RenderViewTarget(
        int index,
        RenderViewInfo info,
        VulkanContext context,
        GpuMemoryAllocator allocator,
        IOptions<EngineOptions> options
    )
    {
        Index = index;
        Info = info;
        _context = context;
        _allocator = allocator;
        View = new MeshView(this, options);
    }

    /// <summary>The view's place among <see cref="RenderViews"/>'.</summary>
    public int Index { get; }

    /// <summary>The view's declaration.</summary>
    public RenderViewInfo Info { get; }

    /// <summary>The view's size in texels.</summary>
    public Extent2D Extent => new((uint)Info.Width, (uint)Info.Height);

    /// <summary>The view's frustum and point lights each frame it is drawn (main thread).</summary>
    public MeshView View { get; }

    public GpuBuffer Draws { get; private set; } = null!;

    public GpuBuffer Commands { get; private set; } = null!;

    public GpuBuffer Lighting { get; private set; } = null!;

    public GpuBuffer Lights { get; private set; } = null!;

    public GpuBuffer Clusters { get; private set; } = null!;

    public GpuBuffer ModelDraws { get; private set; } = null!;

    public GpuBuffer ModelCommands { get; private set; } = null!;

    public GpuBuffer AssetDraws { get; private set; } = null!;

    public GpuBuffer AssetCommands { get; private set; } = null!;

    /// <summary>The colour the view is drawn into.</summary>
    public GpuImage Colour { get; private set; } = null!;

    /// <summary>The depth the view is drawn with.</summary>
    public GpuImage Depth { get; private set; } = null!;

    /// <summary>
    /// The image the shaders and the HUD read: the last update's colour, one sample a pixel, always in the shader-read
    /// layout but while an update is copied into it, outside any rendering.
    /// </summary>
    public GpuImage Shown { get; private set; } = null!;

    /// <summary>True once <see cref="Shown"/> has been cleared, its first frame.</summary>
    public bool ShownReady { get; set; }

    /// <summary>The view's set of the mesh pass's layout (set 0 of its pipelines).</summary>
    public DescriptorSet MeshSet { get; private set; }

    /// <summary>The view's set of the mesh asset pass's layout (set 1 of its pipelines).</summary>
    public DescriptorSet AssetSet { get; private set; }

    /// <summary>The format <see cref="Colour"/> and <see cref="Shown"/> were made in: the mesh pass's.</summary>
    public Format Format { get; private set; }

    public ulong HeapBytes => 0;

    /// <summary>
    /// Makes the buffers, the images in the mesh pass's format, and both sets. At the run's "image views" step, after the
    /// mesh asset pass is made.
    /// </summary>
    public void Initialize(MeshPass meshPass, MeshAssetPass? assetPass)
    {
        var slots = (ulong)FrameSync.FramesInFlight;
        var name = Info.Name;
        Draws = Upload(slots * MaxDraws * (ulong)sizeof(MeshDraw), BufferUsageFlags.StorageBufferBit, "draws");
        Commands = Upload(
            slots * MaxDraws * (ulong)sizeof(DrawIndexedIndirectCommand),
            BufferUsageFlags.IndirectBufferBit,
            "commands"
        );
        Lighting = Upload(slots * (ulong)sizeof(MeshLighting), BufferUsageFlags.StorageBufferBit, "lighting");
        Lights = Upload(
            slots * MeshPass.MostLights * (ulong)sizeof(MeshLight),
            BufferUsageFlags.StorageBufferBit,
            "lights"
        );
        Clusters = _allocator.CreateBuffer(
            slots * MeshPass.ClusterSlotWords * sizeof(uint),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.DeviceLocal,
            $"image view {name} light clusters"
        );
        ModelDraws = Upload(
            slots * MaxModelRecords * (ulong)sizeof(MeshModelDraw),
            BufferUsageFlags.StorageBufferBit,
            "model draws"
        );
        ModelCommands = Upload(
            slots * MaxModelCommands * (ulong)sizeof(DrawIndexedIndirectCommand),
            BufferUsageFlags.IndirectBufferBit,
            "model commands"
        );
        AssetDraws = Upload(
            slots * AssetLists * MaxAssetDraws * (ulong)sizeof(MeshAssetDraw),
            BufferUsageFlags.StorageBufferBit,
            "asset draws"
        );
        AssetCommands = Upload(
            slots * AssetLists * MaxAssetCommands * (ulong)sizeof(DrawIndexedIndirectCommand),
            BufferUsageFlags.IndirectBufferBit,
            "asset commands"
        );
        CreateImages(meshPass);
        MeshSet = meshPass.CreateViewSet(this, out _meshPool);
        if (assetPass is not null)
        {
            AssetSet = assetPass.CreateViewSet(AssetDraws, $"image view {name} mesh assets", out _assetPool);
        }
    }

    /// <summary>
    /// Makes the images again in the mesh pass's format when it changed (true then: the sets that read
    /// <see cref="Shown"/> must be written again). Render thread, device idle.
    /// </summary>
    public bool OnTargetsChanged(MeshPass meshPass)
    {
        if (meshPass.ColorFormat == Format)
        {
            return false;
        }

        Colour.Dispose();
        Depth.Dispose();
        Shown.Dispose();
        ShownReady = false;
        CreateImages(meshPass);
        return true;
    }

    /// <summary>The frame record of <paramref name="slot"/>.</summary>
    public ref RenderViewFrame FrameOf(int slot) => ref _frames[slot];

    /// <summary>The index in the shaders' draws of <paramref name="slot"/>'s first.</summary>
    public static int RecordBase(int slot) => slot * MaxDraws;

    /// <summary>The index in the shaders' model draws of <paramref name="slot"/>'s first.</summary>
    public static int ModelRecordBase(int slot) => slot * MaxModelRecords;

    /// <summary>The index in the shaders' asset draws of list <paramref name="list"/> of <paramref name="slot"/>'s first.</summary>
    public static int AssetDrawBase(int slot, int list) => (slot * AssetLists + list) * MaxAssetDraws;

    /// <summary>The byte offset of list <paramref name="list"/> of <paramref name="slot"/> in <see cref="AssetCommands"/>.</summary>
    public static ulong AssetCommandsOffset(int slot, int list) =>
        (ulong)((slot * AssetLists) + list) * MaxAssetCommands * (ulong)sizeof(DrawIndexedIndirectCommand);

    /// <summary>The byte offset of <paramref name="slot"/>'s commands in <see cref="Commands"/>.</summary>
    public static ulong CommandsOffset(int slot) => (ulong)slot * MaxDraws * (ulong)sizeof(DrawIndexedIndirectCommand);

    /// <summary>The byte offset of <paramref name="slot"/>'s commands in <see cref="ModelCommands"/>.</summary>
    public static ulong ModelCommandsOffset(int slot) =>
        (ulong)slot * MaxModelCommands * (ulong)sizeof(DrawIndexedIndirectCommand);

    public Span<MeshDraw> DrawsOf(int slot) =>
        new(Draws.Mapped + (ulong)RecordBase(slot) * (ulong)sizeof(MeshDraw), MaxDraws);

    /// <summary>The view's commands of <paramref name="slot"/>; it has one list, the camera's.</summary>
    public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) =>
        new(Commands.Mapped + CommandsOffset(slot), MaxDraws);

    public ref MeshLighting LightingOf(int slot) =>
        ref *(MeshLighting*)(Lighting.Mapped + (ulong)slot * (ulong)sizeof(MeshLighting));

    public Span<MeshLight> LightsOf(int slot) =>
        new(Lights.Mapped + (ulong)slot * MeshPass.MostLights * (ulong)sizeof(MeshLight), MeshPass.MostLights);

    /// <summary>The view's model draws of <paramref name="slot"/>; it has one list, the camera's.</summary>
    public Span<MeshModelDraw> ModelDrawsOf(int slot, int list) =>
        new(ModelDraws.Mapped + (ulong)ModelRecordBase(slot) * (ulong)sizeof(MeshModelDraw), MaxModelRecords);

    public Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list) =>
        new(ModelCommands.Mapped + ModelCommandsOffset(slot), MaxModelCommands);

    /// <summary>The view's asset draws of list <paramref name="list"/> of <paramref name="slot"/>.</summary>
    public Span<MeshAssetDraw> AssetDrawsOf(int slot, int list) =>
        new(AssetDraws.Mapped + (ulong)AssetDrawBase(slot, list) * (ulong)sizeof(MeshAssetDraw), MaxAssetDraws);

    /// <summary>The view's asset commands of list <paramref name="list"/> of <paramref name="slot"/>.</summary>
    public Span<DrawIndexedIndirectCommand> AssetCommandsOf(int slot, int list) =>
        new(AssetCommands.Mapped + AssetCommandsOffset(slot, list), MaxAssetCommands);

    /// <summary>
    /// Makes what the main thread wrote into <paramref name="slot"/> for <paramref name="frame"/> visible to the GPU, on
    /// memory that is not coherent. Render thread.
    /// </summary>
    public void Flush(int slot, in RenderViewFrame frame)
    {
        var records = Math.Clamp(frame.Records, 0, MaxDraws);
        if (records > 0)
        {
            Draws.Flush((ulong)RecordBase(slot) * (ulong)sizeof(MeshDraw), (ulong)records * (ulong)sizeof(MeshDraw));
        }

        var draws = Math.Clamp(frame.Draws, 0, MaxDraws);
        if (draws > 0)
        {
            Commands.Flush(CommandsOffset(slot), (ulong)draws * (ulong)sizeof(DrawIndexedIndirectCommand));
        }

        Lighting.Flush((ulong)slot * (ulong)sizeof(MeshLighting), (ulong)sizeof(MeshLighting));
        Lights.Flush(
            (ulong)slot * MeshPass.MostLights * (ulong)sizeof(MeshLight),
            MeshPass.MostLights * (ulong)sizeof(MeshLight)
        );
        var modelRecords = Math.Clamp(frame.Models.Records, 0, MaxModelRecords);
        if (modelRecords > 0)
        {
            ModelDraws.Flush(
                (ulong)ModelRecordBase(slot) * (ulong)sizeof(MeshModelDraw),
                (ulong)modelRecords * (ulong)sizeof(MeshModelDraw)
            );
        }

        var modelCommands = Math.Clamp(frame.Models.Commands, 0, MaxModelCommands);
        if (modelCommands > 0)
        {
            ModelCommands.Flush(
                ModelCommandsOffset(slot),
                (ulong)modelCommands * (ulong)sizeof(DrawIndexedIndirectCommand)
            );
        }

        FlushAssets(slot, AssetSeenList, frame.Assets.Draws, frame.Assets.Commands);
        FlushAssets(slot, AssetBlendedList, frame.Blended, frame.Blended);
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
            // Destroying a pool frees the set allocated from it.
            if (_meshPool.Handle != 0)
            {
                _context.Vk.DestroyDescriptorPool(_context.Device, _meshPool, null);
            }

            if (_assetPool.Handle != 0)
            {
                _context.Vk.DestroyDescriptorPool(_context.Device, _assetPool, null);
            }
        }

        _meshPool = default;
        _assetPool = default;
        Colour?.Dispose();
        Depth?.Dispose();
        Shown?.Dispose();
        Draws?.Dispose();
        Commands?.Dispose();
        Lighting?.Dispose();
        Lights?.Dispose();
        Clusters?.Dispose();
        ModelDraws?.Dispose();
        ModelCommands?.Dispose();
        AssetDraws?.Dispose();
        AssetCommands?.Dispose();
    }

    private void FlushAssets(int slot, int list, int draws, int commands)
    {
        draws = Math.Clamp(draws, 0, MaxAssetDraws);
        if (draws > 0)
        {
            AssetDraws.Flush(
                (ulong)AssetDrawBase(slot, list) * (ulong)sizeof(MeshAssetDraw),
                (ulong)draws * (ulong)sizeof(MeshAssetDraw)
            );
        }

        commands = Math.Clamp(commands, 0, MaxAssetCommands);
        if (commands > 0)
        {
            AssetCommands.Flush(
                AssetCommandsOffset(slot, list),
                (ulong)commands * (ulong)sizeof(DrawIndexedIndirectCommand)
            );
        }
    }

    private GpuBuffer Upload(ulong bytes, BufferUsageFlags usage, string what) =>
        _allocator.CreateBuffer(bytes, usage, MemoryUsage.Upload, $"image view {Info.Name} {what}");

    private void CreateImages(MeshPass meshPass)
    {
        Format = meshPass.ColorFormat;
        var name = Info.Name;
        Colour = _allocator.CreateImage(
            Format,
            Extent,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit,
            $"image view {name} colour"
        );
        Depth = _allocator.CreateImage(
            DeviceRequirements.DepthFormat,
            Extent,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransientAttachmentBit,
            ImageAspectFlags.DepthBit,
            $"image view {name} depth"
        );
        Shown = _allocator.CreateImage(
            Format,
            Extent,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit,
            $"image view {name}"
        );
    }
}
