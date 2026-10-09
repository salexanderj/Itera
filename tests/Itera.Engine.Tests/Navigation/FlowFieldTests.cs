using Itera.Engine.Navigation;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// <see cref="FlowFieldBuilder"/> on small hand-made grids of 64 by 64 cells: routes to the goal, round walls through
/// gaps, never across a corner, through a corridor only for the size that fits, up a ramp rather than a step, and the
/// cost of breaking through against walking round (a profile with a low break-through cost breaks a thin or 4 m wall
/// rather than take a long detour, but walks round a very thick one; a profile with a high one walks round every wall
/// and crosses only a fully sealed goal).
/// </summary>
public sealed class FlowFieldTests
{
    private const int Size = 64;
    private const byte OpenGround = 0;
    private const byte Road = 1;

    private static readonly NavSize s_small = new("small", 200, 30, 45, 64);
    private static readonly NavSize s_large = new("large", 300, 45, 45, 64);
    private static readonly NavCostTable s_breaking = new([200, 10], 400, 3000);
    private static readonly NavCostTable s_avoiding = new([200, 10], 20_000, 3000);

    [Fact]
    public void Sizes_cover_the_cells_their_radius_reaches()
    {
        Assert.Equal(5, s_small.FootprintCells);
        Assert.Equal(9, s_large.FootprintCells);
    }

    [Fact]
    public void Open_ground_leads_straight_to_the_goal()
    {
        var grid = Grid(Road);
        var route = Build(grid, s_small, s_breaking, [grid.Index(40, 32)]).Follow(grid.Index(4, 32));
        Assert.True(route.Arrived);
        Assert.Equal(36, route.Cells.Count);
        Assert.All(route.Cells, cell => Assert.Equal(32, grid.Row(cell)));
    }

    [Fact]
    public void A_wall_is_passed_through_its_gap()
    {
        var grid = Grid(Road);
        Wall(grid, 20, 20, 0, Size - 1);
        Unblock(grid, 20, 20, 40, 44);
        var result = Build(grid, s_small, s_breaking, [grid.Index(40, 10)]);
        var route = result.Follow(grid.Index(5, 10));
        Assert.True(route.Arrived);
        Assert.Equal(0, route.Blocked);
        Assert.Contains(route.Cells, cell => grid.Column(cell) == 20 && grid.Row(cell) is >= 40 and <= 44);
    }

    [Fact]
    public void Routes_never_cut_a_corner()
    {
        var grid = Grid(Road);
        // A staircase of single blocked cells that a diagonal line would slip between.
        for (var i = 10; i < 50; i++)
        {
            grid.Clear[grid.Index(i, i)] = 0;
        }

        var result = Build(grid, NavSize0(), s_breaking, [grid.Index(50, 10)]);
        var route = result.Follow(grid.Index(10, 50));
        Assert.True(route.Arrived);
        var previous = grid.Index(10, 50);
        foreach (var cell in route.Cells)
        {
            var dx = grid.Column(cell) - grid.Column(previous);
            var dz = grid.Row(cell) - grid.Row(previous);
            if (dx != 0 && dz != 0)
            {
                Assert.False(grid.IsBlocked(grid.Index(grid.Column(previous) + dx, grid.Row(previous))));
                Assert.False(grid.IsBlocked(grid.Index(grid.Column(previous), grid.Row(previous) + dz)));
            }

            previous = cell;
        }
    }

    [Fact]
    public void A_gap_three_cells_wide_passes_both_sizes_and_two_cells_neither()
    {
        foreach (var (wide, open) in new[] { (3, true), (2, false) })
        {
            var grid = Grid(Road);
            Wall(grid, 30, 31, 0, Size - 1);
            Unblock(grid, 30, 31, 30, 30 + wide - 1);
            foreach (var size in new[] { s_small, s_large })
            {
                var route = Build(grid, size, s_breaking, [grid.Index(50, 31)]).Follow(grid.Index(10, 31));
                Assert.True(route.Arrived);
                Assert.Equal(open, route.Blocked == 0);
            }
        }
    }

