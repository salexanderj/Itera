using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One draw of a mesh asset's primitive in a list of the mesh pass (<c>mesh-asset-data.glsl</c>'s <c>MeshAssetDraw</c>,
/// set 1 binding 4, read at <c>gl_InstanceIndex</c>): its transform from the asset's space to the world's (uploaded
/// unchanged and used as <c>M * v</c>), its material's index among every asset's (<see cref="MeshAssetMaterial"/>), its
/// tint (linear RGBA bytes its colours are multiplied by, red in the low byte), and flags. 76 bytes. Written by
/// <c>MeshAssetDraws</c> into the frame slot's region, one per instance and primitive in each list that draws it.
/// </summary>
public struct MeshAssetDraw
{
    public Matrix4x4 Transform;

    public uint Material;

    public uint Tint;

    public uint Flags;
}
