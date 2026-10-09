using System.Runtime.InteropServices;

namespace Itera.Engine.Voxels;

/// <summary>
/// The 12-byte record stored beside a brick's 64-byte occupancy mask. <see cref="Bits"/> is the width of each voxel's
/// palette index: 0 means every solid voxel is <see cref="MaterialOrCount"/> (air when none is) and there is no block;
/// 1, 2, or 4 mean the block holds a 16-byte palette of <see cref="MaterialOrCount"/> materials followed by the indices;
/// 8 means the block holds one material byte per voxel. <see cref="Density"/> is the brick's block of densities in
/// <see cref="VoxelPool.Density"/>, one byte per voxel (<see cref="VoxelDensity"/>), or -1 when every voxel has its
/// material's default (<see cref="VoxelDensity.Default"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BrickRecord
{
    public byte Bits;
    public byte MaterialOrCount;
    public ushort Reserved;
    public int Block;
    public int Density;
}

/// <summary>
/// A node of the chunk tree: a root node (children are 32 cm cells) or a cell node (children are 8 cm bricks).
/// <see cref="ChildMask"/> marks the children that are not all air, <see cref="UniformMask"/> the ones that are one
/// solid material. The list block holds one entry per child in <see cref="ChildMask"/>, in child order: the material
/// for a uniform child, otherwise the child's node or brick handle.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NodeRecord
{
    public ulong ChildMask;
    public ulong UniformMask;
    public int List;
    public int ListClass;
}
