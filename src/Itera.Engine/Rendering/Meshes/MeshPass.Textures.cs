using Itera.Engine.Rendering.Materials;
using Itera.Engine.Rendering.Memory;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The world materials' texture sets (docs/rendering-redesign.md, the materials plan; <see cref="MaterialTextureSets"/>):
/// six block-compressed texture arrays with every mip level, read by <c>mesh.frag</c> at bindings 11 to 16: the large
/// sets' colour with occlusion (BC7 sRGB), normals (BC5), and heights (BC4), then the small sets' the same. A group with
/// no set has a 4-texel stand-in. <see cref="Initialize"/> reads the sets and copies their levels into an upload buffer;
/// the first frame the pass records copies them into the arrays, and the buffer goes once no frame in flight can read it.
/// One sampler for all: linear between texels and levels, repeating, anisotropic up to 16 times where the device can.
/// </summary>
public sealed unsafe partial class MeshPass
{
    /// <summary>The first of the six texture bindings, in the order of <see cref="_textureArrays"/>.</summary>
    private const int FirstTextureBinding = 11;

    private const int TextureArrays = 6;

    /// <summary>Texels of a stand-in array, one block.</summary>
    private const uint StandInTexels = 4;

    private readonly GpuImage?[] _textureArrays = new GpuImage?[TextureArrays];
    private readonly BufferImageCopy[][] _textureCopies = new BufferImageCopy[TextureArrays][];
    private GpuBuffer? _textureUpload;
    private Sampler _textureSampler;
    private bool _texturesRecorded;
    private int _uploadFramesLeft;

    /// <summary>The world materials' texture sets, read by <see cref="Initialize"/>.</summary>
    public MaterialTextureSets TextureSets { get; } = new();

    /// <summary>
    /// Reads the texture sets, makes the six arrays and their sampler, and copies every level into the upload buffer.
    /// Before the descriptors.
    /// </summary>
    private void CreateTextures()
    {
        var sets = TextureSets;
        sets.Load(MaterialTextureSets.Folder, LogTextureSetSkipped);
        // Every level starts on 16 bytes: a block of BC7 or BC5, two of BC4.
        ulong bytes = 0;
        foreach (var set in sets.Large.Concat(sets.Small))
        {
            foreach (var file in set.Files)
            {
                foreach (var length in DdsFile.Read(file).Lengths)
                {
                    bytes += AlignLevel((ulong)length);
                }
            }
        }

        if (bytes > 0)
        {
            _textureUpload = _allocator.CreateBuffer(
                bytes,
                BufferUsageFlags.TransferSrcBit,
                MemoryUsage.Upload,
                "material texture upload"
            );
        }

        ulong offset = 0;
        for (var array = 0; array < TextureArrays; array++)
        {
            var large = array < 3;
            var layer = array % 3;
            var group = large ? sets.Large : sets.Small;
            var format = MaterialTextureSets.LayerFormats[layer];
            var texels =
                group.Count == 0 ? StandInTexels
                : large ? (uint)MaterialTextureSets.LargeTexels
                : (uint)MaterialTextureSets.SmallTexels;
            var mips = group.Count > 0 ? (uint)DdsFile.MipCount((int)texels) : 1;
            _textureArrays[array] = _allocator.CreateImage(
                VulkanFormat(format),
                new Extent2D(texels, texels),
                ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                ImageAspectFlags.ColorBit,
                $"material {(large ? "large" : "small")} {MaterialTextureSets.LayerFiles[layer]}",
                layers: (uint)Math.Max(group.Count, 1),
                mips: mips,
                arrayView: true
            );
            var copies = new List<BufferImageCopy>();
            for (var i = 0; i < group.Count; i++)
            {
                var file = group[i].Files[layer];
                var dds = DdsFile.Read(file);
                for (var mip = 0; mip < dds.Offsets.Count; mip++)
                {
                    var size = Math.Max(1u, texels >> mip);
                    new ReadOnlySpan<byte>(file, dds.Offsets[mip], dds.Lengths[mip]).CopyTo(
                        new Span<byte>(TextureUpload.Mapped + offset, dds.Lengths[mip])
                    );
                    copies.Add(
                        new BufferImageCopy
                        {
                            BufferOffset = offset,
                            ImageSubresource = new ImageSubresourceLayers(
                                ImageAspectFlags.ColorBit,
                                (uint)mip,
                                (uint)i,
                                1
                            ),
                            ImageExtent = new Extent3D(size, size, 1),
                        }
                    );
                    offset += AlignLevel((ulong)dds.Lengths[mip]);
                }
            }

            _textureCopies[array] = [.. copies];
        }

        _textureUpload?.Flush(0, _textureUpload.Size);
        LogTextureSets(sets.Large.Count, sets.Small.Count, bytes / (1024.0 * 1024.0));
        sets.ReleaseFiles();
        CreateTextureSampler();
    }

