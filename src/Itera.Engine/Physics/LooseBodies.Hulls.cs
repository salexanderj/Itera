using System.Numerics;

namespace Itera.Engine.Physics;

// The convex hulls of large pieces: their farthest points in directions spread over the sphere.
public sealed partial class LooseBodies
{
    /// <summary>
    /// Keeps, of <paramref name="points"/>, the farthest in each of <see cref="HullDirections"/> directions spread over
    /// the sphere (each once), written to its start; returns how many. They are corners of the full hull, so the hull of
    /// them lies inside it and touches it in every direction: for a round piece 73 cm across, within about 1.7 cm, with
    /// 80 faces instead of 1,274. Allocates nothing.
    /// </summary>
    internal int SupportPoints(Span<Vector3> points) => SupportPoints(points, s_hullDirections);

    /// <summary>
    /// <see cref="SupportPoints(Span{Vector3})"/> in <paramref name="directions"/> (at most
    /// <see cref="PartHullDirections"/> of them).
    /// </summary>
    private int SupportPoints(Span<Vector3> points, ReadOnlySpan<Vector3> directions)
    {
        var kept = 0;
        foreach (var direction in directions)
        {
            var best = 0;
            var bestDot = float.NegativeInfinity;
            for (var i = 0; i < points.Length; i++)
            {
                var dot = Vector3.Dot(points[i], direction);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = i;
                }
            }

            var point = points[best];
            if (_support.AsSpan(0, kept).IndexOf(point) < 0)
            {
                _support[kept++] = point;
            }
        }

        _support.AsSpan(0, kept).CopyTo(points);
        return kept;
    }

    /// <summary>
    /// Unit directions spread evenly over the sphere: an icosahedron's 12 corners, with its edges' middles pushed out onto
    /// the sphere and its faces split in four, <paramref name="splits"/> times: 42 directions after one split
    /// (<see cref="HullDirections"/>), 162 after two (<see cref="PartHullDirections"/>).
    /// </summary>
    private static Vector3[] IcosphereDirections(int splits)
    {
        var t = (1 + MathF.Sqrt(5)) / 2;
        var corners = new List<Vector3>
        {
            new(-1, t, 0),
            new(1, t, 0),
            new(-1, -t, 0),
            new(1, -t, 0),
            new(0, -1, t),
            new(0, 1, t),
            new(0, -1, -t),
            new(0, 1, -t),
            new(t, 0, -1),
            new(t, 0, 1),
            new(-t, 0, -1),
            new(-t, 0, 1),
        };
        int[] faces =
        [
            0,
            11,
            5,
            0,
            5,
            1,
            0,
            1,
            7,
            0,
            7,
            10,
            0,
            10,
            11,
            1,
            5,
            9,
            5,
            11,
            4,
            11,
            10,
            2,
            10,
            7,
            6,
            7,
            1,
            8,
            3,
            9,
            4,
            3,
            4,
            2,
            3,
            2,
            6,
            3,
            6,
            8,
            3,
            8,
            9,
            4,
            9,
            5,
            2,
            4,
            11,
            6,
            2,
            10,
            8,
            6,
            7,
            9,
            8,
            1,
        ];
        for (var i = 0; i < corners.Count; i++)
        {
            corners[i] = Vector3.Normalize(corners[i]);
        }

        for (var split = 0; split < splits; split++)
        {
            var middles = new Dictionary<(int, int), int>();
            var next = new List<int>(faces.Length * 4);
            for (var f = 0; f < faces.Length; f += 3)
            {
                int a = faces[f],
                    b = faces[f + 1],
                    c = faces[f + 2];
                int ab = Middle(a, b),
                    bc = Middle(b, c),
                    ca = Middle(c, a);
                next.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
            }

            faces = [.. next];

            int Middle(int p, int q)
            {
                var key = p < q ? (p, q) : (q, p);
                if (!middles.TryGetValue(key, out var index))
                {
                    index = corners.Count;
                    corners.Add(Vector3.Normalize(corners[p] + corners[q]));
                    middles.Add(key, index);
                }

                return index;
            }
        }

        return [.. corners];
    }
}
