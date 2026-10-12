using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// What a ray met of a placed object (<see cref="ObjectRays.Raycast"/>): the object, its kind, the kind's voxel (its
/// index in the kind's own voxels from its box's corner, not centimetres), the face of it the ray entered as a direction
/// in the world's frame (zero when the ray starts inside it), its material, and how far along the ray it was entered and
/// where the ray leaves that voxel, in metres (also past the ray's length).
/// </summary>
public readonly record struct ObjectHit(
    ObjectId Id,
    VoxelObjectKindId Kind,
    VoxelCoord Voxel,
    Vector3 Normal,
    byte Material,
    float DistanceMetres,
    float ExitMetres
);

/// <summary>
/// Rays through the voxel objects a match's world places (<see cref="ObjectWorld"/>): the first solid voxel of any
/// object along a ray, as the world's ray finds its voxels (<see cref="VoxelWorld.Raycast"/>: any voxel of a material,
/// passable ones too). The ray is walked in stretches of <see cref="StretchMetres"/>, each asking for the objects whose
/// reach meets it (<see cref="ObjectWorld.Near"/>; objects of areas not filled are not met), and turned into each
/// object's own voxels (a damaged object's as edits left them, <see cref="ObjectDamage"/>), which it steps through one by
/// one inside the object's box. A ray looks at most
/// <see cref="MostMetres"/>. Scoped to the match, with voxel terrain; main thread; allocates nothing.
/// </summary>
public sealed class ObjectRays(
    ObjectWorld objects,
    VoxelObjectKinds kinds,
    VoxelWorld world,
    ObjectDamage? damage = null
)
{
    /// <summary>How long a stretch of the ray each search for objects covers, in metres.</summary>
    public const float StretchMetres = 16;

    /// <summary>The farthest a ray looks for objects, in metres.</summary>
    public const float MostMetres = 10_000;

    private const double Scale = 1 << ObjectTurns.Shift;

    private readonly List<ObjectPlacement> _near = [];

    /// <summary>
    /// The first solid voxel of a placed object along the ray from <paramref name="originMetres"/> (local to the world's
    /// origin) along <paramref name="direction"/> (any length but zero) within <paramref name="maxMetres"/>; false for
    /// none.
    /// </summary>
    public bool Raycast(Vector3 originMetres, Vector3 direction, float maxMetres, out ObjectHit hit)
    {
        hit = default;
        var length = direction.Length();
        if (!(length > 0) || !float.IsFinite(length))
        {
            throw new ArgumentException("The ray direction must be finite and not zero.", nameof(direction));
        }

        if (objects.Count == 0 || !(maxMetres > 0))
        {
            return false;
        }

        // The ray's start in absolute centimetres and its direction, in doubles.
        var origin = world.Origin;
        double sx = origin.WorldX(originMetres.X) * VoxelConstants.CentimetresPerMetre,
            sy = origin.WorldY(originMetres.Y) * VoxelConstants.CentimetresPerMetre,
            sz = origin.WorldZ(originMetres.Z) * VoxelConstants.CentimetresPerMetre;
        double dx = direction.X / (double)length,
            dy = direction.Y / (double)length,
            dz = direction.Z / (double)length;
        var best = Math.Min((double)maxMetres, MostMetres);
        var found = false;
        for (var from = 0.0; from < best; from += StretchMetres)
        {
            var to = Math.Min(from + StretchMetres, best);
            const double perMetre = VoxelConstants.CentimetresPerMetre;
            double ax = sx + dx * from * perMetre,
                bx = sx + dx * to * perMetre,
                az = sz + dz * from * perMetre,
                bz = sz + dz * to * perMetre;
            _near.Clear();
            objects.Near(
                (int)Math.Floor(Math.Min(ax, bx)),
                (int)Math.Ceiling(Math.Max(ax, bx)),
                (int)Math.Floor(Math.Min(az, bz)),
                (int)Math.Ceiling(Math.Max(az, bz)),
                _near
            );
            foreach (var placement in _near)
            {
                if (Through(in placement, sx, sy, sz, dx, dy, dz, best, out var met))
                {
                    best = met.DistanceMetres;
                    hit = met;
                    found = true;
                }
            }

            // Every object the ray meets before the stretch's end reaches into a stretch searched by now.
            if (found && best <= to)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Walks the ray from absolute centimetre (sx, sy, sz) along the unit (dx, dy, dz) through one object's voxels, to
    /// <paramref name="most"/> metres: its first solid voxel.
    /// </summary>
    private bool Through(
        in ObjectPlacement placement,
        double sx,
        double sy,
        double sz,
        double dx,
        double dy,
        double dz,
        double most,
        out ObjectHit hit
    )
    {
        hit = default;
        var kind = kinds[placement.Kind];
        IObjectVoxelSource voxels = (IObjectVoxelSource?)damage?.Find(placement.Id) ?? kind;
        var anchor = placement.Anchor;
        double cos = ObjectTurns.Cos(placement.Heading) / Scale,
            sin = ObjectTurns.Sin(placement.Heading) / Scale;
        double rx = sx - anchor.X,
            ry = sy - anchor.Y,
            rz = sz - anchor.Z;
        // Along the object's own axes, then in its voxels from its box's corner; the direction in voxels a metre.
        var unit = (double)kind.UnitCentimetres;
        var offset = kind.AnchorCentimetres;
        Span<double> start = stackalloc double[3];
        start[0] = (rx * cos + rz * sin + offset.X) / unit;
        start[1] = (ry + offset.Y) / unit;
        start[2] = (rz * cos - rx * sin + offset.Z) / unit;
        Span<double> along = stackalloc double[3];
        along[0] = (dx * cos + dz * sin) * VoxelConstants.CentimetresPerMetre / unit;
        along[1] = dy * VoxelConstants.CentimetresPerMetre / unit;
        along[2] = (dz * cos - dx * sin) * VoxelConstants.CentimetresPerMetre / unit;
        Span<int> size = stackalloc int[3];
        size[0] = kind.Size.X;
        size[1] = kind.Size.Y;
        size[2] = kind.Size.Z;

        // The stretch of the ray inside the kind's box, and the side it enters through.
        var enter = 0.0;
        var leave = most;
        var axis = -1;
        for (var a = 0; a < 3; a++)
        {
            if (Math.Abs(along[a]) < 1e-12)
            {
                if (start[a] < 0 || start[a] >= size[a])
                {
                    return false;
                }

                continue;
            }

            var low = -start[a] / along[a];
            var high = (size[a] - start[a]) / along[a];
            if (low > high)
            {
                (low, high) = (high, low);
            }

            // A ray starting on the box's side and going in enters through that side.
            if (low >= enter)
            {
                enter = low;
                axis = a;
            }

            leave = Math.Min(leave, high);
        }

        // None that only touches the box, or starts on its side going away.
        if (enter >= leave)
        {
            return false;
        }

        // The voxel it enters, and the distances to each axis's next boundary.
        Span<int> voxel = stackalloc int[3];
        Span<int> step = stackalloc int[3];
        Span<double> next = stackalloc double[3];
        Span<double> across = stackalloc double[3];
        for (var a = 0; a < 3; a++)
        {
            // On a boundary, the voxel past it along the ray.
            var at = start[a] + along[a] * enter;
            var into = along[a] < 0 ? (int)Math.Ceiling(at) - 1 : (int)Math.Floor(at);
            voxel[a] = Math.Clamp(into, 0, size[a] - 1);
            step[a] = along[a] > 0 ? 1 : -1;
            across[a] = Math.Abs(along[a]) < 1e-12 ? double.PositiveInfinity : 1 / Math.Abs(along[a]);
            next[a] =
                Math.Abs(along[a]) < 1e-12
                    ? double.PositiveInfinity
                    : (voxel[a] + (along[a] > 0 ? 1 : 0) - start[a]) / along[a];
        }

        var t = enter;
        while (true)
        {
            voxels.Voxel(voxel[0], voxel[1], voxel[2], out var material, out _);
            var nearest = next[0] < next[1] ? (next[0] < next[2] ? 0 : 2) : (next[1] < next[2] ? 1 : 2);
            if (material != VoxelConstants.Air)
            {
                // The face entered, along the object's axes, turned to the world's.
                var normal = Vector3.Zero;
                if (axis >= 0)
                {
                    var own = new Vector3(axis == 0 ? -step[0] : 0, axis == 1 ? -step[1] : 0, axis == 2 ? -step[2] : 0);
                    normal = new Vector3((float)(own.X * cos - own.Z * sin), own.Y, (float)(own.X * sin + own.Z * cos));
                }

                hit = new ObjectHit(
                    placement.Id,
                    placement.Kind,
                    new VoxelCoord(voxel[0], voxel[1], voxel[2]),
                    normal,
                    material,
                    (float)t,
                    (float)next[nearest]
                );
                return true;
            }

            t = next[nearest];
            if (t > leave)
            {
                return false;
            }

            voxel[nearest] += step[nearest];
            if ((uint)voxel[nearest] >= (uint)size[nearest])
            {
                return false;
            }

            next[nearest] += across[nearest];
            axis = nearest;
        }
    }
}
