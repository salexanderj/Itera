using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Game.WorldGeneration;

/// <summary>
/// Where the starter's world places its voxel objects (<see cref="GameObjects"/>; the generator's
/// <see cref="IObjectPlacer"/>): boulders, one in each 16 m cell at a chance of 0.6, of the size nearest a radius drawn
/// from 1 to 3 m, a third of each buried; forest in patches where a broad noise is high (<see cref="Cover"/>: about 40 % of the
/// map), a tree in each 8 m cell inside them at a chance of 0.7 (about one every 91 m²), thinning to none over their
/// edges, seven in ten broadleaf and three young; bushes in 6 m cells, at a chance of 0.15 outside the forest rising to
/// 0.45 inside; and rocks in 12 m cells, at 0.1 rising to 0.2. Each takes one of its kind's shapes and a heading from its
/// cell's hash, and stands on the ground's height at its anchor. None comes within <see cref="ClearCentimetres"/> of the
/// map's middle (the start and the fox's circle, which meets objects nowhere), no tree, bush, or rock onto a boulder,
/// and no bush or rock against a tree's trunk.
/// Thread safe and a pure function of the seed and the box, as the generator is.
/// </summary>
public sealed class GameForests : IObjectPlacer
{
    /// <summary>How far from the map's middle no object reaches, in centimetres.</summary>
    public const int ClearCentimetres = 1_500;

    /// <summary>The cover's value from which forest grows, thin, and where it is whole.</summary>
    public const double CoverFrom = 0.01,
        CoverWhole = 0.11;

    private const double TreeChance = 0.7,
        YoungShare = 0.3;

    /// <summary>The share of the boulders' 16 m cells that hold one.</summary>
    public const double BoulderChance = 0.6;

    private static readonly FractalNoise s_cover = new([40_000, 12_000], [1, 0.45], 0x666F72657374);
    private static readonly JitteredGrid s_boulders = new(1_600, 0, 0x626F756C64, Margin: 100);
    private static readonly JitteredGrid s_trees = new(800, 0, 0x7472656573, Margin: 50);
    private static readonly JitteredGrid s_bushes = new(600, 0, 0x627573686573);
    private static readonly JitteredGrid s_rocks = new(1_200, 0, 0x726F636B73, Margin: 100);

    [ThreadStatic]
    private static JitteredPoint[]? t_points;

    /// <summary>The forest's cover at (x, z), from 0 (open ground) to 1 (whole forest).</summary>
    public static double Cover(int x, int z, ulong seed) =>
        DeterministicMath.SmoothStep(CoverFrom, CoverWhole, s_cover.Value(x, z, seed));

    public void Find(ulong seed, int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (minX >= maxX || minZ >= maxZ)
        {
            return;
        }

        // The grids find the points in the box, inclusive: anchors from min up to but not including max.
        var points = Points(s_boulders.Capacity(minX, maxX - 1, minZ, maxZ - 1));
        var count = s_boulders.Find(seed, BoulderChance, minX, maxX - 1, minZ, maxZ - 1, points);
        for (var i = 0; i < count; i++)
        {
            if (Boulder(in points[i], out var size, out var state))
            {
                var shape = DeterministicMath.NextRange(ref state, 0, GameObjects.BoulderShapes - 1);
                var heading = DeterministicMath.NextRange(ref state, 0, ObjectPlacement.HeadingSteps - 1);
                var ground = (int)Math.Floor(GameGenerator.Height(points[i].X, points[i].Z, seed));
                found.Add(
                    new ObjectPlacement(
                        ObjectId.Of(in points[i], 0x6F),
                        GameObjects.BoulderId(size, shape),
                        new VoxelCoord(points[i].X, ground, points[i].Z),
                        heading
                    )
                );
            }
        }

        points = Points(s_trees.Capacity(minX, maxX - 1, minZ, maxZ - 1));
        count = s_trees.Find(seed, TreeChance, minX, maxX - 1, minZ, maxZ - 1, points);
        for (var i = 0; i < count; i++)
        {
            if (Tree(in points[i], seed, out var kind, out var state))
            {
                Add(found, in points[i], kind, ref state, seed, 0x74);
            }
        }

        points = Points(s_bushes.Capacity(minX, maxX - 1, minZ, maxZ - 1));
        count = s_bushes.Find(seed, 0.45, minX, maxX - 1, minZ, maxZ - 1, points);
        for (var i = 0; i < count; i++)
        {
            ref readonly var point = ref points[i];
            var state = point.State;
            if (
                point.Draw < 0.15 + 0.3 * Cover(point.X, point.Z, seed)
                && Free(point.X, point.Z, GameObjectKind.Bush, seed)
            )
            {
                Add(found, in point, GameObjectKind.Bush, ref state, seed, 0x62);
            }
        }

        points = Points(s_rocks.Capacity(minX, maxX - 1, minZ, maxZ - 1));
        count = s_rocks.Find(seed, 0.2, minX, maxX - 1, minZ, maxZ - 1, points);
        for (var i = 0; i < count; i++)
        {
            ref readonly var point = ref points[i];
            var state = point.State;
            if (
                point.Draw < 0.1 + 0.1 * Cover(point.X, point.Z, seed)
                && Free(point.X, point.Z, GameObjectKind.Rock, seed)
            )
            {
                Add(found, in point, GameObjectKind.Rock, ref state, seed, 0x72);
            }
        }
    }