    [Fact]
    public void Only_the_large_size_keeps_clear_of_a_blocked_cell_diagonally()
    {
        var grid = Grid(Road);
        grid.Clear[grid.Index(20, 20)] = 0;
        Assert.False(s_small.IsWalkable(grid, grid.Index(21, 20)));
        Assert.False(s_large.IsWalkable(grid, grid.Index(21, 20)));
        Assert.True(s_small.IsWalkable(grid, grid.Index(21, 21)));
        Assert.False(s_large.IsWalkable(grid, grid.Index(21, 21)));
        Assert.True(s_large.IsWalkable(grid, grid.Index(22, 20)));
    }

    [Fact]
    public void A_cell_too_low_for_the_large_size_blocks_it_only()
    {
        var grid = Grid(Road);
        grid.Clear[grid.Index(20, 20)] = 125;
        Assert.True(s_small.IsWalkable(grid, grid.Index(20, 20)));
        Assert.False(s_large.IsWalkable(grid, grid.Index(20, 20)));
    }

    [Fact]
    public void A_rise_past_the_climb_is_taken_by_the_ramp()
    {
        var grid = Grid(Road);
        // A 1 m step at column 30, with a ramp of 25 cm a cell in rows 50 to 54.
        for (var z = 0; z < Size; z++)
        {
            for (var x = 30; x < Size; x++)
            {
                grid.Floor[grid.Index(x, z)] = 100;
            }
        }

        for (var z = 50; z <= 54; z++)
        {
            grid.Floor[grid.Index(27, z)] = 25;
            grid.Floor[grid.Index(28, z)] = 50;
            grid.Floor[grid.Index(29, z)] = 75;
        }

        var result = Build(grid, s_small, s_breaking, [grid.Index(40, 10)]);
        var route = result.Follow(grid.Index(10, 10));
        Assert.True(route.Arrived);
        Assert.Equal(0, route.Steep);
        Assert.Contains(route.Cells, cell => grid.Column(cell) == 29 && grid.Row(cell) is >= 50 and <= 54);
    }

