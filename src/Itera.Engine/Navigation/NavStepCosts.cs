namespace Itera.Engine.Navigation;

/// <summary>
/// What one step costs a walker of a field's size and cost table over a grid's cells, as <see cref="FlowFieldBuilder"/>
/// prices it: entering a cell the size cannot stand in costs a break, a rise past the climb or a drop past the drop the
/// steep cost, else the cell's class cost with its drop's cost; a diagonal step needs both cells beside it walkable.
/// Reads the grid and the walkable cells it was given as they are at each call. One thread at a time.
/// </summary>
internal sealed class NavStepCosts
{
    private readonly NavGrid _grid;
    private readonly byte[] _walkable;
    private readonly NavCostTable _costs;
    private readonly int _climb;
    private readonly int _diagonalClimb;
    private readonly long _drop;
    private readonly long _diagonalDrop;

    public NavStepCosts(NavGrid grid, byte[] walkable, NavSize size, NavCostTable costs)
    {
        _grid = grid;
        _walkable = walkable;
        _costs = costs;
        _climb = size.ClimbCentimetres;
        _diagonalClimb = size.DiagonalClimbCentimetres;
        _drop = size.DropCentimetres;
        _diagonalDrop = size.DiagonalDropCentimetres;
    }

    /// <summary>The grid's cells.</summary>
    public NavGrid Grid => _grid;

    /// <summary>The cells the size can stand in, one byte a cell.</summary>
    public byte[] Walkable => _walkable;

    /// <summary>The dearest one step can cost (<see cref="NavCostTable.Largest"/>).</summary>
    public int LargestStep => _costs.Largest;

    /// <summary>
    /// The cost of stepping from cell <paramref name="from"/> into its neighbour <paramref name="to"/>, which lies in
    /// direction <paramref name="direction"/> of it (<see cref="FlowField.StepX"/>); -1 for a diagonal step with a cell
    /// beside it the size cannot stand in.
    /// </summary>
    public int Step(int from, int to, int direction)
    {
        var floor = _grid.Floor;
        var diagonal = FlowField.IsDiagonal(direction);
        if (
            diagonal
            && (
                _walkable[from + FlowField.StepZ[direction] * _grid.Width] == 0
                || _walkable[from + FlowField.StepX[direction]] == 0
            )
        )
        {
            return -1;
        }

        if (_walkable[to] == 0)
        {
            return diagonal ? _costs.DiagonalBreak : _costs.Break;
        }

        if (
            floor[to] - floor[from] > (diagonal ? _diagonalClimb : _climb)
            || (long)floor[from] - floor[to] > (diagonal ? _diagonalDrop : _drop)
        )
        {
            return diagonal ? _costs.DiagonalSteep : _costs.Steep;
        }

        var cell = _grid.Class[to];
        return _costs.WithDrop(
            diagonal ? _costs.DiagonalCost(cell) : _costs.Straight(cell),
            floor[from] - floor[to],
            diagonal ? _costs.DiagonalSteep : _costs.Steep
        );
    }

    /// <summary>
    /// Writes, for each cell of the rectangle from <paramref name="x0"/>, <paramref name="z0"/> of
    /// <paramref name="width"/> by <paramref name="depth"/> cells, eight entries at <paramref name="into"/>[(row *
    /// <paramref name="stride"/> + column) * 8 + direction]: what stepping into it from its neighbour in that direction
    /// costs (<see cref="Step"/>), or -1 where that neighbour lies outside the rectangle or the step is not allowed. The
    /// same costs as <see cref="Step"/>, worked out a cell at a time.
    /// </summary>
    public void FillInto(int x0, int z0, int width, int depth, int stride, Span<int> into)
    {
        var windowWidth = _grid.Width;
        var floor = _grid.Floor;
        var classes = _grid.Class;
        var walkable = _walkable;
        var costs = _costs;
        var stepX = FlowField.StepX;
        var stepZ = FlowField.StepZ;
        for (var lz = 0; lz < depth; lz++)
        {
            for (var lx = 0; lx < width; lx++)
            {
                var entries = into.Slice((lz * stride + lx) * 8, 8);
                var to = (z0 + lz) * windowWidth + x0 + lx;
                var toWalkable = walkable[to] != 0;
                var toFloor = floor[to];
                var straight = costs.Straight(classes[to]);
                var diagonalCost = costs.DiagonalCost(classes[to]);
                for (var direction = 0; direction < 8; direction++)
                {
                    var fx = lx + stepX[direction];
                    var fz = lz + stepZ[direction];
                    if (fx < 0 || fx >= width || fz < 0 || fz >= depth)
                    {
                        entries[direction] = -1;
                        continue;
                    }

                    // The neighbour steps in the opposite way: the cells beside that diagonal are this cell's along x
                    // and along z.
                    var diagonal = (direction & 1) != 0;
                    if (
                        diagonal
                        && (walkable[to + stepX[direction]] == 0 || walkable[to + stepZ[direction] * windowWidth] == 0)
                    )
                    {
                        entries[direction] = -1;
                        continue;
                    }

                    if (!toWalkable)
                    {
                        entries[direction] = diagonal ? costs.DiagonalBreak : costs.Break;
                        continue;
                    }

                    var fromFloor = floor[to + stepZ[direction] * windowWidth + stepX[direction]];
                    if (
                        toFloor - fromFloor > (diagonal ? _diagonalClimb : _climb)
                        || (long)fromFloor - toFloor > (diagonal ? _diagonalDrop : _drop)
                    )
                    {
                        entries[direction] = diagonal ? costs.DiagonalSteep : costs.Steep;
                        continue;
                    }

                    entries[direction] = costs.WithDrop(
                        diagonal ? diagonalCost : straight,
                        fromFloor - toFloor,
                        diagonal ? costs.DiagonalSteep : costs.Steep
                    );
                }
            }
        }
    }

    /// <summary>
    /// The kind of the straight crossing between neighbours <paramref name="a"/> and <paramref name="b"/> (<paramref name="b"/>
    /// in direction <paramref name="direction"/> of <paramref name="a"/>): 0 a walk both ways, 1 a block on either side,
    /// 2 a step past the climb or the drop either way. A tile's edge is cut into portals where the kind changes.
    /// </summary>
    public int Kind(int a, int b, int direction)
    {
        if (_walkable[a] == 0 || _walkable[b] == 0)
        {
            return 1;
        }

        return Step(a, b, direction) == _costs.Steep || Step(b, a, (direction + 4) & 7) == _costs.Steep ? 2 : 0;
    }
}
