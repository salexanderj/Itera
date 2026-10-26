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
    /// descriptors, this set's six storage buffers, and the colour attachment. The image views' slots (one a view) count
    /// among the textures.
    /// </summary>
    private const int OtherStageResources = 25;

    /// <summary>Every level of a texture starts on 16 bytes in the upload buffer.</summary>
    private static ulong AlignLevel(ulong bytes) => (bytes + 15) & ~15UL;

    /// <summary>
    /// The texture slots the fragment stage can hold beside <see cref="MeshPass"/>'s (the assets' textures and one slot an
    /// image view share them): the device's per-stage and per-set limits on sampled images less the mesh pass's own, and
    /// its per-stage resources less the rest of both sets', at most <see cref="MeshAssetMaterial.NoTexture"/>.
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
        // Textures past the device's room are left out, their materials drawn with their factors alone; the image views'
        // slots come after the textures kept.
        var keptTextures = Math.Max(Math.Min(layout.TextureCount, TextureCapacity() - _views.Count), 1);
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

        // Each frame slot's skinned vertices, in two streams of their own: their positions and their normals and tangents
        // (SkinnedVertexBytes a vertex), as many as one skinning dispatch reaches.
        var slots = (ulong)FrameSync.FramesInFlight;
        var vertexSize = (ulong)sizeof(MeshAssetVertex);
        _skinCapacity =
            layout.SkinVertexCount == 0
                ? 0
                : (int)
                    Math.Min(
                        Math.Min(
                            ((ulong)_options.Value.Renderer.SkinMegabytes << 20) / SkinnedVertexBytes,
                            range / (slots * (ulong)sizeof(System.Numerics.Vector3))
                        ),
                        MostSkinnedVertices
                    );
        var skinned = _skinCapacity > 0;
        var skinBytes = (ulong)layout.SkinVertexCount * (ulong)sizeof(MeshSkinVertex);

        var indexBytes = (ulong)layout.IndexCount * sizeof(uint);
        var materialBytes = (ulong)layout.MaterialCount * (ulong)sizeof(MeshAssetMaterial);
        ulong textureBytes = 0;
        // Each texture once, however many assets share it, after the stand-in.
        var textures = new List<MeshTextureData?> { null };
        textures.AddRange(layout.Textures.Take(keptTextures - 1));
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
            Math.Max(vertexBytes, vertexSize),
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
        var skinVertices = Math.Max(slots * (ulong)_skinCapacity, 1);
        _skinPositions = _allocator.CreateBuffer(
            skinVertices * (ulong)sizeof(System.Numerics.Vector3),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.DeviceLocal,
            "mesh asset skinned positions"
        );
        _skinFrames = _allocator.CreateBuffer(
            skinVertices * 2 * sizeof(uint),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.DeviceLocal,
            "mesh asset skinned normals"
        );
        _skinGroups = _allocator.CreateBuffer(
            (skinned ? slots * MaxSkinGroups : 1) * (ulong)sizeof(MeshSkinGroup),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "mesh asset skin groups"
        );
        _palettes = _allocator.CreateBuffer(
            (skinned ? slots * MeshInstances.MaxPaletteJoints : 1) * (ulong)sizeof(System.Numerics.Matrix4x4),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "mesh asset skinning matrices"
        );
        _draws = _allocator.CreateBuffer(
            slots * SlotLists * MaxDraws * (ulong)sizeof(MeshAssetDraw),
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "mesh asset draws"
        );
        _commands = _allocator.CreateBuffer(
            slots * SlotLists * MaxCommands * (ulong)sizeof(DrawIndexedIndirectCommand),
            BufferUsageFlags.IndirectBufferBit,
            MemoryUsage.Stream,
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
                var material = asset.Materials[m];
                var packed = MeshAssetMaterial.From(layout.Slotted(a, material), 0, keptTextures);
                var view = _views.ViewOnMaterial(asset.Name, material.Name);
                materials[place.FirstMaterial + m] =
                    view >= 0 ? MeshAssetMaterial.ShowingView(in packed, material, keptTextures + view) : packed;
            }
        }

        ReportUnknownViewMaterials();

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

    /// <summary>A Warning for each material a view is shown on that no asset of the run has.</summary>
    private void ReportUnknownViewMaterials()
    {
        foreach (var shown in _views.MaterialViews)
        {
            var found = false;
            for (var a = 0; a < _assets.Count && !found; a++)
            {
                if (string.Equals(_assets[a].Name, shown.Asset, StringComparison.Ordinal))
                {
                    foreach (var material in _assets[a].Materials)
                    {
                        found |= string.Equals(material.Name, shown.Material, StringComparison.Ordinal);
                    }
                }
            }

            if (!found)
            {
                LogUnknownViewMaterial(shown.Asset, shown.Material);
            }
        }
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
        var textureCount = (uint)SlotCount;
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
            new()
            {
                Binding = 8,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit,
            },
            new()
            {
                Binding = 9,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.ComputeBit,
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

        _descriptorPool = CreatePool();
        _set = WriteSet(_descriptorPool, Draws, "mesh assets");

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
    /// The nine pipelines: the camera's (<c>mesh-asset.vert</c>, <c>mesh-asset.frag</c>, at the mesh pass's samples and
    /// colour format, reverse depth as the world's) and the cascades' (<c>mesh-asset-shadow.vert</c> with
    /// <c>depth-only.frag</c>, or <c>mesh-asset-shadow.frag</c> for masked materials), each for the four ranges: opaque
    /// or masked, culling back faces or two-sided; and the blended surfaces' (<c>mesh-asset.frag</c> with
    /// <c>MESH_ASSET_BLEND</c>: no culling, the depth tested and not written, premultiplied alpha). A single-sided asset's
    /// shadow, as the world's, is drawn from its faces turned away from the sun, so a lit surface never meets its own
    /// depth; an open single-sided mesh casts from those faces only.
    /// </summary>
    private void CreatePipelines(Format format, SampleCountFlags samples)
    {
        var depth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Greater, Write: true);
        var shadowDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Less, Write: true);
        for (var range = 0; range < MeshAssetLayout.ListRanges; range++)
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

        var blendDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Greater, Write: false);
        _blendPipeline = PipelineFactory.CreateTriangles(
            _context,
            _blendVertexSpirv,
            _blendFragmentSpirv,
            _layout,
            format,
            CullModeFlags.None,
            in blendDepth,
            "mesh assets blended",
            ColorBlend.Premultiplied,
            samples
        );
        _skinPipeline = PipelineFactory.CreateCompute(_context, _skinSpirv, _skinLayout, "mesh asset skinning");
        _format = format;
        _samples = samples;
        CreateViewPipelines();
    }

    /// <summary>
    /// The camera's pipelines again at one sample a pixel, for the image views (<see cref="WantViewPipelines"/>), when
    /// the world is multisampled; with one sample the world's serve.
    /// </summary>
    private void CreateViewPipelines()
    {
        if (!_viewPipelinesWanted || _samples == SampleCountFlags.Count1Bit || _viewBlendPipeline.Handle != 0)
        {
            return;
        }

        var depth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Greater, Write: true);
        for (var range = 0; range < MeshAssetLayout.ListRanges; range++)
        {
            var masked = range >= (int)MeshAssetRange.Masked;
            var cull = range is (int)MeshAssetRange.OpaqueTwoSided or (int)MeshAssetRange.MaskedTwoSided
                ? CullModeFlags.None
                : CullModeFlags.BackBit;
            _viewPipelines[range] = PipelineFactory.CreateTriangles(
                _context,
                _vertexSpirv,
                masked ? _maskedFragmentSpirv : _fragmentSpirv,
                _layout,
                _format,
                cull,
                in depth,
                $"image view mesh assets {(MeshAssetRange)range}"
            );
        }

        var blendDepth = new DepthState(DeviceRequirements.DepthFormat, CompareOp.Greater, Write: false);
        _viewBlendPipeline = PipelineFactory.CreateTriangles(
            _context,
            _blendVertexSpirv,
            _blendFragmentSpirv,
            _layout,
            _format,
            CullModeFlags.None,
            in blendDepth,
            "image view mesh assets blended",
            ColorBlend.Premultiplied
        );
    }

    private void DestroyPipelines()
    {
        DestroyPipeline(ref _skinPipeline);
        DestroyPipeline(ref _blendPipeline);
        DestroyPipeline(ref _viewBlendPipeline);
        for (var i = 0; i < MeshAssetLayout.ListRanges; i++)
        {
            DestroyPipeline(ref _viewPipelines[i]);
        }

        for (var i = 0; i < MeshAssetLayout.ListRanges; i++)
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

    /// <summary>A pool for one set of the pass's layout.</summary>
    private DescriptorPool CreatePool()
    {
        var textureCount = (uint)SlotCount;
        var poolSizes = stackalloc DescriptorPoolSize[3]
        {
            new(DescriptorType.Sampler, Samplers),
            new(DescriptorType.SampledImage, textureCount),
            new(DescriptorType.StorageBuffer, 8),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 3,
            PPoolSizes = poolSizes,
        };
        _context
            .Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, out var pool)
            .ThrowIfFailed("vkCreateDescriptorPool");
        return pool;
    }

    /// <summary>
    /// A set of the pass's layout from <paramref name="pool"/>: the textures, the vertices, the materials, the given
    /// draws (the pass's own, or an image view's), and the skinning's buffers.
    /// </summary>
    private DescriptorSet WriteSet(DescriptorPool pool, GpuBuffer draws, string name)
    {
        var vk = _context.Vk;
        var device = _context.Device;
        var textureCount = (uint)SlotCount;
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

        var images = new DescriptorImageInfo[textureCount];
        for (var i = 0; i < images.Length; i++)
        {
            // The asset textures, then each image view's shown image (the white stand-in until the views are made).
            var view = i < _textures!.Length ? _textures[i].View : ViewImage(i - _textures.Length);
            images[i] = new DescriptorImageInfo(default, view, ImageLayout.ShaderReadOnlyOptimal);
        }

        const int bufferCount = 8;
        var infos = stackalloc DescriptorBufferInfo[bufferCount]
        {
            new(Vertices.Buffer, 0, Vk.WholeSize),
            new(Materials.Buffer, 0, Vk.WholeSize),
            new(draws.Buffer, 0, Vk.WholeSize),
            new(Skins.Buffer, 0, Vk.WholeSize),
            new(SkinGroups.Buffer, 0, Vk.WholeSize),
            new(Palettes.Buffer, 0, Vk.WholeSize),
            new(SkinPositions.Buffer, 0, Vk.WholeSize),
            new(SkinFrames.Buffer, 0, Vk.WholeSize),
        };
        fixed (DescriptorImageInfo* imageInfos = images)
        {
            var writes = stackalloc WriteDescriptorSet[1 + bufferCount];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
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
                    DstSet = set,
                    DstBinding = (uint)(2 + i),
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &infos[i],
                };
            }

            vk.UpdateDescriptorSets(device, 1 + bufferCount, writes, 0, null);
        }

        _context.SetObjectName(ObjectType.DescriptorSet, set.Handle, name);
        return set;
    }
}
