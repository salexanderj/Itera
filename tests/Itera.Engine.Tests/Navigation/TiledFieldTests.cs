using Itera.Engine.Navigation;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// <see cref="TiledField"/> over <see cref="NavPortals"/>: on a grid of walls with gaps, a raised block, and a steep
/// bump, whose far tiles are cut short, every cell's route reaches a goal without looping, and routes cost within 1 % of
/// an exact build's (<see cref="FlowFieldBuilder"/>); a wall built after the first build is gone round as an exact build
/// goes round it, and taking it down again gives the first build's routes; a goal past the window stands at the edge
/// cell nearest it; and an update allocates nothing once its arrays have grown.
/// </summary>
public sealed class TiledFieldTests
{
    private const int Width = 100;
    private const int Depth = 90;

    private static readonly NavSize s_size = new("walker", 200, 30, 45, 64);
    private static readonly NavCostTable s_costs = new([10], 20_000, 3_000, dropPerMetre: 100);

    // Cells so dear that routes cost many times the dearest step: far past the length of a search's ring of costs.
    private static readonly NavCostTable s_dear = new([2_000], 20_000, 6_000);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_cell_reaches_a_goal_without_looping_at_about_an_exact_builds_cost(bool dear)
    {
        var setup = new Setup(Grid(), Goals(Width - 3, Depth - 3), dear ? s_dear : s_costs);

        var realized = Realize(setup, out var loops, out var lost);

        Assert.Equal(0, loops);
        Assert.Equal(0, lost);
        AssertNearExact(setup, realized, 0.01);
    }

    [Fact]
    public void A_wall_built_after_the_first_build_is_gone_round_and_taking_it_down_gives_the_first_routes()
    {
        var setup = new Setup(Grid(), Goals(Width - 3, Depth - 3));
        var first = (byte[])setup.Field.Directions.Clone();

        // A wall across the open middle from x 10 to 89 at z 60: the way round is its west end.
        var clear = (byte[])setup.Grid.Clear.Clone();
        for (var x = 10; x < 90; x++)
        {
            setup.Grid.Clear[setup.Grid.Index(x, 60)] = 0;
        }

        setup.Update(10, 60, 89, 60);
        var walled = Realize(setup, out var loops, out var lost);
        Assert.Equal(0, loops);
        Assert.Equal(0, lost);
        AssertNearExact(setup, walled, 0.01);
        // South of the wall's middle the exact route goes round it, far cheaper than breaking through.
        var below = setup.Grid.Index(50, 55);
        Assert.True(walled[below] < s_costs.Break, $"The route from below the wall costs {walled[below]}.");

        clear.CopyTo(setup.Grid.Clear, 0);
        setup.Update(10, 60, 89, 60);
        Realize(setup, out loops, out lost);
        Assert.Equal(0, loops);
        Assert.Equal(first, setup.Field.Directions);
    }

    [Fact]
    public void A_goal_past_the_window_stands_at_the_edge_cell_nearest_it()
    {
        var grid = Grid();
        var setup = new Setup(grid, [new VoxelCoord(grid.CentreX(40), 0, grid.CentreZ(0) + 100_00)]);

        Assert.Equal(FlowField.Goal, setup.Field.Directions[grid.Index(40, Depth - 1)]);
        Assert.Equal(1, setup.Field.Directions.Count(direction => direction == FlowField.Goal));
    }

