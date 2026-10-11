using System.Numerics;
using Itera.Engine.Maths;

namespace Itera.Engine.Navigation;

/// <summary>
/// A flow field over a <see cref="NavGrid"/>'s window: for every cell, the neighbour to step into on the cheapest route
/// to the field's goal (<see cref="FlowFieldBuilder"/>). Directions 0 to 7 name the neighbours counter-clockwise from
/// +x; <see cref="Goal"/> marks a goal cell and <see cref="None"/> a cell with no route. It keeps the window it was
/// built over (its corner and size), so a walker reads it in that window whatever the grid has done since: a walker
/// finds its cell with <see cref="TryCell(int, int, out int)"/> and steps from there.
/// </summary>
public sealed class FlowField
{
    /// <summary>A goal cell: the route ends here.</summary>
    public const byte Goal = 8;

    /// <summary>No route from this cell.</summary>
    public const byte None = byte.MaxValue;

    private static readonly int[] s_stepX = [1, 1, 0, -1, -1, -1, 0, 1];
    private static readonly int[] s_stepZ = [0, 1, 1, 1, 0, -1, -1, -1];

    /// <summary>A field over <paramref name="grid"/>'s window, every cell without a route until it is built.</summary>
    public FlowField(NavGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Width = grid.Width;
        Depth = grid.Depth;
        OriginX = grid.OriginX;
        OriginZ = grid.OriginZ;
        Directions = new byte[grid.Count];
        Directions.AsSpan().Fill(None);
    }

    /// <summary>Column step of each direction.</summary>
    public static ReadOnlySpan<int> StepX => s_stepX;

    /// <summary>Row step of each direction.</summary>
    public static ReadOnlySpan<int> StepZ => s_stepZ;

    /// <summary>Cells along x.</summary>
    public int Width { get; }

    /// <summary>Cells along z.</summary>
    public int Depth { get; }

    /// <summary>The absolute x of the west edge of the window it was built over, in centimetres.</summary>
    public int OriginX { get; private set; }

    /// <summary>The absolute z of the south edge of the window it was built over, in centimetres.</summary>
    public int OriginZ { get; private set; }

    /// <summary>Each cell's direction.</summary>
    public byte[] Directions { get; }

    /// <summary>True when direction <paramref name="direction"/> is diagonal.</summary>
    public static bool IsDiagonal(int direction) => (direction & 1) != 0;

    /// <summary>The cell at column <paramref name="column"/>, row <paramref name="row"/>.</summary>
    public int Index(int column, int row) => row * Width + column;

    /// <summary>The column of cell <paramref name="cell"/>.</summary>
    public int Column(int cell) => cell % Width;

    /// <summary>The row of cell <paramref name="cell"/>.</summary>
    public int Row(int cell) => cell / Width;

    /// <summary>The absolute x of the centre of column <paramref name="column"/>, in centimetres.</summary>
    public int CentreX(int column) => OriginX + column * NavGrid.CellCentimetres + NavGrid.CellCentimetres / 2;

    /// <summary>The absolute z of the centre of row <paramref name="row"/>, in centimetres.</summary>
    public int CentreZ(int row) => OriginZ + row * NavGrid.CellCentimetres + NavGrid.CellCentimetres / 2;

    /// <summary>The cell under the absolute point (<paramref name="x"/>, <paramref name="z"/>); false outside the window.</summary>
    public bool TryCell(int x, int z, out int cell)
    {
        var column = NavGrid.FloorDivide(x - OriginX, NavGrid.CellCentimetres);
        var row = NavGrid.FloorDivide(z - OriginZ, NavGrid.CellCentimetres);
        if (column < 0 || column >= Width || row < 0 || row >= Depth)
        {
            cell = -1;
            return false;
        }

        cell = Index(column, row);
        return true;
    }

    /// <summary>The cell under the local position <paramref name="metres"/>; false outside the window.</summary>
    public bool TryCell(WorldOrigin origin, Vector3 metres, out int cell)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var at = origin.ToCentimetres(metres);
        return TryCell(at.X, at.Z, out cell);
    }

    /// <summary>
    /// The cell a walker in <paramref name="cell"/> steps into next; the same cell at a goal or where there is no route.
    /// </summary>
    public int Next(int cell)
    {
        var direction = Directions[cell];
        if (direction >= Goal)
        {
            return cell;
        }

        return cell + StepZ[direction] * Width + StepX[direction];
    }

    /// <summary>The cell <paramref name="steps"/> steps along the route from <paramref name="cell"/>, or where it ends.</summary>
    public int LookAhead(int cell, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            var next = Next(cell);
            if (next == cell)
            {
                break;
            }

            cell = next;
        }

        return cell;
    }

    /// <summary>Takes <paramref name="grid"/>'s window (the same size) as the one the field is built over.</summary>
    internal void BuildOver(NavGrid grid)
    {
        if (grid.Width != Width || grid.Depth != Depth)
        {
            throw new ArgumentException("The grid's window is not the field's size.", nameof(grid));
        }

        OriginX = grid.OriginX;
        OriginZ = grid.OriginZ;
    }
}
