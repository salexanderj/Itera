using System.Numerics;

namespace Itera.Engine.Physics;

/// <summary>What a ray met: the voxel terrain or a mesh collider.</summary>
public enum SurfaceSource : byte
{
    Voxel = 0,
    Mesh = 1,
}

/// <summary>
/// Where a ray met a surface (<see cref="CollisionSources.Raycast"/>, <see cref="MeshColliders.Raycast"/>): the point in
/// metres, the surface's normal turned towards the ray's origin, the distance along the ray in metres, the surface's
/// tags (<c>IVoxelSurfaceTags</c>, or the mesh collider's material's), what it met, and for a voxel its material, for a
/// mesh its collider and triangle.
/// </summary>
public readonly record struct SurfaceHit(
    Vector3 Point,
    Vector3 Normal,
    float Distance,
    uint Tags,
    SurfaceSource Source,
    byte Material = 0,
    int Collider = -1,
    int Triangle = -1
);
