using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Navigation;
using Itera.Engine.Tests.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// <see cref="NavigationService"/>'s window on a map wider than it: the window moves by whole tiles when its focus has
/// gone a quarter of it, and only then; a goal past it stands in at its edge; a moved window holds what a window placed
/// there at the start holds, edits included (read from the voxels where they are loaded, stamped where not); an edit past
/// the window changes no cell; a reset during a move fills the window again; a walker keeps its route across moves, and
/// one walks 2 km on a 4 km map to its goal; moves allocate nothing on the main thread.
/// </summary>
[RunsLong]
public sealed class NavigationWindowTests(ITestOutputHelper output)
{
    private const int Floor = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>4 km across: 8,000 cells each way round the middle.</summary>
    private static readonly NavArea FourKilometres = new(-200_000, -200_000, 8_000);

    [Fact]
    public void The_window_moves_by_whole_tiles_once_its_focus_has_gone_a_quarter_of_it()
    {
        using var service = Started(new FlatSource([new VoxelCoord(0, 0, 0)]), FourKilometres, windowCells: 256);
        var grid = service.Grid;
        // Round the middle: the window's corner a whole number of tiles from the area's.
        Assert.Equal((256, 256), (grid.Width, grid.Depth));
        Assert.Equal(0, (grid.OriginX - FourKilometres.MinX) % (NavigationService.TileCells * NavGrid.CellCentimetres));
        var corner = (grid.OriginX, grid.OriginZ);

        // 30 m along x puts its corner a tile (32 m) on: not more than a quarter of the window, so it stays.
        service.Follow(new WorldPoint(30, 0, 0));
        Assert.True(service.Settle(Timeout));
        Assert.Equal((0, corner), (service.Moves, (grid.OriginX, grid.OriginZ)));

        service.Follow(new WorldPoint(100, 0, -70));
        Assert.True(service.Settle(Timeout));
        Assert.Equal(1, service.Moves);
        // The focus in the window's middle, give or take a tile.
        var middleX = grid.OriginX + 128 * NavGrid.CellCentimetres;
        var middleZ = grid.OriginZ + 128 * NavGrid.CellCentimetres;
        Assert.InRange(10_000 - middleX, 0, NavigationService.TileCells * NavGrid.CellCentimetres);
        Assert.InRange(-7_000 - middleZ, 0, NavigationService.TileCells * NavGrid.CellCentimetres);
        // The field's goal, the middle of the map, now past the window's low x and high z edges: its corner cell.
        var field = service.Field(0);
        Assert.Equal((grid.OriginX, grid.OriginZ), (field.OriginX, field.OriginZ));
        Assert.Equal(FlowField.Goal, field.Directions[grid.Index(0, grid.Depth - 1)]);
        Assert.Equal(1, field.Directions.Count(direction => direction == FlowField.Goal));
    }

    [Fact]
    public void At_the_maps_edge_the_window_stays_inside_the_map()
    {
        using var service = Started(new FlatSource([new VoxelCoord(0, 0, 0)]), FourKilometres, windowCells: 256);

        service.Follow(new WorldPoint(1_999, 0, -1_999));
        Assert.True(service.Settle(Timeout));

        var grid = service.Grid;
        Assert.Equal(FourKilometres.MinX + (8_000 - 256) * NavGrid.CellCentimetres, grid.OriginX);
        Assert.Equal(FourKilometres.MinZ, grid.OriginZ);
        var moves = service.Moves;
        service.Follow(new WorldPoint(1_990, 0, -1_990));
        Assert.True(service.Settle(Timeout));
        Assert.Equal(moves, service.Moves);
    }

