using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// Where an object's level-0 voxels are read from, in its kind's own voxels from its box's corner (air of no density
/// outside the box): the shape it was built from, a kind's parts, or a damaged object's. The parts' levels are built
/// through it, so a damaged object's are made by the kind's rule.
/// </summary>
internal interface IObjectVoxelSource
{
    void Voxel(int x, int y, int z, out byte material, out byte density);
}

/// <summary>A shape's voxels, as a source.</summary>
internal readonly struct ShapeVoxels(VoxelObjectShape shape) : IObjectVoxelSource
{
    public void Voxel(int x, int y, int z, out byte material, out byte density)
    {
        material = shape.MaterialAt(x, y, z);
        density = shape.DensityAt(x, y, z);
    }
}

/// <summary>
/// A part's level-0 voxels decoded into a 128^3 chunk (index <c>x + 128y + 16384z</c>, its box's corner, its border's,
/// at 0), as a source in the part box's own voxels: air of no density outside the chunk.
/// </summary>
internal readonly ref struct ChunkVoxels(ReadOnlySpan<byte> materials, ReadOnlySpan<byte> densities)
    : IObjectVoxelSource
{
    private readonly ReadOnlySpan<byte> _materials = materials;
    private readonly ReadOnlySpan<byte> _densities = densities;

    public void Voxel(int x, int y, int z, out byte material, out byte density)
    {
        const int edge = VoxelConstants.ChunkSize;
        if ((uint)x >= edge || (uint)y >= edge || (uint)z >= edge)
        {
            material = VoxelConstants.Air;
            density = VoxelDensity.Empty;
            return;
        }

        var i = x + edge * (y + edge * z);
        material = _materials[i];
        density = _densities[i];
    }
}
