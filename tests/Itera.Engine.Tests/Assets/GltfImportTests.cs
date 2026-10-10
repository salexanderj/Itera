using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using Itera.Engine.Maths;
using Itera.Engine.Rendering.GpuData;
using SkiaSharp;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="GltfImport"/> on glTF files written in the test (<see cref="GltfBuilder"/>): vertices, normals, indices,
/// transforms, units, and winding; materials and their textures in their roles; what it refuses and warns of.
/// </summary>
public sealed class GltfImportTests : IDisposable
{
    // A triangle in the plane z = 0, counter-clockwise seen from +z.
    private static readonly Vector3[] s_triangle = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
    private static readonly Vector3[] s_up = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
    private static readonly Vector2[] s_uvs = [new(0, 0), new(1, 0), new(0, 1)];

    private readonly string _folder = Directory.CreateTempSubdirectory("itera-gltf-").FullName;
    private readonly List<string> _warnings = [];

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_triangle_with_normals_uvs_and_indices_reads_as_given(bool glb)
    {
        var gltf = new GltfBuilder();
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(s_triangle),
                gltf.Accessor(s_up),
                gltf.Accessor(s_uvs),
                gltf.Indices(0, 1, 2)
            )
        );
        gltf.Node(new JsonObject { ["mesh"] = mesh });

        var asset = Load(glb ? gltf.WriteGlb(_folder, "triangle") : gltf.WriteGltf(_folder, "triangle"));

        Assert.Equal(3, asset.Vertices.Length);
        Assert.Equal(new uint[] { 0, 1, 2 }, asset.Indices);
        Assert.Equal(new MeshPrimitive(0, 3, 0), Assert.Single(asset.Primitives));
        Assert.Same(MeshMaterialData.Default, Assert.Single(asset.Materials));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(s_triangle[i], asset.Vertices[i].Position);
            Assert.Equal(s_uvs[i], asset.Vertices[i].Uv);
            AssertNear(Vector3.UnitZ, Octahedral.UnpackSnorm16(asset.Vertices[i].Normal));
            Assert.Equal(MeshAssetVertex.White, asset.Vertices[i].Colour);
        }

        Assert.Equal(Vector3.Zero, asset.BoundsMin);
        Assert.Equal(new Vector3(1, 1, 0), asset.BoundsMax);
        Assert.Empty(_warnings);
    }

    [Fact]
    public void Triangles_without_indices_or_normals_get_flat_normals_by_their_winding()
    {
        var gltf = new GltfBuilder();
        // The first counter-clockwise from +z, the second from -z.
        Vector3[] positions = [.. s_triangle, new(0, 0, 1), new(0, 1, 1), new(1, 0, 1)];
        gltf.Node(new JsonObject { ["mesh"] = gltf.Mesh(GltfBuilder.Primitive(gltf.Accessor(positions))) });

        var asset = Load(gltf.WriteGltf(_folder, "flat"));

        Assert.Equal(6, asset.Vertices.Length);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, asset.Indices);
        for (var i = 0; i < 6; i++)
        {
            AssertNear(i < 3 ? Vector3.UnitZ : -Vector3.UnitZ, Octahedral.UnpackSnorm16(asset.Vertices[i].Normal));
        }
    }

    [Fact]
    public void Indexed_triangles_without_normals_are_unwelded_so_each_face_keeps_its_own()
    {
        var gltf = new GltfBuilder();
        // A fold along the y axis: one face in z = 0 facing +z, the other in x = 0 facing -x.
        Vector3[] positions = [new(0, 0, 0), new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)];
        var mesh = gltf.Mesh(GltfBuilder.Primitive(gltf.Accessor(positions), indices: gltf.Indices(0, 2, 1, 0, 1, 3)));
        gltf.Node(new JsonObject { ["mesh"] = mesh });

        var asset = Load(gltf.WriteGltf(_folder, "fold"));

        Assert.Equal(6, asset.Vertices.Length);
        Assert.Equal(6, asset.Indices.Length);
        AssertNear(Vector3.UnitZ, Octahedral.UnpackSnorm16(asset.Vertices[0].Normal));
        AssertNear(-Vector3.UnitX, Octahedral.UnpackSnorm16(asset.Vertices[3].Normal));
    }

    [Fact]
    public void Node_transforms_and_the_unit_are_baked_into_the_vertices()
    {
        var gltf = new GltfBuilder();
        var mesh = gltf.Mesh(GltfBuilder.Primitive(gltf.Accessor(s_triangle), gltf.Accessor(s_up)));
        // A child turned a quarter about z and scaled twice, under a parent moved 10 along x.
        var half = MathF.Sqrt(0.5f);
        var child = gltf.Node(
            new JsonObject
            {
                ["mesh"] = mesh,
                ["rotation"] = new JsonArray(0f, 0f, half, half),
                ["scale"] = new JsonArray(2f, 2f, 2f),
            },
            root: false
        );
        gltf.Node(new JsonObject { ["translation"] = new JsonArray(10f, 0f, 0f), ["children"] = new JsonArray(child) });

        var asset = Load(gltf.WriteGltf(_folder, "nodes"), metresPerUnit: 0.5f);

        // (1, 0, 0): scaled to (2, 0, 0), turned to (0, 2, 0), moved to (10, 2, 0), in metres (5, 1, 0).
        AssertNear(new Vector3(5, 0, 0), asset.Vertices[0].Position);
        AssertNear(new Vector3(5, 1, 0), asset.Vertices[1].Position);
        AssertNear(new Vector3(4, 0, 0), asset.Vertices[2].Position);
        AssertNear(Vector3.UnitZ, Octahedral.UnpackSnorm16(asset.Vertices[1].Normal));
    }

    [Fact]
    public void A_mirroring_transform_flips_the_winding_so_the_front_stays_the_front()
    {
        var gltf = new GltfBuilder();
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(gltf.Accessor(s_triangle), gltf.Accessor(s_up), indices: gltf.Indices(0, 1, 2))
        );
        gltf.Node(new JsonObject { ["mesh"] = mesh, ["scale"] = new JsonArray(-1f, 1f, 1f) });

        var asset = Load(gltf.WriteGltf(_folder, "mirrored"));

        var a = asset.Vertices[(int)asset.Indices[0]].Position;
        var b = asset.Vertices[(int)asset.Indices[1]].Position;
        var c = asset.Vertices[(int)asset.Indices[2]].Position;
        var front = Vector3.Cross(b - a, c - a);
        var normal = Octahedral.UnpackSnorm16(asset.Vertices[0].Normal);
        Assert.True(Vector3.Dot(front, normal) > 0, $"winding {front} against normal {normal}");
    }

    [Fact]
    public void Materials_read_their_factors_and_their_textures_in_their_roles()
    {
        var gltf = new GltfBuilder();
        gltf.ExtensionsUsed.Add("KHR_materials_emissive_strength");
        var colour = gltf.Texture(
            gltf.Image(GltfBuilder.Png(4, 4, new SKColor(200, 100, 50, 255))),
            new JsonObject
            {
                ["magFilter"] = 9728,
                ["wrapS"] = 33071,
                ["wrapT"] = 33071,
            }
        );
        var linear = gltf.Texture(gltf.Image(GltfBuilder.Png(4, 4, new SKColor(255, 128, 0, 255))));
        var normalMap = gltf.Texture(gltf.Image(GltfBuilder.Png(4, 4, new SKColor(128, 128, 255, 255))));
        var material = gltf.Material(
            new JsonObject
            {
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorFactor"] = new JsonArray(0.5f, 0.25f, 1f, 1f),
                    ["baseColorTexture"] = new JsonObject { ["index"] = colour },
                    ["metallicFactor"] = 0.25f,
                    ["roughnessFactor"] = 0.75f,
                    ["metallicRoughnessTexture"] = new JsonObject { ["index"] = linear },
                },
                ["occlusionTexture"] = new JsonObject { ["index"] = linear, ["strength"] = 0.5f },
                ["normalTexture"] = new JsonObject { ["index"] = normalMap, ["scale"] = 0.7f },
                ["emissiveFactor"] = new JsonArray(1f, 0.5f, 0f),
                ["extensions"] = new JsonObject
                {
                    ["KHR_materials_emissive_strength"] = new JsonObject { ["emissiveStrength"] = 2f },
                },
                ["alphaMode"] = "MASK",
                ["alphaCutoff"] = 0.3f,
                ["doubleSided"] = true,
            }
        );
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(
                gltf.Accessor(s_triangle),
                gltf.Accessor(s_up),
                gltf.Accessor(s_uvs),
                material: material
            )
        );
        gltf.Node(new JsonObject { ["mesh"] = mesh });

        var asset = Load(gltf.WriteGltf(_folder, "material"));

        var data = Assert.Single(asset.Materials);
        Assert.Equal(new Vector4(0.5f, 0.25f, 1f, 1f), data.BaseColour);
        Assert.Equal(0.25f, data.Metallic);
        Assert.Equal(0.75f, data.Roughness);
        Assert.Equal(0.5f, data.OcclusionStrength);
        Assert.Equal(0.7f, data.NormalScale, 5);
        Assert.Equal(new Vector3(2f, 1f, 0f), data.Emission);
        Assert.Equal(MeshAlphaMode.Mask, data.AlphaMode);
        Assert.Equal(0.3f, data.AlphaCutoff, 5);
        Assert.True(data.DoubleSided);
        Assert.Equal(MeshSampler.NearestClamp, data.BaseColourTexture.Sampler);
        Assert.Equal(MeshSampler.LinearRepeat, data.MetalRoughTexture.Sampler);
        // The metallic-roughness and the occlusion share one image in one role: one texture.
        Assert.Equal(3, asset.Textures.Length);
        Assert.Equal(data.MetalRoughTexture.Texture, data.OcclusionTexture.Texture);
        Assert.Equal(MeshTextureFormat.Rgba8Srgb, asset.Textures[data.BaseColourTexture.Texture].Format);
        Assert.Equal(MeshTextureFormat.Rgba8Unorm, asset.Textures[data.MetalRoughTexture.Texture].Format);
        Assert.Equal(MeshTextureFormat.Rg8Unorm, asset.Textures[data.NormalTexture.Texture].Format);
        Assert.Equal(3, asset.Textures[data.BaseColourTexture.Texture].Levels);
        Assert.Equal(200, asset.Textures[data.BaseColourTexture.Texture].Texels[0]);
        // A normal-mapped material without tangents gets them: u runs along +x here, v along +y.
        var tangent = Octahedral.UnpackTangent(asset.Vertices[0].Tangent, out var handedness);
        AssertNear(Vector3.UnitX, tangent);
        Assert.Equal(1, handedness);
    }

    [Fact]
    public void One_image_in_two_roles_makes_two_textures()
    {
        var gltf = new GltfBuilder();
        var texture = gltf.Texture(gltf.Image(GltfBuilder.Png(2, 2, SKColors.White)));
        var material = gltf.Material(
            new JsonObject
            {
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorTexture"] = new JsonObject { ["index"] = texture },
                },
                ["occlusionTexture"] = new JsonObject { ["index"] = texture },
            }
        );
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(gltf.Accessor(s_triangle), uv: gltf.Accessor(s_uvs), material: material)
        );
        gltf.Node(new JsonObject { ["mesh"] = mesh });

        var asset = Load(gltf.WriteGltf(_folder, "roles"));

        Assert.Equal(2, asset.Textures.Length);
        Assert.Equal(MeshTextureRole.Colour, asset.Textures[asset.Materials[0].BaseColourTexture.Texture].Role);
        Assert.Equal(MeshTextureRole.Linear, asset.Textures[asset.Materials[0].OcclusionTexture.Texture].Role);
    }

    [Fact]
    public void An_image_that_cannot_be_decoded_leaves_its_material_the_factor_and_warns()
    {
        var gltf = new GltfBuilder();
        var texture = gltf.Texture(gltf.Image([0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4]));
        var material = gltf.Material(
            new JsonObject
            {
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorTexture"] = new JsonObject { ["index"] = texture },
                },
            }
        );
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(gltf.Accessor(s_triangle), uv: gltf.Accessor(s_uvs), material: material)
        );
        gltf.Node(new JsonObject { ["mesh"] = mesh });

        var asset = Load(gltf.WriteGltf(_folder, "broken"));

        Assert.True(asset.Materials[0].BaseColourTexture.IsNone);
        Assert.Empty(asset.Textures);
        Assert.Contains(_warnings, w => w.Contains("cannot be decoded", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_textures_no_image_is_decoded_and_materials_name_none()
    {
        var gltf = new GltfBuilder();
        var texture = gltf.Texture(gltf.Image(GltfBuilder.Png(2, 2, SKColors.Red)));
        var material = gltf.Material(
            new JsonObject
            {
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorTexture"] = new JsonObject { ["index"] = texture },
                },
            }
        );
        var mesh = gltf.Mesh(
            GltfBuilder.Primitive(gltf.Accessor(s_triangle), uv: gltf.Accessor(s_uvs), material: material)
        );
        gltf.Node(new JsonObject { ["mesh"] = mesh });

        var asset = Load(gltf.WriteGltf(_folder, "untextured"), textures: TextureReading.None);

        Assert.Empty(asset.Textures);
        Assert.True(asset.Materials[0].BaseColourTexture.IsNone);
    }

    [Theory]
    [InlineData("KHR_draco_mesh_compression", false)]
    [InlineData("KHR_draco_mesh_compression", true)]
    [InlineData("EXT_meshopt_compression", false)]
    [InlineData("KHR_texture_basisu", true)]
    [InlineData("EXT_something_new", false)]
    public void A_file_that_requires_an_extension_it_cannot_read_is_refused_by_name(string extension, bool glb)
    {
        var gltf = Triangle();
        gltf.ExtensionsUsed.Add(extension);
        gltf.ExtensionsRequired.Add(extension);
        var path = glb ? gltf.WriteGlb(_folder, "refused") : gltf.WriteGltf(_folder, "refused");

        var error = Assert.Throws<InvalidDataException>(() => Load(path));

        Assert.Contains(Path.GetFileName(path), error.Message, StringComparison.Ordinal);
        Assert.Contains(extension, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_requires_mesh_quantization_is_read()
    {
        var gltf = Triangle();
        gltf.ExtensionsUsed.Add("KHR_mesh_quantization");
        gltf.ExtensionsRequired.Add("KHR_mesh_quantization");

        var asset = Load(gltf.WriteGltf(_folder, "quantised"));

        Assert.Equal(3, asset.Vertices.Length);
    }

    [Fact]
    public void Lines_are_left_out_with_a_warning()
    {
        var gltf = new GltfBuilder();
        var lines = GltfBuilder.Primitive(gltf.Accessor(s_triangle));
        lines["mode"] = 1;
        gltf.Node(new JsonObject { ["mesh"] = gltf.Mesh(lines) });

        var asset = Load(gltf.WriteGltf(_folder, "lines"));

        Assert.Empty(asset.Primitives);
        Assert.Contains(_warnings, w => w.Contains("LINES", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_is_not_gltf_is_refused_by_name()
    {
        var path = Path.Combine(_folder, "nonsense.gltf");
        File.WriteAllText(path, "{ not json");

        var error = Assert.Throws<InvalidDataException>(() => Load(path));

        Assert.Contains("nonsense.gltf", error.Message, StringComparison.Ordinal);
    }

    private MeshAsset Load(
        string path,
        float metresPerUnit = 1,
        TextureReading textures = TextureReading.PreferCompressed
    ) => GltfImport.Load("test", path, metresPerUnit, textures, _warnings);

    private static GltfBuilder Triangle()
    {
        var gltf = new GltfBuilder();
        var mesh = gltf.Mesh(GltfBuilder.Primitive(gltf.Accessor(s_triangle), gltf.Accessor(s_up)));
        gltf.Node(new JsonObject { ["mesh"] = mesh });
        return gltf;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");
    }
}
