using System.Numerics;

namespace Itera.Engine.Spatial;

public sealed partial class SpatialGrid
{
    // Steps of the search for a line's nearest point to a cylinder: the bracket shrinks to 0.618^32 of the line, 2e-7.
    private const int SearchSteps = 32;
    private const float Golden = 0.618034f;

    /// <summary>
    /// Hands <paramref name="visitor"/> every live entry whose tags match and whose cylinder comes within
    /// <paramref name="radius"/> of <paramref name="centre"/> (local metres), with the distance to it.
    /// </summary>
    /// <exception cref="ArgumentException">The centre is not finite, or the radius is below 0 or not a number.</exception>
    public void Within<TVisitor>(Vector3 centre, float radius, SpatialMatch match, ref TVisitor visitor)
        where TVisitor : struct, ISpatialVisitor, allows ref struct
    {
        Check(centre, nameof(centre));
        CheckRadius(radius);
        var offset = Offset();
        var shape = new Point(centre + offset, radius);
        Walk(
            in shape,
            shape.Centre.X - radius,
            shape.Centre.Z - radius,
            shape.Centre.X + radius,
            shape.Centre.Z + radius,
            match,
            offset,
            ref visitor
        );
    }

    /// <summary>
    /// Hands <paramref name="visitor"/> every live entry whose tags match and whose cylinder comes within
    /// <paramref name="radius"/> of <paramref name="centre"/> (local metres) across the ground, at any height, with the
    /// distance across to it.
    /// </summary>
    /// <exception cref="ArgumentException">The centre is not finite, or the radius is below 0 or not a number.</exception>
    public void Across<TVisitor>(Vector3 centre, float radius, SpatialMatch match, ref TVisitor visitor)
        where TVisitor : struct, ISpatialVisitor, allows ref struct
    {
        Check(centre, nameof(centre));
        CheckRadius(radius);
        var offset = Offset();
        var shape = new Column(centre + offset, radius);
        Walk(
            in shape,
            shape.Centre.X - radius,
            shape.Centre.Z - radius,
            shape.Centre.X + radius,
            shape.Centre.Z + radius,
            match,
            offset,
            ref visitor
        );
    }

    /// <summary>
    /// Hands <paramref name="visitor"/> every live entry whose tags match and whose cylinder comes within
    /// <paramref name="radius"/> of the line from <paramref name="from"/> to <paramref name="to"/> (local metres), with the
    /// least distance between them. It goes over the cells of the line's box, so a line much longer than a cell is
    /// better split by the caller where it crosses open ground.
    /// </summary>
    /// <exception cref="ArgumentException">An end is not finite, or the radius is below 0 or not a number.</exception>
    public void Along<TVisitor>(Vector3 from, Vector3 to, float radius, SpatialMatch match, ref TVisitor visitor)
        where TVisitor : struct, ISpatialVisitor, allows ref struct
    {
        Check(from, nameof(from));
        Check(to, nameof(to));
        CheckRadius(radius);
        var offset = Offset();
        var shape = new Line(from + offset, to + offset, radius);
        Walk(
            in shape,
            MathF.Min(shape.From.X, shape.To.X) - radius,
            MathF.Min(shape.From.Z, shape.To.Z) - radius,
            MathF.Max(shape.From.X, shape.To.X) + radius,
            MathF.Max(shape.From.Z, shape.To.Z) + radius,
            match,
            offset,
            ref visitor
        );
    }

    /// <summary>
    /// Fills <paramref name="nearest"/> with the live entries nearest <paramref name="centre"/> within
    /// <paramref name="radius"/> whose tags match and that <paramref name="filter"/> keeps, nearest first, as many as it
    /// holds; entries as near are in the order of their entities (by id, then version), so the answer does not hang on the
    /// order they were set in. Returns how many it holds.
    /// </summary>
    /// <exception cref="ArgumentException">The centre is not finite, or the radius is below 0 or not a number.</exception>
    public int Nearest<TFilter>(
        Vector3 centre,
        float radius,
        SpatialMatch match,
        in TFilter filter,
        Span<SpatialHit> nearest
    )
        where TFilter : struct, ISpatialFilter
    {
        var keeper = new Keeper<TFilter>(filter, nearest);
        Within(centre, radius, match, ref keeper);
        return keeper.Count;
    }

