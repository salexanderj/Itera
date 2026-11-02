namespace Itera.Engine.Navigation;

/// <summary>
/// The portals between a window's tiles (<see cref="NavTiles"/>) for one size and cost table, and what crossing each tile
/// between them costs. Each edge between two tiles is cut into runs of at most <see cref="MostRun"/> cells whose
/// crossings are of one kind (<see cref="NavStepCosts.Kind"/>); a run is a portal, with a node on each side of it at the
/// run's middle cell. Each tile keeps the cost from every one of its nodes to every other inside it (<see cref="Intra"/>),
/// and each node what stepping into it from its partner across the portal costs (<see cref="CrossIn"/>). Fields of this
/// size and cost table search this graph for each node's cost to their goals (<see cref="TiledField"/>). Built and
/// changed on the navigation thread; read by the fields' builds after.
/// </summary>
internal sealed class NavPortals
{
    /// <summary>
    /// The longest portal, in cells: 16 (8 m). On Zorf TD's valley, against runs of 8, it halved the nodes and the costs
    /// inside tiles, and a field's routes cost within 1.2 % of exact for 99 % of cells, against 0.1 %.
    /// </summary>
    public const int MostRun = 16;

    private readonly NavTiles _tiles;
    private readonly NavStepCosts _steps;

    // Each edge's runs: their first place along it and their length, and how many.
    private readonly byte[] _runStart;
    private readonly byte[] _runLength;
    private readonly byte[] _runCount;

    // Nodes, tile by tile, each tile's side by side (east, north, west, south), each side's runs in order.
    private readonly int[] _tileFirst;
    private readonly int[] _sideFirst;
    private int[] _nodeCell = [];
    private int[] _nodePartner = [];
    private int[] _nodeTile = [];
    private byte[] _nodeSide = [];
    private byte[] _nodeStart = [];
    private byte[] _nodeLength = [];
    private int[] _crossIn = [];
    private int _nodeCount;

    // Each tile's costs from node to node inside it, [from * count + to]; int.MaxValue for none.
    private int[][] _intra;
    private int[] _intraCount;
    private int[][] _spareIntra;
    private int[] _spareCount;
    private readonly bool[] _layoutChanged;
    private readonly List<int> _intraWanted = [];
    private readonly byte[] _oldStart = new byte[NavTiles.Cells];
    private readonly byte[] _oldLength = new byte[NavTiles.Cells];

    public NavPortals(NavTiles tiles, NavStepCosts steps)
    {
        _tiles = tiles;
        _steps = steps;
        _runStart = new byte[tiles.Edges * NavTiles.Cells];
        _runLength = new byte[tiles.Edges * NavTiles.Cells];
        _runCount = new byte[tiles.Edges];
        _tileFirst = new int[tiles.Count + 1];
        _sideFirst = new int[tiles.Count * 4 + 1];
        _intra = new int[tiles.Count][];
        _intraCount = new int[tiles.Count];
        _spareIntra = new int[tiles.Count][];
        _spareCount = new int[tiles.Count];
        _layoutChanged = new bool[tiles.Count];
    }

    /// <summary>The tiles.</summary>
    public NavTiles Tiles => _tiles;

    /// <summary>The steps' costs over the grid's cells for this size and cost table.</summary>
    public NavStepCosts Steps => _steps;

    /// <summary>The nodes.</summary>
    public int NodeCount => _nodeCount;

    /// <summary>The tiles whose costs inside wait to be worked out (<see cref="ComputeIntra"/>), after <see cref="Lay"/>.</summary>
    public IReadOnlyList<int> IntraWanted => _intraWanted;

    /// <summary>The first of tile <paramref name="tile"/>'s nodes.</summary>
    public int FirstNode(int tile) => _tileFirst[tile];

    /// <summary>How many nodes tile <paramref name="tile"/> has.</summary>
    public int NodesOf(int tile) => _tileFirst[tile + 1] - _tileFirst[tile];

    /// <summary>Node <paramref name="node"/>'s cell in the window: its run's middle cell, inside its tile.</summary>
    public int Cell(int node) => _nodeCell[node];

    /// <summary>The node across the portal from node <paramref name="node"/>.</summary>
    public int Partner(int node) => _nodePartner[node];

    /// <summary>Node <paramref name="node"/>'s tile.</summary>
    public int TileOf(int node) => _nodeTile[node];

    /// <summary>The side of its tile node <paramref name="node"/>'s portal lies on (0 east to 3 south).</summary>
    public int Side(int node) => _nodeSide[node];

    /// <summary>The first place along its tile's side of node <paramref name="node"/>'s run.</summary>
    public int Start(int node) => _nodeStart[node];

    /// <summary>The cells of node <paramref name="node"/>'s run.</summary>
    public int Length(int node) => _nodeLength[node];

    /// <summary>What stepping into node <paramref name="node"/>'s cell from its partner's costs; -1 for no step.</summary>
    public int CrossIn(int node) => _crossIn[node];

