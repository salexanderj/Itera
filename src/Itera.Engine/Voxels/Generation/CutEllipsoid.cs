namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// A convex solid, in centimetres: an ellipsoid about (X, Y, Z) that leans by <see cref="LeanX"/> and
/// <see cref="LeanZ"/> (the shift in x and z per unit of height), cut flat below <see cref="Floor"/> and by planes
/// <see cref="FirstPlane"/> to <see cref="FirstPlane"/> + <see cref="PlaneCount"/> of its holder
/// (<see cref="ShapePlane"/>). In the solid's own coordinates <c>u = x - X - LeanX v</c>, <c>v = y - Y</c>,
/// <c>w = z - Z - LeanZ v</c> the ellipsoid is upright and the planes are fixed. Rows along x are filled from the range
/// of x each covers, worked out in doubles with + - * / and sqrt only, so every level and every machine fills the same
/// voxels.
/// </summary>
public readonly record struct CutEllipsoid(
    double X,
    double Y,
    double Z,
    double RadiusX,
    double RadiusY,
    double RadiusZ,
    double LeanX,
    double LeanZ,
    double Floor,
    int FirstPlane,
    int PlaneCount
)
{
    /// <summary>Farthest the solid reaches from its centre in x: the leaning ellipsoid's half width.</summary>
    public double ExtentX { get; } = Math.Sqrt(RadiusX * RadiusX + LeanX * LeanX * RadiusY * RadiusY);

    /// <summary>Farthest the solid reaches from its centre in z.</summary>
    public double ExtentZ { get; } = Math.Sqrt(RadiusZ * RadiusZ + LeanZ * LeanZ * RadiusY * RadiusY);

    /// <summary>Lowest y the solid reaches.</summary>
    public double MinY => Math.Max(Floor, Y - RadiusY);

    /// <summary>Highest y the solid reaches.</summary>
    public double MaxY => Y + RadiusY;

    /// <summary>True when the solid's box touches the box of sample points in centimetres.</summary>
    public bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
        X + ExtentX >= minX
        && X - ExtentX <= maxX
        && MaxY >= minY
        && MinY <= maxY
        && Z + ExtentZ >= minZ
        && Z - ExtentZ <= maxZ;

    /// <summary>
    /// The range of x that the solid covers in the row at (<paramref name="y"/>, <paramref name="z"/>); false when the
    /// row misses it. <paramref name="planes"/> is the holder's whole plane list.
    /// </summary>
    public bool Span(double y, double z, ReadOnlySpan<ShapePlane> planes, out double min, out double max)
    {
        min = 0;
        max = -1;
        var v = y - Y;
        if (y < Floor)
        {
            return false;
        }

        var w = z - Z - LeanZ * v;
        var room = 1 - v * v / (RadiusY * RadiusY) - w * w / (RadiusZ * RadiusZ);
        if (room < 0)
        {
            return false;
        }

        var high = RadiusX * Math.Sqrt(room);
        var low = -high;
        foreach (ref readonly var plane in planes.Slice(FirstPlane, PlaneCount))
        {
            var rest = plane.Distance - plane.Y * v - plane.Z * w;
            if (plane.X > 0)
            {
                high = Math.Min(high, rest / plane.X);
            }
            else if (plane.X < 0)
            {
                low = Math.Max(low, rest / plane.X);
            }
            else if (rest < 0)
            {
                return false;
            }
        }

        if (low > high)
        {
            return false;
        }

        var shift = X + LeanX * v;
        min = shift + low;
        max = shift + high;
        return true;
    }

    /// <summary>
    /// The highest y of the solid on the vertical line at (<paramref name="x"/>, <paramref name="z"/>); false when the
    /// line misses it. Along the line u and w are linear in v, so the ellipsoid gives a quadratic in v and each plane a
    /// bound on it.
    /// </summary>
    public bool Top(double x, double z, ReadOnlySpan<ShapePlane> planes, out double top)
    {
        top = 0;
        var du = x - X;
        var dw = z - Z;
        var xx = RadiusX * RadiusX;
        var zz = RadiusZ * RadiusZ;

        // (du - LeanX v)² / xx + v² / yy + (dw - LeanZ v)² / zz <= 1, as a v² + 2 b v + c <= 0.
        var a = LeanX * LeanX / xx + 1 / (RadiusY * RadiusY) + LeanZ * LeanZ / zz;
        var b = -(du * LeanX / xx + dw * LeanZ / zz);
        var c = du * du / xx + dw * dw / zz - 1;
        var room = b * b - a * c;
        if (room < 0)
        {
            return false;
        }

        var root = Math.Sqrt(room);
        var high = (root - b) / a;
        var low = Math.Max((-root - b) / a, Floor - Y);
        foreach (ref readonly var plane in planes.Slice(FirstPlane, PlaneCount))
        {
            var slope = plane.Y - plane.X * LeanX - plane.Z * LeanZ;
            var rest = plane.Distance - plane.X * du - plane.Z * dw;
            if (slope > 0)
            {
                high = Math.Min(high, rest / slope);
            }
            else if (slope < 0)
            {
                low = Math.Max(low, rest / slope);
            }
            else if (rest < 0)
            {
                return false;
            }
        }

        if (low > high)
        {
            return false;
        }

        top = Y + high;
        return true;
    }
}
