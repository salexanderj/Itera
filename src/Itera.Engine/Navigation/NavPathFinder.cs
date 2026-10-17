namespace Itera.Engine.Navigation;

/// <summary>What a path search found (<see cref="NavPathFinder.Find"/>).</summary>
public enum NavPathResult : byte
{
    /// <summary>The start lies outside the grid, or the walker's size cannot stand in it: no path.</summary>
    None,

    /// <summary>A path to the goal.</summary>
    Reached,

    /// <summary>
    /// The goal could not be reached within the budget (closed off, outside the grid, or too far): a path to the cell
    /// searched that lies nearest it.
    /// </summary>
    Nearest,
}

/// <summary>
/// Paths from one cell of a <see cref="NavGrid"/> to another for one walker (A*), for a walker whose goal changes (a
/// character moving to cover), where a <see cref="FlowField"/> serves many walkers bound for one goal. Its steps are a
/// flow field's (<see cref="FlowFieldBuilder"/>): the eight neighbours, a diagonal only where both cells beside it are
/// walkable, a walkable cell's class cost, and the steep cost for a rise past the size's climb or a drop past its drop;
/// but a cell the size cannot stand in is closed instead of priced, so a path walks round what blocks it and never
/// through. The heuristic is the octile distance at the cheapest class's cost, so a path found is a cheapest one. At
/// most a budget of cells is expanded; past it, or where the goal cannot be reached, the path ends at the expanded cell
/// nearest the goal. Keeps its arrays (an entry a grid cell, and a heap) between searches and allocates nothing once
/// they have grown; one thread at a time (on the main thread, over <see cref="NavigationService.Grid"/>).
/// </summary>
public sealed class NavPathFinder
{
    /// <summary>The cells a search expands at most unless told otherwise: a few hundred metres of open ground.</summary>
    public const int DefaultBudget = 4096;

    private int[] _cost = [];
    private int[] _parent = [];
    private int[] _seen = [];
    private int[] _closed = [];
    private int[] _walkSeen = [];
    private byte[] _walk = [];
    private int[] _heapCell = new int[256];
    private int[] _heapKey = new int[256];
    private int _heapCount;
    private int _stamp;

    /// <summary>The cells the last search expanded.</summary>
    public int Expanded { get; private set; }

    /// <summary>The cost of the last search's path, in the cost table's units.</summary>
    public int Cost { get; private set; }

    /// <summary>
    /// Searches <paramref name="grid"/> from cell <paramref name="start"/> to cell <paramref name="goal"/> for a walker of
    /// <paramref name="size"/> paying <paramref name="costs"/>, expanding at most <paramref name="budget"/> cells, and
    /// writes the path's cells after the start, in order (the goal, or the nearest cell reached, last), into
    /// <paramref name="path"/>: <paramref name="length"/> says how many it has, at most the span's length, the first of a
    /// longer path. A start that is the goal is reached with no cells.
    /// </summary>
    public NavPathResult Find(
        NavGrid grid,
        NavSize size,
        NavCostTable costs,
        int start,
        int goal,
        Span<int> path,
        out int length,
        int budget = DefaultBudget
    )
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(size);
        ArgumentNullException.ThrowIfNull(costs);
        length = 0;
        Expanded = 0;
        Cost = 0;
        if ((uint)start >= (uint)grid.Count)
        {
            return NavPathResult.None;
        }

        Begin(grid.Count);
        if (!Walkable(grid, size, start))
        {
            return NavPathResult.None;
        }

        var goalInside = (uint)goal < (uint)grid.Count;
        var goalColumn = goalInside ? grid.Column(goal) : 0;
        var goalRow = goalInside ? grid.Row(goal) : 0;
        var straight = costs.Cheapest;
        var diagonal = costs.Cheapest * 14 / 10;
        var width = grid.Width;
        var depth = grid.Depth;
        var floor = grid.Floor;
        var classes = grid.Class;
        var climb = size.ClimbCentimetres;
        var diagonalClimb = size.DiagonalClimbCentimetres;
        var drop = size.DropCentimetres;
        var diagonalDrop = size.DiagonalDropCentimetres;
        var stepX = FlowField.StepX;
        var stepZ = FlowField.StepZ;

