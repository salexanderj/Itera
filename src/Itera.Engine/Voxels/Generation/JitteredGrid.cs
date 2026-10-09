using Itera.Engine.Maths;

namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// One candidate point of a <see cref="JitteredGrid"/> cell, in centimetres. <see cref="Draw"/> is the cell's first draw,
/// from 0 to 1 and below the chance asked for: a caller whose chance varies from place to place keeps the point when
/// <c>Draw</c> is below the chance there. <see cref="State"/> is the hash sequence after the point's own draws; the
/// caller goes on drawing from it (<see cref="DeterministicMath.Next(ref ulong)"/>) to shape what grows there.
/// </summary>
public readonly record struct JitteredPoint(int CellX, int CellZ, int X, int Z, double Draw, ulong State);

/// <summary>
/// A way to scatter things over the world: at most one candidate point in each square cell of a grid, found from the
/// seed alone, so every worker, level of detail, and machine finds the same points. Each cell's hash (from its place,
/// the seed, and <see cref="Salt"/>) gives a first draw that decides whether the cell has a point (against a chance),
/// then the point's x and z within the cell. Units are centimetres.
/// </summary>
/// <param name="Cell">The cell's edge.</param>
/// <param name="Reach">
/// How far a thing grown at a point reaches from it, in x and z. <see cref="Find"/> returns the points that can touch a box.
/// </param>
/// <param name="Salt">Names what the points are for, so two grids with the same cell do not place things alike.</param>
/// <param name="Margin">How far inside its cell a point stays; 0 lets it lie anywhere in the cell.</param>
public readonly record struct JitteredGrid(int Cell, int Reach, ulong Salt, int Margin = 0)
{
    /// <summary>
    /// The most points <see cref="Find"/> can return for the box [minX, maxX] x [minZ, maxZ]: the cells its reach touches.
    /// </summary>
    public int Capacity(int minX, int maxX, int minZ, int maxZ) =>
        ((maxX - minX + 2 * Reach) / Cell + 2) * ((maxZ - minZ + 2 * Reach) / Cell + 2);

    /// <summary>
    /// The candidate point of cell (<paramref name="cellX"/>, <paramref name="cellZ"/>), if its first draw is below
    /// <paramref name="chance"/>. <paramref name="layer"/> stacks grids on one another: layer n is the grid with salt
    /// <c>Salt + n</c>, for a pattern in layers (one grid for each band of height, say).
    /// </summary>
    public bool TryGet(int cellX, int cellZ, ulong seed, double chance, out JitteredPoint point, int layer = 0)
    {
        point = default;
        var state = DeterministicMath.Hash(cellX, cellZ, seed, Salt + (ulong)layer);
        var draw = DeterministicMath.NextUnit(ref state);
        if (draw >= chance)
        {
            return false;
        }

        var span = Cell - 2 * Margin;
        var x = cellX * Cell + Margin + DeterministicMath.NextRange(ref state, 0, span - 1);
        var z = cellZ * Cell + Margin + DeterministicMath.NextRange(ref state, 0, span - 1);
        point = new JitteredPoint(cellX, cellZ, x, z, draw, state);
        return true;
    }

    /// <summary>
    /// Writes the points that can touch the box [minX, maxX] x [minZ, maxZ] into <paramref name="found"/>, in grid order
    /// (rows of increasing z, each of increasing x), and returns how many. A point can touch the box when its square of
    /// <see cref="Reach"/> about it meets the box. <paramref name="found"/> must hold <see cref="Capacity"/> points.
    /// </summary>
    public int Find(
        ulong seed,
        double chance,
        int minX,
        int maxX,
        int minZ,
        int maxZ,
        Span<JitteredPoint> found,
        int layer = 0
    )
    {
        var count = 0;
        DeterministicMath.CellRange(Cell, Reach, minX, maxX, out var firstX, out var lastX);
        DeterministicMath.CellRange(Cell, Reach, minZ, maxZ, out var firstZ, out var lastZ);
        for (var cellZ = firstZ; cellZ <= lastZ; cellZ++)
        {
            for (var cellX = firstX; cellX <= lastX; cellX++)
            {
                if (
                    TryGet(cellX, cellZ, seed, chance, out var point, layer)
                    && point.X + Reach >= minX
                    && point.X - Reach <= maxX
                    && point.Z + Reach >= minZ
                    && point.Z - Reach <= maxZ
                )
                {
                    found[count++] = point;
                }
            }
        }

        return count;
    }
}
