namespace Itera.Engine.Navigation;

/// <summary>
/// Builds a <see cref="FlowField"/> by Dijkstra's search backwards from its goal cells over the eight neighbours of each
/// cell, with a ring of buckets for the queue (every cost is a small whole number). Entering a cell costs its class's
/// cost, or the break cost where the walker's size cannot stand, or the steep cost for a rise past its climb; a
/// diagonal step needs both cells beside it walkable, so routes never cut a corner. Where a straight step and a diagonal
/// one cost the same, a cell takes the straight one, so a route goes straight until it must turn (a road's cells lead
/// down it side by side, not diagonally into the few lines that pass its obstacles). Blocked cells are expensive rather
/// than closed, so every cell of the grid gets a route. Keeps its arrays between builds and allocates nothing once
/// they have grown; one thread at a time.
/// </summary>
public sealed class FlowFieldBuilder
{
    private int[] _cost = [];
    private int[][] _buckets = [];
    private int[] _counts = [];

    /// <summary>
    /// Each cell's cost to reach the goal on the last build; valid until the next build. For tests and measurements.
    /// </summary>
    public ReadOnlySpan<int> Costs => _cost;

    /// <summary>
    /// Writes into <paramref name="field"/> the route from every cell of <paramref name="grid"/> to the goals of
    /// <paramref name="spec"/> (each the cell under it, or the window's edge cell nearest it), with
    /// <paramref name="walkable"/> (from <see cref="NavSize.Derive"/> for the spec's size) saying where the walker can
    /// stand; the field takes the grid's window.
    /// </summary>
    public void Build(NavGrid grid, NavFieldSpec spec, byte[] walkable, FlowField field)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(walkable);
        ArgumentNullException.ThrowIfNull(field);
        if (_cost.Length != grid.Count)
        {
            _cost = new int[grid.Count];
        }

        var ring = spec.Costs.Largest + 1;
        if (_buckets.Length < ring)
        {
            var buckets = new int[ring][];
            var counts = new int[ring];
            for (var i = 0; i < ring; i++)
            {
                buckets[i] = i < _buckets.Length ? _buckets[i] : [];
            }

            _buckets = buckets;
            _counts = counts;
        }

        var cost = _cost;
        var directions = field.Directions;
        cost.AsSpan().Fill(int.MaxValue);
        directions.AsSpan().Fill(FlowField.None);
        _counts.AsSpan().Clear();

        field.BuildOver(grid);
        var pending = 0;
        var goals = spec.Goals;
        for (var i = 0; i < goals.Length; i++)
        {
            // Outside the window, the edge cell nearest it stands in.
            var goal = grid.NearestCell(goals[i].X, goals[i].Z);
            if (cost[goal] != 0)
            {
                cost[goal] = 0;
                directions[goal] = FlowField.Goal;
                Push(0, goal);
                pending++;
            }
        }

        var width = grid.Width;
        var depth = grid.Depth;
        var floor = grid.Floor;
        var classes = grid.Class;
        var costs = spec.Costs;
        var climb = spec.Size.ClimbCentimetres;
        var diagonalClimb = spec.Size.DiagonalClimbCentimetres;
        var stepX = FlowField.StepX;
        var stepZ = FlowField.StepZ;
        for (var distance = 0; pending > 0; distance++)
        {
            var slot = distance % ring;
            while (_counts[slot] > 0)
            {
                var cell = _buckets[slot][--_counts[slot]];
                pending--;
                if (cost[cell] != distance)
                {
                    // Reached more cheaply after this entry was queued.
                    continue;
                }

                var column = cell % width;
                var row = cell / width;
                var enterWalkable = walkable[cell] != 0;
                var enterFloor = floor[cell];
                var enterClass = classes[cell];
                for (var direction = 0; direction < 8; direction++)
                {
                    // The neighbour that would step into this cell.
                    var fromColumn = column + stepX[direction];
                    var fromRow = row + stepZ[direction];
                    if (fromColumn < 0 || fromColumn >= width || fromRow < 0 || fromRow >= depth)
                    {
                        continue;
                    }

                    var from = fromRow * width + fromColumn;
                    var diagonal = FlowField.IsDiagonal(direction);
                    if (
                        diagonal && (walkable[row * width + fromColumn] == 0 || walkable[fromRow * width + column] == 0)
                    )
                    {
                        continue;
                    }

                    int step;
                    if (!enterWalkable)
                    {
                        step = diagonal ? costs.DiagonalBreak : costs.Break;
                    }
                    else if (enterFloor - floor[from] > (diagonal ? diagonalClimb : climb))
                    {
                        step = diagonal ? costs.DiagonalSteep : costs.Steep;
                    }
                    else
                    {
                        step = diagonal ? costs.DiagonalCost(enterClass) : costs.Straight(enterClass);
                    }

                    var total = distance + step;
                    if (total < cost[from])
                    {
                        cost[from] = total;
                        // From the neighbour, this cell lies the opposite way.
                        directions[from] = (byte)((direction + 4) & 7);
                        Push(total % ring, from);
                        pending++;
                    }
                    else if (total == cost[from] && !diagonal && FlowField.IsDiagonal(directions[from]))
                    {
                        // A straight step as cheap as the diagonal it has: straight, so a route goes straight until it
                        // must turn, and cells side by side lead ahead rather than into the lines their diagonals meet.
                        directions[from] = (byte)((direction + 4) & 7);
                    }
                }
            }
        }

        void Push(int slot, int cell)
        {
            ref var bucket = ref _buckets[slot];
            var count = _counts[slot];
            if (count == bucket.Length)
            {
                Array.Resize(ref bucket, Math.Max(64, count * 2));
            }

            bucket[count] = cell;
            _counts[slot] = count + 1;
        }
    }
}
