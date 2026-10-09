using System.Numerics;

namespace Itera.Engine.Navigation;

/// <summary>
/// The navigation grid: one layer of square cells <see cref="CellCentimetres"/> centimetres wide, each with the floor a
/// walker stands on at its centre, the headroom above that floor, and a cost class the game defines. A cell with no
/// headroom is blocked: something solid rises too far above the ground there to step onto. Cells are numbered row by
/// row, x fastest. Arrays are public so the builder, the scanner, and the flow field read them without copies; one
/// thread writes a grid at a time.
/// </summary>
public sealed class NavGrid
{
    /// <summary>Width of a cell in centimetres: 50 cm.</summary>
    public const int CellCentimetres = 50;

    /// <summary>What <see cref="Clear"/> counts in: 2 cm.</summary>
    public const int ClearUnit = 2;

    /// <summary>The most headroom a cell records: 5.1 m (more is stored as this).</summary>
    public const int MostClear = byte.MaxValue * ClearUnit;

    public NavGrid(int originX, int originZ, int width, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        OriginX = originX;
        OriginZ = originZ;
        Width = width;
        Depth = depth;
        Floor = new short[width * depth];
        Clear = new byte[width * depth];
        Class = new byte[width * depth];
    }

    /// <summary>Level-0 x of the grid's west edge.</summary>
    public int OriginX { get; }

    /// <summary>Level-0 z of the grid's south edge.</summary>
    public int OriginZ { get; }

    /// <summary>Cells along x.</summary>
    public int Width { get; }

    /// <summary>Cells along z.</summary>
    public int Depth { get; }

    /// <summary>Number of cells.</summary>
    public int Count => Floor.Length;

    /// <summary>Each cell's floor: the level-0 y a walker's feet stand at (the first voxel above the ground).</summary>
    public short[] Floor { get; }

    /// <summary>
    /// Each cell's headroom above its floor, in <see cref="ClearUnit"/>s, up to <see cref="MostClear"/>; 0 means blocked.
    /// </summary>
    public byte[] Clear { get; }

    /// <summary>Each cell's cost class, which the game defines and <see cref="NavCostTable"/> prices.</summary>
    public byte[] Class { get; }

    /// <summary>The cell at column <paramref name="x"/>, row <paramref name="z"/>.</summary>
    public int Index(int x, int z) => z * Width + x;

    /// <summary>The column of cell <paramref name="cell"/>.</summary>
    public int Column(int cell) => cell % Width;

    /// <summary>The row of cell <paramref name="cell"/>.</summary>
    public int Row(int cell) => cell / Width;

    /// <summary>Level-0 x of the centre of column <paramref name="column"/>.</summary>
    public int CentreX(int column) => OriginX + column * CellCentimetres + CellCentimetres / 2;

    /// <summary>Level-0 z of the centre of row <paramref name="row"/>.</summary>
    public int CentreZ(int row) => OriginZ + row * CellCentimetres + CellCentimetres / 2;

    /// <summary>True when cell <paramref name="cell"/> is blocked (no headroom).</summary>
    public bool IsBlocked(int cell) => Clear[cell] == 0;

    /// <summary>Headroom of cell <paramref name="cell"/> in centimetres.</summary>
    public int ClearCentimetres(int cell) => Clear[cell] * ClearUnit;

    /// <summary>
    /// The cell under the level-0 point (<paramref name="x"/>, <paramref name="z"/>); false outside the grid.
    /// </summary>
    public bool TryCell(int x, int z, out int cell)
    {
        var column = FloorDivide(x - OriginX, CellCentimetres);
        var row = FloorDivide(z - OriginZ, CellCentimetres);
        if (column < 0 || column >= Width || row < 0 || row >= Depth)
        {
            cell = -1;
            return false;
        }

        cell = Index(column, row);
        return true;
    }

    /// <summary>The cell under a position in metres; false outside the grid.</summary>
    public bool TryCell(Vector3 metres, out int cell) =>
        TryCell((int)MathF.Floor(metres.X * 100), (int)MathF.Floor(metres.Z * 100), out cell);

    /// <summary>The centre of cell <paramref name="cell"/> in metres, at its floor.</summary>
    public Vector3 CentreMetres(int cell) =>
        new(CentreX(Column(cell)) / 100f, Floor[cell] / 100f, CentreZ(Row(cell)) / 100f);

    /// <summary>
    /// The floor under (<paramref name="xMetres"/>, <paramref name="zMetres"/>) in metres, blended between the four
    /// nearest cell centres, so a walker's feet rise and fall smoothly instead of stepping at cell edges. Outside the grid
    /// the nearest edge cells are used.
    /// </summary>
    public float FloorMetres(float xMetres, float zMetres)
    {
        var u = (xMetres * 100 - OriginX) / CellCentimetres - 0.5f;
        var v = (zMetres * 100 - OriginZ) / CellCentimetres - 0.5f;
        var column = (int)MathF.Floor(u);
        var row = (int)MathF.Floor(v);
        var fu = u - column;
        var fv = v - row;
        var c0 = Math.Clamp(column, 0, Width - 1);
        var c1 = Math.Clamp(column + 1, 0, Width - 1);
        var r0 = Math.Clamp(row, 0, Depth - 1);
        var r1 = Math.Clamp(row + 1, 0, Depth - 1);
        var south = Floor[Index(c0, r0)] + (Floor[Index(c1, r0)] - Floor[Index(c0, r0)]) * fu;
        var north = Floor[Index(c0, r1)] + (Floor[Index(c1, r1)] - Floor[Index(c0, r1)]) * fu;
        return (south + (north - south) * fv) / 100f;
    }

    /// <summary>Copies rows <paramref name="firstRow"/> to <paramref name="lastRow"/> of <paramref name="source"/>.</summary>
    public void CopyRows(NavGrid source, int firstRow, int lastRow)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width != Width || source.Depth != Depth)
        {
            throw new ArgumentException("The grids differ in size.", nameof(source));
        }

        var start = firstRow * Width;
        var length = (lastRow - firstRow + 1) * Width;
        source.Floor.AsSpan(start, length).CopyTo(Floor.AsSpan(start, length));
        source.Clear.AsSpan(start, length).CopyTo(Clear.AsSpan(start, length));
        source.Class.AsSpan(start, length).CopyTo(Class.AsSpan(start, length));
    }

    private static int FloorDivide(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