    [Fact]
    public void A_sealed_goal_is_broken_into_at_its_thinnest()
    {
        var grid = Grid(Road);
        // A ring round the goal, three cells thick except one cell on its east side.
        Wall(grid, 26, 38, 26, 28);
        Wall(grid, 26, 38, 36, 38);
        Wall(grid, 26, 28, 26, 38);
        Wall(grid, 36, 38, 26, 38);
        Unblock(grid, 37, 38, 29, 35);
        var goal = grid.Index(32, 32);
        foreach (var costs in new[] { s_breaking, s_avoiding })
        {
            var route = Build(grid, s_small, costs, [goal]).Follow(grid.Index(60, 32));
            Assert.True(route.Arrived);
            Assert.All(route.Cells.Where(grid.IsBlocked), cell => Assert.Equal(36, grid.Column(cell)));
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(8, true)]
    [InlineData(40, false)]
    public void Breaking_costs_break_a_wall_across_a_road_unless_it_is_very_thick(int thickness, bool breaks)
    {
        var (grid, start, goal) = RoadWithWall(thickness);
        var route = Build(grid, s_small, s_breaking, [goal]).Follow(start);
        Assert.True(route.Arrived);
        Assert.Equal(breaks, route.Blocked > 0);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void Avoiding_costs_walk_round_a_wall_across_a_road(int thickness)
    {
        var (grid, start, goal) = RoadWithWall(thickness);
        var route = Build(grid, s_small, s_avoiding, [goal]).Follow(start);
        Assert.True(route.Arrived);
        Assert.Equal(0, route.Blocked);
    }

    [Fact]
    public void Of_two_goals_the_nearer_by_route_is_taken()
    {
        var grid = Grid(Road);
        var near = grid.Index(20, 32);
        var far = grid.Index(60, 32);
        var route = Build(grid, s_small, s_breaking, [near, far]).Follow(grid.Index(10, 32));
        Assert.Equal(near, route.Cells[^1]);
        route = Build(grid, s_small, s_breaking, [near, far]).Follow(grid.Index(50, 32));
        Assert.Equal(far, route.Cells[^1]);
    }

    [Fact]
    public void The_same_grid_gives_the_same_field()
    {
        var (grid, _, goal) = RoadWithWall(2);
        var first = Build(grid, s_large, s_breaking, [goal]).Field.Directions.ToArray();
        var second = Build(grid, s_large, s_breaking, [goal]).Field.Directions;
        Assert.Equal(first, second);
    }

    [Fact]
    public void Building_again_allocates_nothing()
    {
        var (grid, _, goal) = RoadWithWall(2);
        var spec = new NavFieldSpec("test", s_small, s_avoiding, [goal]);
        var walkable = new byte[grid.Count];
        s_small.Derive(grid, walkable, 0, grid.Depth - 1);
        var builder = new FlowFieldBuilder();
        var field = new FlowField(grid);
        // Enough builds for .NET to finish compiling the loops' faster versions.
        for (var i = 0; i < 100; i++)
        {
            builder.Build(grid, spec, walkable, field);
            s_small.Derive(grid, walkable, 0, grid.Depth - 1);
        }

        var before = SystemAllocation.StartMeasurement();
        builder.Build(grid, spec, walkable, field);
        var afterBuild = GC.GetAllocatedBytesForCurrentThread() - before;
        before = SystemAllocation.StartMeasurement();
        s_small.Derive(grid, walkable, 0, grid.Depth - 1);
        Assert.Equal((0L, 0L), (afterBuild, GC.GetAllocatedBytesForCurrentThread() - before));
    }

    /// <summary>
    /// Open ground with a road along row 32 to its goal at the east edge, and a wall of <paramref name="thickness"/>
    /// cells across it at column 20, 21 cells long: walking round it means about 30 cells of open ground.
    /// </summary>
    private static (NavGrid Grid, int Start, int Goal) RoadWithWall(int thickness)
    {
        var grid = Grid(OpenGround);
        for (var x = 0; x < Size; x++)
        {
            for (var z = 31; z <= 33; z++)
            {
                grid.Class[grid.Index(x, z)] = Road;
            }
        }

        Wall(grid, 20, 20 + thickness - 1, 22, 42);
        return (grid, grid.Index(2, 32), grid.Index(Size - 1, 32));
    }

    /// <summary>A size with no radius, so a blocked cell blocks only itself.</summary>
    private static NavSize NavSize0() => new("point", 200, 0, 45, 64);

    private static NavGrid Grid(byte costClass)
    {
        var grid = new NavGrid(0, 0, Size, Size);
        grid.Clear.AsSpan().Fill(byte.MaxValue);
        grid.Class.AsSpan().Fill(costClass);
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

    private static Result Build(NavGrid grid, NavSize size, NavCostTable costs, int[] goals)
    {
        var walkable = new byte[grid.Count];
        size.Derive(grid, walkable, 0, grid.Depth - 1);
        var field = new FlowField(grid);
        new FlowFieldBuilder().Build(grid, new NavFieldSpec("test", size, costs, goals), walkable, field);
        return new Result(grid, size, field, walkable);
    }

    private sealed record Result(NavGrid Grid, NavSize Size, FlowField Field, byte[] Walkable)
    {
        public Route Follow(int cell)
        {
            var route = new Route();
            for (var step = 0; step < Grid.Count; step++)
            {
                var direction = Field.Directions[cell];
                if (direction == FlowField.Goal)
                {
                    route.Arrived = true;
                    return route;
                }

                var next = Field.Next(cell);
                if (next == cell)
                {
                    return route;
                }

                var climb = FlowField.IsDiagonal(direction) ? Size.DiagonalClimbCentimetres : Size.ClimbCentimetres;
                route.Steep += Grid.Floor[next] - Grid.Floor[cell] > climb ? 1 : 0;
                route.Blocked += Walkable[next] == 0 ? 1 : 0;
                route.Cells.Add(next);
                cell = next;
            }

            return route;
        }
    }

    private sealed class Route
    {
        public bool Arrived;
        public int Blocked;
        public int Steep;
        public List<int> Cells { get; } = [];
    }
}
