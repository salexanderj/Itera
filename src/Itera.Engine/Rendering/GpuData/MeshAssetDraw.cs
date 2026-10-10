using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One draw of a mesh asset's primitive in a list of the mesh pass (<c>mesh-asset-data.glsl</c>'s <c>MeshAssetDraw</c>,
/// set 1 binding 4, read at <c>gl_InstanceIndex</c>): its transform from the asset's space to the world's (uploaded
/// unchanged and used as <c>M * v</c>), its material's index among every asset's (<see cref="MeshAssetMaterial"/>), its
/// tint (linear RGBA bytes its colours are multiplied by, red in the low byte), and what to add to
/// <c>gl_VertexIndex</c> to read its vertices: 0 for the asset's own, or the distance from them to this instance's
/// skinned copy in the frame slot's region. 76 bytes. Written by <c>MeshAssetDraws</c> into the frame slot's region,
/// one per instance and primitive in each list that draws it.
/// </summary>
public struct MeshAssetDraw
{
    public Matrix4x4 Transform;

    public uint Material;

    public uint Tint;

    public int VertexDelta;
}