    /// <summary>Tile <paramref name="tile"/>'s costs from node to node, <c>[from * count + to]</c>, by its nodes' order.</summary>
    public ReadOnlySpan<int> Intra(int tile) => _intra[tile].AsSpan(0, _intraCount[tile] * _intraCount[tile]);

    /// <summary>
    /// Cuts every edge into portals and lays out the nodes, every tile's costs inside wanted (<see cref="IntraWanted"/>):
    /// for a window filled whole.
    /// </summary>
    public void LayAll()
    {
        for (var edge = 0; edge < _tiles.Edges; edge++)
        {
            CutEdge(edge);
        }

        Array.Fill(_layoutChanged, true);
        Flatten();
        _intraWanted.Clear();
        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            _intraWanted.Add(tile);
        }
    }

    /// <summary>
    /// After the window moved by <paramref name="shiftX"/>, <paramref name="shiftZ"/> tiles (its new corner that many
    /// tiles from the old one, so a tile's cells were the old tile's that many further on), with the cells both windows
    /// hold kept: cuts every edge again and lays out the nodes. A tile keeps its costs inside where it and each tile beside
    /// it held cells in the old window too, and its cells did not change (<paramref name="touched"/>, in the new window):
    /// its cells and its edges' runs are the old tile's. The rest want theirs worked out again (<see cref="IntraWanted"/>).
    /// </summary>
    public void LayMoved(int shiftX, int shiftZ, ReadOnlySpan<int> touched)
    {
        var tilesX = _tiles.TilesX;
        var tilesZ = _tiles.TilesZ;
        Array.Fill(_layoutChanged, false);
        foreach (var tile in touched)
        {
            _layoutChanged[tile] = true;
        }

        // Each tile's old self, its costs inside moved with it; the old tiles no tile keeps lend their arrays.
        var lent = 0;
        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            var old = Old(tile);
            _spareIntra[tile] = old >= 0 ? _intra[old] : null!;
            _spareCount[tile] = old >= 0 ? _intraCount[old] : 0;
        }

        for (var old = 0; old < _tiles.Count; old++)
        {
            var x = old % tilesX - shiftX;
            var z = old / tilesX - shiftZ;
            if (x < 0 || x >= tilesX || z < 0 || z >= tilesZ)
            {
                // Kept by no tile: its array goes to the next tile that has none.
                while (lent < _tiles.Count && _spareIntra[lent] is not null)
                {
                    lent++;
                }

                if (lent < _tiles.Count)
                {
                    _spareIntra[lent++] = _intra[old];
                }
            }
        }

        (_intra, _spareIntra) = (_spareIntra, _intra);
        (_intraCount, _spareCount) = (_spareCount, _intraCount);
        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            if (Old(tile) < 0)
            {
                _layoutChanged[tile] = true;
                continue;
            }

            for (var side = 0; side < 4; side++)
            {
                // A tile on the window's border keeps nothing either: cells past a grid's edge count for no size, so
                // its walkable cells by the border may differ from the old window's.
                var neighbour = _tiles.Neighbour(tile, side);
                if (neighbour < 0 || Old(neighbour) < 0)
                {
                    _layoutChanged[tile] = true;
                }
            }
        }

        for (var edge = 0; edge < _tiles.Edges; edge++)
        {
            CutEdge(edge);
        }

        Flatten();
        _intraWanted.Clear();
        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            if (_layoutChanged[tile])
            {
                _intraWanted.Add(tile);
            }
        }

        // The old tile whose cells tile t holds now, or -1 for one in a strip coming in.
        int Old(int t)
        {
            var x = t % tilesX + shiftX;
            var z = t / tilesX + shiftZ;
            return x < 0 || x >= tilesX || z < 0 || z >= tilesZ ? -1 : z * tilesX + x;
        }
    }

    /// <summary>
    /// After the cells of <paramref name="touched"/> tiles changed (with their walkable cells): cuts their edges again
    /// and lays out the nodes; the tiles touched, and those whose portals changed, want their costs inside worked out
    /// again (<see cref="IntraWanted"/>).
    /// </summary>
    public void Lay(ReadOnlySpan<int> touched)
    {
        Array.Clear(_layoutChanged);
        foreach (var tile in touched)
        {
            for (var side = 0; side < 4; side++)
            {
                var edge = _tiles.EdgeOf(tile, side);
                if (edge >= 0 && CutEdge(edge))
                {
                    _layoutChanged[tile] = true;
                    _layoutChanged[_tiles.Neighbour(tile, side)] = true;
                }
            }
        }

        Flatten();
        _intraWanted.Clear();
        foreach (var tile in touched)
        {
            _layoutChanged[tile] = true;
        }

        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            if (_layoutChanged[tile])
            {
                _intraWanted.Add(tile);
            }
        }
    }

    /// <summary>Works out tile <paramref name="tile"/>'s costs from node to node inside it, with <paramref name="search"/>.</summary>
    public void ComputeIntra(int tile, TileSearch search)
    {
        var first = _tileFirst[tile];
        var count = _tileFirst[tile + 1] - first;
        var size = count * count;
        if (_intra[tile] is null || _intra[tile].Length < size)
        {
            _intra[tile] = new int[Math.Max(size, 16)];
        }

        _intraCount[tile] = count;
        var intra = _intra[tile];
        if (count == 0)
        {
            return;
        }

        search.Begin(_tiles, tile, _steps);
        Span<int> from = stackalloc int[1];
        for (var to = 0; to < count; to++)
        {
            from[0] = _nodeCell[first + to];
            search.Search(from, [], []);
            for (var node = 0; node < count; node++)
            {
                intra[node * count + to] = search.CostOf(_nodeCell[first + node]);
            }
        }
    }

    /// <summary>Cuts edge <paramref name="edge"/> into runs of one crossing kind; true when its runs changed.</summary>
    private bool CutEdge(int edge)
    {
        // The tile west or south of the edge, and its side facing the other.
        int tile;
        int side;
        if (edge < _tiles.VerticalEdges)
        {
            var rowTiles = _tiles.TilesX - 1;
            tile = edge / rowTiles * _tiles.TilesX + edge % rowTiles;
            side = 0;
        }
        else
        {
            tile = edge - _tiles.VerticalEdges;
            side = 1;
        }

        var length = _tiles.SideLength(tile, side);
        var across = NavTiles.Across(side);
        var at = edge * NavTiles.Cells;
        var oldCount = _runCount[edge];
        Array.Copy(_runStart, at, _oldStart, 0, oldCount);
        Array.Copy(_runLength, at, _oldLength, 0, oldCount);
        var count = 0;
        var along = 0;
        while (along < length)
        {
            var kind = _steps.Kind(_tiles.InsideCell(tile, side, along), _tiles.OutsideCell(tile, side, along), across);
            var run = 1;
            while (
                along + run < length
                && run < MostRun
                && _steps.Kind(
                    _tiles.InsideCell(tile, side, along + run),
                    _tiles.OutsideCell(tile, side, along + run),
                    across
                ) == kind
            )
            {
                run++;
            }

            _runStart[at + count] = (byte)along;
            _runLength[at + count] = (byte)run;
            count++;
            along += run;
        }

        _runCount[edge] = (byte)count;
        if (count != oldCount)
        {
            return true;
        }

        for (var i = 0; i < count; i++)
        {
            if (_runStart[at + i] != _oldStart[i] || _runLength[at + i] != _oldLength[i])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Numbers the nodes tile by tile from the edges' runs, with their cells, partners, and crossings in.</summary>
    private void Flatten()
    {
        var total = 0;
        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            _tileFirst[tile] = total;
            for (var side = 0; side < 4; side++)
            {
                _sideFirst[tile * 4 + side] = total;
                var edge = _tiles.EdgeOf(tile, side);
                total += edge >= 0 ? _runCount[edge] : 0;
            }
        }

        _tileFirst[_tiles.Count] = total;
        _sideFirst[_tiles.Count * 4] = total;
        if (_nodeCell.Length < total)
        {
            var size = Math.Max(total, _nodeCell.Length * 3 / 2);
            _nodeCell = new int[size];
            _nodePartner = new int[size];
            _nodeTile = new int[size];
            _nodeSide = new byte[size];
            _nodeStart = new byte[size];
            _nodeLength = new byte[size];
            _crossIn = new int[size];
        }

        _nodeCount = total;
        for (var tile = 0; tile < _tiles.Count; tile++)
        {
            for (var side = 0; side < 4; side++)
            {
                var edge = _tiles.EdgeOf(tile, side);
                if (edge < 0)
                {
                    continue;
                }

                var neighbour = _tiles.Neighbour(tile, side);
                var opposite = (side + 2) & 3;
                var into = (NavTiles.Across(side) + 4) & 7;
                var first = _sideFirst[tile * 4 + side];
                var partnerFirst = _sideFirst[neighbour * 4 + opposite];
                var at = edge * NavTiles.Cells;
                for (var run = 0; run < _runCount[edge]; run++)
                {
                    var node = first + run;
                    var start = _runStart[at + run];
                    var length = _runLength[at + run];
                    var middle = start + length / 2;
                    var cell = _tiles.InsideCell(tile, side, middle);
                    var outside = _tiles.OutsideCell(tile, side, middle);
                    _nodeCell[node] = cell;
                    _nodePartner[node] = partnerFirst + run;
                    _nodeTile[node] = tile;
                    _nodeSide[node] = (byte)side;
                    _nodeStart[node] = start;
                    _nodeLength[node] = length;
                    _crossIn[node] = _steps.Step(outside, cell, into);
                }
            }
        }
    }
}
