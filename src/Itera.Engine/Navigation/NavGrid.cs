using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Navigation;

/// <summary>
/// The navigation grid: a window of square cells <see cref="CellCentimetres"/> centimetres wide over the world (the whole
/// map where it fits, else a square that moves with the players, <see cref="NavigationService"/>), each with the floor a
/// walker stands on at its centre (absolute centimetres), the headroom above that floor, and a cost class the game
/// defines. A cell with no headroom is blocked: something solid rises too far above the ground there to step onto.
/// Cells are numbered row by row, x fastest, from the window's corner (<see cref="OriginX"/>, <see cref="OriginZ"/>,
/// absolute and a whole number of cells from the absolute 0). Arrays are public so the builder, the scanner, and the flow
/// field read them without copies; one thread writes a grid at a time.
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
        Floor = new int[width * depth];
        Clear = new byte[width * depth];
        Class = new byte[width * depth];
    }

    /// <summary>The absolute x of the window's west edge, in centimetres.</summary>
    public int OriginX { get; private set; }

    /// <summary>The absolute z of the window's south edge, in centimetres.</summary>
    public int OriginZ { get; private set; }

    /// <summary>Cells along x.</summary>
    public int Width { get; }

    /// <summary>Cells along z.</summary>
    public int Depth { get; }

    /// <summary>Number of cells.</summary>
    public int Count => Floor.Length;

    /// <summary>Each cell's floor: the absolute y in centimetres a walker's feet stand at (the top of the ground).</summary>
    public int[] Floor { get; }

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

    /// <summary>
    /// The cell nearest the absolute point (<paramref name="x"/>, <paramref name="z"/>) in centimetres: the one under it,
    /// or past the window's edge the edge cell nearest it (a goal outside the window stands in at its border).
    /// </summary>
    public int NearestCell(int x, int z) =>
        Index(
            Math.Clamp(FloorDivide(x - OriginX, CellCentimetres), 0, Width - 1),
            Math.Clamp(FloorDivide(z - OriginZ, CellCentimetres), 0, Depth - 1)
        );

    /// <summary>The cell under the local position <paramref name="metres"/>; false outside the grid.</summary>
    public bool TryCell(WorldOrigin origin, Vector3 metres, out int cell)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var at = origin.ToCentimetres(metres);
        return TryCell(at.X, at.Z, out cell);
    }

    /// <summary>The centre of cell <paramref name="cell"/> at its floor, local to <paramref name="origin"/>.</summary>
    public Vector3 CentreMetres(WorldOrigin origin, int cell)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin.ToLocal(new VoxelCoord(CentreX(Column(cell)), Floor[cell], CentreZ(Row(cell))));
    }

    /// <summary>
    /// The floor's local height under the local point (<paramref name="xMetres"/>, <paramref name="zMetres"/>), blended
    /// between the four nearest cell centres, so a walker's feet rise and fall smoothly instead of stepping at cell
    /// edges. Outside the grid the nearest edge cells are used.
    /// </summary>
    public float FloorMetres(WorldOrigin origin, float xMetres, float zMetres)
    {
        ArgumentNullException.ThrowIfNull(origin);
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        var u = (origin.WorldX(xMetres) * perMetre - OriginX) / CellCentimetres - 0.5;
        var v = (origin.WorldZ(zMetres) * perMetre - OriginZ) / CellCentimetres - 0.5;
        var column = (int)Math.Floor(u);
        var row = (int)Math.Floor(v);
        var fu = u - column;
        var fv = v - row;
        var c0 = Math.Clamp(column, 0, Width - 1);
        var c1 = Math.Clamp(column + 1, 0, Width - 1);
        var r0 = Math.Clamp(row, 0, Depth - 1);
        var r1 = Math.Clamp(row + 1, 0, Depth - 1);
        var south = Floor[Index(c0, r0)] + (Floor[Index(c1, r0)] - Floor[Index(c0, r0)]) * fu;
        var north = Floor[Index(c0, r1)] + (Floor[Index(c1, r1)] - Floor[Index(c0, r1)]) * fu;
        return (float)((south + (north - south) * fv - origin.Centimetres.Y) / perMetre);
    }

    /// <summary>Moves the window's corner to the absolute (<paramref name="originX"/>, <paramref name="originZ"/>); the cells keep their values.</summary>
    internal void MoveTo(int originX, int originZ) => (OriginX, OriginZ) = (originX, originZ);

    /// <summary>
    /// Copies every cell of <paramref name="source"/> (a window of the same size elsewhere) that lies in this window too,
    /// to its place here; the rest of this window is left as it was.
    /// </summary>
    public void CopyOverlap(NavGrid source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width != Width || source.Depth != Depth)
        {
            throw new ArgumentException("The grids differ in size.", nameof(source));
        }

        // Cells from this window's corner to the source's.
        var dx = (source.OriginX - OriginX) / CellCentimetres;
        var dz = (source.OriginZ - OriginZ) / CellCentimetres;
        var firstColumn = Math.Max(0, dx);
        var lastColumn = Math.Min(Width - 1, Width - 1 + dx);
        if (firstColumn > lastColumn)
        {
            return;
        }

        var length = lastColumn - firstColumn + 1;
        for (var row = Math.Max(0, dz); row <= Math.Min(Depth - 1, Depth - 1 + dz); row++)
        {
            var to = Index(firstColumn, row);
            var from = source.Index(firstColumn - dx, row - dz);
            source.Floor.AsSpan(from, length).CopyTo(Floor.AsSpan(to, length));
            source.Clear.AsSpan(from, length).CopyTo(Clear.AsSpan(to, length));
            source.Class.AsSpan(from, length).CopyTo(Class.AsSpan(to, length));
        }
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

    internal static int FloorDivide(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
