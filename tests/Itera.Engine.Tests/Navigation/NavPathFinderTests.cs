using Itera.Engine.Navigation;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// <see cref="NavPathFinder"/> on small hand-made grids of 64 by 64 cells: straight over open ground at the same cost as a
/// flow field's route, round a wall through its gap at the field's cost where the field walks round too, never across a
/// corner, up a ramp rather than a step, down one rather than a drop past the size's drop, never through what blocks the
/// walker's size; a sealed goal or one past the
/// budget ending at the nearest cell reached; a start the size cannot stand in finding none, and the goal as start
/// reached at once; a short span taking the path's first cells; a second search allocating nothing.
/// </summary>
public sealed class NavPathFinderTests
{
    private const int Size = 64;

    private static readonly NavSize s_point = new("point", 200, 0, 45, 64);
    private static readonly NavSize s_wide = new("wide", 200, 60, 45, 64);
    private static readonly NavCostTable s_avoiding = new([10], 20_000, 3000);

    [Fact]
    public void Open_ground_leads_straight_to_the_goal_at_a_flow_fields_cost()
    {
        var grid = Grid();
        var finder = new NavPathFinder();
        var path = new int[256];

        var result = finder.Find(
            grid,
            s_point,
            s_avoiding,
            grid.Index(4, 32),
            grid.Index(40, 32),
            path,
            out var length
        );

        Assert.Equal(NavPathResult.Reached, result);
        Assert.Equal(36, length);
        Assert.All(path[..length], cell => Assert.Equal(32, grid.Row(cell)));
        Assert.Equal(grid.Index(40, 32), path[length - 1]);
        Assert.Equal(FieldCost(grid, s_point, grid.Index(4, 32), grid.Index(40, 32)), finder.Cost);
    }

    [Fact]
    public void A_wall_is_walked_round_through_its_gap_at_the_fields_cost()
    {
        var grid = Grid();
        Wall(grid, 20, 20, 0, Size - 1);
        Unblock(grid, 20, 20, 40, 44);
        var finder = new NavPathFinder();
        var path = new int[256];

        var result = finder.Find(
            grid,
            s_point,
            s_avoiding,
            grid.Index(5, 10),
            grid.Index(40, 10),
            path,
            out var length
        );

        Assert.Equal(NavPathResult.Reached, result);
        Assert.All(path[..length], cell => Assert.False(grid.IsBlocked(cell)));
        Assert.Contains(path[..length], cell => grid.Column(cell) == 20 && grid.Row(cell) is >= 40 and <= 44);
        Assert.Equal(FieldCost(grid, s_point, grid.Index(5, 10), grid.Index(40, 10)), finder.Cost);
    }

    [Fact]
    public void A_path_never_cuts_a_corner_nor_passes_where_its_size_cannot_stand()
    {
        var grid = Grid();
        for (var i = 10; i < 50; i++)
        {
            grid.Clear[grid.Index(i, i)] = 0;
        }

        var finder = new NavPathFinder();
        var path = new int[256];
        var start = grid.Index(10, 50);
        Assert.Equal(
            NavPathResult.Reached,
            finder.Find(grid, s_point, s_avoiding, start, grid.Index(50, 10), path, out var length)
        );
        var previous = start;
        foreach (var cell in path[..length])
        {
            var dx = grid.Column(cell) - grid.Column(previous);
            var dz = grid.Row(cell) - grid.Row(previous);
            Assert.True(Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1);
            if (dx != 0 && dz != 0)
            {
                Assert.False(grid.IsBlocked(grid.Index(grid.Column(previous) + dx, grid.Row(previous))));
                Assert.False(grid.IsBlocked(grid.Index(grid.Column(previous), grid.Row(previous) + dz)));
            }

            previous = cell;
        }

        // A one-cell gap in a wall lets the point through, not the wide size.
        var gap = Grid();
        Wall(gap, 30, 30, 0, Size - 1);
        Unblock(gap, 30, 30, 32, 32);
        Assert.Equal(
            NavPathResult.Reached,
            finder.Find(gap, s_point, s_avoiding, gap.Index(10, 32), gap.Index(50, 32), path, out _)
        );
        Assert.Equal(
            NavPathResult.Nearest,
            finder.Find(gap, s_wide, s_avoiding, gap.Index(10, 32), gap.Index(50, 32), path, out var stopped)
        );
        Assert.All(path[..stopped], cell => Assert.True(gap.Column(cell) < 30));
    }

