namespace Itera.Engine.Navigation;

/// <summary>
/// One field built tile by tile (<see cref="NavTiles"/>) over its size's portals (<see cref="NavPortals"/>). A search of
/// the portals' graph from the goals gives each node its cost to them. A portal leads one way only, from the side whose
/// node costs more to the side whose node costs less: those are the ways out of a tile. Each tile's field is a search
/// inside it (<see cref="TileSearch"/>) from the goals in it and from the far cells of its ways out, at those cells' own
/// costs where they are known, else at the far node's. When a tile's field changes its costs, the tiles that leave into
/// it are looked at again, nearest the goals first, until no tile's seeds change: every step then leads to a cheaper
/// cell, so a route never loops. A tile whose seeds all moved by one amount keeps its directions and moves its costs.
/// After cells changed (<see cref="Update"/>), each tile's costs first move by the change in its nodes' least cost, so
/// costs left from before stand near their new values and the tiles agree again in a few looks; the tiles whose cells
/// changed are built again. Routes cost within about 0.1 % of an exact build's for 99 % of cells (salexanderj/Itera#7).
/// Each cell's cost to the goals is kept (<see cref="Costs"/>). Runs on one thread at a time; allocates nothing once its
/// arrays have grown.
/// </summary>
internal sealed class TiledField
{
    /// <summary>The most looks at tiles in one build, as many times the tiles: past it a build stops where it is.</summary>
    public const int MostLooksPerTile = 64;

    private const int TileBits = 16;
    private const int NodeBits = 22;

    private readonly NavFieldSpec _spec;
    private readonly NavPortals _portals;
    private readonly NavTiles _tiles;
    private readonly int[] _cost;
    private readonly int[] _seeds;
    private readonly bool[] _built;
    private readonly long[] _least;
    private readonly long[] _newLeast;
    private readonly ulong[] _exits;
    private readonly bool[] _queued;
    private readonly bool[] _forced;
    private readonly int[] _goalFirst;
    private readonly int[] _slots = new int[NavTiles.Slots];
    private readonly int[] _seedCells = new int[NavTiles.Slots];
    private readonly int[] _seedCosts = new int[NavTiles.Slots];
    private readonly List<int> _written = [];
    private readonly bool[] _isWritten;
    private int[] _goalCells = [];
    private long[] _nodeCost = [];
    private long[] _heap = new long[4096];
    private int _heapCount;
    private bool _wroteAll;

    public TiledField(NavFieldSpec spec, NavPortals portals)
    {
        _spec = spec;
        _portals = portals;
        _tiles = portals.Tiles;
        var cells = _tiles.Width * _tiles.Depth;
        _cost = new int[cells];
        _seeds = new int[_tiles.Count * NavTiles.Slots];
        _built = new bool[_tiles.Count];
        _least = new long[_tiles.Count];
        _newLeast = new long[_tiles.Count];
        _exits = new ulong[_tiles.Count];
        _queued = new bool[_tiles.Count];
        _forced = new bool[_tiles.Count];
        _goalFirst = new int[_tiles.Count + 1];
        _isWritten = new bool[_tiles.Count];
    }

    /// <summary>The field's spec.</summary>
    public NavFieldSpec Spec => _spec;

    /// <summary>The size and cost table's portals the field is built over.</summary>
    public NavPortals Portals => _portals;

    /// <summary>Each cell's cost to the goals along its route, from the last build; <see cref="int.MaxValue"/> for none.</summary>
    public ReadOnlySpan<int> Costs => _cost;

    /// <summary>How long the last build took, in milliseconds.</summary>
    public double Milliseconds { get; private set; }

    /// <summary>The tiles looked at in the last build.</summary>
    public int Looks { get; private set; }

    /// <summary>The tile fields searched again in the last build.</summary>
    public int Built { get; private set; }

    /// <summary>The tiles whose costs only moved in the last build.</summary>
    public int Moved { get; private set; }

    /// <summary>True when the last build ended with every tile agreeing, under <see cref="MostLooksPerTile"/>.</summary>
    public bool Agreed { get; private set; }

    /// <summary>
    /// Makes <paramref name="back"/> agree with <paramref name="front"/> where the last build wrote the other one: every
    /// cell after a build of the whole field, else the tiles it searched again. Call before a build into
    /// <paramref name="back"/>.
    /// </summary>
    public void Catch(FlowField front, FlowField back)
    {
        if (_wroteAll)
        {
            if (!ReferenceEquals(front, back))
            {
                back.CopyFrom(front);
            }
        }
        else
        {
            foreach (var tile in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_written))
            {
                CopyTile(front.Directions, back.Directions, tile);
                _isWritten[tile] = false;
            }
        }

