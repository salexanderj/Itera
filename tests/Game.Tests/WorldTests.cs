using Game.WorldGeneration;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Game.Tests;

/// <summary>
/// The world: the same seed gives the same voxels in the same bounds (a pinned hash and grid: a change of either means
/// <see cref="GameGenerator.Version"/> must rise, or a save or a session of the old world would not match), the middle
/// as version 1 made it, the broad relief past 1 km inside the grid, the start on the ground with the eye a person's
/// height over it, and every benchmark view over the ground.
/// </summary>
public sealed class WorldTests
{
    [Fact]
    public void The_same_seed_gives_the_same_voxels()
    {
        var generator = new GameGenerator();

        // Pinned with the generator's version: a change of the world (its voxels, or its size: the grid's base and bounds
        // in level-0 chunks) must come with a new version, and all change here. The last chunk lies in the broad relief
        // (2 km out at level 2).
        var grid = generator.Grid;
        Assert.Equal(
            (2, 14497145286141956781UL, (10, -313, 312, -4, 7)),
            (
                generator.Version,
                Hash(generator, [(0, 0), (3, -2), (-7, 5), (40, -30)]),
                (grid.BaseCentimetres, grid.MinChunkXZ, grid.MaxChunkXZ, grid.MinChunkY, grid.MaxChunkY)
            )
        );
    }

    /// <summary>
    /// Within 1 km of the middle the world is version 1's, voxel for voxel (the hash version 1 pinned): the broad relief
    /// starts past it, so the start and its views look as they did.
    /// </summary>
    [Fact]
    public void Near_the_middle_the_voxels_are_version_1s()
    {
        Assert.Equal(5789805400214179090UL, Hash(new GameGenerator(), [(0, 0), (3, -2), (-7, 5)]));
    }

    /// <summary>
    /// The broad relief: none within 1 km, rising smoothly to whole at 1.5 km, and up to 30 m up or down; the ground
    /// over the whole map, every 25 m, keeps more than 15 m of stone over the world's floor and 50 m under its top.
    /// </summary>
    [Fact]
    public void The_broad_relief_rises_past_1_km_and_the_ground_stays_inside_the_grid()
    {
        var grid = new GameGenerator().Grid;
        var floor = grid.MinVoxelY * grid.BaseCentimetres;
        var top = grid.EndVoxelY * grid.BaseCentimetres;
        var edge = grid.EndVoxelXZ * grid.BaseCentimetres;
        Assert.Equal((-5_120, 10_240, 400_640), (floor, top, edge));

        Assert.Equal(0, GameGenerator.Relief(99_990, 1_000, 1));
        Assert.Equal(0, GameGenerator.Relief(-70_000, 70_000, 1));
        double lowest = double.MaxValue,
            highest = double.MinValue,
            deepest = 0;
        for (var x = -400_000; x <= 400_000; x += 2_500)
        {
            for (var z = -400_000; z <= 400_000; z += 2_500)
            {
                var height = GameGenerator.SurfaceHeight(x, z, 1);
                lowest = Math.Min(lowest, height);
                highest = Math.Max(highest, height);
                deepest = Math.Max(deepest, Math.Abs(GameGenerator.Relief(x, z, 1)));
            }
        }

        Assert.InRange(deepest, 1_000, GameGenerator.ReliefCentimetres);
        Assert.True(lowest > floor + 1_500, $"The ground falls to {lowest} cm.");
        Assert.True(highest < top - 5_000, $"The ground rises to {highest} cm.");
    }

    /// <summary>
    /// A column's range and its layers stand for every layer as the generator describes it, round the middle and out in
    /// the broad relief, at every level: only the few layers holding the ground and the boulders are stored of the 12.
    /// </summary>
    [Fact]
    public void A_columns_range_stands_for_every_layer()
    {
        var generator = new GameGenerator();
        int stored = 0,
            total = 0;
        for (var level = 0; level <= VoxelConstants.MaxLevel; level++)
        {
            var reach = level <= 2 ? 3 : 1;
            foreach (var (cx, cz) in new[] { (0, 0), (200 >> level, -150 >> level) })
            {
                for (var z = cz - reach; z <= cz + reach; z++)
                {
                    for (var x = cx - reach; x <= cx + reach; x++)
                    {
                        var column = new ColumnKey(level, x, z);
                        if (!column.IsInBounds(generator.Grid))
                        {
                            continue;
                        }

                        var fills = new ChunkFill[column.LayerCount(generator.Grid)];
                        generator.DescribeColumn(column, 1, fills);
                        generator.DescribeRange(column, 1, out var first, out var last, out var below, out var above);
                        var window = new ChunkFill[Math.Max(last - first + 1, 0)];
                        if (window.Length > 0)
                        {
                            generator.DescribeLayers(column, 1, first, window);
                        }

                        for (var layer = 0; layer < fills.Length; layer++)
                        {
                            var stands =
                                layer < first ? below
                                : layer > last ? above
                                : window[layer - first];
                            Assert.True(fills[layer] == stands, $"{column}, layer {layer}: {fills[layer]}, {stands}");
                        }

                        stored += window.Length;
                        total += fills.Length;
                    }
                }
            }
        }

        Assert.True(stored * 2 < total, $"{stored} of {total} layers stored.");
    }

    /// <summary>FNV-1a over the voxels and densities of three layers of chunks at each place, levels 0 to 2, seed 1.</summary>
    private static ulong Hash(GameGenerator generator, (int X, int Z)[] places)
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        var hash = 14695981039346656037UL;
        foreach (var (x, z) in places)
        {
            for (var level = 0; level < 3; level++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    generator.Fill(new ChunkKey(level, x, y, z), 1, voxels, densities);
                    foreach (var value in voxels)
                    {
                        hash = (hash ^ value) * 1099511628211UL;
                    }

                    foreach (var value in densities)
                    {
                        hash = (hash ^ value) * 1099511628211UL;
                    }
                }
            }
        }

        return hash;
    }

    [Fact]
    public void The_start_stands_on_the_ground_and_the_views_over_it()
    {
        var start = StartPose.For(new WorldOptions(), 1, 1.56f);
        var ground = (float)GameGenerator.SurfaceHeight(0, 0, 1) / VoxelConstants.CentimetresPerMetre;

        Assert.Equal(ground + 1.56f, start.Eye.Y, 4);
        foreach (var view in BenchmarkViews.For(start, 1))
        {
            var below =
                (float)
                    GameGenerator.SurfaceHeight(
                        (int)(view.Position.X * VoxelConstants.CentimetresPerMetre),
                        (int)(view.Position.Z * VoxelConstants.CentimetresPerMetre),
                        1
                    ) / VoxelConstants.CentimetresPerMetre;
            Assert.True(
                view.Position.Y > below + 1,
                $"The view {view.Name} stands at {view.Position.Y} m, the ground {below} m."
            );
        }
    }

    [Fact]
    public void A_camera_pose_option_sets_the_start()
    {
        var start = StartPose.For(new WorldOptions { CameraPose = "1,20,3,90,-10" }, 1, 1.56f);

        Assert.Equal(new WorldPoint(1, 20, 3), start.Eye);
        Assert.Equal(MathF.PI / 2, start.Yaw, 4);
        Assert.Equal(-10 * MathF.PI / 180, start.Pitch, 4);
    }
}
