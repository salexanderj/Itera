using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary><see cref="GroundLayers"/> and <see cref="SeedCache{T}"/>.</summary>
public sealed class GroundLayersTests
{
    private const byte Dirt = 2;
    private const byte Stone = 3;
    private const byte Clay = 6;
    private const byte Surface = 1;

    private static readonly GroundLayers s_layers = new(
        5,
        [new GroundLayer(40, Dirt), new GroundLayer(100, Clay)],
        Stone
    );

    [Fact]
    public void A_voxel_takes_the_material_of_the_band_its_depth_lies_in()
    {
        Assert.Equal(Surface, s_layers.MaterialAt(0, Surface, 5));
        Assert.Equal(Surface, s_layers.MaterialAt(4, Surface, 5));
        Assert.Equal(Dirt, s_layers.MaterialAt(5, Surface, 5));
        Assert.Equal(Dirt, s_layers.MaterialAt(39, Surface, 5));
        Assert.Equal(Clay, s_layers.MaterialAt(40, Surface, 5));
        Assert.Equal(Clay, s_layers.MaterialAt(99, Surface, 5));
        Assert.Equal(Stone, s_layers.MaterialAt(100, Surface, 5));
        Assert.Equal(Stone, s_layers.MaterialAt(100_000, Surface, 5));
    }

    [Fact]
    public void The_surface_layer_is_at_least_one_voxel_thick()
    {
        var unit = new GridLevel(VoxelGrid.Unit, 0);
        var coarse = new GridLevel(new VoxelGrid(10), 3);
        Assert.Equal(5, s_layers.SurfaceDepthAt(unit));
        Assert.Equal(80, s_layers.SurfaceDepthAt(coarse));
        Assert.Equal(100, s_layers.DeepDepthAt(unit));
        Assert.Equal(100, s_layers.DeepDepthAt(coarse));
        Assert.Equal(640, s_layers.DeepDepthAt(new GridLevel(new VoxelGrid(10), 6)));
    }

    [Fact]
    public void Layers_without_bands_are_surface_over_the_deep_material()
    {
        var layers = new GroundLayers(8, [], Stone);
        Assert.Equal(Surface, layers.MaterialAt(7, Surface, 8));
        Assert.Equal(Stone, layers.MaterialAt(8, Surface, 8));
        Assert.Equal(8, layers.DeepDepthAt(new GridLevel(VoxelGrid.Unit, 0)));
    }

    [Fact]
    public void Layers_that_make_no_sense_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroundLayers(0, [], Stone));
        Assert.Throws<ArgumentException>(() => new GroundLayers(5, [], VoxelConstants.Air));
        Assert.Throws<ArgumentException>(() => new GroundLayers(5, [new GroundLayer(50, VoxelConstants.Air)], Stone));
        Assert.Throws<ArgumentException>(() =>
            new GroundLayers(5, [new GroundLayer(50, Dirt), new GroundLayer(50, Clay)], Stone)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => new GroundLayers(5, [new GroundLayer(-3, Dirt)], Stone));
    }

    [Fact]
    public void A_seed_cache_builds_once_for_a_seed_and_again_for_another()
    {
        var builds = 0;
        var cache = new SeedCache<string>(seed =>
        {
            builds++;
            return $"layout {seed}";
        });

        Assert.Equal("layout 4", cache.Get(4));
        Assert.Same(cache.Get(4), cache.Get(4));
        Assert.Equal(1, builds);
        Assert.Equal("layout 9", cache.Get(9));
        Assert.Equal("layout 4", cache.Get(4));
        Assert.Equal(3, builds);
    }

    [Fact]
    public void Threads_asking_at_once_for_one_seed_share_one_build()
    {
        var builds = 0;
        var cache = new SeedCache<object>(seed =>
        {
            Interlocked.Increment(ref builds);
            Thread.Sleep(20);
            return new object();
        });

        var found = new object[8];
        Parallel.For(0, found.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => found[i] = cache.Get(7));

        Assert.Equal(1, builds);
        Assert.All(found, value => Assert.Same(found[0], value));
    }
}
