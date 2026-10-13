using System.Numerics;
using Arch.Core;
using Itera.Engine.Maths;

namespace Itera.Engine.Physics;

/// <summary>
/// A capsule a character's body part takes up: the points within <paramref name="Radius"/> of the segment from
/// <paramref name="A"/> to <paramref name="B"/>. Both ends are in local metres, and may be the same point (a sphere).
/// </summary>
/// <param name="A">One end of the axis.</param>
/// <param name="B">The other end.</param>
/// <param name="Radius">The radius in metres, 0 or more.</param>
/// <param name="Part">The game's number for the body part the capsule stands for, returned with a hit.</param>
public readonly record struct HitCapsule(Vector3 A, Vector3 B, float Radius, int Part);

/// <summary>What <see cref="HitHistory.Raycast"/> met first.</summary>
/// <param name="Entity">The entity whose capsule it was.</param>
/// <param name="Part">The capsule's part number.</param>
/// <param name="Distance">The distance along the ray from its start, in metres.</param>
/// <param name="Point">Where the ray met it, in local metres as the match's origin is now.</param>
/// <param name="Normal">The capsule's outward normal there.</param>
public readonly record struct HitResult(Entity Entity, int Part, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>
/// Where characters' hit shapes were over the last <see cref="Steps"/> steps, so that a host judges a shot against where
/// its targets were when the shooter saw them (lag compensation), not where they are now. A game records each
/// character's capsules once a step (<see cref="Record"/>) and asks with the tick the shooter saw
/// (<see cref="Raycast"/>), which may fall between two steps: each capsule is then taken as far between its two records
/// as the fraction says. Records are kept in absolute terms (an anchor and offsets from it), so a move of the match's
/// origin between steps changes nothing. A character with no record near the tick is not there and is not met. Each
/// entity's storage is made at its first record and reused after <see cref="Forget"/>; once an entity is kept, recording,
/// asking, and forgetting allocate nothing. Scoped to the match; main thread.
/// </summary>
public sealed class HitHistory(WorldOrigin origin)
{
    /// <summary>How many steps of each entity it keeps: more than the steps a client may be shown behind the host.</summary>
    public const int Steps = 32;

    /// <summary>The most capsules one entity records in a step.</summary>
    public const int MostCapsules = 32;

    /// <summary>The most entities it keeps.</summary>
    public const int MostEntities = 1024;

    // A ray this much outside a bounding sphere still tests the capsules: float error must not drop a graze.
    private const float SphereSlack = 1e-3f;

    private readonly Dictionary<Entity, int> _slots = [];
    private readonly List<Track> _tracks = [];
    private readonly int[] _free = new int[MostEntities];
    private int _freeCount;

    /// <summary>How many entities it keeps records of.</summary>
    public int Count => _slots.Count;

    /// <summary>
    /// Records <paramref name="entity"/>'s capsules at step <paramref name="tick"/>, in local metres as the origin is now.
    /// Recording a tick again replaces it. A tick 32 or more steps older than a newer one kept is dropped. An empty list
    /// records that the entity is there with no shape.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The entity is null, there are more than <see cref="MostCapsules"/> capsules, or a capsule has a radius below 0 or a
    /// number that is not finite.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The tick is below 0.</exception>
    /// <exception cref="InvalidOperationException">The entity is new and <see cref="MostEntities"/> are kept.</exception>
    public void Record(Entity entity, long tick, ReadOnlySpan<HitCapsule> capsules)
    {
        if (entity == Entity.Null)
        {
            throw new ArgumentException("A record needs an entity.", nameof(entity));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (capsules.Length > MostCapsules)
        {
            throw new ArgumentException(
                $"An entity records at most {MostCapsules} capsules a step, not {capsules.Length}.",
                nameof(capsules)
            );
        }

        foreach (var capsule in capsules)
        {
            if (!IsValid(in capsule))
            {
                throw new ArgumentException(
                    $"A capsule with a radius below 0 or a number not finite: {capsule}.",
                    nameof(capsules)
                );
            }
        }

        if (!_slots.TryGetValue(entity, out var slot))
        {
            if (_slots.Count >= MostEntities)
            {
                throw new InvalidOperationException($"{MostEntities} entities are kept; {entity} is one more.");
            }

            slot = Take();
            _tracks[slot].Entity = entity;
            _slots.Add(entity, slot);
        }

        var track = _tracks[slot];
        var step = (int)(tick % Steps);
        if (track.Ticks[step] > tick)
        {
            return;
        }

        // The first capsule's A anchors the step, and every end is kept as the float offset from it.
        var anchorLocal = capsules.Length > 0 ? capsules[0].A : Vector3.Zero;
        track.Ticks[step] = tick;
        track.Counts[step] = capsules.Length;
        track.Anchors[step] = capsules.Length > 0 ? origin.ToWorld(anchorLocal) : default;
        var stored = track.Capsules.AsSpan(step * MostCapsules, capsules.Length);
        for (var i = 0; i < stored.Length; i++)
        {
            var capsule = capsules[i];
            stored[i] = capsule with { A = capsule.A - anchorLocal, B = capsule.B - anchorLocal };
        }

        track.Bounds[step] = BoundsOf(stored);
        if (tick > track.Newest)
        {
            track.Newest = tick;
        }

        track.Oldest = long.MaxValue;
        foreach (var kept in track.Ticks)
        {
            if (kept >= 0 && kept < track.Oldest)
            {
                track.Oldest = kept;
            }
        }
    }

    /// <summary>
    /// The nearest capsule that a ray from <paramref name="from"/> (local metres, as the origin is now) along
    /// <paramref name="direction"/> (any length but zero) meets within <paramref name="maxMetres"/>, with each entity's
    /// capsules where they were at <paramref name="tick"/> (a step number; a fraction falls between two steps). Entities
    /// are taken from their records of the steps either side of the tick, each capsule's ends moved straight between them
    /// by the fraction (a capsule is matched with the one at the same place in the other step's list, if it has the same
    /// part number; else each is used where it was recorded). With only one of the two steps recorded, that one is used;
    /// with neither, the step before the tick, or the one after it when the tick falls between steps. An entity whose
    /// newest record is older than a step before the tick, or whose oldest is newer than a step after it, is not there
    /// and is not met. <paramref name="ignore"/> is skipped (the shooter; <see cref="Entity.Null"/> for none). A ray that
    /// starts inside a capsule meets it at distance 0 with the normal opposite the ray. Returns false for a zero or
    /// non-finite direction or start, a negative or non-finite range, or a tick that is not finite. Allocates nothing.
    /// </summary>
    public bool Raycast(Vector3 from, Vector3 direction, float maxMetres, double tick, Entity ignore, out HitResult hit)
    {
        hit = default;
        var lengthSquared = direction.LengthSquared();
        if (
            !(lengthSquared > 0)
            || !float.IsFinite(lengthSquared)
            || !float.IsFinite(from.X + from.Y + from.Z)
            || !float.IsFinite(maxMetres)
            || maxMetres < 0
            || !double.IsFinite(tick)
            || Math.Abs(tick) > 1e15
        )
        {
            return false;
        }

        var ray = new Ray(from, direction / MathF.Sqrt(lengthSquared), origin.Metres);
        var floor = (long)Math.Floor(tick);
        var fraction = (float)(tick - floor);
        var best = new Best { Distance = maxMetres };
        foreach (var track in _tracks)
        {
            if (!track.InUse || track.Entity == ignore)
            {
                continue;
            }

            // Present from a step before the tick to a step after it.
            var ceiling = fraction > 0 ? floor + 1 : floor;
            if (track.Newest < floor - 1 || track.Oldest > ceiling + 1)
            {
                continue;
            }

            var before = track.Find(floor);
            var after = track.Find(floor + 1);
            if (before < 0 && after < 0)
            {
                before = track.Find(floor - 1);
                if (before < 0 && fraction > 0)
                {
                    before = track.Find(floor + 2);
                }

                if (before < 0)
                {
                    continue;
                }
            }

            TestTrack(track, before, after, fraction, in ray, ref best);
        }

        if (!best.Found)
        {
            return false;
        }

        hit = new HitResult(best.Entity, best.Part, best.Distance, from + ray.Direction * best.Distance, best.Normal);
        return true;
    }

    /// <summary>Drops <paramref name="entity"/>'s records (nothing, for one not kept) and keeps its storage for another.</summary>
    public void Forget(Entity entity)
    {
        if (_slots.Remove(entity, out var slot))
        {
            _tracks[slot].InUse = false;
            _free[_freeCount++] = slot;
        }
    }

    /// <summary>Drops every record.</summary>
    public void Clear()
    {
        _slots.Clear();
        _freeCount = 0;
        for (var slot = _tracks.Count - 1; slot >= 0; slot--)
        {
            _tracks[slot].InUse = false;
            _free[_freeCount++] = slot;
        }
    }

    private int Take()
    {
        int slot;
        if (_freeCount > 0)
        {
            slot = _free[--_freeCount];
        }
        else
        {
            slot = _tracks.Count;
            _tracks.Add(new Track());
        }

        _tracks[slot].Start();
        return slot;
    }

    private static bool IsValid(in HitCapsule capsule) =>
        capsule.Radius >= 0
        && float.IsFinite(capsule.Radius)
        && float.IsFinite(capsule.A.X + capsule.A.Y + capsule.A.Z)
        && float.IsFinite(capsule.B.X + capsule.B.Y + capsule.B.Z);

    /// <summary>The sphere round some capsules, from their offsets: its centre, and its radius (below 0 for none).</summary>
    private static Vector4 BoundsOf(ReadOnlySpan<HitCapsule> capsules)
    {
        if (capsules.Length == 0)
        {
            return new Vector4(0, 0, 0, -1);
        }

        var low = new Vector3(float.PositiveInfinity);
        var high = new Vector3(float.NegativeInfinity);
        foreach (var capsule in capsules)
        {
            low = Vector3.Min(low, Vector3.Min(capsule.A, capsule.B));
            high = Vector3.Max(high, Vector3.Max(capsule.A, capsule.B));
        }

        var centre = (low + high) * 0.5f;
        var radius = 0f;
        foreach (var capsule in capsules)
        {
            var reach = MathF.Max((capsule.A - centre).Length(), (capsule.B - centre).Length()) + capsule.Radius;
            radius = MathF.Max(radius, reach);
        }

        return new Vector4(centre, radius);
    }

    private static void TestTrack(Track track, int before, int after, float fraction, in Ray ray, ref Best best)
    {
        var origin0 = Vector3.Zero;
        var origin1 = Vector3.Zero;
        var count0 = 0;
        var count1 = 0;
        var sphere0 = new Vector4(0, 0, 0, -1);
        var sphere1 = sphere0;
        if (before >= 0)
        {
            origin0 = ray.Local(track.Anchors[before]);
            count0 = track.Counts[before];
            sphere0 = Sphere(origin0, track.Bounds[before]);
        }

        if (after >= 0)
        {
            origin1 = ray.Local(track.Anchors[after]);
            count1 = track.Counts[after];
            sphere1 = Sphere(origin1, track.Bounds[after]);
        }

        // Matched capsules lie within the blend of the two spheres; unmatched ones within their own.
        var blended = count0 > 0 && count1 > 0 ? Vector4.Lerp(sphere0, sphere1, fraction) : new Vector4(0, 0, 0, -1);
        if (
            ray.Misses(sphere0, best.Distance)
            && ray.Misses(sphere1, best.Distance)
            && ray.Misses(blended, best.Distance)
        )
        {
            return;
        }

        for (var i = 0; i < Math.Max(count0, count1); i++)
        {
            var c0 = i < count0 ? track.Capsules[before * MostCapsules + i] : default;
            var c1 = i < count1 ? track.Capsules[after * MostCapsules + i] : default;
            if (i < count0 && i < count1 && c0.Part == c1.Part)
            {
                Test(
                    track,
                    in ray,
                    Vector3.Lerp(origin0 + c0.A, origin1 + c1.A, fraction),
                    Vector3.Lerp(origin0 + c0.B, origin1 + c1.B, fraction),
                    float.Lerp(c0.Radius, c1.Radius, fraction),
                    c0.Part,
                    ref best
                );
                continue;
            }

            if (i < count0)
            {
                Test(track, in ray, origin0 + c0.A, origin0 + c0.B, c0.Radius, c0.Part, ref best);
            }

            if (i < count1)
            {
                Test(track, in ray, origin1 + c1.A, origin1 + c1.B, c1.Radius, c1.Part, ref best);
            }
        }
    }

    private static Vector4 Sphere(Vector3 anchor, Vector4 bounds) =>
        bounds.W < 0 ? bounds : new Vector4(anchor + new Vector3(bounds.X, bounds.Y, bounds.Z), bounds.W);

    private static void Test(Track track, in Ray ray, Vector3 a, Vector3 b, float radius, int part, ref Best best)
    {
        if (
            HitShapes.RaycastCapsule(ray.From, ray.Direction, a, b, radius, out var distance, out var normal)
            && distance <= best.Distance
            && (!best.Found || distance < best.Distance)
        )
        {
            best = new Best
            {
                Found = true,
                Distance = distance,
                Entity = track.Entity,
                Part = part,
                Normal = normal,
            };
        }
    }

    /// <summary>A ray in the match's local metres, with the origin's place for turning anchors local.</summary>
    private readonly struct Ray(Vector3 from, Vector3 direction, WorldPoint originMetres)
    {
        public Vector3 From => from;

        public Vector3 Direction => direction;

        /// <summary>The local place of an absolute anchor.</summary>
        public Vector3 Local(in WorldPoint anchor) =>
            new(
                (float)(anchor.X - originMetres.X),
                (float)(anchor.Y - originMetres.Y),
                (float)(anchor.Z - originMetres.Z)
            );

        /// <summary>True when the ray cannot meet a sphere of this centre and radius (W) within the range.</summary>
        public bool Misses(Vector4 sphere, float range)
        {
            if (sphere.W < 0)
            {
                return true;
            }

            var radius = sphere.W + SphereSlack;
            var offset = new Vector3(sphere.X, sphere.Y, sphere.Z) - from;
            var distanceSquared = offset.LengthSquared();
            if (distanceSquared <= radius * radius)
            {
                return false;
            }

            var along = Vector3.Dot(offset, direction);
            return along < 0 || along - radius > range || distanceSquared - along * along > radius * radius;
        }
    }

    private struct Best
    {
        public bool Found;
        public float Distance;
        public Entity Entity;
        public int Part;
        public Vector3 Normal;
    }

    /// <summary>One entity's ring of steps: for the step at ring place <c>tick % Steps</c>, its tick, capsules, and bounds.</summary>
    private sealed class Track
    {
        public readonly long[] Ticks = new long[Steps];
        public readonly int[] Counts = new int[Steps];
        public readonly WorldPoint[] Anchors = new WorldPoint[Steps];
        public readonly Vector4[] Bounds = new Vector4[Steps];
        public readonly HitCapsule[] Capsules = new HitCapsule[Steps * MostCapsules];
        public Entity Entity;
        public bool InUse;
        public long Newest;
        public long Oldest;

        /// <summary>Empties the ring for an entity.</summary>
        public void Start()
        {
            Array.Fill(Ticks, -1L);
            Array.Clear(Counts);
            InUse = true;
            Newest = -1;
            Oldest = long.MaxValue;
        }

        /// <summary>The ring place that holds the record of step <paramref name="tick"/>, or -1.</summary>
        public int Find(long tick)
        {
            if (tick < 0)
            {
                return -1;
            }

            var step = (int)(tick % Steps);
            return Ticks[step] == tick ? step : -1;
        }
    }
}
