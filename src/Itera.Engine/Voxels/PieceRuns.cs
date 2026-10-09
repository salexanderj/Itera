namespace Itera.Engine.Voxels;

/// <summary>
/// A lifted piece's voxels as runs of one material over its box, x fastest, then y, then z: how a save holds a piece
/// (<see cref="EditLogSerializer"/>) and how the host sends one with its lift. A piece's voxels are mostly air around a
/// solid ball, so runs take a small part of its volume.
/// </summary>
internal static class PieceRuns
{
    /// <summary>
    /// Appends the runs of <paramref name="piece"/> to <paramref name="runs"/>, read from <paramref name="unpacked"/>, the
    /// piece's box already decoded into a chunk's scratch (<see cref="ChunkTree.Decode(VoxelMemory, ChildState, Span{byte}, VoxelCoord)"/>
    /// with its size): one read a voxel, not a walk down the tree (2.7 ms for a 73 cm ball, sent to each
    /// client).
    /// </summary>
    public static void Collect(in VoxelPiece piece, ReadOnlySpan<byte> unpacked, List<(byte Material, int Length)> runs)
    {
        var run = 0;
        byte current = 0;
        for (var z = 0; z < piece.Size.Z; z++)
        {
            for (var y = 0; y < piece.Size.Y; y++)
            {
                var row = unpacked.Slice(VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z), piece.Size.X);
                foreach (var material in row)
                {
                    if (run > 0 && material != current)
                    {
                        runs.Add((current, run));
                        run = 0;
                    }

                    current = material;
                    run++;
                }
            }
        }

        runs.Add((current, run));
    }

    /// <summary>
    /// Returns why <paramref name="runs"/> cannot be a piece of <paramref name="size"/>, or null when every run holds at
    /// least one voxel and together they fill the box exactly. The box is at most a chunk on each side (a lift's).
    /// </summary>
    public static string? Check(VoxelCoord size, ReadOnlySpan<(byte Material, int Length)> runs)
    {
        if (
            size.X < 1
            || size.Y < 1
            || size.Z < 1
            || size.X > VoxelConstants.ChunkSize
            || size.Y > VoxelConstants.ChunkSize
            || size.Z > VoxelConstants.ChunkSize
        )
        {
            return $"a box of {size} is not a lift's";
        }

        long left = size.X * size.Y * size.Z;
        foreach (var (_, length) in runs)
        {
            if (length < 1 || length > left)
            {
                return $"a run of {length} voxels with {left} left";
            }

            left -= length;
        }

        return left == 0 ? null : $"the runs leave {left} voxels of the box empty";
    }
}
