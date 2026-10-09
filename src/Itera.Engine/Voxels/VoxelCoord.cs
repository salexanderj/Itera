using System.Numerics;

namespace Itera.Engine.Voxels;

/// <summary>A position in centimetres, with the origin at the map centre.</summary>
public readonly record struct VoxelCoord(int X, int Y, int Z)
{
    /// <summary>The centimetre position that contains <paramref name="metres"/>.</summary>
    public static VoxelCoord FromMetres(Vector3 metres) =>
        new(
            (int)MathF.Floor(metres.X * VoxelConstants.CentimetresPerMetre),
            (int)MathF.Floor(metres.Y * VoxelConstants.CentimetresPerMetre),
            (int)MathF.Floor(metres.Z * VoxelConstants.CentimetresPerMetre)
        );

    public static VoxelCoord operator +(VoxelCoord a, VoxelCoord b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static VoxelCoord operator -(VoxelCoord a, VoxelCoord b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>The voxel's lowest corner in metres.</summary>
    public Vector3 ToMetres() =>
        new(
            X / (float)VoxelConstants.CentimetresPerMetre,
            Y / (float)VoxelConstants.CentimetresPerMetre,
            Z / (float)VoxelConstants.CentimetresPerMetre
        );
}