    [Fact]
    public void An_update_allocates_nothing_once_its_arrays_have_grown()
    {
        var setup = new Setup(Grid(), Goals(Width - 3, Depth - 3));
        var cell = setup.Grid.Index(30, 30);
        var floor = setup.Grid.Floor[cell];
        setup.Grid.Floor[cell] = floor - 80;
        setup.Update(30, 30, 30, 30);
        setup.Grid.Floor[cell] = floor;
        setup.Update(30, 30, 30, 30);

        var allocated = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            setup.Grid.Floor[cell] = floor - 80;
            setup.Update(30, 30, 30, 30);
            setup.Grid.Floor[cell] = floor;
            setup.Update(30, 30, 30, 30);
        });

        Assert.Equal(0, allocated);
    }

    private static void AssertNearExact(Setup setup, long[] realized, double share)
    {
        var exact = new FlowFieldBuilder();
        exact.Build(setup.Grid, setup.Spec, setup.Walkable, new FlowField(setup.Grid));
        var costs = exact.Costs;
        var worst = 0d;
        var at = -1;
        for (var cell = 0; cell < costs.Length; cell++)
        {
            if (setup.Walkable[cell] == 0 || costs[cell] == 0)
            {
                continue;
            }

            var ratio = (double)realized[cell] / costs[cell];
            if (ratio > worst)
            {
                (worst, at) = (ratio, cell);
            }
        }

        if (worst > 1 + share)
        {
            Assert.Fail(
                $"Cell {at} costs {realized[at]} along its route against {costs[at]} exactly ({worst:F3} times)."
            );
        }
    }

    /// <summary>Each cell's cost along the route its directions give; counts loops and cells with no route.</summary>
    private static long[] Realize(Setup setup, out int loops, out int lost)
    {
        var grid = setup.Grid;
        var directions = setup.Field.Directions;
        var steps = new NavStepCosts(grid, setup.Walkable, s_size, setup.Spec.Costs);
        var realized = new long[grid.Count];
        var state = new byte[grid.Count];
        var stack = new int[grid.Count];
        loops = 0;
        lost = 0;
        for (var start = 0; start < grid.Count; start++)
        {
            var top = 0;
            var cell = start;
            long tail;
            while (true)
            {
                if (state[cell] == 2)
                {
                    tail = realized[cell];
                    break;
                }

                if (state[cell] == 1)
                {
                    loops++;
                    tail = long.MaxValue;
                    break;
                }

                var direction = directions[cell];
                if (direction == FlowField.Goal || direction == FlowField.None)
                {
                    lost += direction == FlowField.None ? 1 : 0;
                    tail = direction == FlowField.Goal ? 0 : long.MaxValue;
                    realized[cell] = tail;
                    state[cell] = 2;
                    break;
                }

                state[cell] = 1;
                stack[top++] = cell;
                cell += FlowField.StepZ[direction] * grid.Width + FlowField.StepX[direction];
            }

            while (top > 0)
            {
                var from = stack[--top];
                var direction = directions[from];
                var step = steps.Step(
                    from,
                    from + FlowField.StepZ[direction] * grid.Width + FlowField.StepX[direction],
                    direction
                );
                tail = tail == long.MaxValue || step < 0 ? long.MaxValue : tail + step;
                realized[from] = tail;
                state[from] = 2;
            }
        }

        return realized;
    }

    /// <summary>Walls with gaps across the grid, a raised block, and a steep bump, over ground rising to the east.</summary>
    private static NavGrid Grid()
    {
        var grid = new NavGrid(0, 0, Width, Depth);
        grid.Clear.AsSpan().Fill(byte.MaxValue);
        for (var z = 0; z < Depth; z++)
        {
            for (var x = 0; x < Width; x++)
            {
                grid.Floor[grid.Index(x, z)] = x * 3;
            }
        }

        for (var x = 0; x < Width; x++)
        {
            if (x is not (20 or 21 or 70))
            {
                grid.Clear[grid.Index(x, 40)] = 0;
            }

            if (x is > 50 and < 95)
            {
                grid.Clear[grid.Index(x, 20)] = 0;
            }
        }

        for (var z = 50; z < 58; z++)
        {
            for (var x = 30; x < 40; x++)
            {
                grid.Floor[grid.Index(x, z)] += 200;
            }
        }

        grid.Floor[grid.Index(60, 70)] += 90;
        return grid;
    }

    private static VoxelCoord[] Goals(int column, int row) =>
        [new VoxelCoord(column * NavGrid.CellCentimetres + 25, 0, row * NavGrid.CellCentimetres + 25)];

    /// <summary>A field built whole over its portals, and its updates after cells changed.</summary>
    private sealed class Setup
    {
        private readonly NavTiles _tiles;
        private readonly NavPortals _portals;
        private readonly TiledField _field;
        private readonly TileSearch _search = new();
        private readonly List<int> _touched = [];

        public Setup(NavGrid grid, VoxelCoord[] goals, NavCostTable? costs = null)
        {
            Grid = grid;
            Spec = new NavFieldSpec("test", s_size, costs ?? s_costs, goals);
            Walkable = new byte[grid.Count];
            s_size.Derive(grid, Walkable, 0, grid.Depth - 1);
            _tiles = new NavTiles(grid.Width, grid.Depth);
            _portals = new NavPortals(_tiles, new NavStepCosts(grid, Walkable, s_size, Spec.Costs));
            _portals.LayAll();
            Intra();
            Field = new FlowField(grid);
            _field = new TiledField(Spec, _portals);
            _field.BuildWhole(grid, Field, _search);
            Assert.True(_field.Agreed);
        }

        public NavGrid Grid { get; }

        public NavFieldSpec Spec { get; }

        public byte[] Walkable { get; }

        public FlowField Field { get; }

        /// <summary>After the cells from (c0, r0) to (c1, r1) changed: as the navigation thread updates the field.</summary>
        public void Update(int c0, int r0, int c1, int r1)
        {
            var reach = s_size.ReachCells;
            s_size.DeriveArea(Grid, Walkable, c0 - reach, r0 - reach, c1 + reach, r1 + reach);
            _touched.Clear();
            var margin = reach + 1;
            for (
                var tz = Math.Max(0, r0 - margin) / NavTiles.Cells;
                tz <= Math.Min(Grid.Depth - 1, r1 + margin) / NavTiles.Cells;
                tz++
            )
            {
                for (
                    var tx = Math.Max(0, c0 - margin) / NavTiles.Cells;
                    tx <= Math.Min(Grid.Width - 1, c1 + margin) / NavTiles.Cells;
                    tx++
                )
                {
                    _touched.Add(tz * _tiles.TilesX + tx);
                }
            }

            var touched = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_touched);
            _portals.Lay(touched);
            Intra();
            _field.Catch(Field, Field);
            _field.Update(touched, Field, _search);
            Assert.True(_field.Agreed);
        }

        private void Intra()
        {
            for (var i = 0; i < _portals.IntraWanted.Count; i++)
            {
                _portals.ComputeIntra(_portals.IntraWanted[i], _search);
            }
        }
    }
}
