using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Navigation;
using Itera.Engine.Tests.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// <see cref="NavigationService"/> with a small fake source: the grid is filled and every field built on the service's
/// own thread and handed over at a fixed step, a session client builds nothing, a game without a source builds
/// nothing, and taking fields in allocates nothing. Over a flat voxel floor: a live edit changes the grid's cells and
/// the fields are built again; cells are read again at most so many a step; edits made before the start (a save's)
/// and edits where no voxels are loaded are stamped from their shapes, then read from the voxels once loaded.
/// </summary>
public sealed class NavigationServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void The_grid_and_fields_are_handed_over_at_a_fixed_step()
    {
        var source = new FakeSource();
        using var service = new NavigationService(
            source,
            () => true,
            NullLogger<NavigationService>.Instance,
            area: Area
        );
        service.Initialize();
        Assert.True(service.Enabled);
        Assert.False(service.Ready);
        Assert.Throws<InvalidOperationException>(() => service.Field(0));
        Assert.True(service.WaitUntilPublished(Timeout));
        Assert.False(service.Ready);

        service.AfterFixedStep();
        Assert.True(service.Ready);
        Assert.Equal(1, source.Fills);
        Assert.Equal(2, service.Fields.Count);
        var grid = service.Grid;
        Assert.Equal(FlowField.Goal, service.Field(0).Directions[grid.Index(30, 30)]);
        Assert.Equal(FlowField.Goal, service.Field(1).Directions[grid.Index(1, 1)]);
        // Every cell has a route, towards its field's goal.
        Assert.DoesNotContain(FlowField.None, service.Field(0).Directions);
        Assert.Equal(grid.Index(30, 30), service.Field(0).LookAhead(grid.Index(1, 1), 100));
        Assert.Equal(grid.Index(1, 1), service.Field(1).LookAhead(grid.Index(30, 30), 100));
        // The timings are the taken job's, copied when its fields were taken.
        Assert.True(service.Timings.FieldsMilliseconds > 0);
        Assert.True(service.Timings.FieldsMilliseconds >= service.Timings.LongestFieldMilliseconds);
    }

    [Fact]
    public void A_session_client_builds_nothing()
    {
        var source = new FakeSource();
        using var service = new NavigationService(
            source,
            () => false,
            NullLogger<NavigationService>.Instance,
            area: Area
        );
        service.Initialize();
        service.AfterFixedStep();
        Assert.False(service.Enabled);
        Assert.False(service.Ready);
        Assert.Equal(0, source.FieldSets);
    }

    [Fact]
    public void A_game_without_a_source_builds_nothing()
    {
        using var service = new NavigationService(null, () => true, NullLogger<NavigationService>.Instance, area: Area);
        service.Initialize();
        service.AfterFixedStep();
        Assert.False(service.Enabled);
        Assert.False(service.Ready);
        Assert.Empty(service.Fields);
    }

    [Fact]
    public void Disposing_stops_the_thread_and_may_be_repeated()
    {
        var service = new NavigationService(
            new FakeSource(),
            () => true,
            NullLogger<NavigationService>.Instance,
            area: Area
        );
        service.Initialize();
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public void Fixed_steps_allocate_nothing()
    {
        using var service = new NavigationService(
            new FakeSource(),
            () => true,
            NullLogger<NavigationService>.Instance,
            area: Area
        );
        service.Initialize();
        Assert.True(service.WaitUntilPublished(Timeout));
        service.AfterFixedStep();
        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            service.AfterFixedStep();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// The steps that read edits into the grid, read cells again from the voxels, start a job, and take its fields
    /// allocate nothing once a first edit has grown the queue (only idle steps were measured).
    /// </summary>
    [Fact]
    public void Fixed_steps_reading_edits_allocate_nothing()
    {
        using var world = FlatWorld(radiusMetres: 12);
        using var service = WithWorld(world);
        Dig(world, -500);
        Assert.True(service.Settle(Timeout));

        long allocated = 0;
        for (var round = 0; round < 3; round++)
        {
            // Small digs, applied at once: each is read at its next step.
            for (var i = 0; i < 4; i++)
            {
                Dig(world, -400 + 200 * i + 50 * round);
            }

            var before = SystemAllocation.StartMeasurement();
            var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
            for (var step = 0; Stopwatch.GetTimestamp() < deadline; step++)
            {
                service.AfterFixedStep();
                if (step > 2 && !service.Recomputing && service.PendingRescans == 0)
                {
                    break;
                }

                Thread.Sleep(1);
            }

            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.False(service.Recomputing);
        }

        Assert.Equal(0, allocated);

        static void Dig(VoxelWorld world, int x) =>
            world.Apply(VoxelEdit.Sphere(new VoxelCoord(x, Floor, 25), 30, VoxelEditOperation.Clear));
    }

    [Fact]
    public void A_live_edit_changes_the_grid_and_the_fields_are_built_again()
    {
        using var world = FlatWorld();
        using var service = WithWorld(world);
        var grid = service.Grid;
        var before = service.Field(0);
        // A wall of stone across column 32, rows 26 to 37, beside the goal at (30, 30).
        world.Apply(
            VoxelEdit.Box(
                new VoxelCoord(0, Floor, -300),
                new VoxelCoord(50, 100, 600),
                VoxelEditOperation.Set,
                BoxesGenerator.Stone
            )
        );
        // A large edit: the grid waits until it is in the voxels, not only in the log.
        service.AfterFixedStep();
        Assert.False(service.Recomputing);
        world.ApplyPendingEdits(int.MaxValue);
        service.AfterFixedStep();
        Assert.True(service.Recomputing);
        Assert.True(service.Settle(Timeout));
        Assert.True(grid.IsBlocked(grid.Index(32, 32)));
        Assert.False(grid.IsBlocked(grid.Index(30, 32)));
        Assert.NotSame(before, service.Field(0));
        Assert.Equal(0, service.UncheckedEdits);
        // The route from behind the wall no longer crosses it, nor a cell beside it, past its ends too (where a body of the
        // size would touch the wall: the cells a size can stand in are worked out again round the changed rows, not only
        // in them).
        // From behind its middle the route passes its near end, and from beside its far end, that end.
        var size = new NavSize("small", 200, 30, 45, 64);
        foreach (var start in new[] { grid.Index(50, 32), grid.Index(34, 36) })
        {
            var cell = start;
            for (var i = 0; i < 200 && service.Field(0).Directions[cell] != FlowField.Goal; i++)
            {
                cell = service.Field(0).Next(cell);
                Assert.False(grid.IsBlocked(cell));
                Assert.True(
                    size.IsWalkable(grid, cell),
                    $"The route steps beside the wall at ({grid.Column(cell)}, {grid.Row(cell)})."
                );
            }
        }
    }

    [Fact]
    public void Cells_are_read_again_at_most_so_many_a_step()
    {
        using var world = FlatWorld(radiusMetres: 12);
        using var service = WithWorld(world);
        // A dig 12 m across: 676 cells to read, with the cells round it.
        world.Apply(
            VoxelEdit.Box(
                new VoxelCoord(-600, Floor - 20, -600),
                new VoxelCoord(1200, 20, 1200),
                VoxelEditOperation.Clear
            )
        );
        world.ApplyPendingEdits(int.MaxValue);
        service.AfterFixedStep();
        var left = service.PendingRescans;
        Assert.Equal((0, 676 - NavigationService.RescansPerStep), (service.UncheckedEdits, left));
        service.AfterFixedStep();
        Assert.Equal(left - NavigationService.RescansPerStep, service.PendingRescans);
        Assert.True(service.Settle(Timeout));
        Assert.Equal(Floor - 20, service.Grid.Floor[service.Grid.Index(32, 32)]);
    }

    [Fact]
    public void Edits_without_loaded_voxels_are_stamped_then_read_once_loaded()
    {
        using var world = FlatWorld();
        // Before the start, as a save's: a dig 12 m from the loaded disc's middle, outside it.
        var saved = VoxelEdit.Sphere(new VoxelCoord(1225, Floor, 25), 30, VoxelEditOperation.Clear);
        world.Apply(in saved);
        using var service = WithWorld(world);
        var grid = service.Grid;
        var cell = grid.Index(56, 32);
        Assert.Equal(1, service.UncheckedEdits);
        Assert.Equal(Floor - 30, grid.Floor[cell]);

        // A live one, just as far the other way.
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(-1175, Floor, 25), 30, VoxelEditOperation.Clear));
        Assert.True(service.Settle(Timeout));
        Assert.Equal(2, service.UncheckedEdits);
        Assert.Equal(Floor - 30, grid.Floor[grid.Index(8, 32)]);

        world.SetCenters([new Vector3(12, 2, 0)]);
        TestWorlds.WaitUntilLoaded(world);
        Assert.True(service.Settle(Timeout));
        Assert.Equal(1, service.UncheckedEdits);
        Assert.Equal(Floor - 30, grid.Floor[cell]);
    }

    /// <summary>
    /// Stamped edits are checked in turn, so more than a step's checks whose voxels stay unloaded do not keep an older one
    /// from being read once its voxels load (only the last four were checked).
    /// </summary>
    /// <summary>
    /// A navigation thread that fails is reported and never ready, and the fixed steps and the dispose go on.
    /// </summary>
    [Fact]
    public void A_failed_fill_leaves_the_service_failed_and_never_ready()
    {
        using var world = FlatWorld();
        using var service = new NavigationService(
            new FakeSource { Throws = true },
            () => true,
            NullLogger<NavigationService>.Instance,
            world,
            new MoverWorldFixture.LeavesPass(),
            Area
        );
        service.Initialize();

        Assert.False(service.WaitUntilPublished(TimeSpan.FromSeconds(5)));
        service.AfterFixedStep();
        Assert.True(service.Failed);
        Assert.False(service.Ready);
        Assert.False(service.Settle(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void Every_stamped_edit_is_checked_in_turn()
    {
        using var world = FlatWorld();
        using var service = WithWorld(world);
        // The oldest east of the loaded disc, then 40 west of it.
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(1225, Floor, 25), 30, VoxelEditOperation.Clear));
        const int West = 40;
        Assert.True(West > NavigationService.StampChecksPerStep);
        for (var i = 0; i < West; i++)
        {
            world.Apply(
                VoxelEdit.Sphere(
                    new VoxelCoord(-1175 + 50 * (i % 5), Floor, -500 + 100 * (i / 5)),
                    30,
                    VoxelEditOperation.Clear
                )
            );
        }

        Assert.True(service.Settle(Timeout));
        Assert.Equal(West + 1, service.UncheckedEdits);

        // The east loads; the west stays unloaded.
        world.SetCenters([new Vector3(12, 2, 0)]);
        TestWorlds.WaitUntilLoaded(world);
        for (var step = 0; step < 4; step++)
        {
            service.AfterFixedStep();
        }

        Assert.Equal(West, service.UncheckedEdits);
    }

    /// <summary>
    /// An edit that covers no cell's centre column (a thin channel between them) is neither stamped nor read, since it
    /// cannot change a cell (many thin channels filled the stamped list).
    /// </summary>
    [Fact]
    public void An_edit_between_cell_centres_is_passed_over()
    {
        using var world = FlatWorld();
        using var service = WithWorld(world);
        // Cell centres lie at 25 cm past every 50 cm: a 3 cm channel along z = 0 m passes between two rows of them, far
        // and near.
        world.Apply(
            VoxelEdit.Capsule(
                new VoxelCoord(-1400, Floor - 5, 0),
                new VoxelCoord(-900, Floor - 5, 0),
                3,
                VoxelEditOperation.Clear
            )
        );
        world.Apply(
            VoxelEdit.Capsule(
                new VoxelCoord(-300, Floor - 5, 0),
                new VoxelCoord(300, Floor - 5, 0),
                3,
                VoxelEditOperation.Clear
            )
        );
        service.AfterFixedStep();

        Assert.Equal((0, 0), (service.UncheckedEdits, service.PendingRescans));
    }

    /// <summary>
    /// A queued cell whose voxels unload before its read waits with the stamped edits and is read once they load again
    /// (it was dropped).
    /// </summary>
    [Fact]
    public void A_cell_whose_voxels_unload_before_its_read_is_read_once_they_load()
    {
        using var world = FlatWorld(radiusMetres: 12);
        using var service = WithWorld(world);
        // A dig 12 m across: more cells than one step reads.
        world.Apply(
            VoxelEdit.Box(
                new VoxelCoord(-600, Floor - 20, -600),
                new VoxelCoord(1200, 20, 1200),
                VoxelEditOperation.Clear
            )
        );
        world.ApplyPendingEdits(int.MaxValue);
        service.AfterFixedStep();
        Assert.True(service.PendingRescans > 0);

        // Its voxels unload before the rest are read.
        world.SetCenters([new Vector3(300, 2, 300)]);
        TestWorlds.WaitUntilLoaded(world);
        for (var i = 0; i < 10_000 && world.ColumnsResident(-600, -600, 600, 600); i++)
        {
            world.Update(0);
        }

        Assert.False(world.ColumnsResident(0, 0, 0, 0));
        service.AfterFixedStep();
        while (service.PendingRescans > 0)
        {
            service.AfterFixedStep();
        }

        Assert.True(service.UncheckedEdits > 0);

        world.SetCenters([new Vector3(0, 2, 0)]);
        TestWorlds.WaitUntilLoaded(world);
        Assert.True(service.Settle(Timeout));
        var grid = service.Grid;
        Assert.Equal(0, service.UncheckedEdits);
        for (var row = 20; row <= 43; row++)
        {
            for (var column = 20; column <= 43; column++)
            {
                Assert.Equal(Floor - 20, grid.Floor[grid.Index(column, row)]);
            }
        }
    }

    [Fact]
    public void A_reset_fills_the_grid_again_from_the_layout_then_reads_the_edits_after_it()
    {
        using var world = FlatWorld();
        var source = new FakeSource();
        // Before the start, as a save's: a dig outside the loaded disc, stamped.
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(1225, Floor, 25), 30, VoxelEditOperation.Clear));
        using var service = WithWorld(world, source);
        var grid = service.Grid;
        // A live wall of stone across column 32, read from the voxels.
        world.Apply(
            VoxelEdit.Box(
                new VoxelCoord(0, Floor, -100),
                new VoxelCoord(50, 100, 200),
                VoxelEditOperation.Set,
                BoxesGenerator.Stone
            )
        );
        Assert.True(service.Settle(Timeout));
        var walled = grid.IsBlocked(grid.Index(32, 32));
        var dug = grid.Floor[grid.Index(56, 32)];

        world.Reset();
        // After the reset, outside the loaded disc: stamped once the grid is filled again.
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(-1175, Floor, 25), 30, VoxelEditOperation.Clear));
        service.AfterFixedStep();
        var refilling = service.Refilling;
        var stampedDuringRefill = service.UncheckedEdits;
        TestWorlds.WaitUntilLoaded(world);
        Assert.True(service.Settle(Timeout));

        Assert.True(walled);
        Assert.Equal(Floor - 30, dug);
        Assert.True(refilling);
        Assert.Equal(0, stampedDuringRefill);
        Assert.False(service.Refilling);
        Assert.Equal(2, source.Fills);
        Assert.False(grid.IsBlocked(grid.Index(32, 32)));
        Assert.Equal(Floor, grid.Floor[grid.Index(56, 32)]);
        Assert.Equal(1, service.UncheckedEdits);
        Assert.Equal(Floor - 30, grid.Floor[grid.Index(8, 32)]);
    }

    [Fact]
    public void A_save_with_a_reset_stamps_only_the_edits_after_it()
    {
        using var world = FlatWorld();
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(1225, Floor, 25), 30, VoxelEditOperation.Clear));
        world.Reset();
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(-1175, Floor, 25), 30, VoxelEditOperation.Clear));
        TestWorlds.WaitUntilLoaded(world);

        using var service = WithWorld(world);
        var grid = service.Grid;

        Assert.False(service.Refilling);
        Assert.Equal(1, service.UncheckedEdits);
        Assert.Equal(Floor, grid.Floor[grid.Index(56, 32)]);
        Assert.Equal(Floor - 30, grid.Floor[grid.Index(8, 32)]);
    }

    private const int Floor = 200;

    /// <summary>The 64 by 64 cells round the middle the tests' grid covers: their whole map, so it never moves.</summary>
    private static readonly NavArea Area = new(-1600, -1600, 64);

    private static VoxelWorld FlatWorld(double radiusMetres = 7) =>
        TestWorlds.Load(new BoxesGenerator(Floor), new Vector3(0, 2, 0), radiusMetres);

    private static NavigationService WithWorld(VoxelWorld world, FakeSource? source = null)
    {
        var service = new NavigationService(
            source ?? new FakeSource(),
            () => true,
            NullLogger<NavigationService>.Instance,
            world,
            new MoverWorldFixture.LeavesPass(),
            Area
        )
        {
            JobIntervalSeconds = 0,
        };
        service.Initialize();
        Assert.True(service.WaitUntilPublished(Timeout));
        service.AfterFixedStep();
        Assert.True(service.Ready);
        return service;
    }

    /// <summary>A 64 by 64 grid of open ground, with one field to (30, 30) and one to (1, 1).</summary>
    private sealed class FakeSource : INavGridSource
    {
        public int LowCentimetres => 25;

        private static readonly NavSize s_size = new("small", 200, 30, 45, 64);
        private static readonly NavCostTable s_costs = new([10], 400, 3000);

        public int FieldSets { get; private set; }

        public int Fills { get; private set; }

        /// <summary>The fields to the centres of cells (30, 30) and (1, 1) of <see cref="Area"/>.</summary>
        public IReadOnlyList<NavFieldSpec> CreateFields()
        {
            FieldSets++;
            return
            [
                new NavFieldSpec("east", s_size, s_costs, [new VoxelCoord(-75, 0, -75)]),
                new NavFieldSpec("west", s_size, s_costs, [new VoxelCoord(-1525, 0, -1525)]),
            ];
        }

        /// <summary>When set, filling the grid throws, as a bug in the game's builder would.</summary>
        public bool Throws { get; init; }

        public void Fill(NavGrid grid, int firstColumn, int firstRow, int lastColumn, int lastRow)
        {
            Fills++;
            if (Throws)
            {
                throw new InvalidOperationException("The test's fill fails.");
            }

            for (var row = firstRow; row <= lastRow; row++)
            {
                var start = grid.Index(firstColumn, row);
                grid.Floor.AsSpan(start, lastColumn - firstColumn + 1).Fill(Floor);
                grid.Clear.AsSpan(start, lastColumn - firstColumn + 1).Fill(byte.MaxValue);
            }
        }
    }
}