    /// <summary>
    /// True when the boulder grid's point holds a boulder (out of the clear space round the middle), with its size (the
    /// one nearest the radius drawn) and the hash after the draw.
    /// </summary>
    private static bool Boulder(in JitteredPoint point, out int size, out ulong state)
    {
        state = point.State;
        var radius = DeterministicMath.NextRange(ref state, 100, 300);
        size = 0;
        var radii = GameObjects.BoulderRadii;
        for (var i = 1; i < radii.Count; i++)
        {
            size = Math.Abs(radii[i] - radius) < Math.Abs(radii[size] - radius) ? i : size;
        }

        var away = (double)ClearCentimetres + GameObjects.BoulderSpread(radii[size]);
        return (double)point.X * point.X + (double)point.Z * point.Z >= away * away;
    }

    /// <summary>
    /// True when a boulder's footprint (its spread, lumps included) lies within <paramref name="distance"/> of (x, z)
    /// across, in centimetres. Thread safe; allocates nothing for distances under 10 m.
    /// </summary>
    public static bool NearBoulder(int x, int z, int distance, ulong seed)
    {
        var reach = distance + GameObjects.BoulderSpread(GameObjects.BoulderRadii[^1]);
        var capacity = s_boulders.Capacity(x - reach, x + reach, z - reach, z + reach);
        Span<JitteredPoint> found = capacity <= 64 ? stackalloc JitteredPoint[64] : new JitteredPoint[capacity];
        var count = s_boulders.Find(seed, BoulderChance, x - reach, x + reach, z - reach, z + reach, found);
        for (var i = 0; i < count; i++)
        {
            if (!Boulder(in found[i], out var size, out _))
            {
                continue;
            }

            var spread = GameObjects.BoulderSpread(GameObjects.BoulderRadii[size]) + distance;
            double dx = x - found[i].X,
                dz = z - found[i].Z;
            if (dx * dx + dz * dz <= (double)spread * spread)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The top of what is solid at (x, z) in centimetres: the ground, or a boulder's top where one stands there (read from
    /// its voxels, <see cref="GameObjects.BoulderTop"/>). Where a player or a camera may start. Allocates at its first
    /// call; not for generation.
    /// </summary>
    public static double SurfaceHeight(int x, int z, ulong seed)
    {
        var top = GameGenerator.Height(x, z, seed);
        var reach = GameObjects.BoulderSpread(GameObjects.BoulderRadii[^1]);
        Span<JitteredPoint> found = stackalloc JitteredPoint[64];
        var count = s_boulders.Find(seed, BoulderChance, x - reach, x + reach, z - reach, z + reach, found);
        for (var i = 0; i < count; i++)
        {
            ref readonly var point = ref found[i];
            if (!Boulder(in point, out var size, out var state))
            {
                continue;
            }

            var shape = DeterministicMath.NextRange(ref state, 0, GameObjects.BoulderShapes - 1);
            var heading = DeterministicMath.NextRange(ref state, 0, ObjectPlacement.HeadingSteps - 1);
            var id = GameObjects.BoulderId(size, shape);
            var anchor = GameObjects.BoulderAnchor(id);
            // The point turned into the boulder's voxels.
            var (ox, oz) = ObjectTurns.ToObject(heading, x - point.X, z - point.Z);
            var boulder = GameObjects.BoulderTop(
                id,
                (int)DeterministicMath.FloorDivide(ox + anchor.X, GameObjects.UnitCentimetres),
                (int)DeterministicMath.FloorDivide(oz + anchor.Z, GameObjects.UnitCentimetres)
            );
            if (boulder != int.MinValue)
            {
                top = Math.Max(top, Math.Floor(GameGenerator.Height(point.X, point.Z, seed)) + boulder);
            }
        }

        return top;
    }

    /// <summary>
    /// True when the tree grid's point grows a tree (the cover there, the clear space, the boulders), with its kind and
    /// the hash after the kind's draw.
    /// </summary>
    private static bool Tree(in JitteredPoint point, ulong seed, out GameObjectKind kind, out ulong state)
    {
        state = point.State;
        kind = DeterministicMath.NextUnit(ref state) < YoungShare ? GameObjectKind.Young : GameObjectKind.Broadleaf;
        return point.Draw < TreeChance * Cover(point.X, point.Z, seed) && Clear(point.X, point.Z, kind, seed);
    }

    /// <summary>
    /// True when a kind's object at (x, z) keeps out of the clear space round the middle and off the boulders, and, for a
    /// bush or a rock, away from every tree's trunk.
    /// </summary>
    private static bool Free(int x, int z, GameObjectKind kind, ulong seed)
    {
        if (!Clear(x, z, kind, seed))
        {
            return false;
        }

        // The trees whose trunks could stand within the object's spread and a trunk's (60 cm) of it.
        var reach = GameObjects.SpreadCentimetres(kind) + 60;
        Span<JitteredPoint> trees = stackalloc JitteredPoint[16];
        var count = s_trees.Find(seed, TreeChance, x - reach, x + reach, z - reach, z + reach, trees);
        for (var i = 0; i < count; i++)
        {
            if (Tree(in trees[i], seed, out _, out _))
            {
                double dx = x - trees[i].X,
                    dz = z - trees[i].Z;
                if (dx * dx + dz * dz < (double)reach * reach)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>True when a kind's object at (x, z) keeps out of the clear space round the middle and off the boulders.</summary>
    private static bool Clear(int x, int z, GameObjectKind kind, ulong seed)
    {
        var spread = GameObjects.SpreadCentimetres(kind);
        var away = (double)ClearCentimetres + spread;
        return (double)x * x + (double)z * z >= away * away && !NearBoulder(x, z, spread, seed);
    }

    private static void Add(
        List<ObjectPlacement> found,
        in JitteredPoint point,
        GameObjectKind kind,
        ref ulong state,
        ulong seed,
        ulong salt
    )
    {
        var shape = DeterministicMath.NextRange(ref state, 0, GameObjects.Shapes - 1);
        var heading = DeterministicMath.NextRange(ref state, 0, ObjectPlacement.HeadingSteps - 1);
        var ground = (int)Math.Floor(GameGenerator.Height(point.X, point.Z, seed));
        found.Add(
            new ObjectPlacement(
                ObjectId.Of(in point, salt),
                GameObjects.Id(kind, shape),
                new VoxelCoord(point.X, ground, point.Z),
                heading
            )
        );
    }

    /// <summary>
    /// A spot inside a forest for the views of one (the benchmark's <c>forest</c>; absolute centimetres across): the first,
    /// out from the middle along eight directions in steps of 20 m, where the forest is whole for 40 m round, no boulder
    /// lies, and no object's trunk, bush, or rock stands within reach of a person. Allocates; not for generation.
    /// </summary>
    public (int X, int Z) ForestSpot(ulong seed)
    {
        var found = new List<ObjectPlacement>();
        for (var distance = 6_000; distance <= 400_000; distance += 2_000)
        {
            for (var direction = 0; direction < 8; direction++)
            {
                var (cos, sin) = DeterministicMath.CosSin(direction / 8.0);
                int x = (int)(cos * distance),
                    z = (int)(sin * distance);
                if (!Inside(x, z, seed) || NearBoulder(x, z, 200, seed))
                {
                    continue;
                }

                found.Clear();
                Find(seed, x - 500, x + 501, z - 500, z + 501, found);
                if (found.TrueForAll(placement => Away(placement, x, z)))
                {
                    return (x, z);
                }
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// The first spot from (<paramref name="x"/>, <paramref name="z"/>) along +x, a metre at a time and at most 20 m, where
    /// no object's trunk, bush, or rock stands within reach of a person (absolute centimetres; the spot given when none
    /// is). For views placed by distance alone. Allocates; not for generation.
    /// </summary>
    public (int X, int Z) ClearSpot(int x, int z, ulong seed)
    {
        var found = new List<ObjectPlacement>();
        for (var step = 0; step <= 20; step++)
        {
            var at = x + 100 * step;
            found.Clear();
            Find(seed, at - 500, at + 501, z - 500, z + 501, found);
            if (found.TrueForAll(placement => Away(placement, at, z)))
            {
                return (at, z);
            }
        }

        return (x, z);
    }

    /// <summary>True when the forest is whole at (x, z) and 40 m round it.</summary>
    private static bool Inside(int x, int z, ulong seed) =>
        Cover(x, z, seed) >= 1
        && Cover(x + 4_000, z, seed) >= 1
        && Cover(x - 4_000, z, seed) >= 1
        && Cover(x, z + 4_000, seed) >= 1
        && Cover(x, z - 4_000, seed) >= 1;

    /// <summary>True when a person at (x, z) stands clear of the object: a trunk's 1 m, or a bush's or rock's spread and 50 cm.</summary>
    private static bool Away(ObjectPlacement placement, int x, int z)
    {
        var kind = (GameObjectKind)(placement.Kind.Value / GameObjects.Shapes);
        var away = kind is GameObjectKind.Broadleaf or GameObjectKind.Young
            ? 100
            : GameObjects.SpreadCentimetres(kind) + 50;
        double dx = placement.Anchor.X - x,
            dz = placement.Anchor.Z - z;
        return dx * dx + dz * dz >= (double)away * away;
    }

    /// <summary>This thread's list of points, at least <paramref name="capacity"/> long.</summary>
    private static JitteredPoint[] Points(int capacity)
    {
        if (t_points is null || t_points.Length < capacity)
        {
            t_points = new JitteredPoint[Math.Max(capacity, 2 * (t_points?.Length ?? 64))];
        }

        return t_points;
    }
}
