using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Itera.Engine.Rendering.GpuData;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace Itera.Engine.Assets;

/// <summary>
/// Reads a glTF 2.0 file (.gltf with its buffers and images, or .glb) into a <see cref="MeshAsset"/>: the only code that
/// uses SharpGLTF, which it runs at load alone; the asset holds the engine's own arrays. What it does:
/// <list type="bullet">
/// <item>Refuses a file that requires an extension it cannot read (compressed geometry, Basis textures, any other not
/// listed in <see cref="s_handled"/>), with a message naming the file and the extension.</item>
/// <item>Walks the default scene's nodes: each mesh's triangles (strips and fans too; points and lines are left out)
/// with the node's world transform and the unit scale baked into its vertices, the winding flipped where the transform
/// mirrors. A skinned mesh is kept in the space its file stores it in, as glTF asks (its node's own transform does not
/// apply).</item>
/// <item>Gives triangles without normals flat ones (each its own three vertices, as glTF asks), and normal-mapped
/// materials' vertices without tangents generated ones (<see cref="MeshTangents"/>).</item>
/// <item>Reads the first skin a mesh uses, and the animations of its joints, into the asset's <see cref="Rig"/>
/// (<c>GltfImport.Rig.cs</c>).</item>
/// <item>Reads each material's factors and textures: each image a material uses, in each role, from the model's
/// compressed textures when they match it (<see cref="CompressedTextures"/>), or decoded (<see cref="TextureDecode"/>)
/// with its mip levels made in the role's format (<see cref="MipChain"/>). An image it cannot decode leaves the
/// material's factor alone, with a warning.</item>
/// </list>
/// Warnings and notes go into the lists given, for the caller to log. Allocates freely: load time only.
/// </summary>
internal static partial class GltfImport
{
    /// <summary>Extensions a file may require that the importer reads: quantised accessors, and an emission strength.</summary>
    private static readonly string[] s_handled = ["KHR_mesh_quantization", "KHR_materials_emissive_strength"];

    /// <summary>The 'glTF' magic number at the start of a .glb file, and its JSON chunk's type.</summary>
    private const uint GlbMagic = 0x46546C67;
    private const uint GlbJsonChunk = 0x4E4F534A;

    /// <summary>
    /// Reads the file at <paramref name="path"/>, its textures as <paramref name="textures"/> says (with
    /// <see cref="TextureReading.None"/>, a run without graphics, its materials name none). Throws
    /// <see cref="InvalidDataException"/> for a file it cannot read, naming it. A model whose textures were all decoded,
    /// with no compressed textures beside it, gets a note in <paramref name="notes"/>. What it leaves out or makes up
    /// without a warning is counted in <paramref name="findings"/>.
    /// </summary>
    public static MeshAsset Load(
        string name,
        string path,
        float metresPerUnit,
        TextureReading textures,
        List<string> warnings,
        List<string>? notes = null,
        ImportFindings? findings = null
    )
    {
        RefuseUnhandled(path, ReadRequiredExtensions(path));
        ModelRoot model;
        try
        {
            model = ModelRoot.Load(path, new ReadSettings { Validation = ValidationMode.TryFix });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"{path}: not a glTF 2.0 file the importer can read: {ex.Message}", ex);
        }

        var context = new ImportContext(path, textures, warnings, findings ?? new ImportFindings())
        {
            MetresPerUnit = metresPerUnit,
        };
        var asset = Import(name, model, metresPerUnit, context);
        if (textures == TextureReading.PreferCompressed && context.DecodedTextures > 0 && !context.HasCompressed)
        {
            notes?.Add(
                $"{Path.GetFileName(path)}: {context.DecodedTextures} texture(s) decoded at load, with no "
                    + $"{Path.GetFileName(CompressedTextures.FolderOf(path))}/{CompressedTextures.ListFile} beside it: "
                    + $"tools/Textures compresses them (`{CompressedTextures.Command}` on the source file)"
            );
        }

