using Itera.Engine.Assets;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>The asset pass's buffers, textures, samplers, descriptor set, and pipelines, made once at the run's start.</summary>
public sealed unsafe partial class MeshAssetPass
{
    /// <summary>
    /// The combined image samplers of <see cref="MeshPass"/>'s set the fragment stage sees, which count against the
    /// device's per-stage limits on sampled images and on samplers beside this set's.
    /// </summary>
    private const int MeshPassImageSamplers = 9;

    /// <summary>
    /// What a stage holds beside the textures that counts against the device's per-stage resources: the mesh pass's 18
    /// descriptors, this set's six storage buffers, and the colour attachment.
    /// </summary>
    private const int OtherStageResources = 25;

    /// <summary>Every level of a texture starts on 16 bytes in the upload buffer.</summary>
    private static ulong AlignLevel(ulong bytes) => (bytes + 15) & ~15UL;

    /// <summary>
    /// The textures the fragment stage can hold beside <see cref="MeshPass"/>'s: the device's per-stage and per-set limits
    /// on sampled images less the mesh pass's own, and its per-stage resources less the rest of both sets', at most
    /// <see cref="MeshAssetMaterial.NoTexture"/>.
    /// </summary>
    private int TextureCapacity()
    {
        var limits = _context.Limits;
        var images = (long)Math.Min(limits.MaxPerStageDescriptorSampledImages, limits.MaxDescriptorSetSampledImages);
        var room = Math.Min(images - MeshPassImageSamplers, (long)limits.MaxPerStageResources - OtherStageResources);
        return (int)Math.Clamp(room, 1, MeshAssetMaterial.NoTexture);
    }

