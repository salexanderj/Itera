using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>Small voxel worlds for tests, loaded at level 0 only unless asked for more levels.</summary>
internal static class TestWorlds
{
    /// <summary>
    /// A world of <paramref name="generator"/> with every level-0 column within <paramref name="radiusMetres"/> of
    /// <paramref name="centreMetres"/> resident, and with <paramref name="levels"/> above 1 every coarser level's
    /// columns within its radius (twice the one below), local to <paramref name="origin"/> (the map's middle if null).
    /// Fails after 60 s.
    /// </summary>
    public static VoxelWorld Load(
        IChunkGenerator generator,
        Vector3 centreMetres,
        double radiusMetres = 3,
        ulong seed = 17,
        int levels = 1,
        WorldOrigin? origin = null
    )
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = radiusMetres,
            Levels = levels,
            MemoryBudgetMegabytes = 256,
            WorkerThreads = 2,
            Seed = seed,
        };
        var world = new VoxelWorld(settings, 0, generator, NullLogger<VoxelWorld>.Instance, origin: origin);
        world.Initialize();
        world.SetCenters([centreMetres]);
        WaitUntilLoaded(world);
        return world;
    }

    /// <summary>Updates the world until nothing is waiting to load. Fails after 60 s.</summary>
    public static void WaitUntilLoaded(VoxelWorld world)
    {
        VoxelLoading.WaitUntilLoaded(world);
    }
}