    /// <summary>
    /// Digs inside the window, outside it, and past the loaded voxels; then the window moves half its width, a large dig
    /// read in the step the move starts still waiting for its cells to be read. Its cells are those of a window placed
    /// there at the start with the same edits (those both windows hold copied to their new places, the strips filled
    /// and stamped, the waiting cells moved with it), and so are its fields.
    /// </summary>
    [Fact]
    public void A_moved_window_holds_what_one_placed_there_at_the_start_holds()
    {
        using var world = FlatWorld();
        var area = NavArea.Of(world.Grid);
        using var moved = Started(new FlatSource([new VoxelCoord(4_000, 0, 0)]), area, 128, world);
        Assert.Equal(-5_250, moved.Grid.OriginX);
        // In the first window (-52.5 to 11.5 m) only, in both windows, past the first, and past the loaded voxels (40 m:
        // stamped).
        Dig(world, -3_000);
        Dig(world, 500);
        Dig(world, 2_500);
        Dig(world, 3_300);
        Dig(world, 4_200);
        Assert.True(moved.Settle(Timeout));

        // Six digs under both windows, applied at once: more cells than a step reads, so some still wait when the window
        // moves.
        foreach (var (x, z) in new[] { (300, 25), (600, 25), (900, 25), (300, 400), (600, 400), (900, 400) })
        {
            world.Apply(VoxelEdit.Sphere(new VoxelCoord(x, Floor, z), 60, VoxelEditOperation.Clear));
        }

        moved.Follow(new WorldPoint(30, 2, 0));
        moved.AfterFixedStep();
        Assert.True(moved.Moving);
        Assert.True(moved.PendingRescans > 0);
        Assert.True(moved.Settle(Timeout));
        Assert.Equal((1, -2_050), (moved.Moves, moved.Grid.OriginX));

        using var fresh = new NavigationService(
            new FlatSource([new VoxelCoord(4_000, 0, 0)]),
            () => true,
            NullLogger<NavigationService>.Instance,
            world,
            new MoverWorldFixture.LeavesPass(),
            area,
            128
        )
        {
            JobIntervalSeconds = 0,
        };
        fresh.Follow(new WorldPoint(30, 2, 0));
        fresh.Initialize();
        Assert.True(fresh.Settle(Timeout));

        var (a, b) = (moved.Grid, fresh.Grid);
        Assert.Equal((b.OriginX, b.OriginZ), (a.OriginX, a.OriginZ));
        Assert.Equal(b.Floor, a.Floor);
        Assert.Equal(b.Clear, a.Clear);
        Assert.Equal(fresh.Field(0).Directions, moved.Field(0).Directions);
        // The digs at 3, 5, 6, 9, 25, 33, and 42 m are in it (-20.5 to 43.5 m), lowered.
        foreach (var x in (int[])[325, 525, 625, 925, 2_525, 3_325, 4_225])
        {
            Assert.True(a.Floor[a.NearestCell(x, 25)] < Floor, $"The dig at {x} cm is not in the window.");
        }
    }

    [Fact]
    public void An_edit_past_the_window_changes_no_cell_and_waits_for_nothing()
    {
        using var world = FlatWorld();
        using var service = Started(new FlatSource([new VoxelCoord(0, 0, 0)]), NavArea.Of(world.Grid), 64, world);
        var before = service.Grid.Floor.ToArray();

        Dig(world, 4_000);
        Assert.True(service.Settle(Timeout));

        Assert.Equal(before, service.Grid.Floor);
        Assert.Equal((0, 0), (service.PendingRescans, service.UncheckedEdits));
    }

    [Fact]
    public void A_reset_during_a_move_fills_the_window_again()
    {
        using var world = FlatWorld();
        using var service = Started(new FlatSource([new VoxelCoord(0, 0, 0)]), NavArea.Of(world.Grid), 64, world);
        Dig(world, 2_500);
        Assert.True(service.Settle(Timeout));

        service.Follow(new WorldPoint(30, 2, 0));
        service.AfterFixedStep();
        Assert.True(service.Moving);
        world.Reset();
        Assert.True(service.Settle(Timeout));

        Assert.Equal(1, service.Moves);
        Assert.False(service.Refilling);
        Assert.All(service.Grid.Floor, floor => Assert.Equal(Floor, floor));
    }

    /// <summary>
    /// A walker 2 km from its goal at the map's edge, on a 4 km map of open ground, follows the field one cell a step,
    /// the window following it to the edge, and reaches the goal: each step it reads the field it has, in that field's
    /// window, so a move never sends it back.
    /// </summary>
    [Fact]
    public void A_walker_follows_its_field_2_km_across_the_map()
    {
        // 25 cm inside the map's east edge.
        var goal = new VoxelCoord(199_975, 0, 25);
        // The default window, 512 m: the log gives a move's times at its size.
        using var service = Started(new FlatSource([goal]), FourKilometres, NavigationSettings.DefaultWindowCells);
        var at = new VoxelCoord(25, 0, 25);
        var watch = Stopwatch.StartNew();
        var steps = 0;
        var waits = 0;
        while (at.X != goal.X && watch.Elapsed < Timeout)
        {
            var field = service.Field(0);
            Assert.True(field.TryCell(at.X, at.Z, out var cell), $"The walker at {at} is outside its field's window.");
            var next = field.Next(cell);
            if (next == cell)
            {
                // At the window's edge, where the goal stands in: it waits for the window to move.
                Assert.True(field.Directions[cell] == FlowField.Goal);
                waits++;
                Thread.Sleep(1);
            }
            else
            {
                var x = field.CentreX(field.Column(next));
                Assert.True(x >= at.X, $"Step {steps} went back from {at.X} to {x}.");
                at = new VoxelCoord(x, 0, field.CentreZ(field.Row(next)));
                steps++;
            }

            service.Follow(WorldPoint.FromCentimetres(at));
            service.AfterFixedStep();
        }

        output.WriteLine(
            $"{steps} steps, {waits} waits at the window's edge, {service.Moves} moves in {watch.Elapsed.TotalSeconds:F1} s; the last move's strips filled in {service.Timings.FillMilliseconds:F1} ms and its field built in {service.Timings.FieldsMilliseconds:F0} ms"
        );
        Assert.Equal(goal, at);
        Assert.Equal(3_999, steps);
        Assert.True(service.Moves >= 5, $"The window moved {service.Moves} times.");
    }

