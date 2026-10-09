using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;
using SkiaSharp;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The bake of a material's recipe into images (docs/rendering-redesign.md, the materials plan, step 4; the console's
/// <c>materials bake</c>), which <c>tools/Textures</c> makes into a texture set as it makes a photograph's: the render
/// thread runs <c>material-bake.comp</c> for the material over a 2.56 m tile into a 1024-texel image of three layers,
/// copies it into a readback buffer, and when that frame slot comes round again (its fence passed) reads it and writes
/// <c>colour.png</c>, <c>occlusion.png</c>, <c>normal.png</c>, <c>height.png</c>, and <c>metres.txt</c> into the data
/// folder's <c>Bakes/</c> on a worker. The image, buffer, and pipeline are made on the render thread at the first bake.
/// One bake at a time; a development tool, which may hitch a frame.
/// </summary>
public sealed unsafe partial class MeshPass
{
    private const uint BakeTexels = 1024;
    private const uint BakeLayers = 3;
    private const ulong BakeLayerBytes = BakeTexels * BakeTexels * 4;

    /// <summary>The bake's tile, in metres (<c>material-bake.comp</c>'s MESH_SURFACE_TILE).</summary>
    private const string BakeMetres = "2.56";

    private readonly object _bakeLock = new();
    private BakeRequest? _bakeRequest;
    private BakeRequest? _bakeInFlight;
    private int _bakeSlot;
    private readonly string _bakeFolder;
    private GpuImage? _bakeImage;
    private GpuBuffer? _bakeReadback;
    private DescriptorSetLayout _bakeSetLayout;
    private DescriptorPool _bakePool;
    private DescriptorSet _bakeSet;
    private PipelineLayout _bakeLayout;
    private Pipeline _bakePipeline;
    private byte[] _bakeSpirv = [];

    /// <summary>
    /// Asks for a bake of <paramref name="material"/>'s recipe on the plane of x and y (<paramref name="side"/>: trunks
    /// and walls) or of x and z (ground), into the data folder's <c>Bakes/<paramref name="name"/></c>, which it returns;
    /// null while another bake is asked for or running. Any thread.
    /// </summary>
    public string? RequestBake(byte material, bool side, string name)
    {
        var folder = Path.Combine(_bakeFolder, name);
        lock (_bakeLock)
        {
            if (_bakeRequest is not null || _bakeInFlight is not null)
            {
                return null;
            }

            _bakeRequest = new BakeRequest(material, side, folder);
        }

        return folder;
    }

