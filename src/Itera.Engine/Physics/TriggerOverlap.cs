using System.Numerics;
using BepuPhysics;

namespace Itera.Engine.Physics;

/// <summary>
/// Whether an upright character's capsule overlaps a trigger zone (<see cref="TriggerShape"/>): the capsule's segment
/// against a sphere's centre, a capsule's segment, or a box (in the box's own axes). Main thread; allocates nothing.
/// </summary>
internal static class TriggerOverlap
{
    /// <summary>
    /// True when the capsule from <paramref name="a"/> to <paramref name="b"/> of <paramref name="radius"/> overlaps
    /// <paramref name="shape"/> posed at <paramref name="pose"/>.
    /// </summary>
    public static bool Capsule(Vector3 a, Vector3 b, float radius, in TriggerShape shape, in RigidPose pose)
    {
        var size = shape.Size;
        // Past the sphere round the zone, nothing of it is near.
        var bound =
            radius
            + shape.Kind switch
            {
                TriggerShapeKind.Box => size.Length() * 0.5f,
                TriggerShapeKind.Sphere => size.X,
                _ => size.X + size.Y * 0.5f,
            };
        if (PointSegmentSquared(pose.Position, a, b) > bound * bound)
        {
            return false;
        }

        switch (shape.Kind)
        {
            case TriggerShapeKind.Sphere:
                var reach = radius + size.X;
                return PointSegmentSquared(pose.Position, a, b) <= reach * reach;
            case TriggerShapeKind.Capsule:
                var axis = Vector3.Transform(new Vector3(0, size.Y * 0.5f, 0), pose.Orientation);
                var both = radius + size.X;
                return SegmentSegmentSquared(a, b, pose.Position - axis, pose.Position + axis) <= both * both;
            default:
                // In the box's axes, from its centre.
                var undo = Quaternion.Conjugate(pose.Orientation);
                var localA = Vector3.Transform(a - pose.Position, undo);
                var localB = Vector3.Transform(b - pose.Position, undo);
                return SegmentBoxSquared(localA, localB, size * 0.5f) <= radius * radius;
        }
    }

    /// <summary>The squared distance from <paramref name="point"/> to the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static float PointSegmentSquared(Vector3 point, Vector3 a, Vector3 b)
    {
        var along = b - a;
        var length = along.LengthSquared();
        var t = length > 0 ? Math.Clamp(Vector3.Dot(point - a, along) / length, 0, 1) : 0;
        return Vector3.DistanceSquared(point, a + along * t);
    }

    /// <summary>The squared distance between the segments p0-p1 and q0-q1 (Ericson, Real-Time Collision Detection 5.1.9).</summary>
    public static float SegmentSegmentSquared(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        var d1 = p1 - p0;
        var d2 = q1 - q0;
        var r = p0 - q0;
        var a = Vector3.Dot(d1, d1);
        var e = Vector3.Dot(d2, d2);
        var f = Vector3.Dot(d2, r);
        float s,
            t;
        if (a <= 1e-12f && e <= 1e-12f)
        {
            return Vector3.DistanceSquared(p0, q0);
        }

        if (a <= 1e-12f)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-12f)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denominator = a * e - b * b;
                s = denominator > 1e-12f ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0)
                {
                    t = 0;
                    s = Math.Clamp(-c / a, 0, 1);
                }
                else if (t > 1)
                {
                    t = 1;
                    s = Math.Clamp((b - c) / a, 0, 1);
                }
            }
        }

        return Vector3.DistanceSquared(p0 + d1 * s, q0 + d2 * t);
    }

    /// <summary>
    /// The squared distance from the segment a-b to the box of half edges <paramref name="half"/> round the origin: the
    /// least over the segment of a point's squared distance to the box, which is convex along it, found by narrowing
    /// thirds (to well under a millimetre on a segment of a few metres).
    /// </summary>
    public static float SegmentBoxSquared(Vector3 a, Vector3 b, Vector3 half)
    {
        float low = 0,
            high = 1;
        for (var i = 0; i < 40; i++)
        {
            var left = low + (high - low) / 3;
            var right = high - (high - low) / 3;
            if (PointBoxSquared(Vector3.Lerp(a, b, left), half) <= PointBoxSquared(Vector3.Lerp(a, b, right), half))
            {
                high = right;
            }
            else
            {
                low = left;
            }
        }

        return MathF.Min(
            PointBoxSquared(Vector3.Lerp(a, b, (low + high) * 0.5f), half),
            MathF.Min(PointBoxSquared(a, half), PointBoxSquared(b, half))
        );
    }

    private static float PointBoxSquared(Vector3 point, Vector3 half)
    {
        var outside = Vector3.Max(Vector3.Abs(point) - half, Vector3.Zero);
        return outside.LengthSquared();
    }
}
