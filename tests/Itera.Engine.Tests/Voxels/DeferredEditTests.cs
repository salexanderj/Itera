using System.Numerics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// Large edits applied over steps (<see cref="VoxelWorld.ApplyPendingEdits"/>): a large edit is logged at once but in the
/// voxels only once processed, each call applies the count of columns it is given, a small count spreads it over calls
/// and ends with the voxels an unlimited one gives, a
/// small edit applies at once and after the large ones waiting on its column, a lift over a waiting region lifts what the
/// log says is there, <see cref="VoxelWorld.AppliedEditCount"/> follows, and processing allocates nothing.
/// </summary>
public sealed class DeferredEditTests : IDisposable
{
    private const ulong Seed = 7;
    private static readonly VoxelCoord s_centre = new(0, 250, 0);
    private readonly List<VoxelWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    [Fact]
    public void A_large_edit_is_in_the_log_at_once_and_in_the_voxels_once_processed()
    {
        var world = Loaded();
        world.TryGetMaterial(s_centre, out var before);

        world.Apply(Large());

        Assert.Equal(1, world.EditCount);
        Assert.Equal(0, world.AppliedEditCount);
        Assert.True(world.PendingEditColumns > 0);
        world.TryGetMaterial(s_centre, out var waiting);
        Assert.Equal(before, waiting);

        world.ApplyPendingEdits(int.MaxValue);

        world.TryGetMaterial(s_centre, out var after);
        Assert.Equal(SlopeGenerator.Stone, after);
        Assert.Equal(1, world.AppliedEditCount);
        Assert.Equal(0, world.PendingEditColumns);
    }

    /// <summary>
    /// Each call applies the count of columns it is given, whatever their time, so every game applies a large edit at
    /// the same pace.
    /// </summary>
    [Fact]
    public void Each_call_applies_the_count_of_columns_it_is_given()
    {
        var one = Loaded();
        var four = Loaded();
        one.Apply(Large());
        four.Apply(Large());

        var callsAtOne = 0;
        while (one.PendingEditColumns > 0)
        {
            one.ApplyPendingEdits(1);
            callsAtOne++;
        }

        var callsAtFour = 0;
        while (four.PendingEditColumns > 0)
        {
            four.ApplyPendingEdits(4);
            callsAtFour++;
        }

        Assert.True(callsAtOne > 8, $"{callsAtOne} column(s)");
        Assert.Equal((callsAtOne + 3) / 4, callsAtFour);
    }

    [Fact]
    public void A_small_budget_spreads_the_work_and_ends_where_an_unlimited_one_does()
    {
        var spread = Loaded();
        var whole = Loaded();
        spread.Apply(Large());
        whole.Apply(Large());

        var calls = 0;
        while (spread.PendingEditColumns > 0)
        {
            spread.ApplyPendingEdits(1);
            calls++;
        }

        whole.ApplyPendingEdits(int.MaxValue);

        // One column a call at the smallest budget: as many calls as columns the sphere reaches.
        Assert.True(calls > 4, $"{calls} call(s)");
        for (var x = -180; x <= 180; x += 9)
        {
            for (var y = 60; y <= 440; y += 19)
            {
                for (var z = -180; z <= 180; z += 13)
                {
                    var voxel = new VoxelCoord(x, y, z);
                    spread.TryGetMaterial(voxel, out var a);
                    whole.TryGetMaterial(voxel, out var b);
                    Assert.Equal(b, a);
                }
            }
        }
    }

    [Fact]
    public void A_small_edit_applies_at_once_after_the_large_ones_waiting_on_its_column()
    {
        var world = Loaded();
        var far = new VoxelCoord(250, SlopeGenerator.Height(250, 250, Seed) - 5, 250);

        world.Apply(Large());
        // Inside the sphere: stone first, then this hole, in the log's order.
        world.Apply(VoxelEdit.Sphere(s_centre, 10, VoxelEditOperation.Clear));
        // Away from it: applied at once.
        world.Apply(VoxelEdit.Sphere(far, 10, VoxelEditOperation.Clear));

        world.TryGetMaterial(s_centre, out var hole);
        world.TryGetMaterial(s_centre with { Y = s_centre.Y + 50 }, out var stone);
        world.TryGetMaterial(far, out var dug);
        Assert.Equal(VoxelConstants.Air, hole);
        Assert.Equal(SlopeGenerator.Stone, stone);
        Assert.Equal(VoxelConstants.Air, dug);

        world.ApplyPendingEdits(int.MaxValue);
        world.TryGetMaterial(s_centre, out var still);
        Assert.Equal(VoxelConstants.Air, still);
        Assert.Equal(3, world.AppliedEditCount);
    }

