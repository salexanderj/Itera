namespace Itera.Engine.Navigation;

/// <summary>
/// Dijkstra's search backwards inside one tile of <see cref="NavTiles"/>: from cells inside it at cost 0 (a goal, or a
/// portal whose costs to the others are wanted) and from cells just outside it at the costs given (the far sides of the
/// portals the tile is left by), each cell of the tile gets its cost to them and the neighbour it steps into on that way.
/// <see cref="Begin"/> works out the cost of every step inside the tile once, so several searches of one tile share it.
/// Where a straight step and a diagonal one cost the same, a cell takes the straight one, as <see cref="FlowFieldBuilder"/>
/// does. The queue is a ring of buckets, one a cost, at least twice the dearest step long: the cells waiting are linked
/// in their cost's bucket, and the seeds go in cheapest first, each before the queue reaches its cost, so every cost
/// waiting lies within the ring from the last one taken. The tile's cells are numbered with <see cref="NavTiles.Cells"/>
/// to a row. One thread at a time; allocates nothing once its ring is long enough for the dearest step it has met.
/// </summary>
internal sealed class TileSearch
{
    private const int Stride = NavTiles.Cells;
    private const int Cells = Stride * Stride;

    private static readonly int[] s_local = Offsets();

    private readonly int[] _cost = new int[Cells];
    private readonly byte[] _direction = new byte[Cells];

    // Each local cell's eight entries: what stepping into it costs from its neighbour in that direction; -1 for none.
    private readonly int[] _into = new int[Cells * 8];

    // The queue: each waiting cell linked into its cost's bucket of the ring; each bucket's first cell; the buckets that
    // hold any, a bit each.
    private readonly int[] _next = new int[Cells];
    private readonly int[] _previous = new int[Cells];
    private readonly bool[] _waiting = new bool[Cells];
    private readonly int[] _seedOrder = new int[NavTiles.Slots];
    private int[] _head = [];
    private ulong[] _held = [];
    private int _mask;
    private int _count;
    private NavStepCosts? _steps;
    private int _windowWidth;
    private int _x0;
    private int _z0;
    private int _width;
    private int _depth;

    /// <summary>Each local cell's cost, from the last search; <see cref="int.MaxValue"/> where it reached none.</summary>
    public ReadOnlySpan<int> Cost => _cost;

    /// <summary>Each local cell's way on, from the last search (<see cref="FlowField.Goal"/> at a cell searched from).</summary>
    public ReadOnlySpan<byte> Direction => _direction;

    /// <summary>The tile's cells along x, from <see cref="Begin"/>.</summary>
    public int Width => _width;

    /// <summary>The tile's cells along z, from <see cref="Begin"/>.</summary>
    public int Depth => _depth;

    /// <summary>The local number of the window's cell <paramref name="cell"/>, which lies in the tile.</summary>
    public int Local(int cell) => (cell / _windowWidth - _z0) * Stride + cell % _windowWidth - _x0;

    /// <summary>The cost of the window's cell <paramref name="cell"/> in the tile, from the last search.</summary>
    public int CostOf(int cell) => _cost[Local(cell)];

    /// <summary>Takes tile <paramref name="tile"/> of <paramref name="tiles"/>, its steps priced by <paramref name="steps"/>.</summary>
    public void Begin(NavTiles tiles, int tile, NavStepCosts steps)
    {
        _steps = steps;
        _windowWidth = tiles.Width;
        _x0 = tiles.FirstColumn(tile);
        _z0 = tiles.FirstRow(tile);
        _width = tiles.TileWidth(tile);
        _depth = tiles.TileDepth(tile);
        var ring = 64;
        while (ring < 2 * ((long)steps.LargestStep + 1))
        {
            ring *= 2;
        }

        if (_head.Length < ring)
        {
            _head = new int[ring];
            _head.AsSpan().Fill(-1);
            _held = new ulong[ring / 64];
            _mask = ring - 1;
        }

        var stepX = FlowField.StepX;
        var stepZ = FlowField.StepZ;
        for (var lz = 0; lz < _depth; lz++)
        {
            for (var lx = 0; lx < _width; lx++)
            {
                var local = lz * Stride + lx;
                var cell = (_z0 + lz) * _windowWidth + _x0 + lx;
                for (var direction = 0; direction < 8; direction++)
                {
                    var fx = lx + stepX[direction];
                    var fz = lz + stepZ[direction];
                    _into[local * 8 + direction] =
                        fx < 0 || fx >= _width || fz < 0 || fz >= _depth
                            ? -1
                            : steps.Step(
                                cell + stepZ[direction] * _windowWidth + stepX[direction],
                                cell,
                                (direction + 4) & 7
                            );
                }
            }
        }
    }

