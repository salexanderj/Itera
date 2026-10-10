using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// What a match's rays and its character mover meet: the voxel terrain, when the match has one, and the mesh colliders
/// a game registered (<see cref="MeshColliders"/>). <see cref="Raycast"/> answers from both, the nearer winning, each hit
/// with its surface's tags (<see cref="IVoxelSurfaceTags"/> for voxels, the collider's material's for meshes). The voxel
/// tools' ray (<c>ToolAim</c>) stays the terrain's own. Scoped to the match; main thread.
/// </summary>
public sealed class CollisionSources(MeshColliders meshes, IVoxelSurfaceTags voxelTags, VoxelWorld? voxels = null)
{
    /// <summary>The match's mesh colliders.</summary>
    public MeshColliders Meshes => meshes;

    /// <summary>The match's voxel terrain, or null without one.</summary>
    public VoxelWorld? Voxels => voxels;

    /// <summary>
    /// The nearest surface a ray from <paramref name="origin"/> along <paramref name="direction"/> (any length but zero)
    /// meets within <paramref name="maxMetres"/>: a voxel of the terrain (through coarser levels where level 0 is not
    /// loaded with <paramref name="coarse"/>) or a mesh collider's triangle, from either side. Allocates nothing once the
    /// colliders' buckets are made.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxMetres, out SurfaceHit hit, bool coarse = false)
    {
        var found = meshes.Raycast(origin, direction, maxMetres, out hit);
        if (
            voxels is null
            || !voxels.Raycast(origin, direction, found ? hit.Distance : maxMetres, out var voxel, coarse)
            || (found && voxel.DistanceMetres >= hit.Distance)
        )
        {
            return found;
        }

        var d = Vector3.Normalize(direction);
        hit = new SurfaceHit(
            origin + d * voxel.DistanceMetres,
            new Vector3(voxel.Normal.X, voxel.Normal.Y, voxel.Normal.Z),
            voxel.DistanceMetres,
            voxelTags.TagsOf(voxel.Material),
            SurfaceSource.Voxel,
            voxel.Material
        );
        return true;
    }
}
