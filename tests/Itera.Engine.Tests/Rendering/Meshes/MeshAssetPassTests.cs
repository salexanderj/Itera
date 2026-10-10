using System.Numerics;
using Itera.Engine.Assets;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The mesh asset pass's shaders read the vertices, materials, draws, view, and cascades as the C# structs lay them out,
/// in sets 0 (the mesh pass's) and 1 (its own); its materials pack their texture references as the shaders read them; its
/// layout lists every asset's primitives grouped by pipeline range.
/// </summary>
public sealed class MeshAssetPassTests : IDisposable
{
    private const string Count = "MESH_TEXTURE_COUNT 5";
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public MeshAssetPassTests()
    {
        _compiler = new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, _cache);
        _compiler.Initialize();
    }

    public void Dispose()
    {
        _compiler.Dispose();
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public void The_camera_shaders_read_the_vertices_materials_draws_and_view_as_the_structs_lay_them_out()
    {
        var vertex = _compiler.Compile("mesh-asset.vert", [Count]);
        var fragment = _compiler.Compile("mesh-asset.frag", [Count]);
        var masked = _compiler.Compile("mesh-asset.frag", [Count, "MESH_ASSET_MASK"]);

        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshAssetVertex>(vertex, 1, 2));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshAssetDraw>(vertex, 1, 4));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(vertex));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshAssetMaterial>(fragment, 1, 3));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLighting>(fragment, 0, 4));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLight>(fragment, 0, 7));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(fragment));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshAssetMaterial>(masked, 1, 3));
        Assert.Equal([0u, 1u], GpuLayout.DescriptorSets(fragment));
    }

    [Fact]
    public void The_shadow_shaders_read_the_cascades_and_the_draws_as_the_structs_lay_them_out()
    {
        var vertex = _compiler.Compile("mesh-asset-shadow.vert", [Count]);
        var masked = _compiler.Compile("mesh-asset-shadow.vert", [Count, "MESH_ASSET_MASK"]);
        var fragment = _compiler.Compile("mesh-asset-shadow.frag", [Count]);

        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLighting>(vertex, 0, 4));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshAssetVertex>(vertex, 1, 2));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshAssetDraw>(vertex, 1, 4));
        Assert.Empty(GpuLayout.PushConstantDifferences<MeshShadowConstants>(vertex));
        Assert.Empty(GpuLayout.PushConstantDifferences<MeshShadowConstants>(masked));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshAssetMaterial>(fragment, 1, 3));
    }

    [Fact]
    public void A_material_packs_its_textures_after_the_assets_first_and_none_past_the_room()
    {
        var material = new MeshMaterialData
        {
            BaseColourTexture = new MeshTextureRef(0, MeshSampler.NearestClamp, 1),
            NormalTexture = new MeshTextureRef(3, MeshSampler.LinearRepeat, 0),
            AlphaMode = MeshAlphaMode.Blend,
            DoubleSided = true,
        };

        var packed = MeshAssetMaterial.From(material, firstTexture: 5, textureCapacity: 7);

        Assert.Equal(5u | ((uint)MeshSampler.NearestClamp << 16) | (1u << 19), packed.BaseColourTexture);
        // 5 + 3 is past the room for 7: the factor alone.
        Assert.Equal(MeshAssetMaterial.NoTexture, packed.NormalTexture & 0xFFFFu);
        Assert.Equal(MeshAssetMaterial.NoTexture, packed.OcclusionTexture & 0xFFFFu);
        Assert.Equal(MeshAssetMaterial.Masked | MeshAssetMaterial.TwoSided, packed.Flags);
    }

    [Fact]
    public void The_layout_places_each_asset_after_the_last_and_groups_primitives_by_range()
    {
        var opaque = Asset("opaque", MeshMaterialData.Default, vertices: 3);
        var mixed = Asset(
            "mixed",
            new MeshMaterialData { AlphaMode = MeshAlphaMode.Mask },
            vertices: 4,
            second: new MeshMaterialData { DoubleSided = true }
        );

        var layout = MeshAssetLayout.From([opaque, mixed]);

        Assert.Equal(7, layout.VertexCount);
        Assert.Equal(3, layout.MaterialCount);
        Assert.Equal(3, layout.Assets[1].FirstVertex);
        Assert.Equal(1, layout.Assets[1].FirstMaterial);
        Assert.Equal([0, 1, 2, 3, 3], layout.RangeStarts);
        Assert.Equal(MeshAssetRange.Opaque, layout.Primitives[0].Range);
        Assert.Equal(MeshAssetRange.OpaqueTwoSided, layout.Primitives[1].Range);
        Assert.Equal(MeshAssetRange.Masked, layout.Primitives[2].Range);
        // The mixed asset's first primitive (masked) is listed third, its second (two-sided) second.
        Assert.Equal(2, layout.AssetPrimitives[layout.Assets[1].FirstPrimitive]);
        Assert.Equal(1, layout.AssetPrimitives[layout.Assets[1].FirstPrimitive + 1]);
        Assert.Equal(3, layout.Primitives[2].FirstIndex);
        Assert.Equal(3 + 3, layout.Primitives[1].FirstIndex);
        Assert.Equal(3, layout.Primitives[2].FirstVertex);
    }

    /// <summary>
    /// An asset of <paramref name="vertices"/> vertices in a line along x and one triangle a primitive: one of
    /// <paramref name="first"/>, and with <paramref name="second"/> a second.
    /// </summary>
    internal static MeshAsset Asset(string name, MeshMaterialData first, int vertices, MeshMaterialData? second = null)
    {
        var list = new MeshAssetVertex[vertices];
        for (var i = 0; i < vertices; i++)
        {
            list[i] = MeshAssetVertex.Pack(
                new Vector3(i, i % 2, 0),
                Vector3.UnitZ,
                new Vector4(1, 0, 0, 1),
                Vector2.Zero,
                Vector2.Zero,
                Vector4.One
            );
        }

        uint[] indices = second is null ? [0, 1, 2] : [0, 1, 2, 1, 2, 3];
        MeshPrimitive[] primitives = second is null ? [new(0, 3, 0)] : [new(0, 3, 0), new(3, 3, 1)];
        MeshMaterialData[] materials = second is null ? [first] : [first, second];
        return new MeshAsset(name, list, indices, primitives, materials, []);
    }
}