    [Fact]
    public void A_lift_over_a_waiting_region_lifts_what_the_log_says_is_there()
    {
        var world = Loaded();
        world.Apply(Large());

        Assert.True(world.TryLift(VoxelEdit.Sphere(s_centre, 12, VoxelEditOperation.Lift), out var piece));

        var runs = new List<(byte Material, int Length)>();
        Assert.True(world.CollectPieceRuns(piece, runs));
        Assert.Contains(runs, run => run.Material == SlopeGenerator.Stone);
        Assert.DoesNotContain(runs, run => run.Material == SlopeGenerator.Dirt);
    }

    [Fact]
    public void A_column_unloaded_while_waiting_is_dropped_and_its_reload_holds_the_edit()
    {
        var world = Loaded();
        world.Apply(Large());

        // Everything unloads, then loads again around the same place.
        world.SetCenters([new Vector3(5000, 0, 5000)]);
        Settle(world);
        world.ApplyPendingEdits(int.MaxValue);
        Assert.Equal(0, world.PendingEditColumns);
        world.SetCenters([Vector3.Zero]);
        Settle(world);

        world.TryGetMaterial(s_centre, out var material);
        Assert.Equal(SlopeGenerator.Stone, material);
        Assert.Equal(1, world.AppliedEditCount);
    }

    /// <summary>
    /// The first column a large edit is applied to is a level-0 one next to the streaming centre: level 0 first, nearest
    /// first (no test checked the order).
    /// </summary>
    [Fact]
    public void The_first_column_done_is_at_level_0_next_to_the_centre()
    {
        var world = Loaded();
        world.TryGetMaterial(s_centre, 1, out var coarseBefore);
        world.Apply(Large());

        world.ApplyPendingEdits(1);

        var done = new List<(int X, int Z)>();
        for (var z = -2; z <= 1; z++)
        {
            for (var x = -2; x <= 1; x++)
            {
                // The column's voxel nearest the sphere's centre, inside the sphere.
                var voxel = new VoxelCoord(
                    Math.Clamp(0, x * 128, x * 128 + 127),
                    250,
                    Math.Clamp(0, z * 128, z * 128 + 127)
                );
                if (world.TryGetMaterial(voxel, out var material) && material == SlopeGenerator.Stone)
                {
                    done.Add((x, z));
                }
            }
        }

        var only = Assert.Single(done);
        Assert.InRange(only.X, -1, 0);
        Assert.InRange(only.Z, -1, 0);
        world.TryGetMaterial(s_centre, 1, out var coarse);
        Assert.Equal(coarseBefore, coarse);
    }

    [Fact]
    public void Processing_allocates_nothing()
    {
        var world = Loaded();
        world.Apply(Large());
        world.ApplyPendingEdits(int.MaxValue);
        world.Apply(VoxelEdit.Sphere(s_centre, 150, VoxelEditOperation.Clear));

        var start = SystemAllocation.StartMeasurement();
        while (world.PendingEditColumns > 0)
        {
            world.ApplyPendingEdits(1);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>A sphere of stone 3 m across in the middle of the loaded area.</summary>
    private static VoxelEdit Large() => VoxelEdit.Sphere(s_centre, 150, VoxelEditOperation.Set, SlopeGenerator.Stone);

    private VoxelWorld Loaded()
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = 3,
            Levels = 2,
            MemoryBudgetMegabytes = 64,
            WorkerThreads = 2,
            Seed = Seed,
        };
        var world = new VoxelWorld(settings, 0, new SlopeGenerator(), NullLogger<VoxelWorld>.Instance);
        world.Initialize();
        _worlds.Add(world);
        world.SetCenters([Vector3.Zero]);
        Settle(world);
        return world;
    }

    private static void Settle(VoxelWorld world)
    {
        var deadline = TestTime.Deadline(60);
        var quiet = 0;
        while (quiet < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "The voxel world did not settle.");
            world.Update(0);
            quiet = world.PendingColumns == 0 && world.PendingFrees == 0 ? quiet + 1 : 0;
            Thread.Sleep(1);
        }
    }
}
