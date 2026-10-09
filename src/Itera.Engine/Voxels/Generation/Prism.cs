using Itera.Engine.Maths;

namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// A plane that bounds a <see cref="Prism"/> or a <see cref="CutEllipsoid"/>: the points with
/// <c>X x + Y y + Z z &lt;= Distance</c> stay (for a prism in world centimetres, for an ellipsoid in its own
/// coordinates). (X, Y, Z) has unit length, so moving the plane out by d adds d to <see cref="Distance"/>.
/// </summary>
public readonly record struct ShapePlane(double X, double Y, double Z, double Distance);

/// <summary>
/// A convex solid bounded by planes <see cref="FirstPlane"/> to FirstPlane + <see cref="PlaneCount"/> of its holder, in
/// centimetres, and the box that holds it. Every question may grow the solid by moving each plane out by the same
/// distance; an edge then moves out by that distance over the sine of half its angle, so the box grows by
/// <see cref="Reach"/>, enough for edges of 20 degrees or more. Rows along x are filled from the range of x the planes
/// leave, in doubles with + - * / only, so every level and every machine fills the same voxels.
/// </summary>
public readonly record struct Prism(
    int FirstPlane,
    int PlaneCount,
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    double MinZ,
    double MaxZ
)
{
    /// <summary>How far the box reaches past the solid's box when the solid grows by <paramref name="grow"/>.</summary>
    public static double Reach(double grow) => 6 * grow;

    /// <summary>
    /// True when the solid, grown by <paramref name="grow"/>, may touch the box of sample points in centimetres.
    /// </summary>
    public bool Touches(double minX, double maxX, double minY, double maxY, double minZ, double maxZ, double grow)
    {
        var reach = Reach(grow);
        return MaxX + reach >= minX
            && MinX - reach <= maxX
            && MaxY + reach >= minY
            && MinY - reach <= maxY
            && MaxZ + reach >= minZ
            && MinZ - reach <= maxZ;
    }

    /// <summary>
    /// The range of x the solid, grown by <paramref name="grow"/>, covers in the row at (<paramref name="y"/>,
    /// <paramref name="z"/>); false when the row misses it. <paramref name="planes"/> is the holder's whole plane list.
    /// </summary>
    public bool Span(double y, double z, ReadOnlySpan<ShapePlane> planes, double grow, out double min, out double max)
    {
        min = double.NegativeInfinity;
        max = double.PositiveInfinity;
        foreach (ref readonly var plane in planes.Slice(FirstPlane, PlaneCount))
        {
            var rest = plane.Distance + grow - plane.Y * y - plane.Z * z;
            if (plane.X > 0)
            {
                max = Math.Min(max, rest / plane.X);
            }
            else if (plane.X < 0)
            {
                min = Math.Max(min, rest / plane.X);
            }
            else if (rest < 0)
            {
                return false;
            }
        }

        return min <= max;
    }

    /// <summary>
    /// True when (x, y, z) lies in the solid grown by <paramref name="grow"/>: the same sums as <see cref="Span"/>, so a
    /// point that lies on a plane gets the same answer from both.
    /// </summary>
    public bool Contains(double x, double y, double z, ReadOnlySpan<ShapePlane> planes, double grow) =>
        Span(y, z, planes, grow, out var min, out var max) && x >= min && x <= max;

    /// <summary>
    /// True when the whole box lies in the solid: for each plane, the box's corner farthest along it stays inside by a
    /// thousandth of a voxel, so a box that only reaches a plane never counts as held, however the sums round.
    /// </summary>
    public bool Encloses(
        double minX,
        double maxX,
        double minY,
        double maxY,
        double minZ,
        double maxZ,
        ReadOnlySpan<ShapePlane> planes
    )
    {
        foreach (ref readonly var plane in planes.Slice(FirstPlane, PlaneCount))
        {
            var farthest =
                Math.Max(plane.X * minX, plane.X * maxX)
                + Math.Max(plane.Y * minY, plane.Y * maxY)
                + Math.Max(plane.Z * minZ, plane.Z * maxZ);
            if (farthest > plane.Distance - 0.001)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Builds <see cref="Prism"/>s into one plane list: frustums of regular polygons, pyramids, and convex polygons drawn
/// sideways and pushed along a horizontal direction. Centimetres; + - * / and sqrt only.
/// </summary>
public sealed class PrismBuilder
{
    private readonly List<ShapePlane> _planes = [];

    /// <summary>Every plane built so far.</summary>
    public ShapePlane[] Planes() => [.. _planes];

    /// <summary>
    /// A frustum of a regular polygon of <paramref name="sides"/> about the vertical line through (x, z): the first
    /// face's outward normal is (<paramref name="normalX"/>, <paramref name="normalZ"/>), a unit vector, and each next
    /// face is turned towards +z by a 1/sides turn. Its faces lie <paramref name="bottom"/> from the line at
    /// <paramref name="y0"/> and <paramref name="top"/> at <paramref name="y1"/>.
    /// </summary>
    public Prism Frustum(
        double x,
        double z,
        int sides,
        double normalX,
        double normalZ,
        double bottom,
        double y0,
        double top,
        double y1
    )
    {
        var first = _planes.Count;
        var slope = (bottom - top) / (y1 - y0);
        for (var side = 0; side < sides; side++)
        {
            var (cos, sin) = DeterministicMath.CosSin(side / (double)sides);
            var faceX = normalX * cos - normalZ * sin;
            var faceZ = normalX * sin + normalZ * cos;
            Add(faceX, slope, faceZ, bottom + faceX * x + faceZ * z + slope * y0);
        }

        Add(0, -1, 0, -y0);
        Add(0, 1, 0, y1);

        // The corners lie farther out than the faces by 1 / cos(half a side's turn).
        var (half, _) = DeterministicMath.CosSin(0.5 / sides);
        var reach = Math.Max(bottom, top) / half;
        return new Prism(first, _planes.Count - first, x - reach, x + reach, y0, y1, z - reach, z + reach);
    }

    /// <summary>
    /// A pyramid on the flat, convex polygon <paramref name="ring"/> (flat, its corners in turn) with its point at
    /// <paramref name="apex"/>.
    /// </summary>
    public Prism Pyramid(ReadOnlySpan<(double X, double Y, double Z)> ring, (double X, double Y, double Z) apex)
    {
        var first = _planes.Count;
        var (insideX, insideY, insideZ) = (apex.X, apex.Y, apex.Z);
        foreach (var corner in ring)
        {
            insideX += corner.X;
            insideY += corner.Y;
            insideZ += corner.Z;
        }

        var share = 1.0 / (ring.Length + 1);
        var inside = (insideX * share, insideY * share, insideZ * share);
        for (var i = 0; i < ring.Length; i++)
        {
            Through(ring[i], ring[(i + 1) % ring.Length], apex, inside);
        }

        Through(ring[0], ring[1], ring[2], inside);
        return Bounds(first, ring, apex);
    }

    /// <summary>
    /// The convex polygon <paramref name="outline"/>, drawn in (across, up) from <paramref name="origin"/> with its
    /// corners in turn, pushed along the horizontal unit direction (<paramref name="alongX"/>,
    /// <paramref name="alongZ"/>) from <paramref name="from"/> to <paramref name="to"/>. Across is (-alongZ, alongX).
    /// </summary>
    public Prism Pushed(
        (double X, double Y, double Z) origin,
        double alongX,
        double alongZ,
        double from,
        double to,
        ReadOnlySpan<(double Across, double Up)> outline
    )
    {
        var first = _planes.Count;
        var acrossX = -alongZ;
        var acrossZ = alongX;
        double middleAcross = 0;
        double middleUp = 0;
        foreach (var (across, up) in outline)
        {
            middleAcross += across;
            middleUp += up;
        }

        middleAcross /= outline.Length;
        middleUp /= outline.Length;
        Span<(double X, double Y, double Z)> corners = stackalloc (double, double, double)[2 * outline.Length];
        for (var i = 0; i < outline.Length; i++)
        {
            var (a0, u0) = outline[i];
            var (a1, u1) = outline[(i + 1) % outline.Length];

            // The edge's normal in (across, up), turned to point away from the polygon's middle.
            var normalAcross = u1 - u0;
            var normalUp = a0 - a1;
            if (normalAcross * (middleAcross - a0) + normalUp * (middleUp - u0) > 0)
            {
                normalAcross = -normalAcross;
                normalUp = -normalUp;
            }

            var normalX = normalAcross * acrossX;
            var normalZ = normalAcross * acrossZ;
            var reach = normalAcross * a0 + normalUp * u0;
            Add(normalX, normalUp, normalZ, reach + normalX * origin.X + normalUp * origin.Y + normalZ * origin.Z);
            corners[2 * i] = (
                origin.X + acrossX * a0 + alongX * from,
                origin.Y + u0,
                origin.Z + acrossZ * a0 + alongZ * from
            );
            corners[2 * i + 1] = (
                origin.X + acrossX * a0 + alongX * to,
                origin.Y + u0,
                origin.Z + acrossZ * a0 + alongZ * to
            );
        }

        Add(alongX, 0, alongZ, to + alongX * origin.X + alongZ * origin.Z);
        Add(-alongX, 0, -alongZ, -from - alongX * origin.X - alongZ * origin.Z);
        return Bounds(first, corners, corners[0]);
    }

    /// <summary>
    /// A box about (x, y, z) whose long side lies along the horizontal unit direction (<paramref name="alongX"/>,
    /// <paramref name="alongZ"/>): half <paramref name="halfAlong"/>, <paramref name="halfAcross"/>, and
    /// <paramref name="halfUp"/> each way.
    /// </summary>
    public Prism Box(
        (double X, double Y, double Z) centre,
        double alongX,
        double alongZ,
        double halfAlong,
        double halfAcross,
        double halfUp
    ) =>
        Pushed(
            (centre.X, centre.Y - halfUp, centre.Z),
            alongX,
            alongZ,
            -halfAlong,
            halfAlong,
            [(-halfAcross, 0), (halfAcross, 0), (halfAcross, 2 * halfUp), (-halfAcross, 2 * halfUp)]
        );

    /// <summary>
    /// A solid bounded by <paramref name="planes"/>, each keeping the points with <c>X x + Y y + Z z &lt;= Distance</c>
    /// (the normal need not be a unit vector), held in the box <paramref name="min"/> to <paramref name="max"/>.
    /// </summary>
    public Prism Bounded(
        ReadOnlySpan<(double X, double Y, double Z, double Distance)> planes,
        (double X, double Y, double Z) min,
        (double X, double Y, double Z) max
    )
    {
        var first = _planes.Count;
        foreach (var (x, y, z, distance) in planes)
        {
            Add(x, y, z, distance);
        }

        return new Prism(first, _planes.Count - first, min.X, max.X, min.Y, max.Y, min.Z, max.Z);
    }

    /// <summary>Adds the plane through three points, facing away from <paramref name="inside"/>.</summary>
    private void Through(
        (double X, double Y, double Z) a,
        (double X, double Y, double Z) b,
        (double X, double Y, double Z) c,
        (double X, double Y, double Z) inside
    )
    {
        var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        var nx = uy * vz - uz * vy;
        var ny = uz * vx - ux * vz;
        var nz = ux * vy - uy * vx;
        if (nx * (inside.X - a.X) + ny * (inside.Y - a.Y) + nz * (inside.Z - a.Z) > 0)
        {
            (nx, ny, nz) = (-nx, -ny, -nz);
        }

        Add(nx, ny, nz, nx * a.X + ny * a.Y + nz * a.Z);
    }

    /// <summary>Adds a plane, scaling it to a unit normal.</summary>
    private void Add(double x, double y, double z, double distance)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        _planes.Add(new ShapePlane(x / length, y / length, z / length, distance / length));
    }

    private Prism Bounds(
        int first,
        ReadOnlySpan<(double X, double Y, double Z)> points,
        (double X, double Y, double Z) extra
    )
    {
        var (minX, maxX, minY, maxY, minZ, maxZ) = (extra.X, extra.X, extra.Y, extra.Y, extra.Z, extra.Z);
        foreach (var (x, y, z) in points)
        {
            (minX, maxX) = (Math.Min(minX, x), Math.Max(maxX, x));
            (minY, maxY) = (Math.Min(minY, y), Math.Max(maxY, y));
            (minZ, maxZ) = (Math.Min(minZ, z), Math.Max(maxZ, z));
        }

        return new Prism(first, _planes.Count - first, minX, maxX, minY, maxY, minZ, maxZ);
    }
}