    [Fact]
    public void A_step_is_passed_by_its_ramp()
    {
        var grid = Grid();
        // A platform 1 m up over x 30 to 63, reached by a ramp of 10 cm a cell at rows 50 to 52.
        for (var z = 0; z < Size; z++)
        {
            for (var x = 30; x < Size; x++)
            {
                grid.Floor[grid.Index(x, z)] = 100;
            }
        }

        for (var x = 20; x < 30; x++)
        {
            for (var z = 50; z <= 52; z++)
            {
                grid.Floor[grid.Index(x, z)] = (x - 19) * 10;
            }
        }

        var finder = new NavPathFinder();
        var path = new int[256];
        Assert.Equal(
            NavPathResult.Reached,
            finder.Find(grid, s_point, s_avoiding, grid.Index(10, 30), grid.Index(40, 30), path, out var length)
        );

        Assert.Contains(
            path[..length],
            cell => grid.Column(cell) is >= 20 and < 30 && grid.Row(cell) is >= 50 and <= 52
        );
    }

    /// <summary>
    /// Down off the platform 1 m up: a size with no drop limit drops straight off its edge, and one that drops no more than
    /// it climbs walks down its ramp.
    /// </summary>
    [Fact]
    public void A_drop_past_the_sizes_drop_is_passed_by_its_ramp()
    {
        var grid = Grid();
        for (var z = 0; z < Size; z++)
        {
            for (var x = 30; x < Size; x++)
            {
                grid.Floor[grid.Index(x, z)] = 100;
            }
        }

        for (var x = 20; x < 30; x++)
        {
            for (var z = 50; z <= 52; z++)
            {
                grid.Floor[grid.Index(x, z)] = (x - 19) * 10;
            }
        }

        var careful = new NavSize("careful", 200, 0, 45, 64, 45, 64);
        var finder = new NavPathFinder();
        var path = new int[256];
        Assert.Equal(
            NavPathResult.Reached,
            finder.Find(grid, s_point, s_avoiding, grid.Index(40, 30), grid.Index(10, 30), path, out var length)
        );
        Assert.All(path[..length], cell => Assert.Equal(30, grid.Row(cell)));

        Assert.Equal(
            NavPathResult.Reached,
            finder.Find(grid, careful, s_avoiding, grid.Index(40, 30), grid.Index(10, 30), path, out length)
        );
        Assert.Contains(
            path[..length],
            cell => grid.Column(cell) is >= 20 and < 30 && grid.Row(cell) is >= 50 and <= 52
        );
    }

    [Fact]
    public void A_sealed_goal_or_one_past_the_budget_ends_at_the_nearest_cell_reached()
    {
        var grid = Grid();
        Wall(grid, 38, 42, 28, 36);
        Unblock(grid, 40, 40, 32, 32);
        var finder = new NavPathFinder();
        var path = new int[256];

        var sealedOff = finder.Find(
            grid,
            s_point,
            s_avoiding,
            grid.Index(4, 32),
            grid.Index(40, 32),
            path,
            out var length
        );
        var last = path[length - 1];
        var budgeted = finder.Find(
            grid,
            s_point,
            s_avoiding,
            grid.Index(4, 4),
            grid.Index(60, 60),
            path,
            out var partial,
            budget: 20
        );

        Assert.Equal(NavPathResult.Nearest, sealedOff);
        Assert.Equal((37, 32), (grid.Column(last), grid.Row(last)));
        Assert.Equal(NavPathResult.Nearest, budgeted);
        Assert.True(finder.Expanded <= 20);
        Assert.True(partial > 0);
        var reached = path[partial - 1];
        Assert.True(grid.Column(reached) > 4 && grid.Row(reached) > 4);
    }

