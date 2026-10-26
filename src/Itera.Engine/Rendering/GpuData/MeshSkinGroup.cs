namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One posed instance's level of a skinned mesh asset that <c>mesh-skin.comp</c> skins this frame (set 1 binding 6, one
/// region a frame slot): its asset's first vertex and first skin entry, the first vertex of its output (in the skinned
/// streams, bindings 8 and 9; a slot's groups' outputs lie one after another), its first skinning matrix (binding 7),
/// and its vertex count. Written by <c>MeshAssetDraws</c>. 20 bytes.
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
/// skins, and the slot's first skinned vertex and how many there are (one invocation each). 16 bytes.
/// </summary>
public struct MeshSkinConstants
{
    public uint FirstGroup;
    public uint Groups;
    public uint FirstVertex;
    public uint Vertices;
}
