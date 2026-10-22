using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// The particle shaders against the structs the CPU writes and sizes: the pool, the recipes, the bursts, the counters
/// (whose first 32 bytes are the two indirect draws), the compute passes' push constants, and the draws' view; and a
/// recipe's gravity, a multiple of the match's, with the match's up for resting.
/// </summary>
public sealed class ParticlePassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Where the draws find the look: the particles' set's binding 8 (ParticlePass compiles them so).</summary>
    private const string Look = "LOOK_BINDING 8";

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

    /// <summary>
    /// A recipe's gravity is its multiple of the match's (a negative one rises). Its up is against the match's gravity
    /// (straight up for a recipe that rises, or where there is no gravity).
    /// </summary>
    [Fact]
    public void A_recipes_gravity_is_its_multiple_of_the_matchs_and_rests_against_it()
    {
        var sparks = new Itera.Engine.Particles.ParticleRecipe { Gravity = 1 };
        var smoke = new Itera.Engine.Particles.ParticleRecipe { Gravity = -0.04f };

        var earth = GpuParticleRecipe.From(in sparks, Itera.Engine.Physics.PhysicsWorld.DefaultGravity);
        var rising = GpuParticleRecipe.From(in smoke, Itera.Engine.Physics.PhysicsWorld.DefaultGravity);
        var sideways = GpuParticleRecipe.From(in sparks, new Vector3(6, -8, 0));
        var none = GpuParticleRecipe.From(in sparks, Vector3.Zero);

        Assert.Equal(new Vector3(0, -9.81f, 0), earth.Gravity);
        Assert.Equal(Vector3.UnitY, earth.Up);
        // Smoke rises as before, and still rests on surfaces facing up.
        Assert.Equal(0.3924f, rising.Gravity.Y, 5);
        Assert.Equal(Vector3.UnitY, rising.Up);
        Assert.Equal(new Vector3(6, -8, 0), sideways.Gravity);
        Assert.True(Vector3.Distance(new Vector3(-0.6f, 0.8f, 0), sideways.Up) < 1e-6f);
        Assert.Equal(Vector3.Zero, none.Gravity);
        Assert.Equal(Vector3.UnitY, none.Up);
    }

    /// <summary>
    /// The pool's particles move with the match's origin: a frame that simulates in another origin takes the whole move
    /// since the last frame that simulated (frames between may not), and an emptied pool takes the frame's origin.
    /// </summary>
    [Fact]
    public void The_pool_moves_by_the_origins_move_since_it_last_simulated()
    {
        var pool = new ParticlePoolOrigin();
        pool.Emptied(default);

        Assert.Equal(Vector3.Zero, pool.Simulated(default));
        Assert.Equal(new Vector3(300, -20, -512), pool.Simulated(new VoxelCoord(30_000, -2_000, -51_200)));
        Assert.Equal(Vector3.Zero, pool.Simulated(new VoxelCoord(30_000, -2_000, -51_200)));
        // Two moves, the frame between them not simulating: the next frame takes both.
        Assert.Equal(
            new Vector3(-150_300, 40, 150_512),
            pool.Simulated(new VoxelCoord(-15_000_000, 2_000, 15_000_000))
        );
        pool.Emptied(new VoxelCoord(100, 0, 0));
        Assert.Equal(Vector3.Zero, pool.Simulated(new VoxelCoord(100, 0, 0)));
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
            var spirv = _compiler.Compile(name, [Look]);
            Assert.Empty(GpuLayout.ArrayElementDifferences<GpuParticle>(spirv, 0, 0));
            Assert.Empty(GpuLayout.ArrayElementDifferences<GpuParticleRecipe>(spirv, 0, 3));
            Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(spirv));
        }

        var cubes = _compiler.Compile("particle-cubes.frag", [Look]);
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(cubes));
        // The look, the mesh pass's buffer, at binding 8 of the particles' own set.
        Assert.Empty(GpuLayout.ArrayElementDifferences<LookConstants>(cubes, 0, 8));
        Assert.Empty(
            GpuLayout.ArrayElementDifferences<LookConstants>(_compiler.Compile("particle-sprites.vert", [Look]), 0, 8)
        );
        Assert.NotEmpty(_compiler.Compile("particle-sprites.frag", [Look]));
    }

    /// <summary>
    /// The simulate pass reads the particles' set as set 0 and the mesh pass's as set 1 (the cascades' record, the shadow
    /// maps, the sky field), which ParticlePass binds there; the draws read only set 0.
    /// </summary>
    [Fact]
    public void The_simulate_pass_reads_the_mesh_set_as_set_1_and_the_draws_only_their_own()
    {
        var simulate = _compiler.Compile("particle-simulate.comp");
        var cubes = _compiler.Compile("particle-cubes.frag", [Look]);

        Assert.Equal([0u, 1u], GpuLayout.DescriptorSets(simulate).Order());
        Assert.Empty(GpuLayout.ArrayElementDifferences<MeshLighting>(simulate, 1, 4));
        // The cubes' colour reads the look alone, in the particles' own set.
        Assert.Equal([0u], GpuLayout.DescriptorSets(cubes));
        Assert.Equal([0u], GpuLayout.DescriptorSets(_compiler.Compile("particle-cubes.vert", [Look])));
        Assert.Equal([0u], GpuLayout.DescriptorSets(_compiler.Compile("particle-sprites.vert", [Look])));
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
