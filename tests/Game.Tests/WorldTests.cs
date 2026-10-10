using Game.WorldGeneration;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Game.Tests;

/// <summary>
/// The world: the same seed gives the same voxels in the same bounds (a pinned hash and grid: a change of either means
/// <see cref="GameGenerator.Version"/> must rise, or a save or a session of the old world would not match), the start
/// stands on the ground with the eye a person's height over it, and every benchmark view stands over the ground.
/// </summary>
public sealed class WorldTests
{
    [Fact]
    public void The_same_seed_gives_the_same_voxels()
    {
        var generator = new GameGenerator();
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        var hash = 14695981039346656037UL;
        foreach (var (x, z) in new[] { (0, 0), (3, -2), (-7, 5) })
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

        // Pinned with the generator's version: a change of the world (its voxels, or its size: the grid's base and bounds
        // in level-0 chunks) must come with a new version, and all change here.
        var grid = generator.Grid;
        Assert.Equal(
            (1, 5789805400214179090UL, (10, -40, 39, -4, 7)),
            (
                generator.Version,
                hash,
                (grid.BaseCentimetres, grid.MinChunkXZ, grid.MaxChunkXZ, grid.MinChunkY, grid.MaxChunkY)
            )
        );
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
