using System.Numerics;

namespace Itera.Engine.Physics;

/// <summary>
/// What a movement mode (<c>IMovementMode</c>) asks of the world round an upright body: a wall ahead within reach
/// (<see cref="TryWallAhead"/>), the top of the ledge over it (<see cref="TryLedgeTop"/>), and room to stand there
/// (<see cref="HasRoom"/>). They read the shared ray (<see cref="CollisionSources"/>: the voxel terrain, with its
/// materials' surface tags, and the mesh colliders, with theirs; not loose bodies) and the character mover. A voxel's
/// normal is the face the ray entered, so a wall of voxels gives the mean of its rays' faces. Main thread; allocates
/// nothing.
/// </summary>
public sealed class MovementProbes(CollisionSources sources, VoxelCharacterMover mover)
{
    /// <summary>A hit is a wall when its normal's y lies within this of level: steeper than 60 degrees.</summary>
    public const float WallMostNormalY = 0.5f;

    /// <summary>How far over the step the lowest wall ray runs, in metres.</summary>
    public const float OverStepMetres = 0.05f;

    /// <summary>
    /// True when a wall lies ahead of the body along <paramref name="direction"/> (across x and z), no more than
    /// <paramref name="reachMetres"/> past its side: three rays from its axis, just over its step, at half its height, and
    /// at nine tenths of it, each meeting a surface steeper than <see cref="WallMostNormalY"/> allows. The wall is the
    /// nearest hit's point and tags, the mean of the hits' normals across x and z, and the nearest distance from the
    /// body's side.
    /// </summary>
    public bool TryWallAhead(
        Vector3 feetMetres,
        in CharacterShape shape,
        Vector2 direction,
        float reachMetres,
        out WallProbe wall
    )
    {
        wall = default;
        var length = direction.Length();
        if (!(length > 0) || !(reachMetres >= 0) || !float.IsFinite(reachMetres))
        {
            return false;
        }

        var way = new Vector3(direction.X / length, 0, direction.Y / length);
        var ray = shape.RadiusMetres + reachMetres;
        var nearest = default(SurfaceHit);
        var best = float.PositiveInfinity;
        var normal = Vector2.Zero;
        var rays = 0;
        for (var i = 0; i < 3; i++)
        {
            var height = i switch
            {
                0 => Math.Min(shape.StepMetres + OverStepMetres, shape.HeightMetres),
                1 => shape.HeightMetres * 0.5f,
                _ => shape.HeightMetres * 0.9f,
            };
            if (
                !sources.Raycast(feetMetres + new Vector3(0, height, 0), way, ray, out var hit)
                || !(MathF.Abs(hit.Normal.Y) < WallMostNormalY)
            )
            {
                continue;
            }

            rays++;
            normal += Vector2.Normalize(new Vector2(hit.Normal.X, hit.Normal.Z));
            if (hit.Distance < best)
            {
                best = hit.Distance;
                nearest = hit;
            }
        }

        if (rays == 0)
        {
            return false;
        }

        normal = normal.LengthSquared() > 1e-6f ? Vector2.Normalize(normal) : new Vector2(-way.X, -way.Z);
        wall = new WallProbe(nearest.Point, normal, nearest.Tags, best - shape.RadiusMetres, rays);
        return true;
    }

    /// <summary>
    /// True when the wall has a top the body could stand on: a ray straight down from <paramref name="aboveMetres"/> over
    /// the body's head, <paramref name="insetMetres"/> past the wall's face, meets ground no steeper than the shape's
    /// slope before it reaches the wall's point's height. False where the ray starts inside the wall (it goes on up) or
    /// meets nothing walkable. <paramref name="top"/> is where it met the ground.
    /// </summary>
    public bool TryLedgeTop(
        Vector3 feetMetres,
        in CharacterShape shape,
        in WallProbe wall,
        float aboveMetres,
        float insetMetres,
        out Vector3 top
    )
    {
        top = default;
        var start = feetMetres.Y + shape.HeightMetres + aboveMetres;
        var reach = start - wall.Point.Y;
        if (!(reach > 0) || !float.IsFinite(reach))
        {
            return false;
        }

        var origin = new Vector3(
            wall.Point.X - wall.Normal.X * insetMetres,
            start,
            wall.Point.Z - wall.Normal.Y * insetMetres
        );
        var walkable = MathF.Cos(shape.MaxSlopeDegrees * MathF.PI / 180);
        if (
            !sources.Raycast(origin, -Vector3.UnitY, reach, out var hit)
            || !(hit.Distance > 1e-3f)
            || !(hit.Normal.Y >= walkable)
        )
        {
            return false;
        }

        top = hit.Point;
        return true;
    }

    /// <summary>True when a body of <paramref name="shape"/> would overlap nothing with its feet at <paramref name="feetMetres"/>.</summary>
    public bool HasRoom(Vector3 feetMetres, in CharacterShape shape) => !mover.OverlapsAnything(feetMetres, in shape);
}

/// <summary>A wall ahead of a body (<see cref="MovementProbes.TryWallAhead"/>).</summary>
/// <param name="Point">Where the nearest ray met it, in metres.</param>
/// <param name="Normal">The mean of the rays' normals across x and z, a unit length, out of the wall.</param>
/// <param name="Tags">The nearest hit's surface tags.</param>
/// <param name="DistanceMetres">From the body's side to the nearest hit.</param>
/// <param name="Rays">How many of the three rays met it.</param>
public readonly record struct WallProbe(Vector3 Point, Vector2 Normal, uint Tags, float DistanceMetres, int Rays);