    [Fact]
    public void Moves_allocate_nothing_on_the_main_thread()
    {
        using var world = FlatWorld();
        using var service = Started(new FlatSource([new VoxelCoord(0, 0, 0)]), NavArea.Of(world.Grid), 64, world);
        Dig(world, 2_500);
        Dig(world, -2_500);
        Assert.True(service.Settle(Timeout));
        for (var round = 0; round < 4; round++)
        {
            MoveAndBack(service);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var round = 0; round < 4; round++)
        {
            MoveAndBack(service);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(16, service.Moves);

        static void MoveAndBack(NavigationService service)
        {
            Steps(service, new WorldPoint(30, 2, 0));
            Steps(service, new WorldPoint(-30, 2, 0));
        }

        // Fixed steps until the move and the reads after it are done (Settle's own clock would allocate).
        static void Steps(NavigationService service, WorldPoint focus)
        {
            service.Follow(in focus);
            var deadline = Stopwatch.GetTimestamp() + 60 * Stopwatch.Frequency;
            for (var step = 0; Stopwatch.GetTimestamp() < deadline; step++)
            {
                service.AfterFixedStep();
                if (step > 2 && !service.Moving && !service.Recomputing && service.PendingRescans == 0)
                {
                    return;
                }

                Thread.Sleep(1);
            }

            throw new TimeoutException("The window did not settle.");
        }
    }

    private static void Dig(VoxelWorld world, int x) =>
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(x, Floor, 25), 30, VoxelEditOperation.Clear));

    /// <summary>
    /// A closed box stays closed as the window moves past it (with a world, its cells read from the voxels); and, with no
    /// world, one opened while the window moves opens once the move is taken, its cells filled from the source then.
    /// </summary>
    [Fact]
    public void A_closed_box_stays_closed_through_a_move_and_one_opened_during_a_move_opens_after_it()
    {
        using var world = FlatWorld();
        using var service = Started(new FlatSource([new VoxelCoord(0, 0, 0)]), FourKilometres, windowCells: 256, world);
        var closure = service.Close(1_000, -300, 1_100, 300);
        Assert.True(service.Settle(Timeout));
        service.Follow(new WorldPoint(40, 2, 0));
        Assert.True(service.Settle(Timeout));
        var grid = service.Grid;
        Assert.Equal(1, service.Moves);
        Assert.True(grid.IsBlocked(Cell(grid, 1_025, 0)));
        Assert.False(grid.IsBlocked(Cell(grid, 1_125, 0)));
        service.Open(closure);
        Assert.True(service.Settle(Timeout));
        Assert.False(grid.IsBlocked(Cell(grid, 1_025, 0)));

        using var bare = Started(new FlatSource([new VoxelCoord(0, 0, 0)]), FourKilometres, windowCells: 256);
        var box = bare.Close(1_000, -300, 1_100, 300);
        Assert.True(bare.Settle(Timeout));
        bare.Follow(new WorldPoint(40, 2, 0));
        bare.AfterFixedStep();
        Assert.True(bare.Moving);
        bare.Open(box);
        Assert.True(bare.Settle(Timeout));
        Assert.Equal(1, bare.Moves);
        Assert.False(bare.Grid.IsBlocked(Cell(bare.Grid, 1_025, 0)));
        Assert.Equal(0, bare.Closed);
    }

    private static int Cell(NavGrid grid, int x, int z)
    {
        Assert.True(grid.TryCell(x, z, out var cell));
        return cell;
    }

    /// <summary>A flat floor 2 m up on 1 cm voxels, its map ±500 m, loaded 40 m round the middle.</summary>
    private static VoxelWorld FlatWorld() =>
        TestWorlds.Load(new BoxesGenerator(Floor), new Vector3(0, 2, 0), radiusMetres: 40);

    private static NavigationService Started(FlatSource source, NavArea area, int windowCells, VoxelWorld? world = null)
    {
        var service = new NavigationService(
            source,
            () => true,
            NullLogger<NavigationService>.Instance,
            world,
            world is null ? null : new MoverWorldFixture.LeavesPass(),
            area,
            windowCells
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

    /// <summary>Open ground at <see cref="Floor"/> everywhere, judged from it, with one field to <paramref name="goals"/>.</summary>
    private sealed class FlatSource(VoxelCoord[] goals) : INavGridSource
    {
        private static readonly NavSize s_size = new("small", 200, 30, 45, 64);
        private static readonly NavCostTable s_costs = new([10], 400, 3000);

        public int LowCentimetres => 25;

        public IReadOnlyList<NavFieldSpec> CreateFields() => [new NavFieldSpec("goal", s_size, s_costs, goals)];

        public void Fill(NavGrid grid, int firstColumn, int firstRow, int lastColumn, int lastRow)
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                var start = grid.Index(firstColumn, row);
                var length = lastColumn - firstColumn + 1;
                grid.Floor.AsSpan(start, length).Fill(Floor);
                grid.Clear.AsSpan(start, length).Fill(byte.MaxValue);
                grid.Class.AsSpan(start, length).Clear();
            }
        }

        public int GroundAt(int x, int z) => Floor;
    }
}
