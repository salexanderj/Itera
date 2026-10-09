using Itera.Engine.Maths;

namespace Itera.Engine.Voxels.Generation;

/// <summary>Exact fills of upright ellipsoids, row by row in integers.</summary>
public static class Ellipsoid
{
    /// <summary>
    /// Writes <paramref name="material"/> over the voxels of a dense chunk whose sample points lie inside the ellipsoid
    /// about (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) with these radii, all in centimetres. A
    /// point is inside when <c>dx²ry²rz² + dy²rx²rz² + dz²rx²ry² &lt;= rx²ry²rz²</c>; every product fits in a
    /// <see cref="long"/> for radii up to about 1000.
    /// </summary>
    public static void Fill(
        int x,
        int y,
        int z,
        int radiusX,
        int radiusY,
        int radiusZ,
        byte material,
        VoxelCoord first,
        GridLevel level,
        Span<byte> voxels
    )
    {
        const int size = VoxelConstants.ChunkSize;
        long xx = (long)radiusX * radiusX;
        long yy = (long)radiusY * radiusY;
        long zz = (long)radiusZ * radiusZ;
        var whole = xx * yy * zz;
        var (zStart, zEnd) = VoxelRows.Rows(z - radiusZ, z + radiusZ, first.Z, level);
        var (yStart, yEnd) = VoxelRows.Rows(y - radiusY, y + radiusY, first.Y, level);
        for (var row = zStart; row < zEnd; row++)
        {
            long dz = level.SampleAt(first.Z + row) - z;
            for (var column = yStart; column < yEnd; column++)
            {
                long dy = level.SampleAt(first.Y + column) - y;
                var room = whole - dy * dy * xx * zz - dz * dz * xx * yy;
                if (room >= 0)
                {
                    var half = DeterministicMath.SquareRoot(room / (yy * zz));
                    VoxelRows.FillSpan(
                        voxels.Slice((row * size + column) * size, size),
                        x,
                        first.X,
                        level,
                        half,
                        material
                    );
                }
            }
        }
    }
}
