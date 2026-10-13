using System.Numerics;

namespace Itera.Engine.Physics;

/// <summary>
/// Rays against the simple shapes a game judges shots with. Plain functions of their arguments: they keep nothing and
/// allocate nothing, so any thread may call them.
/// </summary>
public static class HitShapes
{
    // The squared length under which a ray counts as parallel to a capsule's axis (about 10 millionths of a turn), and
    // under which a capsule counts as a sphere (a tenth of a millimetre). A ray that near the axis meets the end spheres
    // where it meets the capsule, and a shorter axis has no side to meet.
    private const float ParallelSquared = 1e-10f;
    private const float ShortAxisSquared = 1e-8f;

    /// <summary>
    /// Where a ray first meets a capsule: the points within <paramref name="radius"/> of the segment from
    /// <paramref name="a"/> to <paramref name="b"/> (a cylinder and its two end spheres; a sphere when the ends are the
    /// same point). <paramref name="unitDirection"/> must have length 1. A ray that starts inside the capsule meets it at
    /// distance 0 with the normal opposite the ray. A capsule of radius 0 is never met.
    /// </summary>
    /// <param name="from">Where the ray starts.</param>
    /// <param name="unitDirection">The ray's direction, of length 1.</param>
    /// <param name="a">One end of the capsule's axis.</param>
    /// <param name="b">The other end.</param>
    /// <param name="radius">The capsule's radius, in metres.</param>
    /// <param name="distance">The distance along the ray to the surface, 0 or more.</param>
    /// <param name="normal">The surface's outward normal there.</param>
    /// <returns>True when the ray meets the capsule in front of its start.</returns>
    public static bool RaycastCapsule(
        Vector3 from,
        Vector3 unitDirection,
        Vector3 a,
        Vector3 b,
        float radius,
        out float distance,
        out Vector3 normal
    )
    {
        distance = 0;
        normal = default;
        if (!(radius > 0))
        {
            return false;
        }

        var axis = b - a;
        var lengthSquared = axis.LengthSquared();
        var fromA = from - a;

        // A ray that starts inside meets the capsule where it starts.
        var share = lengthSquared > 0 ? Math.Clamp(Vector3.Dot(fromA, axis) / lengthSquared, 0f, 1f) : 0f;
        if ((fromA - axis * share).LengthSquared() <= radius * radius)
        {
            normal = -unitDirection;
            return true;
        }

        var best = float.PositiveInfinity;
        if (lengthSquared <= ShortAxisSquared)
        {
            if (TrySphere(from, unitDirection, a, radius, out var t))
            {
                best = t;
                normal = Vector3.Normalize(from + unitDirection * t - a);
            }
        }
        else
        {
            var length = MathF.Sqrt(lengthSquared);
            var u = axis / length;
            var fromAlong = Vector3.Dot(fromA, u);
            var directionAlong = Vector3.Dot(unitDirection, u);

            // The side: the ray's parts across the axis against the radius. A ray along the axis has none, and meets the
            // end spheres.
            var across = unitDirection - u * directionAlong;
            var fromAcross = fromA - u * fromAlong;
            var qa = across.LengthSquared();
            if (qa > ParallelSquared)
            {
                var qb = Vector3.Dot(fromAcross, across);
                var h = qb * qb - qa * (fromAcross.LengthSquared() - radius * radius);
                if (h >= 0)
                {
                    var t = (-qb - MathF.Sqrt(h)) / qa;
                    var y = fromAlong + t * directionAlong;
                    if (t >= 0 && y >= 0 && y <= length)
                    {
                        best = t;
                        normal = Vector3.Normalize(fromAcross + across * t);
                    }
                }
            }

            // An end sphere counts where its hit lies beyond the end; elsewhere it lies inside the side.
            if (TrySphere(from, unitDirection, a, radius, out var ta) && ta < best)
            {
                var point = from + unitDirection * ta;
                if (Vector3.Dot(point - a, u) <= 0)
                {
                    best = ta;
                    normal = Vector3.Normalize(point - a);
                }
            }

            if (TrySphere(from, unitDirection, b, radius, out var tb) && tb < best)
            {
                var point = from + unitDirection * tb;
                if (Vector3.Dot(point - a, u) >= length)
                {
                    best = tb;
                    normal = Vector3.Normalize(point - b);
                }
            }
        }

        if (float.IsPositiveInfinity(best))
        {
            return false;
        }

        distance = best;
        return true;
    }

    /// <summary>
    /// The distance to the near side of a sphere, for a ray that starts outside it: false when it misses or points away.
    /// </summary>
    private static bool TrySphere(Vector3 from, Vector3 unitDirection, Vector3 center, float radius, out float distance)
    {
        var offset = from - center;
        var b = Vector3.Dot(offset, unitDirection);
        var h = b * b - (offset.LengthSquared() - radius * radius);
        distance = h < 0 ? 0 : -b - MathF.Sqrt(h);
        return h >= 0 && distance >= 0;
    }
}
