namespace Itera.Engine.Voxels;

/// <summary>
/// A position in whole centimetres from the map's middle, whatever the match's origin: voxels, edits, saves, and what
/// peers send. A local position in metres becomes one only through <see cref="Maths.WorldOrigin"/>.
/// </summary>
public readonly record struct VoxelCoord(int X, int Y, int Z)
{
    public static VoxelCoord operator +(VoxelCoord a, VoxelCoord b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static VoxelCoord operator -(VoxelCoord a, VoxelCoord b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}
