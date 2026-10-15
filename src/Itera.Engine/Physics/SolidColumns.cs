using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// A model's solid voxels by column, for <see cref="SolidModelVoxels"/>: for each (x, z) column of its box, the runs of
/// solid voxels along y, each from its lowest voxel to one above its highest. Every voxel that is not air is solid.
/// Built once (any thread), read only after.
/// </summary>
public sealed class SolidColumns
{
    private readonly int[] _offsets;
    private readonly ushort[] _spans;

    private SolidColumns(VoxelCoord size, int[] offsets, ushort[] spans, int solidVoxels)
    {
        Size = size;
        _offsets = offsets;
        _spans = spans;
        SolidVoxels = solidVoxels;
    }

    /// <summary>The box, in the model's voxels.</summary>
    public VoxelCoord Size { get; }

    /// <summary>Voxels that are not air.</summary>
    public int SolidVoxels { get; }

    /// <summary>
    /// The columns of the voxels in <paramref name="chunk"/> (a chunk at <c>x + 128y + 16384z</c>) within the box from
    /// (0, 0, 0) to <paramref name="size"/> (1 to 128 on each axis).
    /// </summary>
    public static SolidColumns Build(ReadOnlySpan<byte> chunk, VoxelCoord size)
    {
        const int edge = VoxelConstants.ChunkSize;
        if (chunk.Length != VoxelConstants.ChunkVoxels)
        {
            throw new ArgumentException("Columns are built from a whole chunk of voxels.", nameof(chunk));
        }

        if (size.X is < 1 or > edge || size.Y is < 1 or > edge || size.Z is < 1 or > edge)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A model's box is 1 to 128 voxels on each axis.");
        }

        var offsets = new int[size.X * size.Z + 1];
        var spans = new List<ushort>();
        var solid = 0;
        for (var z = 0; z < size.Z; z++)
        {
            for (var x = 0; x < size.X; x++)
            {
                offsets[x + size.X * z] = spans.Count;
                var from = -1;
                for (var y = 0; y <= size.Y; y++)
                {
                    var filled = y < size.Y && chunk[x + edge * y + edge * edge * z] != VoxelConstants.Air;
                    if (filled)
                    {
                        solid++;
                        if (from < 0)
                        {
                            from = y;
                        }
                    }
                    else if (from >= 0)
                    {
                        spans.Add((ushort)from);
                        spans.Add((ushort)y);
                        from = -1;
                    }
                }
            }
        }

        offsets[^1] = spans.Count;
        return new SolidColumns(size, offsets, [.. spans], solid);
    }

    /// <summary>
    /// The highest solid voxel of column (x, z) from y <paramref name="from"/> to <paramref name="to"/> (inclusive, the
    /// model's voxels), or −1 when there is none. The column must lie in the box.
    /// </summary>
    public int Highest(int x, int z, int from, int to)
    {
        var column = x + Size.X * z;
        for (var i = _offsets[column + 1] - 2; i >= _offsets[column]; i -= 2)
        {
            int low = _spans[i],
                high = _spans[i + 1] - 1;
            if (low <= to && high >= from)
            {
                return Math.Min(high, to);
            }
        }

        return -1;
    }

    /// <summary>
    /// The runs of column (x, z), lowest first, in pairs: each run's lowest voxel and one above its highest. The column
    /// must lie in the box.
    /// </summary>
    public ReadOnlySpan<ushort> Runs(int x, int z)
    {
        var column = x + Size.X * z;
        return _spans.AsSpan(_offsets[column], _offsets[column + 1] - _offsets[column]);
    }

    /// <summary>The lowest solid voxel of column (x, z) from y <paramref name="from"/> to <paramref name="to"/>, or −1.</summary>
    public int Lowest(int x, int z, int from, int to)
    {
        var column = x + Size.X * z;
        for (var i = _offsets[column]; i < _offsets[column + 1]; i += 2)
        {
            int low = _spans[i],
                high = _spans[i + 1] - 1;
            if (low <= to && high >= from)
            {
                return Math.Max(low, from);
            }
        }

        return -1;
    }
}