    /// <summary>The live entry nearest <paramref name="centre"/>, as <see cref="Nearest{TFilter}(Vector3, float, SpatialMatch, in TFilter, Span{SpatialHit})"/> finds it; false when none.</summary>
    public bool Nearest<TFilter>(
        Vector3 centre,
        float radius,
        SpatialMatch match,
        in TFilter filter,
        out SpatialHit hit
    )
        where TFilter : struct, ISpatialFilter
    {
        Span<SpatialHit> one = stackalloc SpatialHit[1];
        var found = Nearest(centre, radius, match, in filter, one) > 0;
        hit = found ? one[0] : default;
        return found;
    }

    /// <summary>True when a live entry whose tags match comes within <paramref name="radius"/> of <paramref name="centre"/>.</summary>
    /// <exception cref="ArgumentException">The centre is not finite, or the radius is below 0 or not a number.</exception>
    public bool Any(Vector3 centre, float radius, SpatialMatch match)
    {
        var finder = new Finder();
        Within(centre, radius, match, ref finder);
        return finder.Found;
    }

    /// <summary>
    /// The distance from <paramref name="point"/> to the cylinder of <paramref name="entry"/>, both in the same frame: 0
    /// inside it.
    /// </summary>
    public static float DistanceTo(in SpatialEntry entry, Vector3 point) =>
        MathF.Sqrt(SquaredDistance(in entry, point));

    private static float SquaredDistance(in SpatialEntry entry, Vector3 point)
    {
        var dx = point.X - entry.Feet.X;
        var dz = point.Z - entry.Feet.Z;
        var across = MathF.Max(MathF.Sqrt(dx * dx + dz * dz) - entry.Radius, 0);
        var up = MathF.Max(MathF.Max(entry.Feet.Y - point.Y, point.Y - (entry.Feet.Y + entry.Height)), 0);
        return across * across + up * up;
    }

    /// <summary>
    /// The least squared distance from the line from <paramref name="from"/> to <paramref name="to"/> to the cylinder of
    /// <paramref name="entry"/>. A point's distance to a cylinder only falls and then rises along a line (the cylinder is
    /// convex), so a golden-section search over the line finds its least.
    /// </summary>
    private static float SquaredDistance(in SpatialEntry entry, Vector3 from, Vector3 to)
    {
        var way = to - from;
        float low = 0,
            high = 1;
        var a = high - Golden;
        var b = low + Golden;
        var atA = SquaredDistance(in entry, from + a * way);
        var atB = SquaredDistance(in entry, from + b * way);
        for (var step = 0; step < SearchSteps; step++)
        {
            if (atA <= atB)
            {
                high = b;
                b = a;
                atB = atA;
                a = high - Golden * (high - low);
                atA = SquaredDistance(in entry, from + a * way);
            }
            else
            {
                low = a;
                a = b;
                atA = atB;
                b = low + Golden * (high - low);
                atB = SquaredDistance(in entry, from + b * way);
            }
        }

        var ends = MathF.Min(SquaredDistance(in entry, from), SquaredDistance(in entry, to));
        return MathF.Min(MathF.Min(atA, atB), ends);
    }

    /// <summary>
    /// Offers the visitor every entry whose cell may lie in the box from (minX, minZ) to (maxX, maxZ) (grid frame,
    /// widened by the widest binned entry), going over every slot instead when the box covers more cells than there are
    /// entries, and then the wide entries.
    /// </summary>
    private void Walk<TShape, TVisitor>(
        in TShape shape,
        float minX,
        float minZ,
        float maxX,
        float maxZ,
        SpatialMatch match,
        Vector3 offset,
        ref TVisitor visitor
    )
        where TShape : struct, IShape
        where TVisitor : struct, ISpatialVisitor, allows ref struct
    {
        if (_binned > 0)
        {
            var x0 = Cell(minX - _widestBinned);
            var z0 = Cell(minZ - _widestBinned);
            var x1 = Cell(maxX + _widestBinned);
            var z1 = Cell(maxZ + _widestBinned);
            if (((long)x1 - x0 + 1) * ((long)z1 - z0 + 1) > _binned)
            {
                for (var slot = 0; slot < _used; slot++)
                {
                    if (_bucketOf[slot] >= 0 && !Offer(slot, in shape, match, offset, ref visitor))
                    {
                        return;
                    }
                }
            }
            else
            {
                for (var z = z0; z <= z1; z++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        for (var slot = _heads[Bucket(x, z)]; slot >= 0; slot = _next[slot])
                        {
                            if (
                                _cellX[slot] == x
                                && _cellZ[slot] == z
                                && !Offer(slot, in shape, match, offset, ref visitor)
                            )
                            {
                                return;
                            }
                        }
                    }
                }
            }
        }