        return asset;
    }

    /// <summary>
    /// The extensions the file lists as required, read from its JSON before SharpGLTF reads it (which fails on some
    /// without saying which).
    /// </summary>
    internal static string[] ReadRequiredExtensions(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var json = bytes.AsMemory();
        if (bytes.Length >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
            if (
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != GlbJsonChunk
                || length < 0
                || length > bytes.Length - 20
            )
            {
                throw new InvalidDataException($"{path}: a .glb file whose first chunk is not its JSON");
            }

            json = bytes.AsMemory(20, length);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("extensionsRequired", out var required)
                || required.ValueKind != JsonValueKind.Array
            )
            {
                return [];
            }

            return [.. required.EnumerateArray().Select(element => element.GetString() ?? "")];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: its JSON does not parse: {ex.Message}", ex);
        }
    }

    /// <summary>True when the importer reads a file that requires <paramref name="extension"/>.</summary>
    internal static bool Reads(string extension) => s_handled.Contains(extension, StringComparer.Ordinal);

    /// <summary>Why a file that requires <paramref name="extension"/>, which the importer does not read, is refused.</summary>
    internal static string RefusalOf(string path, string extension)
    {
        var what = extension switch
        {
            "KHR_draco_mesh_compression" or "EXT_meshopt_compression" or "KHR_meshopt_compression" =>
                " (compressed geometry)",
            "KHR_texture_basisu" => " (Basis Universal textures)",
            _ => "",
        };
        return $"{path} requires the glTF extension {extension}{what}, which Itera does not read: export the model without it";
    }

    private static void RefuseUnhandled(string path, string[] required)
    {
        foreach (var extension in required)
        {
            if (!Reads(extension))
            {
                throw new InvalidDataException(RefusalOf(path, extension));
            }
        }
    }

    private static MeshAsset Import(string name, ModelRoot model, float metresPerUnit, ImportContext context)
    {
        if (model.ExtensionsUsed.Contains("KHR_texture_transform", StringComparer.Ordinal))
        {
            context.Warn("uses KHR_texture_transform, which is not applied: textures may sit wrongly");
        }

        context.Findings.ExtensionsUsed.AddRange(model.ExtensionsUsed);
        context.Findings.Scenes = model.LogicalScenes.Count;
        foreach (var material in model.LogicalMaterials)
        {
            context.Materials.Add(ReadMaterial(material, context));
        }

        var scene = model.DefaultScene ?? (model.LogicalScenes.Count > 0 ? model.LogicalScenes[0] : null);
        RigPlan? plan = null;
        if (scene is null)
        {
            context.Warn("has no scene: no triangles");
        }
        else
        {
            Skin? skin = null;
            foreach (var node in scene.VisualChildren)
            {
                skin = FindSkin(node, skin, context);
            }

            plan = skin is null ? null : PlanRig(skin);
            var scale = Matrix4x4.CreateScale(metresPerUnit);
            foreach (var node in scene.VisualChildren)
            {
                AddNode(node, scale, plan, context);
            }
        }

        var rig = plan is null ? null : BuildRig(model, plan, metresPerUnit, context);
        if (plan is null)
        {
            context.Findings.LeftOutAnimations += model.LogicalAnimations.Count;
        }

        context.Findings.ReusedMeshes = context.MeshUses.Count(uses => uses.Value > 1);
        return new MeshAsset(
            name,
            [.. context.Vertices],
            [.. context.Indices],
            [.. context.Primitives],
            [.. context.Materials],
            [.. context.Textures],
            rig,
            rig is null ? null : [.. context.Skins],
            [.. context.Points]
        );
    }

    private static void AddNode(Node node, Matrix4x4 scale, RigPlan? plan, ImportContext context)
    {
        if (node.Camera is not null || node.PunctualLight is not null)
        {
            context.Findings.CameraOrLightNodes++;
        }

        if (HoldsNothing(node))
        {
            if (string.IsNullOrEmpty(node.Name))
            {
                context.Findings.UnnamedEmptyNodes++;
            }
            else
            {
                AddPoint(node, scale, plan, context);
            }
        }

        if (node.Mesh is { } mesh)
        {
            context.MeshUses[mesh.LogicalIndex] = context.MeshUses.GetValueOrDefault(mesh.LogicalIndex) + 1;
            // glTF: a skinned mesh's node transform does not apply; its joints place it.
            var transform = (node.Skin is null ? node.WorldMatrix : Matrix4x4.Identity) * scale;
            var posedBy = plan is not null && node.Skin?.LogicalIndex == plan.Skin.LogicalIndex ? plan : null;
            foreach (var primitive in mesh.Primitives)
            {
                AddPrimitive(primitive, transform, posedBy, context);
            }
        }

        foreach (var child in node.VisualChildren)
        {
            AddNode(child, scale, plan, context);
        }
    }

    /// <summary>
    /// True for a leaf node that holds nothing (an empty in Blender): no mesh, camera, or light, not a joint of any skin,
    /// and no children. One with a name is a point (<see cref="MeshPoint"/>).
    /// </summary>
    private static bool HoldsNothing(Node node) =>
        node.Mesh is null
        && node.Camera is null
        && node.PunctualLight is null
        && !node.IsSkinJoint
        && !node.VisualChildren.Any();

    /// <summary>
    /// The point a named empty marks: in the space of the rig's joint above it, with the nodes between applied, or else
    /// in the asset's space as a static mesh's vertices are. In metres.
    /// </summary>
    private static void AddPoint(Node node, Matrix4x4 scale, RigPlan? plan, ImportContext context)
    {
        var joint = -1;
        var transform = node.WorldMatrix * scale;
        if (plan is not null)
        {
            var offset = node.LocalMatrix;
            var parent = node.VisualParent;
            while (parent is not null && !plan.RigOfNode.ContainsKey(parent.LogicalIndex))
            {
                offset *= parent.LocalMatrix;
                parent = parent.VisualParent;
            }

            if (parent is not null)
            {
                joint = plan.RigOfNode[parent.LogicalIndex];
                transform = InMetres(offset, context.MetresPerUnit);
            }
        }

        if (!Matrix4x4.Decompose(transform, out _, out var rotation, out var position))
        {
            context.Warn(
                $"point '{node.Name}' has a transform that is not a scale, turn, and move: it stands unturned"
            );
            (rotation, position) = (Quaternion.Identity, transform.Translation);
        }

        context.Points.Add(new MeshPoint(node.Name, joint, position, Quaternion.Normalize(rotation)));
    }

    /// <summary>
    /// The primitive's triangles and vertices; with <paramref name="plan"/>, each vertex's joints and weights in the rig
    /// (else none, so the rig never moves it).
    /// </summary>
    private static void AddPrimitive(
        SharpGLTF.Schema2.MeshPrimitive primitive,
        Matrix4x4 transform,
        RigPlan? plan,
        ImportContext context
    )
    {
        if (
            primitive.DrawPrimitiveType
            is not (PrimitiveType.TRIANGLES or PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN)
        )
        {
            context.Warn($"has {primitive.DrawPrimitiveType} primitives, which are not drawn");
            return;
        }

        if (primitive.GetVertexAccessor("POSITION") is not { } positionAccessor)
        {
            context.Warn("has a primitive without positions, which is not drawn");
            return;
        }

        if (primitive.MorphTargetsCount > 0)
        {
            context.Warn("has morph targets, which are not drawn: the base shape is");
        }

        if (primitive.Material is null)
        {
            context.Findings.PrimitivesWithoutMaterial++;
        }

        var materialIndex = primitive.Material?.LogicalIndex ?? context.DefaultMaterial();
        var material = context.Materials[materialIndex];
        var sourcePositions = positionAccessor.AsVector3Array();
        var sourceNormals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
        var sourceTangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
        var sourceUv0 = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        var sourceUv1 = primitive.GetVertexAccessor("TEXCOORD_1")?.AsVector2Array();
        var sourceColours = primitive.GetVertexAccessor("COLOR_0")?.AsColorArray(1f);
        var skinAccessors = plan is null
            ? null
            : new SkinAccessors(
                primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array(),
                primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array(),
                primitive.GetVertexAccessor("JOINTS_1")?.AsVector4Array(),
                primitive.GetVertexAccessor("WEIGHTS_1")?.AsVector4Array()
            );
        if (skinAccessors is { Joints0: null } or { Weights0: null })
        {
            context.Warn("has a skinned primitive without joints or weights, which stands as stored");
        }
        var mirrored = transform.GetDeterminant() < 0;
        if (!Matrix4x4.Invert(transform, out var inverse))
        {
            context.Warn("has a mesh whose node transform cannot be inverted (a zero scale), which is not drawn");
            return;
        }

        var normalTransform = Matrix4x4.Transpose(inverse);

        // The primitive's vertices in their final form, and its triangles over them (each primitive's own, from 0).
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector4>();
        var uv0 = new List<Vector2>();
        var uv1 = new List<Vector2>();
        var colours = new List<Vector4>();
        var skins = new List<MeshSkinVertex>();
        var triangles = new List<uint>();
        void Add(int source, Vector3 normal)
        {
            positions.Add(Vector3.Transform(sourcePositions[source], transform));
            skins.Add(skinAccessors is null ? default : SkinOf(source, skinAccessors, plan!, context));
            normals.Add(normal);
            uv0.Add(sourceUv0 is null ? Vector2.Zero : sourceUv0[source]);
            uv1.Add(sourceUv1 is null ? Vector2.Zero : sourceUv1[source]);
            colours.Add(sourceColours is null ? Vector4.One : sourceColours[source]);
            if (sourceTangents is not null)
            {
                var t = sourceTangents[source];
                var turned = SafeNormalize(
                    Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), transform),
                    Vector3.UnitX
                );
                // A mirroring transform turns the bitangent the other way round its normal and tangent.
                tangents.Add(new Vector4(turned, (t.W < 0 ? -1 : 1) * (mirrored ? -1 : 1)));
            }
            else
            {
                tangents.Add(new Vector4(1, 0, 0, 1));
            }
        }

        if (sourceNormals is null)
        {
            // glTF: without normals, flat ones; each triangle its own vertices.
            foreach (var (a, b, c) in primitive.GetTriangleIndices())
            {
                var (first, second) = mirrored ? (c, b) : (b, c);
                var pa = Vector3.Transform(sourcePositions[a], transform);
                var pb = Vector3.Transform(sourcePositions[first], transform);
                var pc = Vector3.Transform(sourcePositions[second], transform);
                var normal = SafeNormalize(Vector3.Cross(pb - pa, pc - pa), Vector3.UnitY);
                var start = (uint)positions.Count;
                Add(a, normal);
                Add(first, normal);
                Add(second, normal);
                triangles.Add(start);
                triangles.Add(start + 1);
                triangles.Add(start + 2);
            }
        }
        else
        {
            for (var i = 0; i < sourcePositions.Count; i++)
            {
                Add(i, SafeNormalize(Vector3.TransformNormal(sourceNormals[i], normalTransform), Vector3.UnitY));
            }

            foreach (var (a, b, c) in primitive.GetTriangleIndices())
            {
                triangles.Add((uint)a);
                triangles.Add((uint)(mirrored ? c : b));
                triangles.Add((uint)(mirrored ? b : c));
            }
        }

        if (sourceTangents is null && !material.NormalTexture.IsNone)
        {
            context.Findings.GeneratedTangentPrimitives++;
            var tangentArray = new Vector4[positions.Count];
            var uvs = material.NormalTexture.UvSet == 1 ? uv1 : uv0;
            MeshTangents.Generate([.. positions], [.. normals], [.. uvs], [.. triangles], tangentArray);
            tangents.Clear();
            tangents.AddRange(tangentArray);
        }

        var vertexBase = (uint)context.Vertices.Count;
        for (var i = 0; i < positions.Count; i++)
        {
            context.Vertices.Add(
                MeshAssetVertex.Pack(positions[i], normals[i], tangents[i], uv0[i], uv1[i], colours[i])
            );
        }

        context.Skins.AddRange(skins);

        context.Primitives.Add(new MeshPrimitive(context.Indices.Count, triangles.Count, materialIndex));
        foreach (var index in triangles)
        {
            context.Indices.Add(vertexBase + index);
        }
    }

    private static MeshMaterialData ReadMaterial(Material material, ImportContext context)
    {
        var data = new MeshMaterialData
        {
            Name = material.Name ?? "",
            AlphaMode = material.Alpha switch
            {
                AlphaMode.MASK => MeshAlphaMode.Mask,
                AlphaMode.BLEND => MeshAlphaMode.Blend,
                _ => MeshAlphaMode.Opaque,
            },
            AlphaCutoff = material.AlphaCutoff,
            DoubleSided = material.DoubleSided,
        };
        if (material.Unlit)
        {
            context.Warn($"material '{material.Name}' is unlit (KHR_materials_unlit), which is drawn lit");
        }

        if (material.FindChannel("BaseColor") is { } baseColour)
        {
            data = data with
            {
                BaseColour = Parameter(baseColour, "RGBA", Vector4.One),
                BaseColourTexture = ReadTexture(baseColour, MeshTextureRole.Colour, context),
            };
        }

        if (material.FindChannel("MetallicRoughness") is { } metalRough)
        {
            data = data with
            {
                Metallic = Parameter(metalRough, "MetallicFactor", 1f),
                Roughness = Parameter(metalRough, "RoughnessFactor", 1f),
                MetalRoughTexture = ReadTexture(metalRough, MeshTextureRole.Linear, context),
            };
        }

        if (material.FindChannel("Normal") is { } normal)
        {
            data = data with
            {
                NormalScale = Parameter(normal, "NormalScale", 1f),
                NormalTexture = ReadTexture(normal, MeshTextureRole.Normal, context),
            };
        }

        if (material.FindChannel("Occlusion") is { } occlusion)
        {
            data = data with
            {
                OcclusionStrength = Parameter(occlusion, "OcclusionStrength", 1f),
                OcclusionTexture = ReadTexture(occlusion, MeshTextureRole.Linear, context),
            };
        }

        if (material.FindChannel("Emissive") is { } emissive)
        {
            data = data with
            {
                Emission = Parameter(emissive, "RGB", Vector3.Zero) * Parameter(emissive, "EmissiveStrength", 1f),
                EmissionTexture = ReadTexture(emissive, MeshTextureRole.Colour, context),
            };
        }

        return data;
    }

    /// <summary>The channel's parameter of that name, or <paramref name="fallback"/>.</summary>
    private static T Parameter<T>(MaterialChannel channel, string name, T fallback)
    {
        foreach (var parameter in channel.Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal) && parameter.Value is T value)
            {
                return value;
            }
        }

        return fallback;
    }

    /// <summary>
    /// The channel's texture, read the first time an image is used in <paramref name="role"/>; none when the channel has
    /// none, the run reads no textures, or the image cannot be decoded.
    /// </summary>
    private static MeshTextureRef ReadTexture(MaterialChannel channel, MeshTextureRole role, ImportContext context)
    {
        if (channel.Texture is not { } texture || context.Reading == TextureReading.None)
        {
            return MeshTextureRef.None;
        }

        var uvSet = channel.TextureCoordinate;
        if (uvSet is not (0 or 1))
        {
            context.Warn($"a texture reads texture coordinates {uvSet}; the first are used");
            uvSet = 0;
        }

        var sampler = SamplerOf(texture.Sampler, context);
        if ((texture.PrimaryImage ?? texture.FallbackImage) is not { } image)
        {
            context.Warn($"texture {texture.LogicalIndex} has no image: drawn without it");
            return MeshTextureRef.None;
        }

        var key = (image.LogicalIndex, role);
        if (context.TextureIndices.TryGetValue(key, out var existing))
        {
            return existing < 0 ? MeshTextureRef.None : new MeshTextureRef(existing, sampler, uvSet);
        }

        var index = ReadImage(image, role, context);
        context.TextureIndices[key] = index;
        return index < 0 ? MeshTextureRef.None : new MeshTextureRef(index, sampler, uvSet);
    }

    /// <summary>
    /// The image in <paramref name="role"/> as a texture of the asset: from the model's compressed textures when they
    /// hold it made from this image, else decoded (once for every role) with its mip chain made in the role's format;
    /// -1 when it cannot be decoded.
    /// </summary>
    private static int ReadImage(Image image, MeshTextureRole role, ImportContext context)
    {
        var content = image.Content;
        var hash = context.HashOf(image);
        var name = $"{Path.GetFileName(context.Path)} image {image.LogicalIndex} ({role})";
        // An image the model does not hold is never read from a list: it is decoded, which warns that it is missing.
        if (!content.IsEmpty && context.CompressedList() is { } list)
        {
            var folder = CompressedTextures.FolderOf(context.Path);
            var compressed = CompressedTextures.TryRead(
                folder,
                list,
                name,
                image.LogicalIndex,
                role,
                hash,
                out var problem
            );
            if (compressed is not null)
            {
                context.Textures.Add(compressed);
                return context.Textures.Count - 1;
            }

            context.Warn(
                $"image {image.LogicalIndex} ({role}): {Path.GetFileName(folder)}: {problem}: decoded at load; make "
                    + $"them again with tools/Textures (`{CompressedTextures.Command}` on the source file)"
            );
        }

        if (!context.Decoded.TryGetValue(image.LogicalIndex, out var decoded))
        {
            decoded =
                !content.IsEmpty
                && TextureDecode.TryDecode(content.Content.Span, out var width, out var height, out var rgba)
                    ? (width, height, rgba)
                    : null;
            context.Decoded[image.LogicalIndex] = decoded;
            if (decoded is null)
            {
                var kind = content.IsEmpty ? "missing" : content.MimeType;
                context.Warn(
                    $"image {image.LogicalIndex} ({kind}) cannot be decoded: its materials are drawn without it"
                );
            }
        }

        if (decoded is not var (w, h, texels))
        {
            return -1;
        }

        var chain = MipChain.Build(texels, w, h, role, out var format, out var levels);
        context.Textures.Add(new MeshTextureData(name, w, h, levels, role, format, chain, image.LogicalIndex, hash));
        context.DecodedTextures++;
        return context.Textures.Count - 1;
    }

    /// <summary>glTF's sampler as the engine's: nearest when its magnification filter is, its wrap along s for both directions.</summary>
    private static MeshSampler SamplerOf(TextureSampler? sampler, ImportContext context)
    {
        if (sampler is null)
        {
            return MeshSampler.LinearRepeat;
        }

        if (sampler.WrapS != sampler.WrapT && !context.WrapWarned)
        {
            context.WrapWarned = true;
            context.Warn("a sampler wraps differently along t than along s; both wrap as along s");
        }

        var nearest = sampler.MagFilter == TextureInterpolationFilter.NEAREST;
        return sampler.WrapS switch
        {
            TextureWrapMode.CLAMP_TO_EDGE => nearest ? MeshSampler.NearestClamp : MeshSampler.LinearClamp,
            TextureWrapMode.MIRRORED_REPEAT => nearest ? MeshSampler.NearestMirror : MeshSampler.LinearMirror,
            _ => nearest ? MeshSampler.NearestRepeat : MeshSampler.LinearRepeat,
        };
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        var length = value.Length();
        return length > 1e-12f && float.IsFinite(length) ? value / length : fallback;
    }

    /// <summary>What an import gathers: the asset's arrays as they grow, its decoded images, its warnings, and its findings.</summary>
    private sealed class ImportContext(
        string path,
        TextureReading reading,
        List<string> warnings,
        ImportFindings findings
    )
    {
        private int _defaultMaterial = -1;
        private bool _listRead;
        private CompressedTextureList? _list;

        public string Path { get; } = path;

        public TextureReading Reading { get; } = reading;

        public ImportFindings Findings { get; } = findings;

        /// <summary>Metres a unit of the file.</summary>
        public float MetresPerUnit { get; init; } = 1f;

        /// <summary>How many nodes of the scene use each mesh (by glTF's index).</summary>
        public Dictionary<int, int> MeshUses { get; } = [];

        public List<MeshPoint> Points { get; } = [];

        /// <summary>True when the model has a list of compressed textures beside it, whether or not it reads.</summary>
        public bool HasCompressed { get; private set; }

        /// <summary>The textures decoded at load.</summary>
        public int DecodedTextures { get; set; }

        public List<MeshAssetVertex> Vertices { get; } = [];

        /// <summary>Each vertex's joints and weights, beside <see cref="Vertices"/>; kept when the asset has a rig.</summary>
        public List<MeshSkinVertex> Skins { get; } = [];

        public List<uint> Indices { get; } = [];

        public List<MeshPrimitive> Primitives { get; } = [];

        public List<MeshMaterialData> Materials { get; } = [];

        public List<MeshTextureData> Textures { get; } = [];

        public Dictionary<(int Image, MeshTextureRole Role), int> TextureIndices { get; } = [];

        public Dictionary<int, (int Width, int Height, byte[] Rgba)?> Decoded { get; } = [];

        /// <summary>Each image's hash (<see cref="CompressedTextures.Hash"/>), taken once for all its roles.</summary>
        public Dictionary<int, string> Hashes { get; } = [];

        public bool WrapWarned { get; set; }

        public bool SkinsWarned { get; set; }

        public bool JointsWarned { get; set; }

        public void Warn(string message) => warnings.Add($"{System.IO.Path.GetFileName(Path)} {message}");

        /// <summary>The hash of the image's bytes as the model stores them; empty for an image it does not hold.</summary>
        public string HashOf(Image image)
        {
            if (!Hashes.TryGetValue(image.LogicalIndex, out var hash))
            {
                var content = image.Content;
                hash = content.IsEmpty ? "" : CompressedTextures.Hash(content.Content.Span);
                Hashes[image.LogicalIndex] = hash;
            }

            return hash;
        }

        /// <summary>
        /// The model's list of compressed textures when the run prefers them and it has one, read the first time a
        /// texture asks (a list that cannot be read is a warning, and every texture is decoded).
        /// </summary>
        public CompressedTextureList? CompressedList()
        {
            if (_listRead || Reading != TextureReading.PreferCompressed)
            {
                return _list;
            }

            _listRead = true;
            var folder = CompressedTextures.FolderOf(Path);
            try
            {
                _list = CompressedTextures.ReadList(folder);
                HasCompressed = _list is not null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                HasCompressed = true;
                Warn(
                    $"{System.IO.Path.GetFileName(folder)}: {ex.Message}: its textures are decoded at load; make them "
                        + $"again with tools/Textures (`{CompressedTextures.Command}` on the source file)"
                );
            }

            return _list;
        }

        /// <summary>The index of glTF's default material, added the first time a primitive names none.</summary>
        public int DefaultMaterial()
        {
            if (_defaultMaterial < 0)
            {
                Materials.Add(MeshMaterialData.Default);
                _defaultMaterial = Materials.Count - 1;
            }

            return _defaultMaterial;
        }
    }
}
