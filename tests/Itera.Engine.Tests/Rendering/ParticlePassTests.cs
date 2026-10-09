using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// The particle shaders against the structs the CPU writes and sizes: the pool, the recipes, the bursts, the counters
/// (whose first 32 bytes are the two indirect draws), the compute passes' push constants, and the draws' view.
/// </summary>
public sealed class ParticlePassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public ParticlePassTests()
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
    public void The_compute_passes_read_the_pool_recipes_bursts_counters_and_their_constants()
    {
        foreach (var name in (string[])["particle-spawn.comp", "particle-simulate.comp"])
        {
            var spirv = _compiler.Compile(name);
            Assert.Empty(GpuLayout.ArrayElementDifferences<GpuParticle>(spirv, 0, 0));
            Assert.Empty(GpuLayout.ArrayElementDifferences<ParticleCounters>(spirv, 0, 2));
            Assert.Empty(GpuLayout.ArrayElementDifferences<GpuParticleRecipe>(spirv, 0, 3));
            Assert.Empty(GpuLayout.PushConstantDifferences<ParticleConstants>(spirv));
        }

        Assert.Empty(
            GpuLayout.ArrayElementDifferences<GpuParticleBurst>(_compiler.Compile("particle-spawn.comp"), 0, 4)
        );
    }

    [Fact]
    public void The_draws_read_the_pool_and_recipes_with_the_view()
    {
        foreach (var name in (string[])["particle-cubes.vert", "particle-sprites.vert"])
        {
            var spirv = _compiler.Compile(name);
            Assert.Empty(GpuLayout.ArrayElementDifferences<GpuParticle>(spirv, 0, 0));
            Assert.Empty(GpuLayout.ArrayElementDifferences<GpuParticleRecipe>(spirv, 0, 3));
            Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(spirv));
        }

        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(_compiler.Compile("particle-cubes.frag")));
        Assert.NotEmpty(_compiler.Compile("particle-sprites.frag"));
    }

    /// <summary>
    /// The simulate pass reads the particles' set as set 0 and the mesh pass's as set 1 (the cascades' record, the shadow
    /// maps, the sky field), which ParticlePass binds there; the draws read only set 0.
    /// </summary>
    [Fact]
    public void The_simulate_pass_reads_the_mesh_set_as_set_1_and_the_draws_only_their_own()
    {
        var simulate = _compiler.Compile("particle-simulate.comp");
        var cubes = _compiler.Compile("particle-cubes.frag");

        Assert.Equal([0u, 1u], GpuLayout.DescriptorSets(simulate).Order());
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLighting>(simulate, 1, 4));
        Assert.Empty(GpuLayout.PushConstantDifferences<ParticleConstants>(simulate));
        Assert.Empty(GpuLayout.DescriptorSets(cubes));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(cubes));
        Assert.Equal([0u], GpuLayout.DescriptorSets(_compiler.Compile("particle-cubes.vert")));
        Assert.Equal([0u], GpuLayout.DescriptorSets(_compiler.Compile("particle-sprites.vert")));
    }

    [Fact]
    public unsafe void The_structs_have_the_sizes_the_buffers_are_made_for()
    {
        Assert.Equal(64, sizeof(GpuParticle));
        Assert.Equal(48, sizeof(ParticleCounters));
        // Two VkDrawIndirectCommands, cubes then sprites.
        Assert.Equal(36u, ParticleCounters.Start.CubeVertexCount);
        Assert.Equal(6u, ParticleCounters.Start.SpriteVertexCount);
        Assert.True(sizeof(ParticleConstants) <= 128);
    }
}