        Touch(start, 0, -1);
        Push(start, Heuristic(start));
        var nearest = start;
        var nearestLeft = Heuristic(start);
        while (_heapCount > 0 && Expanded < budget)
        {
            var cell = Pop();
            if (_closed[cell] == _stamp)
            {
                continue;
            }

            _closed[cell] = _stamp;
            Expanded++;
            var left = Heuristic(cell);
            if (left < nearestLeft || (left == nearestLeft && _cost[cell] < _cost[nearest]))
            {
                (nearest, nearestLeft) = (cell, left);
            }

            if (cell == goal)
            {
                break;
            }

            var column = cell % width;
            var row = cell / width;
            var fromFloor = floor[cell];
            for (var direction = 0; direction < 8; direction++)
            {
                var toColumn = column + stepX[direction];
                var toRow = row + stepZ[direction];
                if (toColumn < 0 || toColumn >= width || toRow < 0 || toRow >= depth)
                {
                    continue;
                }

                var to = toRow * width + toColumn;
                if (_closed[to] == _stamp || !Walkable(grid, size, to))
                {
                    continue;
                }

                var isDiagonal = FlowField.IsDiagonal(direction);
                if (
                    isDiagonal
                    && (!Walkable(grid, size, row * width + toColumn) || !Walkable(grid, size, toRow * width + column))
                )
                {
                    continue;
                }

                var step =
                    floor[to] - fromFloor > (isDiagonal ? diagonalClimb : climb)
                    || (long)fromFloor - floor[to] > (isDiagonal ? diagonalDrop : drop)
                        ? isDiagonal
                            ? costs.DiagonalSteep
                            : costs.Steep
                        : isDiagonal
                            ? costs.DiagonalCost(classes[to])
                            : costs.Straight(classes[to]);
                var total = _cost[cell] + step;
                if (_seen[to] != _stamp || total < _cost[to])
                {
                    Touch(to, total, cell);
                    Push(to, total + Heuristic(to));
                }
            }
        }

        var end = goalInside && _closed[goal] == _stamp ? goal : nearest;
        Cost = _cost[end];
        length = Write(end, start, path);
        return end == goal ? NavPathResult.Reached : NavPathResult.Nearest;

        int Heuristic(int cell)
        {
            if (!goalInside)
            {
                return 0;
            }

            var dx = Math.Abs(cell % width - goalColumn);
            var dz = Math.Abs(cell / width - goalRow);
            var low = Math.Min(dx, dz);
            return low * diagonal + (Math.Max(dx, dz) - low) * straight;
        }
    }

    /// <summary>Starts a search over a grid of <paramref name="count"/> cells: a new stamp, the arrays grown to it.</summary>
    private void Begin(int count)
    {
        if (_cost.Length < count)
        {
            _cost = new int[count];
            _parent = new int[count];
            _seen = new int[count];
            _closed = new int[count];
            _walkSeen = new int[count];
            _walk = new byte[count];
            _stamp = 0;
        }

        if (_stamp == int.MaxValue)
        {
            Array.Clear(_seen);
            Array.Clear(_closed);
            Array.Clear(_walkSeen);
            _stamp = 0;
        }

        _stamp++;
        _heapCount = 0;
    }

    /// <summary>Whether the walker can stand in <paramref name="cell"/>, found once a search.</summary>
    private bool Walkable(NavGrid grid, NavSize size, int cell)
    {
        if (_walkSeen[cell] != _stamp)
        {
            _walkSeen[cell] = _stamp;
            _walk[cell] = size.IsWalkable(grid, cell) ? (byte)1 : (byte)0;
        }

        return _walk[cell] != 0;
    }

    private void Touch(int cell, int cost, int parent)
    {
        _seen[cell] = _stamp;
        _cost[cell] = cost;
        _parent[cell] = parent;
    }

    /// <summary>Writes the cells after <paramref name="start"/> up to <paramref name="end"/> into <paramref name="path"/>; returns how many.</summary>
    private int Write(int end, int start, Span<int> path)
    {
        var steps = 0;
        for (var cell = end; cell != start && cell >= 0; cell = _parent[cell])
        {
            steps++;
        }

        var index = steps - 1;
        for (var cell = end; cell != start && cell >= 0; cell = _parent[cell])
        {
            if (index < path.Length)
            {
                path[index] = cell;
            }

            index--;
        }

        return Math.Min(steps, path.Length);
    }

    private void Push(int cell, int key)
    {
        if (_heapCount == _heapCell.Length)
        {
            Array.Resize(ref _heapCell, _heapCount * 2);
            Array.Resize(ref _heapKey, _heapCount * 2);
        }

        var i = _heapCount++;
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (_heapKey[parent] <= key)
            {
                break;
            }

            _heapCell[i] = _heapCell[parent];
            _heapKey[i] = _heapKey[parent];
            i = parent;
        }

        _heapCell[i] = cell;
        _heapKey[i] = key;
    }

    private int Pop()
    {
        var top = _heapCell[0];
        var count = --_heapCount;
        var cell = _heapCell[count];
        var key = _heapKey[count];
        var i = 0;
        while (true)
        {
            var child = 2 * i + 1;
            if (child >= count)
            {
                break;
            }

            if (child + 1 < count && _heapKey[child + 1] < _heapKey[child])
            {
                child++;
            }

            if (_heapKey[child] >= key)
            {
                break;
            }

            _heapCell[i] = _heapCell[child];
            _heapKey[i] = _heapKey[child];
            i = child;
        }

        _heapCell[i] = cell;
        _heapKey[i] = key;
        return top;
    }
}
