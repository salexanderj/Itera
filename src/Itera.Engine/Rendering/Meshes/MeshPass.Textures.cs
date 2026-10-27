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
/// Linear between texels and levels, repeating; anisotropic up to 16 times for the colours and 8 for the normals and
/// heights where the device can. The plane noise's tables (<see cref="NoiseTables"/>, binding 18) and the clouds' noise
/// (<see cref="CloudNoise"/>, binding 19) go up with them.
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
    private Sampler _reliefSampler;
    private Sampler _noiseSampler;
    private GpuImage? _noiseTables;
    private BufferImageCopy[] _noiseCopies = [];
    private Sampler _cloudSampler;
    private GpuImage? _cloudNoise;
    private BufferImageCopy[] _cloudCopies = [];
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

        _textureUpload = _allocator.CreateBuffer(
            bytes + NoiseTables.Bytes + (ulong)CloudNoise.Bytes,
            BufferUsageFlags.TransferSrcBit,
            MemoryUsage.Upload,
            "material texture upload"
        );
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
            var mips = group.Count > 0 ? (uint)DdsFile.MipCount((int)texels, (int)texels) : 1;
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

        CreateNoiseTables(offset);
        CreateCloudNoise(offset + NoiseTables.Bytes);
        _textureUpload.Flush(0, _textureUpload.Size);
        LogTextureSets(sets.Large.Count, sets.Small.Count, bytes / (1024.0 * 1024.0));
        sets.ReleaseFiles();
        CreateTextureSampler();
    }

    private static ulong AlignLevel(ulong bytes) => (bytes + 15) & ~15UL;

    /// <summary>The plane noise's tables: written into the upload buffer at <paramref name="offset"/>, a copy a layer.</summary>
    private void CreateNoiseTables(ulong offset)
    {
        const uint Cells = NoiseTables.Cells;
        var layers = (uint)NoiseTables.Salts.Length;
        _noiseTables = _allocator.CreateImage(
            Format.R16Unorm,
            new Extent2D(Cells, Cells),
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.ColorBit,
            "plane noise tables",
            layers: layers,
            arrayView: true
        );
        NoiseTables.Write(new Span<byte>(TextureUpload.Mapped + offset, NoiseTables.Bytes));
        _noiseCopies = new BufferImageCopy[layers];
        for (var layer = 0u; layer < layers; layer++)
        {
            _noiseCopies[layer] = new BufferImageCopy
            {
                BufferOffset = offset + layer * 2 * Cells * Cells,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, layer, 1),
                ImageExtent = new Extent3D(Cells, Cells, 1),
            };
        }
    }

    /// <summary>The clouds' noise: made into the upload buffer at <paramref name="offset"/>, a copy a level.</summary>
    private void CreateCloudNoise(ulong offset)
    {
        const uint Texels = CloudNoise.Texels;
        _cloudNoise = _allocator.CreateImage(
            Format.R8G8B8A8Unorm,
            new Extent2D(Texels, Texels),
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.ColorBit,
            "cloud noise",
            mips: CloudNoise.Mips
        );
        CloudNoise.Write(new Span<byte>(TextureUpload.Mapped + offset, CloudNoise.Bytes));
        _cloudCopies = new BufferImageCopy[CloudNoise.Mips];
        for (var mip = 0; mip < CloudNoise.Mips; mip++)
        {
            var side = (uint)CloudNoise.SideOf(mip);
            _cloudCopies[mip] = new BufferImageCopy
            {
                BufferOffset = offset + (ulong)CloudNoise.OffsetOf(mip),
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mip, 0, 1),
                ImageExtent = new Extent3D(side, side, 1),
            };
        }
    }

    private static Format VulkanFormat(DdsFormat format) =>
        format switch
        {
            DdsFormat.Bc7Srgb => Format.BC7SrgbBlock,
            DdsFormat.Bc5 => Format.BC5UnormBlock,
            _ => Format.BC4UnormBlock,
        };

    /// <summary>
    /// The sets' samplers: linear between texels and mip levels, repeating, anisotropic where the device can, up to 16
    /// times for the colours and 8 for the normals and heights (8 is up to 0.25 ms quicker looking along the ground;
    /// docs/rendering-redesign.md; the relief takes the difference least). And the noise tables' sampler, which only
    /// gathers: nearest, repeating. The clouds' noise takes a set's sampler, anisotropic up to 8 times, as the sky near the
    /// horizon reads it at a slant.
    /// </summary>
    private void CreateTextureSampler()
    {
        _textureSampler = CreateSetSampler(16f, "material textures");
        _reliefSampler = CreateSetSampler(8f, "material normals and heights");
        _cloudSampler = CreateSetSampler(8f, "cloud noise");
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MaxLod = 0,
        };
        _context.Vk.CreateSampler(_context.Device, &info, null, out _noiseSampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, _noiseSampler.Handle, "plane noise tables");
    }

    private Sampler CreateSetSampler(float anisotropy, string name)
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
            MaxAnisotropy = anisotropic ? Math.Min(anisotropy, _context.Limits.MaxSamplerAnisotropy) : 1f,
            MaxLod = Vk.LodClampNone,
        };
        _context.Vk.CreateSampler(_context.Device, &info, null, out var sampler).ThrowIfFailed("vkCreateSampler");
        _context.SetObjectName(ObjectType.Sampler, sampler.Handle, name);
        return sampler;
    }

    /// <summary>The sampler of texture array <paramref name="array"/>: the colours', or the relief's for the others.</summary>
    private Sampler SamplerOf(int array) => array % 3 == 0 ? _textureSampler : _reliefSampler;

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

        var noise = NoiseImage;
        TextureBarrier(commandBuffer, noise, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        fixed (BufferImageCopy* regions = _noiseCopies)
        {
            _context.Vk.CmdCopyBufferToImage(
                commandBuffer,
                TextureUpload.Buffer,
                noise.Image,
                ImageLayout.TransferDstOptimal,
                (uint)_noiseCopies.Length,
                regions
            );
        }

        TextureBarrier(commandBuffer, noise, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        _noiseCopies = [];

        var clouds = CloudImage;
        TextureBarrier(commandBuffer, clouds, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        fixed (BufferImageCopy* regions = _cloudCopies)
        {
            _context.Vk.CmdCopyBufferToImage(
                commandBuffer,
                TextureUpload.Buffer,
                clouds.Image,
                ImageLayout.TransferDstOptimal,
                (uint)_cloudCopies.Length,
                regions
            );
        }

        TextureBarrier(commandBuffer, clouds, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        _cloudCopies = [];
    }

    /// <summary>Moves every level and layer of <paramref name="image"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private void TextureBarrier(CommandBuffer commandBuffer, GpuImage image, ImageLayout from, ImageLayout to)
    {
        // The fragment shaders read the arrays and the tables; the grass's placing reads them too, frames later.
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

    /// <summary>
    /// Destroys the samplers, the arrays, the noise tables, the clouds' noise, and the upload buffer if it is still there.
    /// </summary>
    private void DestroyTextures()
    {
        foreach (var sampler in (ReadOnlySpan<Sampler>)[_textureSampler, _reliefSampler, _noiseSampler, _cloudSampler])
        {
            if (!_context.IsAbandoned && sampler.Handle != 0)
            {
                _context.Vk.DestroySampler(_context.Device, sampler, null);
            }
        }

        _textureSampler = default;
        _reliefSampler = default;
        _noiseSampler = default;
        _cloudSampler = default;
        _noiseTables?.Dispose();
        _noiseTables = null;
        _cloudNoise?.Dispose();
        _cloudNoise = null;
        for (var i = 0; i < TextureArrays; i++)
        {
            _textureArrays[i]?.Dispose();
            _textureArrays[i] = null;
        }

        _textureUpload?.Dispose();
        _textureUpload = null;
    }

    private GpuImage TextureArray(int array) => _textureArrays[array] ?? throw NotInitialised();

    private GpuImage NoiseImage => _noiseTables ?? throw NotInitialised();

    private GpuImage CloudImage => _cloudNoise ?? throw NotInitialised();

    private GpuBuffer TextureUpload => _textureUpload ?? throw NotInitialised();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Meshes: {Large} material texture set(s) of 2048 texels and {Small} of 1024, {Megabytes:F1} MB"
    )]
    private partial void LogTextureSets(int large, int small, double megabytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Meshes: material texture set {Name} left out: {Reason}")]
    private partial void LogTextureSetSkipped(string name, string reason);
}