    /// <summary>
    /// Searches the tile from <paramref name="from"/> (the window's cells inside it, at cost 0) and from the window's
    /// cells <paramref name="seedCells"/> just outside it at <paramref name="seedCosts"/>; every cell of the tile not
    /// reached keeps <see cref="int.MaxValue"/> and <see cref="FlowField.None"/>.
    /// </summary>
    public void Search(ReadOnlySpan<int> from, ReadOnlySpan<int> seedCells, ReadOnlySpan<int> seedCosts)
    {
        var steps = _steps ?? throw new InvalidOperationException("Begin a tile before searching it.");
        Array.Fill(_cost, int.MaxValue);
        Array.Fill(_direction, FlowField.None);
        foreach (var cell in from)
        {
            var local = Local(cell);
            if (_cost[local] != 0)
            {
                _cost[local] = 0;
                _direction[local] = FlowField.Goal;
                Insert(local, 0);
            }
        }

        // The seeds, cheapest first.
        var seeds = seedCells.Length;
        for (var i = 0; i < seeds; i++)
        {
            var j = i;
            while (j > 0 && seedCosts[_seedOrder[j - 1]] > seedCosts[i])
            {
                _seedOrder[j] = _seedOrder[j - 1];
                j--;
            }

            _seedOrder[j] = i;
        }

        var nextSeed = 0;
        long last = 0;
        while (true)
        {
            var least = _count > 0 ? Least(last) : long.MaxValue;
            if (nextSeed < seeds && seedCosts[_seedOrder[nextSeed]] < least)
            {
                // A seed goes in before the queue reaches its cost: what it gives costs more than it, and less than the
                // dearest step past the cheapest waiting.
                var seed = _seedOrder[nextSeed++];
                // The ring counts from here: nothing waiting costs less than this seed, and what it gives costs more.
                last = Math.Max(last, seedCosts[seed]);
                Seed(steps, seedCells[seed], seedCosts[seed]);
                continue;
            }

            if (_count == 0)
            {
                break;
            }

            var bucket = (int)(least & _mask);
            var local = _head[bucket];
            Remove(local, bucket);
            last = least;
            for (var direction = 0; direction < 8; direction++)
            {
                var step = _into[local * 8 + direction];
                if (step >= 0)
                {
                    Relax(local + s_local[direction], least + step, (direction + 4) & 7);
                }
            }
        }
    }

    /// <summary>The tile's cells beside the window's cell <paramref name="seed"/> (outside it) step into it at <paramref name="cost"/>.</summary>
    private void Seed(NavStepCosts steps, int seed, int cost)
    {
        var stepX = FlowField.StepX;
        var stepZ = FlowField.StepZ;
        var sx = seed % _windowWidth - _x0;
        var sz = seed / _windowWidth - _z0;
        for (var direction = 0; direction < 8; direction++)
        {
            var ax = sx + stepX[direction];
            var az = sz + stepZ[direction];
            if (ax < 0 || ax >= _width || az < 0 || az >= _depth)
            {
                continue;
            }

            // From the tile's cell, the seed lies the opposite way.
            var toward = (direction + 4) & 7;
            var step = steps.Step(seed + stepZ[direction] * _windowWidth + stepX[direction], seed, toward);
            if (step >= 0)
            {
                Relax(az * Stride + ax, (long)cost + step, toward);
            }
        }
    }

    /// <summary>Gives local cell <paramref name="local"/> the cost <paramref name="total"/> going <paramref name="toward"/>, where cheaper.</summary>
    private void Relax(int local, long total, int toward)
    {
        if (total >= int.MaxValue)
        {
            return;
        }

        var old = _cost[local];
        if (total < old)
        {
            if (_waiting[local])
            {
                Remove(local, old & _mask);
            }

            _cost[local] = (int)total;
            _direction[local] = (byte)toward;
            Insert(local, (int)total);
        }
        else if (
            total == old
            && !FlowField.IsDiagonal(toward)
            && _direction[local] < FlowField.Goal
            && FlowField.IsDiagonal(_direction[local])
        )
        {
            // A straight step as cheap as the diagonal it has: straight, as the whole field's builder does.
            _direction[local] = (byte)toward;
        }
    }

    /// <summary>The least cost waiting: the first bucket holding any from <paramref name="last"/>'s, round the ring.</summary>
    private long Least(long last)
    {
        var start = (int)(last & _mask);
        var words = _held.Length;
        var word = start >> 6;
        var bits = _held[word] & (ulong.MaxValue << (start & 63));
        for (var i = 0; i <= words; i++)
        {
            if (bits != 0)
            {
                var bucket = ((word + i) % words << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                return last + ((bucket - start) & _mask);
            }

            bits = _held[(word + i + 1) % words];
        }

        throw new InvalidOperationException("The queue holds cells but no bucket does.");
    }

    private void Insert(int local, int cost)
    {
        var bucket = cost & _mask;
        var first = _head[bucket];
        _next[local] = first;
        _previous[local] = -1;
        if (first >= 0)
        {
            _previous[first] = local;
        }

        _head[bucket] = local;
        _held[bucket >> 6] |= 1UL << (bucket & 63);
        _waiting[local] = true;
        _count++;
    }

    private void Remove(int local, int bucket)
    {
        var next = _next[local];
        var previous = _previous[local];
        if (previous >= 0)
        {
            _next[previous] = next;
        }
        else
        {
            _head[bucket] = next;
            if (next < 0)
            {
                _held[bucket >> 6] &= ~(1UL << (bucket & 63));
            }
        }

        if (next >= 0)
        {
            _previous[next] = previous;
        }

        _waiting[local] = false;
        _count--;
    }

    private static int[] Offsets()
    {
        var offsets = new int[8];
        for (var direction = 0; direction < 8; direction++)
        {
            offsets[direction] = FlowField.StepZ[direction] * Stride + FlowField.StepX[direction];
        }

        return offsets;
    }
}