    [Fact]
    public void A_start_its_size_cannot_stand_in_finds_none_and_the_goal_as_start_is_reached_at_once()
    {
        var grid = Grid();
        grid.Clear[grid.Index(10, 10)] = 0;
        var finder = new NavPathFinder();
        var path = new int[4];

        var blocked = finder.Find(
            grid,
            s_point,
            s_avoiding,
            grid.Index(10, 10),
            grid.Index(20, 10),
            path,
            out var none
        );
        var outside = finder.Find(grid, s_point, s_avoiding, -1, grid.Index(20, 10), path, out _);
        var home = finder.Find(grid, s_point, s_avoiding, grid.Index(20, 10), grid.Index(20, 10), path, out var zero);
        var shortSpan = finder.Find(grid, s_point, s_avoiding, grid.Index(0, 0), grid.Index(9, 0), path, out var four);

        Assert.Equal((NavPathResult.None, 0), (blocked, none));
        Assert.Equal(NavPathResult.None, outside);
        Assert.Equal((NavPathResult.Reached, 0), (home, zero));
        Assert.Equal((NavPathResult.Reached, 4), (shortSpan, four));
        Assert.Equal([1, 2, 3, 4], path.Select(grid.Column));
    }

    /// <summary>
    /// One finder shared by walkers that search in turn (the navigation service's, which every brain uses) finds each its
    /// own path: searches of other grids, sizes, and goals between two of one walker's give that walker the same path and
    /// cost as a finder of its own.
    /// </summary>
    [Fact]
    public void Searches_in_turn_on_one_finder_find_what_finders_of_their_own_find()
    {
        var walls = Grid();
        Wall(walls, 30, 31, 0, 60);
        Unblock(walls, 30, 31, 40, 44);
        var open = Grid();
        var shared = new NavPathFinder();
        var path = new int[256];
        var other = new int[256];

        var first = shared.Find(
            walls,
            s_wide,
            s_avoiding,
            walls.Index(10, 10),
            walls.Index(50, 10),
            path,
            out var length
        );
        var cost = shared.Cost;
        shared.Find(open, s_point, s_avoiding, open.Index(40, 50), open.Index(2, 3), other, out _);
        var again = shared.Find(
            walls,
            s_wide,
            s_avoiding,
            walls.Index(10, 10),
            walls.Index(50, 10),
            other,
            out var length2
        );
        var own = new NavPathFinder();
        var alone = own.Find(
            walls,
            s_wide,
            s_avoiding,
            walls.Index(10, 10),
            walls.Index(50, 10),
            path,
            out var length3
        );

        Assert.Equal((NavPathResult.Reached, NavPathResult.Reached, NavPathResult.Reached), (first, again, alone));
        Assert.Equal((length, cost), (length2, shared.Cost));
        Assert.Equal(other[..length2], path[..length3]);
        Assert.Equal(cost, own.Cost);
    }

    [Fact]
    public void A_second_search_allocates_nothing()
    {
        var grid = Grid();
        Wall(grid, 20, 20, 0, 50);
        var finder = new NavPathFinder();
        var path = new int[256];
        finder.Find(grid, s_wide, s_avoiding, grid.Index(5, 10), grid.Index(40, 10), path, out _);

        var start = Itera.TestSupport.SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            finder.Find(grid, s_wide, s_avoiding, grid.Index(5, 10), grid.Index(40, 10), path, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>The cost of a flow field's route from <paramref name="start"/> to <paramref name="goal"/>.</summary>
    private static int FieldCost(NavGrid grid, NavSize size, int start, int goal)
    {
        var walkable = new byte[grid.Count];
        size.Derive(grid, walkable, 0, grid.Depth - 1);
        var builder = new FlowFieldBuilder();
        var place = new VoxelCoord(grid.CentreX(grid.Column(goal)), 0, grid.CentreZ(grid.Row(goal)));
        builder.Build(grid, new NavFieldSpec("test", size, s_avoiding, [place]), walkable, new FlowField(grid));
        return builder.Costs[start];
    }

    private static NavGrid Grid()
    {
        var grid = new NavGrid(0, 0, Size, Size);
        grid.Clear.AsSpan().Fill(byte.MaxValue);
        return grid;
    }

    private static void Wall(NavGrid grid, int x0, int x1, int z0, int z1)
    {
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                grid.Clear[grid.Index(x, z)] = 0;
            }
        }
    }

    private static void Unblock(NavGrid grid, int x0, int x1, int z0, int z1)
    {
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                grid.Clear[grid.Index(x, z)] = byte.MaxValue;
            }
        }
    }
}
