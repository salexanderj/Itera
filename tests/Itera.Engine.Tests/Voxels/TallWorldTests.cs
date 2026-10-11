using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Tests.Voxels.Generation;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// A world 320 km tall (25,000 layers of 12.8 m chunks on 10 cm voxels, ±160 km up and down): its columns store only the
/// layers round their ground, as a world 154 m tall does; an edit 100 km up widens its column, reads back, and leaves two
/// runs of layers that can hold a surface, not the 7,800 between; and rays find it and the ground under it.
/// </summary>
public sealed class TallWorldTests(ITestOutputHelper output)
{
    private static readonly VoxelGrid s_short = new(10, 8, -4, 7);
    private static readonly VoxelGrid s_tall = new(10, 8, -12_500, 12_499);

    [Fact]
    public void A_columns_stored_layers_are_the_same_whatever_the_worlds_height()
    {
        Assert.Equal((12, 25_000), (s_short.MaxLayers, s_tall.MaxLayers));
        using var low = TestWorlds.Load(new HillsGenerator(s_short), new Vector3(0, 2, 0), radiusMetres: 30, levels: 2);
        var watch = Stopwatch.StartNew();
        using var high = TestWorlds.Load(new HillsGenerator(s_tall), new Vector3(0, 2, 0), radiusMetres: 30, levels: 2);
        var loaded = watch.Elapsed;

        var (lowColumns, lowStored) = Stored(low);
        var (highColumns, highStored) = Stored(high);

        output.WriteLine(
            $"{highColumns} columns store {highStored} layers in a world of 25,000 layers ({lowStored} in one of 12); loaded in {loaded.TotalSeconds:F1} s (Debug)"
        );
        Assert.Equal(lowColumns, highColumns);
        Assert.Equal(lowStored, highStored);
        Assert.True(highStored <= 4 * highColumns, $"{highStored} layers stored by {highColumns} columns.");
    }

    [Fact]
    public void An_edit_100_km_up_widens_its_column_reads_back_and_rays_find_it_and_the_ground()
    {
        using var world = TestWorlds.Load(
            new HillsGenerator(s_tall, features: false),
            new Vector3(0, 2, 0),
            radiusMetres: 13
        );
        var key = new ColumnKey(0, 0, 0);
        var ball = new VoxelCoord(64, 10_000_000, 64);

        world.Apply(VoxelEdit.Sphere(ball, 50, VoxelEditOperation.Set, HillsGenerator.Stone));

        Assert.True(world.TryGetMaterial(ball, out var material));
        Assert.Equal(HillsGenerator.Stone, material);
        Assert.True(world.TryGetStoredLayers(key, out var first, out var end));
        var layer = (1_000_000 >> VoxelConstants.ChunkShift) - s_tall.MinChunkY;
        Assert.InRange(layer, first, end - 1);
        var runs = new List<(int First, int End)>();
        Assert.True(world.CollectSurfaceLayers(key, runs));
        Assert.Equal(2, runs.Count);
        Assert.InRange(layer, runs[1].First, runs[1].End - 1);

        // From 100 m over the ball, down onto its top; then from under it, 100 km down to the ground.
        var watch = Stopwatch.StartNew();
        Assert.True(world.Raycast(new Vector3(0.64f, 100_100, 0.64f), -Vector3.UnitY, 200, out var top));
        var onBall = watch.Elapsed;
        watch.Restart();
        Assert.True(world.Raycast(new Vector3(0.64f, 99_990, 0.64f), -Vector3.UnitY, 100_000, out var under));
        var down = watch.Elapsed;

        output.WriteLine(
            $"{end - first} layers stored after the edit, {runs.Count} runs that can hold a surface; rays {onBall.TotalMilliseconds:F2} ms and, 100 km down, {down.TotalMilliseconds:F1} ms (Debug)"
        );
        Assert.InRange(top.Voxel.Y, 10_000_000, 10_000_050);
        var ground = HillsGenerator.Height(64, 64, 17);
        Assert.InRange(under.Voxel.Y, ground - 20, ground + 10);
    }

    /// <summary>The resident columns and the layers they store, together.</summary>
    private static (int Columns, int Stored) Stored(VoxelWorld world)
    {
        var keys = new List<ColumnKey>();
        world.CollectResidentColumns(keys);
        var stored = 0;
        foreach (var key in keys)
        {
            Assert.True(world.TryGetStoredLayers(key, out var first, out var end));
            stored += end - first;
        }

        return (keys.Count, stored);
    }
}
