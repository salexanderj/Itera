using Itera.Engine.Voxels;

namespace Itera.TestSupport;

/// <summary>Loads a test's voxel world: one helper for the loop that was copied into many test files.</summary>
internal static class VoxelLoading
{
    /// <summary>
    /// Updates <paramref name="world"/> until it has resident columns and none waits to load, sleeping 1 ms between
    /// updates. Fails after <paramref name="seconds"/>.
    /// </summary>
    public static void WaitUntilLoaded(VoxelWorld world, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        world.Update(0);
        while (world.PendingColumns > 0 || world.ResidentColumns == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The world did not load.");
            world.Update(0);
            Thread.Sleep(1);
        }
    }
}
