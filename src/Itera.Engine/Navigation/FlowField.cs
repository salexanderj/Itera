namespace Itera.Engine.Navigation;

/// <summary>
/// A flow field over a <see cref="NavGrid"/>: for every cell, the neighbour to step into on the cheapest route to the
/// field's goal (<see cref="FlowFieldBuilder"/>). Directions 0 to 7 name the neighbours counter-clockwise from +x;
/// <see cref="Goal"/> marks a goal cell and <see cref="None"/> a cell with no route.
/// </summary>
public sealed class FlowField
{
    /// <summary>A goal cell: the route ends here.</summary>
    public const byte Goal = 8;

    /// <summary>No route from this cell.</summary>
    public const byte None = byte.MaxValue;

    private static readonly int[] s_stepX = [1, 1, 0, -1, -1, -1, 0, 1];
    private static readonly int[] s_stepZ = [0, 1, 1, 1, 0, -1, -1, -1];

    /// <summary>Column step of each direction.</summary>
    public static ReadOnlySpan<int> StepX => s_stepX;

    /// <summary>Row step of each direction.</summary>
    public static ReadOnlySpan<int> StepZ => s_stepZ;

    public FlowField(NavGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Grid = grid;
        Directions = new byte[grid.Count];
        Directions.AsSpan().Fill(None);
    }

    /// <summary>The grid the field's cells belong to (for its size only; the field reads no cell data).</summary>
    public NavGrid Grid { get; }

    /// <summary>Each cell's direction.</summary>
    public byte[] Directions { get; }

    /// <summary>True when direction <paramref name="direction"/> is diagonal.</summary>
    public static bool IsDiagonal(int direction) => (direction & 1) != 0;

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

        return cell + StepZ[direction] * Grid.Width + StepX[direction];
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
}