        for (var slot = _wideHead; slot >= 0; slot = _next[slot])
        {
            if (!Offer(slot, in shape, match, offset, ref visitor))
            {
                return;
            }
        }
    }

    /// <summary>Hands the slot's entry to the visitor when it matches, reaches the shape, and is alive; false to stop.</summary>
    private bool Offer<TShape, TVisitor>(
        int slot,
        in TShape shape,
        SpatialMatch match,
        Vector3 offset,
        ref TVisitor visitor
    )
        where TShape : struct, IShape
        where TVisitor : struct, ISpatialVisitor, allows ref struct
    {
        ref readonly var stored = ref _entries[slot];
        if (
            !match.Matches(stored.Tags)
            || !shape.Reaches(in stored, out var distance)
            || !_world.IsAlive(stored.Entity)
        )
        {
            return true;
        }

        var entry = stored with { Feet = stored.Feet - offset };
        return visitor.Visit(in entry, distance);
    }

    private static void Check(Vector3 point, string name)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
        {
            throw new ArgumentException($"A query's point is finite, not {point}.", name);
        }
    }

    private static void CheckRadius(float radius)
    {
        if (!(radius >= 0))
        {
            throw new ArgumentException($"A query's radius is 0 or more, not {radius}.", nameof(radius));
        }
    }

    /// <summary>What a query asks about, in the grid's frame: whether an entry's cylinder reaches it, and how far it is.</summary>
    private interface IShape
    {
        bool Reaches(in SpatialEntry stored, out float distance);
    }

    private readonly struct Point(Vector3 centre, float radius) : IShape
    {
        public Vector3 Centre { get; } = centre;

        public bool Reaches(in SpatialEntry stored, out float distance)
        {
            var squared = SquaredDistance(in stored, Centre);
            distance = MathF.Sqrt(squared);
            return squared <= radius * radius;
        }
    }

    private readonly struct Column(Vector3 centre, float radius) : IShape
    {
        public Vector3 Centre { get; } = centre;

        public bool Reaches(in SpatialEntry stored, out float distance)
        {
            var dx = Centre.X - stored.Feet.X;
            var dz = Centre.Z - stored.Feet.Z;
            distance = MathF.Max(MathF.Sqrt(dx * dx + dz * dz) - stored.Radius, 0);
            return distance <= radius;
        }
    }

    private readonly struct Line(Vector3 from, Vector3 to, float radius) : IShape
    {
        public Vector3 From { get; } = from;
        public Vector3 To { get; } = to;

        public bool Reaches(in SpatialEntry stored, out float distance)
        {
            distance = float.PositiveInfinity;
            var bottom = MathF.Min(From.Y, To.Y);
            var top = MathF.Max(From.Y, To.Y);
            if (bottom > stored.Feet.Y + stored.Height + radius || top < stored.Feet.Y - radius)
            {
                return false;
            }

            var squared = SquaredDistance(in stored, From, To);
            distance = MathF.Sqrt(squared);
            return squared <= radius * radius;
        }
    }

    /// <summary>Keeps the nearest entries a filter allows, nearest first, ties by entity.</summary>
    private ref struct Keeper<TFilter>(TFilter filter, Span<SpatialHit> kept) : ISpatialVisitor
        where TFilter : struct, ISpatialFilter
    {
        private readonly Span<SpatialHit> _kept = kept;

        public int Count { get; private set; }

        public bool Visit(in SpatialEntry entry, float distance)
        {
            if (_kept.Length == 0 || !filter.Includes(in entry))
            {
                return true;
            }

            var hit = new SpatialHit(entry, distance);
            if (Count == _kept.Length && !Before(in hit, in _kept[Count - 1]))
            {
                return true;
            }

            var at = Count < _kept.Length ? Count++ : Count - 1;
            while (at > 0 && Before(in hit, in _kept[at - 1]))
            {
                _kept[at] = _kept[at - 1];
                at--;
            }

            _kept[at] = hit;
            return true;
        }

        private static bool Before(in SpatialHit a, in SpatialHit b) =>
            a.Distance != b.Distance ? a.Distance < b.Distance
            : a.Entry.Entity.Id != b.Entry.Entity.Id ? a.Entry.Entity.Id < b.Entry.Entity.Id
            : a.Entry.Entity.Version < b.Entry.Entity.Version;
    }

    private struct Finder : ISpatialVisitor
    {
        public bool Found { get; private set; }

        public bool Visit(in SpatialEntry entry, float distance)
        {
            Found = true;
            return false;
        }
    }
}
