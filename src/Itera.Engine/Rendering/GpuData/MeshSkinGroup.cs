namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One posed instance of a skinned mesh asset that <c>mesh-skin.comp</c> skins this frame (set 1 binding 6, one region a
/// frame slot): its asset's first vertex and first skin entry, the first vertex of its output (in the same vertex buffer,
/// the slot's skinned region), its first skinning matrix (binding 7), and its vertex count. Written by
/// <c>MeshAssetDraws</c>. 20 bytes.
/// </summary>
public struct MeshSkinGroup
{
    public uint SourceFirst;
    public uint SkinFirst;
    public uint OutputFirst;
    public uint PaletteFirst;
    public uint VertexCount;
}

/// <summary>
/// <c>mesh-skin.comp</c>'s push constants: the frame slot's first group (<see cref="MeshSkinGroup"/>) and how many it
/// skins (one workgroup row each). 8 bytes.
/// </summary>
public struct MeshSkinConstants
{
    public uint FirstGroup;
    public uint Groups;
}
