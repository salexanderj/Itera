namespace Itera.Engine.Navigation;

/// <summary>
/// The tiles a navigation window's fields are built in: squares of <see cref="Cells"/> cells from the window's corner,
/// row by row, those on the window's far sides cut short where it ends. Between two tiles side by side lies an edge: a
/// vertical one between a tile and the one east of it, a horizontal one between a tile and the one north of it. A tile's
/// sides are numbered east, north, west, south (0 to 3), and the cells just outside it along a side are its perimeter's
/// slots: the side times <see cref="Cells"/>, plus the place along it.
/// </summary>
internal sealed class NavTiles
{
    /// <summary>Cells along a tile's side: 32, 16 m.</summary>
    public const int Cells = 32;

    /// <summary>The slots of a tile's perimeter: <see cref="Cells"/> on each of its four sides.</summary>
    public const int Slots = 4 * Cells;

    public NavTiles(int width, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        Width = width;
        Depth = depth;
        TilesX = (width + Cells - 1) / Cells;
        TilesZ = (depth + Cells - 1) / Cells;
        VerticalEdges = (TilesX - 1) * TilesZ;
        Edges = VerticalEdges + TilesX * (TilesZ - 1);
    }

    /// <summary>The window's cells along x.</summary>
    public int Width { get; }

    /// <summary>The window's cells along z.</summary>
    public int Depth { get; }

    /// <summary>Tiles along x.</summary>
    public int TilesX { get; }

    /// <summary>Tiles along z.</summary>
    public int TilesZ { get; }

    /// <summary>The tiles.</summary>
    public int Count => TilesX * TilesZ;

    /// <summary>The vertical edges, which come first in the edges' numbers.</summary>
    public int VerticalEdges { get; }

    /// <summary>The edges between tiles side by side.</summary>
    public int Edges { get; }

    /// <summary>The window's column of tile <paramref name="tile"/>'s west side.</summary>
    public int FirstColumn(int tile) => tile % TilesX * Cells;

    /// <summary>The window's row of tile <paramref name="tile"/>'s south side.</summary>
    public int FirstRow(int tile) => tile / TilesX * Cells;

    /// <summary>Tile <paramref name="tile"/>'s cells along x.</summary>
    public int TileWidth(int tile) => Math.Min(Cells, Width - FirstColumn(tile));

    /// <summary>Tile <paramref name="tile"/>'s cells along z.</summary>
    public int TileDepth(int tile) => Math.Min(Cells, Depth - FirstRow(tile));

    /// <summary>The tile holding the window's cell at <paramref name="column"/>, <paramref name="row"/>.</summary>
    public int TileOf(int column, int row) => row / Cells * TilesX + column / Cells;

    /// <summary>The tile holding the window's cell <paramref name="cell"/>.</summary>
    public int TileOfCell(int cell) => TileOf(cell % Width, cell / Width);

    /// <summary>
    /// The edge on side <paramref name="side"/> of tile <paramref name="tile"/> (0 east, 1 north, 2 west, 3 south), or -1
    /// on the window's border.
    /// </summary>
    public int EdgeOf(int tile, int side)
    {
        var x = tile % TilesX;
        var z = tile / TilesX;
        return side switch
        {
            0 => x + 1 < TilesX ? z * (TilesX - 1) + x : -1,
            1 => z + 1 < TilesZ ? VerticalEdges + z * TilesX + x : -1,
            2 => x > 0 ? z * (TilesX - 1) + x - 1 : -1,
            _ => z > 0 ? VerticalEdges + (z - 1) * TilesX + x : -1,
        };
    }

    /// <summary>The tile across side <paramref name="side"/> of tile <paramref name="tile"/>, or -1 on the window's border.</summary>
    public int Neighbour(int tile, int side)
    {
        var x = tile % TilesX;
        var z = tile / TilesX;
        return side switch
        {
            0 => x + 1 < TilesX ? tile + 1 : -1,
            1 => z + 1 < TilesZ ? tile + TilesX : -1,
            2 => x > 0 ? tile - 1 : -1,
            _ => z > 0 ? tile - TilesX : -1,
        };
    }

    /// <summary>The cells along side <paramref name="side"/> of tile <paramref name="tile"/>.</summary>
    public int SideLength(int tile, int side) => (side & 1) == 0 ? TileDepth(tile) : TileWidth(tile);

    /// <summary>
    /// The window's cell inside tile <paramref name="tile"/> at place <paramref name="along"/> of side
    /// <paramref name="side"/> (from its south or west end).
    /// </summary>
    public int InsideCell(int tile, int side, int along)
    {
        var x0 = FirstColumn(tile);
        var z0 = FirstRow(tile);
        return side switch
        {
            0 => (z0 + along) * Width + x0 + TileWidth(tile) - 1,
            1 => (z0 + TileDepth(tile) - 1) * Width + x0 + along,
            2 => (z0 + along) * Width + x0,
            _ => z0 * Width + x0 + along,
        };
    }

    /// <summary>The window's cell just outside tile <paramref name="tile"/>, across side <paramref name="side"/> from <see cref="InsideCell"/>.</summary>
    public int OutsideCell(int tile, int side, int along) =>
        InsideCell(tile, side, along)
        + side switch
        {
            0 => 1,
            1 => Width,
            2 => -1,
            _ => -Width,
        };

    /// <summary>The direction from a cell inside a tile across its side <paramref name="side"/> (<see cref="FlowField.StepX"/>).</summary>
    public static int Across(int side) => side * 2;
}