    /// <summary>
    /// The buffers of every asset (vertices, indices, materials), the textures and their stand-in, and the upload buffer
    /// that holds all of it for the first frame's copies; and the frame slots' draws and commands.
    /// </summary>
    private void CreateBuffersAndTextures(MeshAssetLayout layout)
    {
        // Textures past the device's room are left out, their materials drawn with their factors alone.
        var keptTextures = Math.Min(layout.TextureCount, TextureCapacity());
        if (keptTextures < layout.TextureCount)
        {
            LogTexturesLeftOut(layout.TextureCount - keptTextures, keptTextures - 1);
        }

        var vertexBytes = (ulong)layout.VertexCount * (ulong)sizeof(MeshAssetVertex);
        // The shaders see the vertices as one storage buffer, which the device's range limits.
        var range = (ulong)_context.Limits.MaxStorageBufferRange;
        if (vertexBytes > range)
        {
            throw new InvalidOperationException(
                $"The mesh assets' {layout.VertexCount} vertices take {vertexBytes} bytes, past this device's storage buffer range of {range} bytes."
            );
        }

        // Each frame slot's region of skinned vertices follows every asset's own, within the same range.
        var slots = (ulong)FrameSync.FramesInFlight;
        var vertexSize = (ulong)sizeof(MeshAssetVertex);
        _skinBase = layout.VertexCount;
        _skinCapacity =
            layout.SkinVertexCount == 0
                ? 0
                : (int)
                    Math.Min(
                        Math.Min((ulong)_options.Value.Renderer.SkinMegabytes << 20, (range - vertexBytes) / slots)
                            / vertexSize,
                        int.MaxValue / slots
                    );
        var skinned = _skinCapacity > 0;
        var skinBytes = (ulong)layout.SkinVertexCount * (ulong)sizeof(MeshSkinVertex);

        var indexBytes = (ulong)layout.IndexCount * sizeof(uint);
        var materialBytes = (ulong)layout.MaterialCount * (ulong)sizeof(MeshAssetMaterial);
        ulong textureBytes = 0;
        var textures = new List<MeshTextureData?> { null };
        for (var a = 0; a < _assets.Count; a++)
        {
            foreach (var texture in _assets[a].Textures)
            {
                textures.Add(textures.Count < keptTextures ? texture : null);
            }
        }

        textures.RemoveRange(keptTextures, textures.Count - keptTextures);
        foreach (var texture in textures)
        {
            textureBytes += texture is null ? 16 : LevelBytes(texture);
        }

        var uploadBytes =
            AlignLevel(vertexBytes)
            + AlignLevel(indexBytes)
            + AlignLevel(materialBytes)
            + AlignLevel(skinBytes)
            + textureBytes;
        _upload = _allocator.CreateBuffer(
            uploadBytes,
            BufferUsageFlags.TransferSrcBit,
            MemoryUsage.Upload,
            "mesh asset upload"
        );
        _vertices = _allocator.CreateBuffer(
            Math.Max(vertexBytes + slots * (ulong)_skinCapacity * vertexSize, vertexSize),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "mesh asset vertices"
        );
        _indices = _allocator.CreateBuffer(
            Math.Max(indexBytes, sizeof(uint)),
            BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "mesh asset indices"
        );
        _materials = _allocator.CreateBuffer(
            Math.Max(materialBytes, (ulong)sizeof(MeshAssetMaterial)),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "mesh asset materials"
        );
        _skins = _allocator.CreateBuffer(
            Math.Max(skinBytes, (ulong)sizeof(MeshSkinVertex)),
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "mesh asset skins"
        );
        // Without skinned assets, one entry each, which nothing reads.
        _skinGroups = _allocator.CreateBuffer(
            (skinned ? slots * MaxSkinGroups : 1) * (ulong)sizeof(MeshSkinGroup),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Upload,
            "mesh asset skin groups"
        );
        _palettes = _allocator.CreateBuffer(
            (skinned ? slots * MeshInstances.MaxPaletteJoints : 1) * (ulong)sizeof(System.Numerics.Matrix4x4),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Upload,
            "mesh asset skinning matrices"
        );
        _draws = _allocator.CreateBuffer(
            slots * MeshPass.Lists * MaxDraws * (ulong)sizeof(MeshAssetDraw),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Upload,
            "mesh asset draws"
        );
        _commands = _allocator.CreateBuffer(
            slots * MeshPass.Lists * MaxCommands * (ulong)sizeof(DrawIndexedIndirectCommand),
            BufferUsageFlags.IndirectBufferBit,
            MemoryUsage.Upload,
            "mesh asset commands"
        );

        // The upload buffer: vertices, indices, materials, and skins in that order, then every texture's levels.
        var upload = _upload.Mapped;
        ulong offset = 0;
        _vertexCopy = new BufferCopy(offset, 0, vertexBytes);
        offset = CopyAll(upload, offset, static asset => asset.Vertices);
        _indexCopy = new BufferCopy(offset, 0, indexBytes);
        offset = CopyAll(upload, offset, static asset => asset.Indices);
        _materialCopy = new BufferCopy(offset, 0, materialBytes);
        var materials = new Span<MeshAssetMaterial>(upload + offset, layout.MaterialCount);
        for (var a = 0; a < _assets.Count; a++)
        {
            var asset = _assets[a];
            var place = layout.Assets[a];
            for (var m = 0; m < asset.Materials.Length; m++)
            {
                materials[place.FirstMaterial + m] = MeshAssetMaterial.From(
                    asset.Materials[m],
                    place.FirstTexture,
                    keptTextures
                );
            }
        }

        offset = AlignLevel(offset + materialBytes);
        _skinCopy = new BufferCopy(offset, 0, skinBytes);
        offset = CopyAll(upload, offset, static asset => asset.Skin ?? []);
        _textures = new GpuImage[textures.Count];
        _textureCopies = new BufferImageCopy[textures.Count][];
        for (var t = 0; t < textures.Count; t++)
        {
            offset = CreateTexture(t, textures[t], upload, offset);
        }

        _upload.Flush(0, _upload.Size);
        _textureBytes = textureBytes;
    }

