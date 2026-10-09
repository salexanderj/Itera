using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Animation;

/// <summary>
/// The corners of a part's voxels that can be its lowest point in some pose: for each of <see cref="Directions"/>
/// directions spread over the sphere, the voxel corner farthest along it. However a part is turned, its lowest corner
/// lies within about a millimetre of the lowest of these for a limb 10 cm across (directions are at most about 10
/// degrees from one of them), where the corners of its box lie up to several centimetres below its voxels once it is
/// tilted. Built once per body.
/// </summary>
public static class SupportPoints
{
    /// <summary>How many directions the points are found in.</summary>
    public const int Directions = 162;

    // Spread evenly over the sphere by the golden angle (a Fibonacci sphere).
    private static readonly (double X, double Y, double Z)[] s_directions = Spread(Directions);

    /// <summary>
    /// The support points of a part volume filled at <paramref name="level"/> into <paramref name="voxels"/> (one chunk of
    /// voxels, one byte each, 0 for air), whose box is <paramref name="size"/> level-0 voxels with its
    /// corner at <paramref name="origin"/> in the bone's space: in the bone's space, level-0 voxels, each point once. An
    /// empty volume has none.
    /// </summary>
    public static (double X, double Y, double Z)[] Of(
        ReadOnlySpan<byte> voxels,
        VoxelCoord size,
        int level,
        (double X, double Y, double Z) origin
    )
    {
        const int edge = VoxelConstants.ChunkSize;
        var cell = 1 << level;
        int nx = (size.X + cell - 1) >> level,
            ny = (size.Y + cell - 1) >> level,
            nz = (size.Z + cell - 1) >> level;

        // Only the first and last voxel of each row along x can hold a corner of the hull: every other lies between them.
        var ends = new List<(int X, int Y, int Z)>();
        for (var z = 0; z < nz; z++)
        {
            for (var y = 0; y < ny; y++)
            {
                var row = voxels.Slice((z * edge + y) * edge, nx);
                var first = row.IndexOfAnyExcept((byte)0);
                if (first < 0)
                {
                    continue;
                }

                ends.Add((first, y, z));
                var last = row.LastIndexOfAnyExcept((byte)0);
                if (last != first)
                {
                    ends.Add((last, y, z));
                }
            }
        }

        var points = new HashSet<(double X, double Y, double Z)>();
        if (ends.Count == 0)
        {
            return [];
        }

        foreach (var (dx, dy, dz) in s_directions)
        {
            // The corner of a voxel farthest along the direction is the one on the direction's side of each axis.
            int cx = dx > 0 ? 1 : 0,
                cy = dy > 0 ? 1 : 0,
                cz = dz > 0 ? 1 : 0;
            var best = double.NegativeInfinity;
            var at = (0, 0, 0);
            foreach (var (x, y, z) in ends)
            {
                var along = dx * (x + cx) + dy * (y + cy) + dz * (z + cz);
                if (along > best)
                {
                    best = along;
                    at = (x + cx, y + cy, z + cz);
                }
            }

            points.Add((origin.X + at.Item1 * cell, origin.Y + at.Item2 * cell, origin.Z + at.Item3 * cell));
        }

        // A fixed order, so every machine holds the same list.
        var list = points.ToArray();
        Array.Sort(list);
        return list;
    }

    private static (double X, double Y, double Z)[] Spread(int count)
    {
        var directions = new (double X, double Y, double Z)[count];
        var golden = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < count; i++)
        {
            var y = 1 - (2 * i + 1) / (double)count;
            var radius = Math.Sqrt(1 - y * y);
            // Turned with no library sine or cosine, so every machine holds the same points.
            var (cos, sin) = DeterministicMath.CosSin(golden * i / (2 * Math.PI));
            directions[i] = (cos * radius, y, sin * radius);
        }

        return directions;
    }
}
