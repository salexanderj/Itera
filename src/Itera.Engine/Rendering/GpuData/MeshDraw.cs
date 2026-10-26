using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One block's draw in the raster pipeline (<c>mesh-data.glsl</c>'s <c>MeshDraw</c>, read by <c>mesh.vert</c> at
/// <c>gl_InstanceIndex</c>): where the block's voxels start in the world and how large they are, and where its vertices
/// start in the mesh heap. Written per frame, one region per frame slot, by <c>WorldMeshes</c>.
/// </summary>
public struct MeshDraw
{
    /// <summary>The block's lowest corner in world metres.</summary>
    public Vector3 Origin;

    /// <summary>
    /// The edge of one of the block's voxels in metres: the grid's edge at the block's level (<c>VoxelGrid.EdgeMetres</c>).
    /// </summary>
    public float VoxelMetres;

    /// <summary>
    /// The first word of the block's vertices in the mesh heap (<see cref="MeshVertex"/>, <see cref="MeshVertex.Words"/>
    /// words each).
    /// </summary>
    public uint VertexBase;
}
