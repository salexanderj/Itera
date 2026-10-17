namespace Itera.Engine.Navigation;

/// <summary>
/// A clearance size for walkers, in centimetres: the height that must fit under the headroom, the radius of the
/// body, and the highest rise and the deepest drop between neighbouring cells that count as walking (straight and
/// diagonal; a drop has no limit unless one is given). A cell is walkable for the size when it and every cell the body's
/// circle overlaps from its centre have the headroom.
/// </summary>
public sealed class NavSize
{
    private readonly int[] _offsetX;
    private readonly int[] _offsetZ;

    public NavSize(
        string name,
        int heightCentimetres,
        int radiusCentimetres,
        int climbCentimetres,
        int diagonalClimbCentimetres,
        int dropCentimetres = NoDropLimit,
        int diagonalDropCentimetres = NoDropLimit
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightCentimetres);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(heightCentimetres, NavGrid.MostClear);
        ArgumentOutOfRangeException.ThrowIfNegative(radiusCentimetres);
        ArgumentOutOfRangeException.ThrowIfNegative(dropCentimetres);
        ArgumentOutOfRangeException.ThrowIfNegative(diagonalDropCentimetres);
        Name = name;
        HeightCentimetres = heightCentimetres;
        RadiusCentimetres = radiusCentimetres;
        ClimbCentimetres = climbCentimetres;
        DiagonalClimbCentimetres = diagonalClimbCentimetres;
        DropCentimetres = dropCentimetres;
        DiagonalDropCentimetres = diagonalDropCentimetres;

        // A neighbouring cell counts when its nearest point to this cell's centre lies inside the radius.
        var reach = (radiusCentimetres + NavGrid.CellCentimetres / 2) / NavGrid.CellCentimetres + 1;
        var xs = new List<int>();
        var zs = new List<int>();
        for (var dz = -reach; dz <= reach; dz++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var nearX = Math.Max(0, Math.Abs(dx) * NavGrid.CellCentimetres - NavGrid.CellCentimetres / 2);
                var nearZ = Math.Max(0, Math.Abs(dz) * NavGrid.CellCentimetres - NavGrid.CellCentimetres / 2);
                if ((dx == 0 && dz == 0) || nearX * nearX + nearZ * nearZ < radiusCentimetres * radiusCentimetres)
                {
                    xs.Add(dx);
                    zs.Add(dz);
                }
            }
        }

        _offsetX = [.. xs];
        _offsetZ = [.. zs];
        ReachCells = xs.Max(Math.Abs);
    }

    /// <summary>
    /// How many cells the body's circle reaches past its own each way: a cell changed in one row changes which cells this
    /// size can stand in up to this many rows away.
    /// </summary>
    public int ReachCells { get; }

    /// <summary>A name for logs and the debug picture.</summary>
    public string Name { get; }

    /// <summary>Height that must fit under a cell's headroom.</summary>
    public int HeightCentimetres { get; }

    /// <summary>Radius of the body.</summary>
    public int RadiusCentimetres { get; }

    /// <summary>Highest rise onto a straight neighbour that is still walking.</summary>
    public int ClimbCentimetres { get; }

    /// <summary>Highest rise onto a diagonal neighbour that is still walking.</summary>
    public int DiagonalClimbCentimetres { get; }

    /// <summary>
    /// Deepest drop onto a straight neighbour that is still walking (<see cref="NoDropLimit"/>: any); a deeper one costs
    /// what a rise past the climb does, so routes go round a pit the walker would not climb back out of.
    /// </summary>
    public int DropCentimetres { get; }

    /// <summary>Deepest drop onto a diagonal neighbour that is still walking (<see cref="NoDropLimit"/>: any).</summary>
    public int DiagonalDropCentimetres { get; }

    /// <summary>A drop limit that lets a walker drop any depth.</summary>
    public const int NoDropLimit = int.MaxValue;

    /// <summary>How many cells the body's circle covers, its own included.</summary>
    public int FootprintCells => _offsetX.Length;

    /// <summary>True when cell <paramref name="cell"/> alone has the headroom for this size.</summary>
    public bool Fits(NavGrid grid, int cell) => grid.Clear[cell] * NavGrid.ClearUnit >= HeightCentimetres;

    /// <summary>
    /// True when a body of this size can stand at the centre of cell <paramref name="cell"/>: every cell its circle
    /// covers has the headroom. Cells past the grid's edge do not count against it.
    /// </summary>
    public bool IsWalkable(NavGrid grid, int cell)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var column = grid.Column(cell);
        var row = grid.Row(cell);
        for (var i = 0; i < _offsetX.Length; i++)
        {
            var x = column + _offsetX[i];
            var z = row + _offsetZ[i];
            if (x >= 0 && x < grid.Width && z >= 0 && z < grid.Depth && !Fits(grid, grid.Index(x, z)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes into <paramref name="walkable"/> (one byte per cell, 1 for walkable) which cells of rows
    /// <paramref name="firstRow"/> to <paramref name="lastRow"/> this size can stand in.
    /// </summary>
    public void Derive(NavGrid grid, byte[] walkable, int firstRow, int lastRow)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(walkable);
        for (var row = firstRow; row <= lastRow; row++)
        {
            var start = row * grid.Width;
            for (var column = 0; column < grid.Width; column++)
            {
                walkable[start + column] = IsWalkable(grid, start + column) ? (byte)1 : (byte)0;
            }
        }
    }
}
