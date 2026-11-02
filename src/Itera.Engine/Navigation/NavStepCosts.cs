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