        _written.Clear();
        _wroteAll = false;
    }

    /// <summary>
    /// Builds the whole field over <paramref name="grid"/> into <paramref name="field"/> (which takes the grid's window),
    /// with <paramref name="search"/>: for the first build, a refill, and a move of the window.
    /// </summary>
    public void BuildWhole(NavGrid grid, FlowField field, TileSearch search)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        field.BuildOver(grid);
        FindGoals(grid);
        Array.Fill(_cost, int.MaxValue);
        Array.Clear(_built);
        Array.Fill(_least, long.MaxValue);
        // Every cell is written: the next build into the other field copies the whole of this one (Catch).
        _wroteAll = true;
        SearchNodes(search);
        Pass(field, search, whole: true);
        Milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>
    /// Builds the field again after the cells of the <paramref name="touched"/> tiles changed (and the portals were laid
    /// again over them), into <paramref name="field"/>, with <paramref name="search"/>.
    /// </summary>
    public void Update(ReadOnlySpan<int> touched, FlowField field, TileSearch search)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var tile in touched)
        {
            _forced[tile] = true;
        }

        SearchNodes(search);
        Pass(field, search, whole: false);
        Milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>The goals' cells in the grid's window (each under its goal, or the edge cell nearest it), tile by tile.</summary>
    private void FindGoals(NavGrid grid)
    {
        var goals = _spec.Goals;
        Span<int> cells = goals.Length <= 1024 ? stackalloc int[goals.Length] : new int[goals.Length];
        for (var i = 0; i < goals.Length; i++)
        {
            cells[i] = grid.NearestCell(goals[i].X, goals[i].Z);
        }

        Array.Clear(_goalFirst);
        var distinct = 0;
        cells.Sort();
        for (var i = 0; i < cells.Length; i++)
        {
            if (i == 0 || cells[i] != cells[i - 1])
            {
                cells[distinct++] = cells[i];
                _goalFirst[_tiles.TileOfCell(cells[i]) + 1]++;
            }
        }

        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            _goalFirst[tile + 1] += _goalFirst[tile];
        }

        if (_goalCells.Length != distinct)
        {
            _goalCells = new int[distinct];
        }

        Span<int> next = _tiles.Count <= 4096 ? stackalloc int[_tiles.Count] : new int[_tiles.Count];
        _goalFirst.AsSpan(0, _tiles.Count).CopyTo(next);
        for (var i = 0; i < distinct; i++)
        {
            _goalCells[next[_tiles.TileOfCell(cells[i])]++] = cells[i];
        }
    }

    private ReadOnlySpan<int> GoalsIn(int tile) =>
        _goalCells.AsSpan(_goalFirst[tile], _goalFirst[tile + 1] - _goalFirst[tile]);

    /// <summary>Each node's cost to the goals, by Dijkstra's search over the portals' graph from the goals' tiles.</summary>
    private void SearchNodes(TileSearch search)
    {
        var portals = _portals;
        var count = portals.NodeCount;
        if (_nodeCost.Length < count)
        {
            _nodeCost = new long[Math.Max(count, _nodeCost.Length * 3 / 2)];
        }

        var cost = _nodeCost;
        cost.AsSpan(0, count).Fill(long.MaxValue);
        _heapCount = 0;
        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            var goals = GoalsIn(tile);
            if (goals.IsEmpty)
            {
                continue;
            }

            search.Begin(_tiles, tile, portals.Steps);
            search.Search(goals, [], []);
            var first = portals.FirstNode(tile);
            for (var node = first; node < first + portals.NodesOf(tile); node++)
            {
                var reached = search.CostOf(portals.Cell(node));
                if (reached != int.MaxValue && reached < cost[node])
                {
                    cost[node] = reached;
                    Push(reached, node, NodeBits);
                }
            }
        }

        while (_heapCount > 0)
        {
            var top = Pop();
            var node = (int)(top & ((1L << NodeBits) - 1));
            var reached = top >> NodeBits;
            if (reached != cost[node])
            {
                continue;
            }

            // Across the portal: the partner steps into this node.
            var partner = portals.Partner(node);
            var cross = portals.CrossIn(node);
            if (cross >= 0 && reached + cross < cost[partner])
            {
                cost[partner] = reached + cross;
                Push(cost[partner], partner, NodeBits);
            }

            // Inside the tile: each other node reaching this one.
            var tile = portals.TileOf(node);
            var first = portals.FirstNode(tile);
            var nodes = portals.NodesOf(tile);
            var intra = portals.Intra(tile);
            var to = node - first;
            for (var from = 0; from < nodes; from++)
            {
                var step = intra[from * nodes + to];
                if (step == int.MaxValue || from == to)
                {
                    continue;
                }

                var other = first + from;
                if (reached + step < cost[other])
                {
                    cost[other] = reached + step;
                    Push(cost[other], other, NodeBits);
                }
            }
        }
    }

    /// <summary>
    /// Brings the tiles' fields to agree: the tiles looked at first are every one (<paramref name="whole"/>), else those
    /// whose cells changed, whose ways out changed, or whose costs moved with their nodes'.
    /// </summary>
    private void Pass(FlowField field, TileSearch search, bool whole)
    {
        var portals = _portals;
        var tiles = _tiles.Count;
        Array.Clear(_queued);
        _heapCount = 0;
        var moved = 0;
        for (var tile = 0; tile < tiles; tile++)
        {
            var least = GoalsIn(tile).IsEmpty ? long.MaxValue : 0;
            var first = portals.FirstNode(tile);
            ulong exits = 14_695_981_039_346_656_037;
            for (var node = first; node < first + portals.NodesOf(tile); node++)
            {
                least = Math.Min(least, _nodeCost[node]);
                if (IsExit(node))
                {
                    exits =
                        (exits ^ (ulong)(portals.Side(node) * NavTiles.Cells + portals.Start(node)))
                        * 1_099_511_628_211;
                }
            }

            _newLeast[tile] = least;
            var look = whole || _forced[tile] || exits != _exits[tile];
            if (
                !whole
                && _built[tile]
                && least != long.MaxValue
                && _least[tile] != long.MaxValue
                && least != _least[tile]
            )
            {
                // Costs from before moved by the nodes' change, so they stand near their new values.
                var delta = least - _least[tile];
                Shift(tile, delta);
                var seeds = _seeds.AsSpan(tile * NavTiles.Slots, NavTiles.Slots);
                for (var i = 0; i < seeds.Length; i++)
                {
                    if (seeds[i] >= 0)
                    {
                        seeds[i] = Clamp(seeds[i] + delta);
                    }
                }

                look = true;
            }

            _least[tile] = least;
            _exits[tile] = exits;
            if (look)
            {
                Enqueue(tile);
            }
        }

        var looks = 0;
        var built = 0;
        var cap = (long)MostLooksPerTile * tiles;
        while (_heapCount > 0)
        {
            if (++looks > cap)
            {
                break;
            }

            var tile = (int)(Pop() & ((1L << TileBits) - 1));
            _queued[tile] = false;
            var change = Look(tile, field, search, ref built, ref moved);
            if (!change)
            {
                continue;
            }

            for (var side = 0; side < 4; side++)
            {
                // A neighbour that leaves into this tile is looked at again.
                var neighbour = _tiles.Neighbour(tile, side);
                if (neighbour >= 0 && !_queued[neighbour] && SeedsFrom(neighbour, (side + 2) & 3))
                {
                    Enqueue(neighbour);
                }
            }
        }

        Looks = Math.Min(looks, (int)Math.Min(cap, int.MaxValue));
        Built = built;
        Moved = moved;
        Agreed = looks <= cap;
        Array.Clear(_forced);
        if (!Agreed)
        {
            _heapCount = 0;
        }
    }

    /// <summary>
    /// Looks at tile <paramref name="tile"/>: works out its seeds, and searches its field again where they changed but
    /// for one amount, or moves its costs where they all moved by one. True when its costs changed.
    /// </summary>
    private bool Look(int tile, FlowField field, TileSearch search, ref int built, ref int moved)
    {
        var portals = _portals;
        var slots = _slots.AsSpan();
        slots.Fill(-1);
        var count = 0;
        var first = portals.FirstNode(tile);
        for (var node = first; node < first + portals.NodesOf(tile); node++)
        {
            if (!IsExit(node))
            {
                continue;
            }

            var partner = portals.Partner(node);
            var side = portals.Side(node);
            var start = portals.Start(node);
            for (var k = 0; k < portals.Length(node); k++)
            {
                var cell = _tiles.OutsideCell(tile, side, start + k);
                var known = _cost[cell];
                var seed = known != int.MaxValue ? known : Clamp(_nodeCost[partner]);
                slots[side * NavTiles.Cells + start + k] = seed;
                _seedCells[count] = cell;
                _seedCosts[count++] = seed;
            }
        }

        var stored = _seeds.AsSpan(tile * NavTiles.Slots, NavTiles.Slots);
        var forced = _forced[tile];
        _forced[tile] = false;
        if (_built[tile] && !forced)
        {
            var same = true;
            var uniform = GoalsIn(tile).IsEmpty;
            long delta = 0;
            var any = false;
            for (var i = 0; i < slots.Length && (same || uniform); i++)
            {
                if ((slots[i] < 0) != (stored[i] < 0))
                {
                    same = false;
                    uniform = false;
                    break;
                }

                if (slots[i] < 0)
                {
                    continue;
                }

                var difference = (long)slots[i] - stored[i];
                if (!any)
                {
                    delta = difference;
                    any = true;
                }

                same &= difference == 0;
                uniform &= difference == delta;
            }

            if (same)
            {
                return false;
            }

            if (uniform)
            {
                Shift(tile, delta);
                slots.CopyTo(stored);
                moved++;
                return true;
            }
        }

        search.Begin(_tiles, tile, portals.Steps);
        search.Search(GoalsIn(tile), _seedCells.AsSpan(0, count), _seedCosts.AsSpan(0, count));
        var x0 = _tiles.FirstColumn(tile);
        var z0 = _tiles.FirstRow(tile);
        var width = _tiles.TileWidth(tile);
        var directions = field.Directions;
        var costs = search.Cost;
        var ways = search.Direction;
        for (var lz = 0; lz < _tiles.TileDepth(tile); lz++)
        {
            var row = (z0 + lz) * _tiles.Width + x0;
            costs.Slice(lz * NavTiles.Cells, width).CopyTo(_cost.AsSpan(row, width));
            ways.Slice(lz * NavTiles.Cells, width).CopyTo(directions.AsSpan(row, width));
        }

        slots.CopyTo(stored);
        if (!_wroteAll && !_isWritten[tile])
        {
            _isWritten[tile] = true;
            _written.Add(tile);
        }

        _built[tile] = true;
        built++;
        return true;
    }

    /// <summary>True when node <paramref name="node"/>'s portal is a way out of its tile: the far node costs less.</summary>
    private bool IsExit(int node)
    {
        var far = _nodeCost[_portals.Partner(node)];
        return far != long.MaxValue && far < _nodeCost[node];
    }

    /// <summary>True when tile <paramref name="tile"/> was seeded from across its side <paramref name="side"/>.</summary>
    private bool SeedsFrom(int tile, int side)
    {
        var seeds = _seeds.AsSpan(tile * NavTiles.Slots + side * NavTiles.Cells, NavTiles.Cells);
        foreach (var seed in seeds)
        {
            if (seed >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Moves tile <paramref name="tile"/>'s known costs by <paramref name="delta"/>.</summary>
    private void Shift(int tile, long delta)
    {
        var x0 = _tiles.FirstColumn(tile);
        var z0 = _tiles.FirstRow(tile);
        var width = _tiles.TileWidth(tile);
        for (var lz = 0; lz < _tiles.TileDepth(tile); lz++)
        {
            var row = _cost.AsSpan((z0 + lz) * _tiles.Width + x0, width);
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] != int.MaxValue)
                {
                    row[i] = Clamp(row[i] + delta);
                }
            }
        }
    }

    private void Enqueue(int tile)
    {
        _queued[tile] = true;
        var least = _newLeast[tile] == long.MaxValue ? (1L << 46) : Math.Min(_newLeast[tile], (1L << 46) - 1);
        Push(least, tile, TileBits);
    }

    private void CopyTile(byte[] from, byte[] to, int tile)
    {
        var x0 = _tiles.FirstColumn(tile);
        var z0 = _tiles.FirstRow(tile);
        var width = _tiles.TileWidth(tile);
        for (var lz = 0; lz < _tiles.TileDepth(tile); lz++)
        {
            var row = (z0 + lz) * _tiles.Width + x0;
            Array.Copy(from, row, to, row, width);
        }
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue - 1);

    private void Push(long cost, int item, int bits)
    {
        if (_heapCount == _heap.Length)
        {
            Array.Resize(ref _heap, _heap.Length * 2);
        }

        var key = (cost << bits) | (uint)item;
        var i = _heapCount++;
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            if (_heap[parent] <= key)
            {
                break;
            }

            _heap[i] = _heap[parent];
            i = parent;
        }

        _heap[i] = key;
    }

    private long Pop()
    {
        var top = _heap[0];
        var last = _heap[--_heapCount];
        var i = 0;
        while (true)
        {
            var child = 2 * i + 1;
            if (child >= _heapCount)
            {
                break;
            }

            if (child + 1 < _heapCount && _heap[child + 1] < _heap[child])
            {
                child++;
            }

            if (_heap[child] >= last)
            {
                break;
            }

            _heap[i] = _heap[child];
            i = child;
        }

        if (_heapCount > 0)
        {
            _heap[i] = last;
        }

        return top;
    }
}
