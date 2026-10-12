using System.Runtime.Intrinsics;

namespace Itera.Engine.Voxels;

/// <summary>
/// A world voxel's density: how much of its cube is solid, from 0 (none) to 255 (all), stored beside its material. A
/// voxel has a material exactly when its density is at least <see cref="Solid"/>: a solid voxel's density is 128 to 255,
/// an air voxel's 0 to 127, so everything that reads materials (rays, colliders, the mover, navigation) sees the same
/// world, and the density only places the drawn surface within a voxel. The mesher puts the surface where the density
/// between two voxels' centres crosses one half. A voxel whose density was never given has its material's
/// <see cref="Default"/>: air empty, solid full. Models (characters, items) keep only materials, except an object
/// kind's, which keep densities too.
/// </summary>
public static class VoxelDensity
{
    /// <summary>The density of a voxel with nothing solid in it.</summary>
    public const byte Empty = 0;

    /// <summary>The density of a voxel that is solid throughout.</summary>
    public const byte Full = 255;

    /// <summary>The least density of a solid voxel; an air voxel's is below it.</summary>
    public const byte Solid = 128;

    /// <summary>The density of a voxel of <paramref name="material"/> whose density was never given.</summary>
    public static byte Default(byte material) => material == VoxelConstants.Air ? Empty : Full;

    /// <summary>
    /// <paramref name="density"/> moved into the half that <paramref name="material"/> allows: at most 127 for air, at
    /// least 128 for a solid material.
    /// </summary>
    public static byte Fit(byte material, byte density) =>
        material == VoxelConstants.Air ? Math.Min(density, (byte)(Solid - 1)) : Math.Max(density, Solid);

    /// <summary>Fits every density to its voxel's material in place (<see cref="Fit"/>).</summary>
    public static void FitAll(ReadOnlySpan<byte> materials, Span<byte> densities)
    {
        var count = materials.Length;
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var below = Vector256.Create((byte)(Solid - 1));
            var solid = Vector256.Create(Solid);
            for (; i + Vector256<byte>.Count <= count; i += Vector256<byte>.Count)
            {
                var air = Vector256.Equals(
                    Vector256.Create(materials.Slice(i, Vector256<byte>.Count)),
                    Vector256<byte>.Zero
                );
                var given = Vector256.Create((ReadOnlySpan<byte>)densities.Slice(i, Vector256<byte>.Count));
                Vector256
                    .ConditionalSelect(air, Vector256.Min(given, below), Vector256.Max(given, solid))
                    .CopyTo(densities.Slice(i, Vector256<byte>.Count));
            }
        }

        for (; i < count; i++)
        {
            densities[i] = Fit(materials[i], densities[i]);
        }
    }

    /// <summary>
    /// Gives every voxel whose density says otherwise than its material (a solid one below one half, an air one at one
    /// half or more) its material's default, in place: for a generator whose shapes write materials over densities
    /// worked out before them.
    /// </summary>
    public static void Agree(ReadOnlySpan<byte> materials, Span<byte> densities)
    {
        var count = materials.Length;
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var below = Vector256.Create((byte)(Solid - 1));
            for (; i + Vector256<byte>.Count <= count; i += Vector256<byte>.Count)
            {
                var air = Vector256.Equals(
                    Vector256.Create(materials.Slice(i, Vector256<byte>.Count)),
                    Vector256<byte>.Zero
                );
                var given = Vector256.Create((ReadOnlySpan<byte>)densities.Slice(i, Vector256<byte>.Count));
                var solidDensity = Vector256.GreaterThan(given, below);
                var disagree = Vector256.Equals(air, solidDensity);
                Vector256.ConditionalSelect(disagree, ~air, given).CopyTo(densities.Slice(i, Vector256<byte>.Count));
            }
        }

        for (; i < count; i++)
        {
            if ((materials[i] == VoxelConstants.Air) == (densities[i] >= Solid))
            {
                densities[i] = Default(materials[i]);
            }
        }
    }

    /// <summary>Writes each voxel's default density (<see cref="Default"/>) into <paramref name="densities"/>.</summary>
    public static void Defaults(ReadOnlySpan<byte> materials, Span<byte> densities)
    {
        var count = materials.Length;
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + Vector256<byte>.Count <= count; i += Vector256<byte>.Count)
            {
                var air = Vector256.Equals(
                    Vector256.Create(materials.Slice(i, Vector256<byte>.Count)),
                    Vector256<byte>.Zero
                );
                (~air).CopyTo(densities.Slice(i, Vector256<byte>.Count));
            }
        }

        for (; i < count; i++)
        {
            densities[i] = Default(materials[i]);
        }
    }

    /// <summary>
    /// The density of a voxel <paramref name="covered"/> of whose <paramref name="total"/> equal parts are solid, rounded
    /// to the nearest step.
    /// </summary>
    public static byte OfShare(long covered, long total) => (byte)((covered * Full + total / 2) / total);
}