    private static ulong AlignLevel(ulong bytes) => (bytes + 15) & ~15UL;

    private static Format VulkanFormat(DdsFormat format) =>
        format switch
        {
            DdsFormat.Bc7Srgb => Format.BC7SrgbBlock,
            DdsFormat.Bc5 => Format.BC5UnormBlock,
            _ => Format.BC4UnormBlock,
        };

    /// <summary>
    /// The sets' sampler: linear between texels and mip levels, repeating, anisotropic up to 16 times where the device
    /// can (8 is up to 0.25 ms quicker looking along the ground; docs/rendering-redesign.md).
    /// </summary>
    private void CreateTextureSampler()
    {
        var anisotropic = _context.SupportsAnisotropy;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = anisotropic,
            MaxAnisotropy = anisotropic ? Math.Min(16f, _context.Limits.MaxSamplerAnisotropy) : 1f,
            MaxLod = Vk.LodClampNone,
        };
        _context.Vk.CreateSampler(_context.Device, &info, null, out _textureSampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _textureSampler.Handle, "material textures");
    }

    /// <summary>
    /// The first time only: copies every set's levels from the upload buffer into the arrays and leaves them for the
    /// fragment shader to read; <see cref="FrameSync.FramesInFlight"/> frames later no frame in flight can read the
    /// buffer, and it goes. Host writes to the buffer need no barrier: the queue submit makes them visible. Outside
    /// rendering. Render thread.
    /// </summary>
    private void RecordTextures(CommandBuffer commandBuffer)
    {
        if (_texturesRecorded)
        {
            if (_textureUpload is not null && --_uploadFramesLeft < 0)
            {
                _textureUpload.Dispose();
                _textureUpload = null;
            }

            return;
        }

        _texturesRecorded = true;
        _uploadFramesLeft = FrameSync.FramesInFlight;
        for (var array = 0; array < TextureArrays; array++)
        {
            var image = TextureArray(array);
            TextureBarrier(commandBuffer, image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
            var copies = _textureCopies[array];
            if (copies.Length > 0)
            {
                fixed (BufferImageCopy* regions = copies)
                {
                    _context.Vk.CmdCopyBufferToImage(
                        commandBuffer,
                        TextureUpload.Buffer,
                        image.Image,
                        ImageLayout.TransferDstOptimal,
                        (uint)copies.Length,
                        regions
                    );
                }
            }

            TextureBarrier(commandBuffer, image, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
            _textureCopies[array] = [];
        }
    }

    /// <summary>Moves every level and layer of <paramref name="image"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
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

    /// <summary>Destroys the sampler, the arrays, and the upload buffer if it is still there.</summary>
    private void DestroyTextures()
    {
        if (!_context.IsAbandoned && _textureSampler.Handle != 0)
        {
            _context.Vk.DestroySampler(_context.Device, _textureSampler, null);
        }

        _textureSampler = default;
        for (var i = 0; i < TextureArrays; i++)
        {
            _textureArrays[i]?.Dispose();
            _textureArrays[i] = null;
        }

        _textureUpload?.Dispose();
        _textureUpload = null;
    }

    private GpuImage TextureArray(int array) => _textureArrays[array] ?? throw NotInitialised();

    private GpuBuffer TextureUpload => _textureUpload ?? throw NotInitialised();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Meshes: {Large} material texture set(s) of 2048 texels and {Small} of 1024, {Megabytes:F1} MB"
    )]
    private partial void LogTextureSets(int large, int small, double megabytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Meshes: material texture set {Name} left out: {Reason}")]
    private partial void LogTextureSetSkipped(string name, string reason);
}
