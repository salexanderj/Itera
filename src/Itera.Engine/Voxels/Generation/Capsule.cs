namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// The points within <see cref="Radius"/> of the segment from A to B, in centimetres: a cylinder with round ends.
/// Rows along x are filled from the range of x each covers, worked out in doubles with + - * / and sqrt only, so every
/// level and every machine fills the same voxels.
/// </summary>
public readonly record struct Capsule(double AX, double AY, double AZ, double BX, double BY, double BZ, double Radius)
{
    /// <summary>Lowest y the capsule reaches.</summary>
    public double MinY => Math.Min(AY, BY) - Radius;

    /// <summary>Highest y the capsule reaches.</summary>
    public double MaxY => Math.Max(AY, BY) + Radius;

    /// <summary>Lowest z the capsule reaches.</summary>
    public double MinZ => Math.Min(AZ, BZ) - Radius;

    /// <summary>Highest z the capsule reaches.</summary>
    public double MaxZ => Math.Max(AZ, BZ) + Radius;

    /// <summary>True when the capsule's box touches the box of sample points in centimetres.</summary>
    public bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
        Math.Max(AX, BX) + Radius >= minX
        && Math.Min(AX, BX) - Radius <= maxX
        && MaxY >= minY
        && MinY <= maxY
        && MaxZ >= minZ
        && MinZ <= maxZ;

    /// <summary>
    /// The range of y that points of the capsule reach in the plane at <paramref name="z"/>, for a capsule of
    /// <paramref name="radius"/> about the same segment; false when the plane misses it. The segment's points within
    /// the radius of the plane lie between two values of its parameter, and y is linear along it.
    /// </summary>
    public bool Heights(double z, double radius, out double min, out double max)
    {
        min = 0;
        max = -1;
        double from = 0;
        double to = 1;
        var dz = BZ - AZ;
        if (dz != 0)
        {
            var first = (z - radius - AZ) / dz;
            var second = (z + radius - AZ) / dz;
            from = Math.Max(0, Math.Min(first, second));
            to = Math.Min(1, Math.Max(first, second));
        }
        else if (Math.Abs(z - AZ) > radius)
        {
            return false;
        }

        if (from > to)
        {
            return false;
        }

        var dy = BY - AY;
        var yFrom = AY + dy * from;
        var yTo = AY + dy * to;
        min = Math.Min(yFrom, yTo) - radius;
        max = Math.Max(yFrom, yTo) + radius;
        return true;
    }

    /// <summary>
    /// The range [min, max] of x at which the row at (<paramref name="y"/>, <paramref name="z"/>) lies within
    /// <paramref name="radius"/> of the segment; false when it misses. The capsule is convex, so the row meets it in one
    /// range: the union of where it meets the two end spheres and the cylinder between the end planes.
    /// </summary>
    public bool Span(double y, double z, double radius, out double min, out double max)
    {
        var radiusSquared = radius * radius;
        min = double.PositiveInfinity;
        max = double.NegativeInfinity;
        AddSphere(AX, y - AY, z - AZ, radiusSquared, ref min, ref max);
        AddSphere(BX, y - BY, z - BZ, radiusSquared, ref min, ref max);

        var dx = BX - AX;
        var dy = BY - AY;
        var dz = BZ - AZ;
        var length = dx * dx + dy * dy + dz * dz;
        if (length > 0)
        {
            // With u = x - AX, the point's parameter along the segment is (u dx + k) / length, and it lies between the
            // end planes when that is between 0 and 1.
            var v = y - AY;
            var w = z - AZ;
            var k = v * dy + w * dz;
            var low = double.NegativeInfinity;
            var high = double.PositiveInfinity;
            if (dx > 0)
            {
                low = -k / dx;
                high = (length - k) / dx;
            }
            else if (dx < 0)
            {
                low = (length - k) / dx;
                high = -k / dx;
            }
            else if (k < 0 || k > length)
            {
                return min <= max;
            }

            // Its squared distance from the axis times length: a u^2 + b u + c + radius^2 length, with a >= 0.
            var a = dy * dy + dz * dz;
            var b = -2 * dx * k;
            var c = length * (v * v + w * w) - k * k - radiusSquared * length;
            if (a > 0)
            {
                var discriminant = b * b - 4 * a * c;
                if (discriminant < 0)
                {
                    return min <= max;
                }

                var root = Math.Sqrt(discriminant);
                low = Math.Max(low, (-b - root) / (2 * a));
                high = Math.Min(high, (-b + root) / (2 * a));
            }
            else if (c > 0)
            {
                return min <= max;
            }

            if (low <= high)
            {
                min = Math.Min(min, AX + low);
                max = Math.Max(max, AX + high);
            }
        }

        return min <= max;
    }

    private static void AddSphere(
        double centreX,
        double dy,
        double dz,
        double radiusSquared,
        ref double min,
        ref double max
    )
    {
        var room = radiusSquared - dy * dy - dz * dz;
        if (room >= 0)
        {
            var half = Math.Sqrt(room);
            min = Math.Min(min, centreX - half);
            max = Math.Max(max, centreX + half);
        }
    }
}
