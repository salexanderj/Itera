using System.Numerics;
using Itera.Engine.Rendering;

namespace Itera.Engine.Navigation;

/// <summary>
/// Draws a flow field around a point as debug lines: in each cell a short arrow from its centre towards the next cell
/// of its route, 10 cm over its floor, green where entering the cell costs less than four times the cheapest class (a
/// path) and yellow where it costs more; a red cross where the field's size cannot stand; and an upright white line on each goal cell.
/// </summary>
public static class NavigationDebugLines
{
    private const float LiftMetres = 0.1f;
    private const float ArrowMetres = 0.3f;
    private const float HeadMetres = 0.08f;
    private const float CrossMetres = 0.15f;
    private const float GoalMetres = 0.6f;

    private static readonly uint s_cheap = DebugLines.Colour(60, 220, 80);
    private static readonly uint s_dear = DebugLines.Colour(240, 210, 40);
    private static readonly uint s_blocked = DebugLines.Colour(230, 40, 40);
    private static readonly uint s_goal = DebugLines.Colour(255, 255, 255);

    /// <summary>
    /// Adds the lines of every cell of <paramref name="field"/> whose centre lies within
    /// <paramref name="radiusMetres"/> of <paramref name="around"/> (across x and z). Returns how many cells were drawn.
    /// </summary>
    public static int Add(
        DebugLines lines,
        NavGrid grid,
        NavFieldSpec spec,
        FlowField field,
        Vector3 around,
        float radiusMetres
    )
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(field);
        var firstColumn = Math.Max(
            0,
            (int)MathF.Floor(((around.X - radiusMetres) * 100 - grid.OriginX) / NavGrid.CellCentimetres)
        );
        var lastColumn = Math.Min(
            grid.Width - 1,
            (int)MathF.Floor(((around.X + radiusMetres) * 100 - grid.OriginX) / NavGrid.CellCentimetres)
        );
        var firstRow = Math.Max(
            0,
            (int)MathF.Floor(((around.Z - radiusMetres) * 100 - grid.OriginZ) / NavGrid.CellCentimetres)
        );
        var lastRow = Math.Min(
            grid.Depth - 1,
            (int)MathF.Floor(((around.Z + radiusMetres) * 100 - grid.OriginZ) / NavGrid.CellCentimetres)
        );
        var radiusSquared = radiusMetres * radiusMetres;
        var drawn = 0;
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var cell = grid.Index(column, row);
                var centre = grid.CentreMetres(cell);
                var dx = centre.X - around.X;
                var dz = centre.Z - around.Z;
                if (dx * dx + dz * dz > radiusSquared)
                {
                    continue;
                }

                var at = centre + new Vector3(0, LiftMetres, 0);
                drawn++;
                if (!spec.Size.IsWalkable(grid, cell))
                {
                    lines.Add(
                        at + new Vector3(-CrossMetres, 0, -CrossMetres),
                        at + new Vector3(CrossMetres, 0, CrossMetres),
                        s_blocked
                    );
                    lines.Add(
                        at + new Vector3(-CrossMetres, 0, CrossMetres),
                        at + new Vector3(CrossMetres, 0, -CrossMetres),
                        s_blocked
                    );
                }

                var direction = field.Directions[cell];
                if (direction == FlowField.Goal)
                {
                    lines.Add(at, at + new Vector3(0, GoalMetres, 0), s_goal);
                    continue;
                }

                if (direction == FlowField.None)
                {
                    continue;
                }

                var next = grid.CentreMetres(field.Next(cell)) + new Vector3(0, LiftMetres, 0);
                var towards = next - at;
                var length = towards.Length();
                if (length > 0)
                {
                    var colour = spec.Costs.Straight(grid.Class[cell]) < 4 * spec.Costs.Cheapest ? s_cheap : s_dear;
                    var along = towards / length;
                    var tip = at + along * Math.Min(ArrowMetres, length);
                    var side = new Vector3(-along.Z, 0, along.X) * HeadMetres;
                    lines.Add(at, tip, colour);
                    lines.Add(tip, tip - along * HeadMetres + side, colour);
                    lines.Add(tip, tip - along * HeadMetres - side, colour);
                }
            }
        }

        return drawn;
    }
}