    /// <summary>
    /// Copies each asset's array of one kind into the upload buffer, one after another from <paramref name="offset"/>, and
    /// returns where the next part starts (on 16 bytes).
    /// </summary>
    private ulong CopyAll<T>(byte* upload, ulong offset, Func<MeshAsset, T[]> arrayOf)
        where T : unmanaged
    {
        for (var a = 0; a < _assets.Count; a++)
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(arrayOf(_assets[a]).AsSpan());
            bytes.CopyTo(new Span<byte>(upload + offset, bytes.Length));
            offset += (ulong)bytes.Length;
        }

        return AlignLevel(offset);
    }

    /// <summary>
    /// Texture <paramref name="index"/>'s image with every level, and its levels copied into the upload buffer at
    /// <paramref name="offset"/>; the stand-in (null) is one white texel. Returns where the next texture starts.
    /// </summary>
    private ulong CreateTexture(int index, MeshTextureData? texture, byte* upload, ulong offset)
    {
        if (texture is null)
        {
            _textures![index] = _allocator.CreateImage(
                Format.R8G8B8A8Unorm,
                new Extent2D(1, 1),
                ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                ImageAspectFlags.ColorBit,
                "mesh asset texture stand-in"
            );
            new Span<byte>(upload + offset, 4).Fill(0xFF);
            _textureCopies![index] =
            [
                new BufferImageCopy
                {
                    BufferOffset = offset,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageExtent = new Extent3D(1, 1, 1),
                },
            ];
            return offset + 16;
        }

        _textures![index] = _allocator.CreateImage(
            FormatOf(texture.Format),
            new Extent2D((uint)texture.Width, (uint)texture.Height),
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.ColorBit,
            $"mesh asset {texture.Name}",
            mips: (uint)texture.Levels
        );
        // A compressed level is whole 4x4 blocks packed (a row length of 0), its extent the level's own size, which
        // Vulkan allows for a size that is not a whole number of blocks because it reaches the level's edge; every
        // level starts on 16 bytes, a multiple of a BC7 or BC5 block.
        var copies = new BufferImageCopy[texture.Levels];
        var source = 0;
        for (var level = 0; level < texture.Levels; level++)
        {
            var (width, height) = MipChain.LevelSize(texture.Width, texture.Height, level);
            var bytes = MipChain.LevelBytes(texture.Width, texture.Height, level, texture.Format);
            texture.Texels.AsSpan(source, bytes).CopyTo(new Span<byte>(upload + offset, bytes));
            source += bytes;
            copies[level] = new BufferImageCopy
            {
                BufferOffset = offset,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, 1),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };
            offset += AlignLevel((ulong)bytes);
        }

        _textureCopies![index] = copies;
        return offset;
    }

    /// <summary>The upload buffer's bytes for every level of <paramref name="texture"/>, each on 16 bytes.</summary>
    private static ulong LevelBytes(MeshTextureData texture)
    {
        ulong bytes = 0;
        for (var level = 0; level < texture.Levels; level++)
        {
            bytes += AlignLevel((ulong)MipChain.LevelBytes(texture.Width, texture.Height, level, texture.Format));
        }

        return bytes;
    }

    private static Format FormatOf(MeshTextureFormat format) =>
        format switch
        {
            MeshTextureFormat.Rgba8Srgb => Format.R8G8B8A8Srgb,
            MeshTextureFormat.Rgba8Unorm => Format.R8G8B8A8Unorm,
            MeshTextureFormat.Rg8Unorm => Format.R8G8Unorm,
            MeshTextureFormat.Bc7Srgb => Format.BC7SrgbBlock,
            MeshTextureFormat.Bc7Unorm => Format.BC7UnormBlock,
            MeshTextureFormat.Bc5Unorm => Format.BC5UnormBlock,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    /// <summary>
    /// The six samplers (<see cref="MeshSampler"/>'s order): linear between texels and levels, anisotropic up to 16 times
    /// where the device can, or nearest; each repeating, clamped to the edge, or mirrored.
    /// </summary>
    private void CreateSamplers()
    {
        var anisotropic = _context.SupportsAnisotropy;
        ReadOnlySpan<SamplerAddressMode> wraps =
        [
            SamplerAddressMode.Repeat,
            SamplerAddressMode.ClampToEdge,
            SamplerAddressMode.MirroredRepeat,
        ];
        for (var i = 0; i < Samplers; i++)
        {
            var nearest = i >= 3;
            var wrap = wraps[i % 3];
            var info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = nearest ? Filter.Nearest : Filter.Linear,
                MinFilter = nearest ? Filter.Nearest : Filter.Linear,
                MipmapMode = nearest ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear,
                AddressModeU = wrap,
                AddressModeV = wrap,
                AddressModeW = wrap,
                AnisotropyEnable = anisotropic && !nearest,
                MaxAnisotropy = anisotropic && !nearest ? Math.Min(16f, _context.Limits.MaxSamplerAnisotropy) : 1f,
                MaxLod = Vk.LodClampNone,
            };
            _context.Vk.CreateSampler(_context.Device, &info, null, out _samplers[i]).ThrowIfFailed("vkCreateSampler");
            _context.SetObjectName(ObjectType.Sampler, _samplers[i].Handle, $"mesh asset sampler {(MeshSampler)i}");
        }
    }

    /// <summary>
    /// Set 1 of the asset pipelines (<c>mesh-asset-data.glsl</c>): the six samplers, fixed in the layout; the textures;
    /// the vertices, the materials, and every slot's draws; and for the skinning (<c>mesh-skin.comp</c>) the skins, every
    /// slot's skin groups, and every slot's skinning matrices.
    /// </summary>
    private void CreateDescriptors()
    {
        var vk = _context.Vk;
        var device = _context.Device;
        var textureCount = (uint)_textures!.Length;
        var samplers = stackalloc Sampler[Samplers];
        for (var i = 0; i < Samplers; i++)
        {
            samplers[i] = _samplers[i];
        }

        const ShaderStageFlags buffers =
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[Bindings]
        {
            new()
            {
                Binding = 0,
                DescriptorType = DescriptorType.Sampler,
                DescriptorCount = Samplers,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = samplers,
            },
            new()
            {
                Binding = 1,
                DescriptorType = DescriptorType.SampledImage,
                DescriptorCount = textureCount,
                StageFlags = ShaderStageFlags.FragmentBit,
            },
            new()
            {
                Binding = 2,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = buffers,
            },
            new()
            {
                Binding = 3,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = buffers,
            },
            new()
            {
                Binding = 4,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = buffers,
            },
            new()
            {
                Binding = 5,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
            new()
            {
                Binding = 6,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
            new()
            {
                Binding = 7,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            },
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = Bindings,
            PBindings = bindings,
        };
        vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _setLayout)
            .ThrowIfFailed("vkCreateDescriptorSetLayout");

        var poolSizes = stackalloc DescriptorPoolSize[3]
        {
            new(DescriptorType.Sampler, Samplers),
            new(DescriptorType.SampledImage, textureCount),
            new(DescriptorType.StorageBuffer, 6),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 3,
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

        var images = new DescriptorImageInfo[textureCount];
        for (var i = 0; i < images.Length; i++)
        {
            images[i] = new DescriptorImageInfo(default, _textures[i].View, ImageLayout.ShaderReadOnlyOptimal);
        }

        const int bufferCount = 6;
        var infos = stackalloc DescriptorBufferInfo[bufferCount]
        {
            new(Vertices.Buffer, 0, Vk.WholeSize),
            new(Materials.Buffer, 0, Vk.WholeSize),
            new(Draws.Buffer, 0, Vk.WholeSize),
            new(Skins.Buffer, 0, Vk.WholeSize),
            new(SkinGroups.Buffer, 0, Vk.WholeSize),
            new(Palettes.Buffer, 0, Vk.WholeSize),
        };
        fixed (DescriptorImageInfo* imageInfos = images)
        {
            var writes = stackalloc WriteDescriptorSet[1 + bufferCount];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 1,
                DescriptorCount = textureCount,
                DescriptorType = DescriptorType.SampledImage,
                PImageInfo = imageInfos,
            };
            for (var i = 0; i < bufferCount; i++)
            {
                writes[1 + i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _set,
                    DstBinding = (uint)(2 + i),
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &infos[i],
                };
            }

            vk.UpdateDescriptorSets(device, 1 + bufferCount, writes, 0, null);
        }

        _context.SetObjectName(ObjectType.DescriptorSet, _set.Handle, "mesh assets");

        // Set 0 is the mesh pass's (the view's lighting, the cascades, the sky field, the lights), set 1 this pass's.
        var sets = stackalloc DescriptorSetLayout[2] { _meshPass.SetLayout, _setLayout };
        _layout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(sets, 2),
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            (uint)sizeof(ViewConstants),
            "mesh asset layout"
        );
        _shadowLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(sets, 2),
            ShaderStageFlags.VertexBit,
            (uint)sizeof(MeshShadowConstants),
            "mesh asset shadow layout"
        );
        _skinLayout = PipelineFactory.CreateLayout(
            _context,
            new ReadOnlySpan<DescriptorSetLayout>(sets, 2),
            ShaderStageFlags.ComputeBit,
            (uint)sizeof(MeshSkinConstants),
            "mesh asset skin layout"
        );
    }

    /// <summary>
    /// The eight pipelines: the camera's (<c>mesh-asset.vert</c>, <c>mesh-asset.frag</c>, at the mesh pass's samples and
    /// colour format, reverse depth as the world's) and the cascades' (<c>mesh-asset-shadow.vert</c> with
    /// <c>depth-only.frag</c>, or <c>mesh-asset-shadow.frag</c> for masked materials), each for the four ranges: opaque
    /// or masked, culling back faces or two-sided. A single-sided asset's shadow, as the world's, is drawn from its faces
    /// turned away from the sun, so a lit surface never meets its own depth; an open single-sided mesh casts from those
    /// faces only.
    /// </summary>
    private void CreatePipelines(Format format, SampleCountFlags samples)
    {
        var depth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Greater, Write: true);
        var shadowDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Less, Write: true);
        for (var range = 0; range < MeshAssetLayout.Ranges; range++)
        {
            var masked = range >= (int)MeshAssetRange.Masked;
            var cull = range is (int)MeshAssetRange.OpaqueTwoSided or (int)MeshAssetRange.MaskedTwoSided
                ? CullModeFlags.None
                : CullModeFlags.BackBit;
            var name = ((MeshAssetRange)range).ToString();
            _pipelines[range] = PipelineFactory.CreateTriangles(
                _context,
                _vertexSpirv,
                masked ? _maskedFragmentSpirv : _fragmentSpirv,
                _layout,
                format,
                cull,
                in depth,
                $"mesh assets {name}",
                samples: samples
            );
            _shadowPipelines[range] = PipelineFactory.CreateTriangles(
                _context,
                masked ? _maskedShadowVertexSpirv : _shadowVertexSpirv,
                masked ? _shadowFragmentSpirv : _depthOnlySpirv,
                _shadowLayout,
                Format.Undefined,
                cull,
                in shadowDepth,
                $"mesh asset shadows {name}"
            );
        }

        _skinPipeline = PipelineFactory.CreateCompute(_context, _skinSpirv, _skinLayout, "mesh asset skinning");
        _format = format;
        _samples = samples;
    }

    private void DestroyPipelines()
    {
        DestroyPipeline(ref _skinPipeline);
        for (var i = 0; i < MeshAssetLayout.Ranges; i++)
        {
            DestroyPipeline(ref _pipelines[i]);
            DestroyPipeline(ref _shadowPipelines[i]);
        }
    }

    private void DestroyPipeline(ref Pipeline pipeline)
    {
        if (pipeline.Handle != 0 && !_context.IsAbandoned)
        {
            _context.Vk.DestroyPipeline(_context.Device, pipeline, null);
        }

        pipeline = default;
    }
}
