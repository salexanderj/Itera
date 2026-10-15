using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// What a match's rays and its character mover meet: the voxel terrain and the voxel objects its world places
/// (<see cref="ObjectRays"/>), when the match has terrain, the mesh colliders a game registered
/// (<see cref="MeshColliders"/>), and the solid models a game places (<see cref="SolidModelVoxels"/>, a gate's leaf; with
/// the loose bodies' voxels and the match's origin, which the solids' poses are absolute in). <see cref="Raycast"/> answers from all of them, the nearest winning, each hit with its
/// surface's tags (<see cref="IVoxelSurfaceTags"/> for voxels, the objects' too, the collider's material's for meshes).
/// The voxel tools' own ray, past passable voxels, is <c>ToolAim.Raycast</c>. Scoped to the match; main thread.
/// </summary>
public sealed class CollisionSources(
    MeshColliders meshes,
    IVoxelSurfaceTags voxelTags,
    VoxelWorld? voxels = null,
    ObjectRays? objects = null,
    LooseBodyVoxels? bodies = null,
    WorldOrigin? worldOrigin = null
)
{
    /// <summary>The match's mesh colliders.</summary>
    public MeshColliders Meshes => meshes;

    /// <summary>The match's voxel terrain, or null without one.</summary>
    public VoxelWorld? Voxels => voxels;

    /// <summary>The rays through the voxel objects the match's world places, or null without terrain.</summary>
    public ObjectRays? Objects => objects;

    /// <summary>The tags of the voxels' materials.</summary>
    public IVoxelSurfaceTags VoxelTags => voxelTags;

    /// <summary>
    /// The nearest surface a ray from <paramref name="origin"/> along <paramref name="direction"/> (any length but zero)
    /// meets within <paramref name="maxMetres"/>: a voxel of the terrain (through coarser levels where level 0 is not
    /// loaded with <paramref name="coarse"/>), a voxel object's voxel, or a mesh collider's triangle, from either side.
    /// Allocates nothing once the colliders' buckets are made.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxMetres, out SurfaceHit hit, bool coarse = false)
    {
        var found = meshes.Raycast(origin, direction, maxMetres, out hit);
        var d = Vector3.Normalize(direction);
        if (
            voxels is not null
            && voxels.Raycast(origin, direction, found ? hit.Distance : maxMetres, out var voxel, coarse)
            && (!found || voxel.DistanceMetres < hit.Distance)
        )
        {
            hit = FromVoxel(origin, d, in voxel, voxelTags);
            found = true;
        }

        if (
            objects is not null
            && objects.Raycast(origin, direction, found ? hit.Distance : maxMetres, out var met)
            && (!found || met.DistanceMetres < hit.Distance)
        )
        {
            hit = FromObject(origin, d, in met, voxelTags);
            found = true;
        }

        if (
            bodies is { Solids.Count: > 0 }
            && worldOrigin is not null
            && bodies.Solids.Raycast(
                worldOrigin.ToWorld(origin),
                direction,
                found ? hit.Distance : maxMetres,
                out var solid
            )
            && (!found || solid.DistanceMetres < hit.Distance)
        )
        {
            hit = new SurfaceHit(
                origin + d * solid.DistanceMetres,
                solid.Normal,
                solid.DistanceMetres,
                0,
                SurfaceSource.SolidModel,
                Collider: solid.Id
            );
            found = true;
        }

        return found;
    }

    /// <summary>A terrain voxel's hit along the unit <paramref name="direction"/> from <paramref name="origin"/>.</summary>
    internal static SurfaceHit FromVoxel(
        Vector3 origin,
        Vector3 direction,
        in VoxelHit voxel,
        IVoxelSurfaceTags tags
    ) =>
        new(
            origin + direction * voxel.DistanceMetres,
            new Vector3(voxel.Normal.X, voxel.Normal.Y, voxel.Normal.Z),
            voxel.DistanceMetres,
            tags.TagsOf(voxel.Material),
            SurfaceSource.Voxel,
            voxel.Material
        );

    /// <summary>An object voxel's hit along the unit <paramref name="direction"/> from <paramref name="origin"/>.</summary>
    internal static SurfaceHit FromObject(
        Vector3 origin,
        Vector3 direction,
        in ObjectHit met,
        IVoxelSurfaceTags tags
    ) =>
        new(
            origin + direction * met.DistanceMetres,
            met.Normal,
            met.DistanceMetres,
            tags.TagsOf(met.Material),
            SurfaceSource.VoxelObject,
            met.Material,
            ObjectId: met.Id
        );
}
