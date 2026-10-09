using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One block of one model drawn (<c>mesh-data.glsl</c>'s <c>MeshModelDraw</c>, read by <c>mesh.vert</c> and
/// <c>mesh-shadow.vert</c> compiled with <c>MESH_MODEL</c>, at <c>gl_InstanceIndex</c>): where the model's box stands
/// and how it is turned, how large its voxels are at the level drawn, where the block lies in the model and where its
/// vertices start in the mesh heap, and how far its materials blend to their tint colours. The draws of one block of
/// one model level lie together, so one indexed indirect command draws them all as instances. Written per frame, one
/// region per frame slot and list, by <c>WorldMeshes</c>.
/// </summary>
public struct MeshModelDraw
{
    /// <summary>The corner of the model's box (its voxel (0, 0, 0)) in world metres.</summary>
    public Vector3 Corner;

    /// <summary>The edge of one of the model's voxels at the level drawn, in metres.</summary>
    public float VoxelMetres;

    /// <summary>The turn from the model's axes to the world's.</summary>
    public Quaternion Turn;

    /// <summary>The first word of the block's vertices in the mesh heap (<see cref="MeshVertex"/>, three words each).</summary>
    public uint VertexBase;

    /// <summary>The block's place in the model, in blocks of 32 voxels: x, y, and z in bits 0, 8, and 16.</summary>
    public uint Block;

    /// <summary>How far the materials blend from their base to their tint colour, 0 to 1.</summary>
    public float Tint;

    /// <summary>The packed <see cref="Block"/> of block (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>).</summary>
    public static uint PackBlock(int x, int y, int z) => (uint)x | ((uint)y << 8) | ((uint)z << 16);
}
