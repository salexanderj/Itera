namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// Row helpers for filling shapes into a dense chunk: a chunk is filled row by row along x, and each shape works out the
/// range of x, in centimetres, that its row covers. A voxel takes a material when its sample point lies in that range.
/// </summary>
public static class VoxelRows
{
    /// <summary>Fills the row's voxels whose sample x lies within <paramref name="half"/> of <paramref name="centre"/>.</summary>
    public static void FillSpan(Span<byte> row, int centre, int firstX, GridLevel level, int half, byte material) =>
        FillRange(row, (long)centre - half, (long)centre + half, firstX, level, material);

    /// <summary>Fills the row's voxels whose sample x lies in the range [<paramref name="min"/>, <paramref name="max"/>] of centimetres.</summary>
    public static void FillRange(Span<byte> row, long min, long max, int firstX, GridLevel level, byte material)
    {
        var (start, end) = Range(row.Length, min, max, firstX, level);
        if (start < end)
        {
            row[start..end].Fill(material);
        }
    }

    /// <summary>Fills the row's voxels whose sample x lies in the range [<paramref name="min"/>, <paramref name="max"/>] of centimetres.</summary>
    public static void FillRange(Span<byte> row, double min, double max, int firstX, GridLevel level, byte material) =>
        FillRange(row, (long)Math.Ceiling(min), (long)Math.Floor(max), firstX, level, material);

    /// <summary>Like <see cref="FillSpan"/>, but only air voxels take the material.</summary>
    public static void ReplaceAirInSpan(
        Span<byte> row,
        int centre,
        int firstX,
        GridLevel level,
        int half,
        byte material
    )
    {
        var (start, end) = Range(row.Length, (long)centre - half, (long)centre + half, firstX, level);
        if (start < end)
        {
            row[start..end].Replace(VoxelConstants.Air, material);
        }
    }

    /// <summary>
    /// Like <see cref="FillRange(Span{byte}, double, double, int, GridLevel, byte)"/>, but only air voxels take the material.
    /// </summary>
    public static void ReplaceAirInRange(
        Span<byte> row,
        double min,
        double max,
        int firstX,
        GridLevel level,
        byte material
    )
    {
        var (start, end) = Range(row.Length, (long)Math.Ceiling(min), (long)Math.Floor(max), firstX, level);
        if (start < end)
        {
            row[start..end].Replace(VoxelConstants.Air, material);
        }
    }

    /// <summary>
    /// Like <see cref="FillRange(Span{byte}, double, double, int, GridLevel, byte)"/>, but only voxels of
    /// <paramref name="from"/> become <paramref name="to"/>.
    /// </summary>
    public static void ReplaceInRange(
        Span<byte> row,
        double min,
        double max,
        int firstX,
        GridLevel level,
        byte from,
        byte to
    )
    {
        var (start, end) = Range(row.Length, (long)Math.Ceiling(min), (long)Math.Floor(max), firstX, level);
        if (start < end)
        {
            row[start..end].Replace(from, to);
        }
    }

    /// <summary>
    /// The chunk-local rows [start, end) along one axis whose sample points lie in the range [min, max] of centimetres,
    /// for a chunk whose first voxel on that axis is <paramref name="first"/> (in voxels of <paramref name="level"/>).
    /// </summary>
    public static (int Start, int End) Rows(int min, int max, int first, GridLevel level) =>
        (
            Math.Max(0, level.FirstSampleAtOrAfter(min) - first),
            Math.Min(VoxelConstants.ChunkSize, level.FirstSampleAtOrAfter(max + 1) - first)
        );

    /// <summary>Like <see cref="Rows(int, int, int, GridLevel)"/> for a range with fractional ends.</summary>
    public static (int Start, int End) Rows(double min, double max, int first, GridLevel level) =>
        Rows((int)Math.Ceiling(min), (int)Math.Floor(max), first, level);

    private static (int Start, int End) Range(int length, long min, long max, int firstX, GridLevel level) =>
        (
            Math.Max(0, level.FirstSampleAtOrAfter((int)min) - firstX),
            Math.Min(length, level.FirstSampleAtOrAfter((int)max + 1) - firstX)
        );
}
