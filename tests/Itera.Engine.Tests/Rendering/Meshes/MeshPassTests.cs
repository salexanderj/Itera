using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The raster pipeline's world pass: its shaders read the view, the draws, the materials, and the shadow cascades as the
/// C# structs lay them out.
/// </summary>
public sealed class MeshPassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public MeshPassTests()
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
    public void The_sky_reads_the_view_and_the_look_alone()
    {
        var spirv = _compiler.Compile("world-sky.frag");

        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(spirv));
        Assert.Equal([0u], GpuLayout.DescriptorSets(spirv));
        Assert.Empty(GpuLayout.ArrayElementDifferences<LookConstants>(spirv, 0, 17));
    }

    [Fact]
    public void The_meshes_read_the_draws_materials_and_view_as_the_structs_lay_them_out()
    {
        var vertex = _compiler.Compile("mesh.vert");
        var fragment = _compiler.Compile("mesh.frag");

        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshDraw>(vertex, 0, 1));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(vertex));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshMaterial>(fragment, 0, 2));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(fragment));
    }

    [Fact]
    public void The_lighting_is_read_as_the_structs_lay_it_out()
    {
        var shadow = _compiler.Compile("mesh-shadow.vert");
        var fragment = _compiler.Compile("mesh.frag");

        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLighting>(shadow, 0, 4));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshDraw>(shadow, 0, 1));
        Assert.Empty(GpuLayout.PushConstantDifferences<MeshShadowConstants>(shadow));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLighting>(fragment, 0, 4));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLight>(fragment, 0, 7));
    }

    /// <summary>The world, the sky, and the mesh assets read the frame's look as its record lays it out, at binding 17.</summary>
    [Fact]
    public void The_look_is_read_as_its_record_lays_it_out()
    {
        foreach (var name in (string[])["mesh.frag", "world-sky.frag", "mesh-asset.frag"])
        {
            Assert.Empty(GpuLayout.ArrayElementDifferences<LookConstants>(_compiler.Compile(name), 0, 17));
        }
    }

    /// <summary>
    /// The cluster pass reads the view's push constants and the lights as the structs lay them out, in the mesh pass's
    /// set; the world's fragments read the clusters it writes.
    /// </summary>
    [Fact]
    public void The_cluster_pass_reads_the_view_and_the_lights_as_the_structs_lay_them_out()
    {
        var clusters = _compiler.Compile("mesh-clusters.comp");

        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(clusters));
        Assert.Equal([0u], GpuLayout.DescriptorSets(clusters));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLight>(clusters, 0, 7));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLighting>(clusters, 0, 4));
    }

    [Fact]
    public void The_models_are_read_as_their_draws_lay_them_out()
    {
        var vertex = _compiler.Compile("mesh.vert", ["MESH_MODEL"]);
        var fading = _compiler.Compile("mesh.vert", ["MESH_MODEL", "MESH_FADE"]);
        var shadow = _compiler.Compile("mesh-shadow.vert", ["MESH_MODEL"]);

        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshModelDraw>(vertex, 0, 6));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(vertex));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshModelDraw>(fading, 0, 6));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(fading));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshModelDraw>(shadow, 0, 6));
        Assert.Empty(GpuLayout.PushConstantDifferences<MeshShadowConstants>(shadow));
    }

    /// <summary>
    /// The fading models' fragment shaders, the variants of the world's and the wireframe's that draw a draw's share of
    /// the dither (<c>mesh-fade.glsl</c>), compile apart from them and read the sets they do.
    /// </summary>
    [Theory]
    [InlineData("mesh.frag")]
    [InlineData("mesh-wire.frag")]
    public void The_fading_models_fragment_shaders_read_what_the_worlds_do(string name)
    {
        var world = _compiler.Compile(name);
        var fading = _compiler.Compile(name, ["MESH_FADE"]);

        Assert.NotEqual(world, fading);
        Assert.Equal(GpuLayout.DescriptorSets(world), GpuLayout.DescriptorSets(fading));
    }

    /// <summary>
    /// Draws given shares that meet (0 to a, a to b, b to 1) show each of the dither's thresholds once, compared in
    /// 65,536ths as <c>mesh-fade.glsl</c> compares them, also where a share rounds to nothing; a record left at zero shows
    /// every threshold.
    /// </summary>
    [Theory]
    [InlineData(0.3f, 0.7f)]
    [InlineData(0.123456f, 0.123457f)]
    [InlineData(0.000001f, 0.5f)]
    [InlineData(0.5f, 0.999999f)]
    public void Fade_shares_that_meet_show_each_threshold_once(float a, float b)
    {
        Assert.Equal(MeshModelDraw.ShowsAll, MeshModelDraw.Shows(0, 1));
        Assert.Equal(MeshModelDraw.ShowsAll, default(MeshModelDraw).Shown);
        Assert.Equal(MeshModelDraw.ShowsNone, MeshModelDraw.Shows(a, a));
        uint[] draws = [MeshModelDraw.Shows(0, a), MeshModelDraw.Shows(a, b), MeshModelDraw.Shows(b, 1)];
        for (uint threshold = 0; threshold < 65536; threshold++)
        {
            var count = 0;
            foreach (var shown in draws)
            {
                count += Shows(shown, threshold) ? 1 : 0;
            }

            Assert.Equal(1, count);
        }
    }

    /// <summary><c>mesh-fade.glsl</c>'s <c>meshShows</c> for a threshold in 65,536ths.</summary>
    private static bool Shows(uint shown, uint threshold) =>
        threshold >= (shown & 0xFFFF) && threshold < 65536 - (shown >> 16);

    [Fact]
    public void The_depth_only_shader_reads_nothing()
    {
        var spirv = _compiler.Compile("depth-only.frag");

        Assert.Empty(GpuLayout.DescriptorSets(spirv));
    }

    /// <summary>
    /// The recipes' bake reads the materials in the mesh set (set 0, as the meshes' layout) and writes its own set's
    /// image (set 1), with the material and side as push constants.
    /// </summary>
    [Fact]
    public void The_bake_reads_the_materials_and_writes_its_own_set()
    {
        var spirv = _compiler.Compile("material-bake.comp");

        Assert.Equal([0u, 1u], GpuLayout.DescriptorSets(spirv));
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshMaterial>(spirv, 0, 2));
    }

    /// <summary>The vertex shader reads a vertex as four words of the heap, in the order of the struct's fields.</summary>
    [Fact]
    public unsafe void A_vertex_is_four_words()
    {
        Assert.Equal(MeshVertex.Words * sizeof(uint), sizeof(MeshVertex));
        var vertex = new MeshVertex
        {
            PositionXY = 1,
            PositionZMaterial = 2,
            Normal = 3,
            Weights = 4,
        };
        var words = new ReadOnlySpan<uint>(&vertex, 4);
        Assert.Equal([1u, 2u, 3u, 4u], words.ToArray());
    }

    /// <summary>The materials and the tint map lie at multiples of 256 bytes, which any storage buffer alignment divides.</summary>
    [Fact]
    public void The_heap_places_its_materials_and_tint_map_on_granules()
    {
        Assert.Equal(0UL, MeshPass.MaterialsOffset % 256);
        Assert.Equal(0UL, MeshPass.TintMapOffset % 256);
        Assert.True(MeshPass.TintMapOffset >= MeshPass.MaterialsOffset + MeshPass.MaterialsBytes);
    }
}