    /// <summary>
    /// Reads back the bake recorded in this slot's last frame, whose fence has passed; then records the bake asked for,
    /// if any. Outside rendering. Render thread.
    /// </summary>
    private void RecordBake(CommandBuffer commandBuffer, int slot)
    {
        if (_bakeInFlight is { } done && slot == _bakeSlot)
        {
            ReadBake(done);
            lock (_bakeLock)
            {
                _bakeInFlight = null;
            }
        }

        BakeRequest? request;
        lock (_bakeLock)
        {
            request = _bakeRequest;
            if (request is null || _bakeInFlight is not null)
            {
                return;
            }

            _bakeRequest = null;
            _bakeInFlight = request;
        }

        _bakeSlot = slot;
        EnsureBake();
        var vk = _context.Vk;
        var image = _bakeImage!;
        BakeBarrier(
            commandBuffer,
            image.Image,
            ImageLayout.Undefined,
            ImageLayout.General,
            PipelineStageFlags2.None,
            AccessFlags2.None,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit
        );
        var sets = stackalloc DescriptorSet[2] { _set, _bakeSet };
        var constants = stackalloc uint[2] { request.Material, request.Side ? 1u : 0u };
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _bakePipeline);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _bakeLayout, 0, 2, sets, 0, null);
        vk.CmdPushConstants(commandBuffer, _bakeLayout, ShaderStageFlags.ComputeBit, 0, 8, constants);
        vk.CmdDispatch(commandBuffer, BakeTexels / 8, BakeTexels / 8, 1);
        BakeBarrier(
            commandBuffer,
            image.Image,
            ImageLayout.General,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferReadBit
        );
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, BakeLayers),
            ImageExtent = new Extent3D(BakeTexels, BakeTexels, 1),
        };
        vk.CmdCopyImageToBuffer(
            commandBuffer,
            image.Image,
            ImageLayout.TransferSrcOptimal,
            _bakeReadback!.Buffer,
            1,
            &region
        );
        Barrier(
            commandBuffer,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit
        );
    }

    /// <summary>The bake's image, readback buffer, set, and pipeline, made at the first bake. Render thread.</summary>
    private void EnsureBake()
    {
        if (_bakeImage is not null)
        {
            return;
        }

        var vk = _context.Vk;
        var device = _context.Device;
        _bakeImage = _allocator.CreateImage(
            Format.R8G8B8A8Unorm,
            new Extent2D(BakeTexels, BakeTexels),
            ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit,
            "material bake",
            layers: BakeLayers
        );
        _bakeReadback = _allocator.CreateBuffer(
            BakeLayers * BakeLayerBytes,
            BufferUsageFlags.TransferDstBit,
            MemoryUsage.Readback,
            "material bake readback"
        );
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _bakeSetLayout)
            .ThrowIfFailed("vkCreateDescriptorSetLayout");
        var poolSize = new DescriptorPoolSize(DescriptorType.StorageImage, 1);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        vk.CreateDescriptorPool(device, &poolInfo, null, out _bakePool).ThrowIfFailed("vkCreateDescriptorPool");
        var bakeLayout = _bakeSetLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _bakePool,
            DescriptorSetCount = 1,
            PSetLayouts = &bakeLayout,
        };
        DescriptorSet set;
        vk.AllocateDescriptorSets(device, &allocateInfo, &set).ThrowIfFailed("vkAllocateDescriptorSets");
        _bakeSet = set;
        var imageInfo = new DescriptorImageInfo(default, _bakeImage.View, ImageLayout.General);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _bakeSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &imageInfo,
        };
        vk.UpdateDescriptorSets(device, 1, &write, 0, null);
        var layouts = stackalloc DescriptorSetLayout[2] { _setLayout, _bakeSetLayout };
        _bakeLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(layouts, 2),
            ShaderStageFlags.ComputeBit,
            8,
            "material bake layout"
        );
        _bakePipeline = PipelineFactory.CreateCompute(_context, _bakeSpirv, _bakeLayout, "material bake");
    }

    /// <summary>
    /// After a shader reload, the bake's pipeline made again from the new shader, if a bake has made it (the console's
    /// <c>materials bake</c> then bakes the edited recipe). Render thread, device idle.
    /// </summary>
    private void ReplaceBakePipeline()
    {
        if (_bakeLayout.Handle == 0)
        {
            return;
        }

        DestroyPipeline(ref _bakePipeline);
        _bakePipeline = PipelineFactory.CreateCompute(_context, _bakeSpirv, _bakeLayout, "material bake");
    }

    /// <summary>Copies the bake out of the readback buffer and writes its images on a worker. Render thread.</summary>
    private void ReadBake(BakeRequest request)
    {
        var readback = _bakeReadback!;
        readback.Invalidate(0, readback.Size);
        var bytes = new byte[BakeLayers * BakeLayerBytes];
        new ReadOnlySpan<byte>(readback.Mapped, bytes.Length).CopyTo(bytes);
        _ = Task.Run(() => WriteBake(request, bytes));
    }

    /// <summary>Writes the bake's layers as the images <c>tools/Textures</c> reads. Worker thread.</summary>
    private void WriteBake(BakeRequest request, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(request.Folder);
            const int texels = (int)BakeTexels;
            var layer = (int)BakeLayerBytes;
            var colour = new byte[layer];
            var occlusion = new byte[layer];
            var height = new byte[layer];
            for (var i = 0; i < texels * texels; i++)
            {
                colour[i * 4] = bytes[i * 4];
                colour[i * 4 + 1] = bytes[i * 4 + 1];
                colour[i * 4 + 2] = bytes[i * 4 + 2];
                colour[i * 4 + 3] = 255;
                occlusion[i * 4] = occlusion[i * 4 + 1] = occlusion[i * 4 + 2] = bytes[i * 4 + 3];
                occlusion[i * 4 + 3] = 255;
                height[i * 4] = height[i * 4 + 1] = height[i * 4 + 2] = bytes[2 * layer + i * 4];
                height[i * 4 + 3] = 255;
            }

            WritePng(Path.Combine(request.Folder, "colour.png"), colour, 0);
            WritePng(Path.Combine(request.Folder, "occlusion.png"), occlusion, 0);
            WritePng(Path.Combine(request.Folder, "normal.png"), bytes, layer);
            WritePng(Path.Combine(request.Folder, "height.png"), height, 0);
            File.WriteAllText(Path.Combine(request.Folder, "metres.txt"), BakeMetres);
            LogBaked(request.Material, request.Folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogBakeFailed(request.Folder, exception);
        }
    }

    private static void WritePng(string path, byte[] bytes, int offset)
    {
        const int texels = (int)BakeTexels;
        var info = new SKImageInfo(texels, texels, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        new ReadOnlySpan<byte>(bytes, offset, texels * texels * 4).CopyTo(bitmap.GetPixelSpan());
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    private void BakeBarrier(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout from,
        ImageLayout to,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess
    )
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, BakeLayers),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    /// <summary>Destroys the bake's pipeline, set, image, and buffer.</summary>
    private void DestroyBake()
    {
        if (!_context.IsAbandoned)
        {
            var vk = _context.Vk;
            var device = _context.Device;
            DestroyPipeline(ref _bakePipeline);
            if (_bakeLayout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _bakeLayout, null);
            }

            if (_bakePool.Handle != 0)
            {
                vk.DestroyDescriptorPool(device, _bakePool, null);
            }

            if (_bakeSetLayout.Handle != 0)
            {
                vk.DestroyDescriptorSetLayout(device, _bakeSetLayout, null);
            }
        }

        _bakeLayout = default;
        _bakePool = default;
        _bakeSetLayout = default;
        _bakeImage?.Dispose();
        _bakeReadback?.Dispose();
        _bakeImage = null;
        _bakeReadback = null;
    }

    private sealed record BakeRequest(byte Material, bool Side, string Folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Meshes: baked material {Material}'s recipe into {Folder}")]
    private partial void LogBaked(byte material, string folder);

    [LoggerMessage(Level = LogLevel.Error, Message = "Meshes: the bake into {Folder} failed")]
    private partial void LogBakeFailed(string folder, Exception exception);
}
