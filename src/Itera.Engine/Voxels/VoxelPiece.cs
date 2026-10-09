namespace Itera.Engine.Voxels;

/// <summary>
/// The voxels a lift edit took out of the world (<see cref="VoxelEditOperation.Lift"/>): a box of at most 128 voxels on
/// each side, stored as one chunk tree in the world's voxel memory, with air where the edit did not reach. The piece is
/// named by the lift's sequence. It never changes after it is made, so worker threads read it while they replay edits.
/// </summary>
/// <param name="Sequence">The lift edit's sequence.</param>
/// <param name="Min">The lowest level-0 voxel of the box in the world, where it was lifted from.</param>
/// <param name="Size">The box's size in voxels, 1 to 128 on each axis.</param>
/// <param name="Tree">The voxels, at chunk-local (x, y, z) within <paramref name="Size"/>.</param>
/// <param name="SolidVoxels">Voxels of the piece that are not air.</param>
/// <param name="Radius">
/// The distance from the pivot to the farthest corner of a solid voxel, rounded up, in voxels
/// (<see cref="FarthestCornerSquared"/>, <see cref="RadiusOf"/>). A piece edit's bounds reach this far.
/// </param>
internal readonly record struct VoxelPiece(
    long Sequence,
    VoxelCoord Min,
    VoxelCoord Size,
    ChildState Tree,
    int SolidVoxels,
    int Radius
)
{
    /// <summary>
    /// The pivot a piece turns about and is placed by: the voxel corner at the middle of the box (rounded down), in the
    /// piece's own voxels. When lifted, it sat at world voxel corner <c>Min + Pivot</c>.
    /// </summary>
    public VoxelCoord Pivot => new(Size.X >> 1, Size.Y >> 1, Size.Z >> 1);

    /// <summary>
    /// The square of the distance from <paramref name="pivot"/> (a voxel corner) to the farthest corner of voxel (x, y, z),
    /// for finding a piece's radius.
    /// </summary>
    public static long FarthestCornerSquared(VoxelCoord pivot, int x, int y, int z)
    {
        long dx = Math.Max(Math.Abs(x - pivot.X), Math.Abs(x + 1 - pivot.X)),
            dy = Math.Max(Math.Abs(y - pivot.Y), Math.Abs(y + 1 - pivot.Y)),
            dz = Math.Max(Math.Abs(z - pivot.Z), Math.Abs(z + 1 - pivot.Z));
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>
    /// The largest radius a piece has: a lift is at most a chunk (128 voxels) on each side, so its farthest corner lies
    /// at most 64 voxels from the pivot on each axis, sqrt(3) x 64 rounded up. A piece edit asks for no more
    /// (<see cref="VoxelEdit.Validate"/>).
    /// </summary>
    public const int MaxRadius = 111;

    /// <summary>The radius, rounded up, of a squared distance from <see cref="FarthestCornerSquared"/>.</summary>
    public static int RadiusOf(long squared)
    {
        var radius = (long)Math.Sqrt(squared);
        while (radius * radius < squared)
        {
            radius++;
        }

        return (int)radius;
    }

    /// <summary>The material at piece voxel (x, y, z); air outside the box.</summary>
    public byte Read(VoxelMemory memory, int x, int y, int z) =>
        (uint)x < (uint)Size.X && (uint)y < (uint)Size.Y && (uint)z < (uint)Size.Z
            ? ChunkTree.ReadVoxel(memory, Tree, x, y, z)
            : VoxelConstants.Air;
}

/// <summary>
/// A piece and where its voxels are read: from its tree in voxel memory (any thread), or, on the main thread, unpacked
/// into a 128^3 array (index <c>x + 128y + 16384z</c>), where each voxel is one read.
/// </summary>
internal readonly ref struct PieceView
{
    private readonly VoxelMemory? _memory;
    private readonly ReadOnlySpan<byte> _unpacked;

    // The piece's fields a read needs, held apiece: the 48-byte piece was copied out on every voxel read.
    private readonly VoxelCoord _size;
    private readonly ChildState _tree;

    public PieceView(in VoxelPiece piece, VoxelMemory memory, ReadOnlySpan<byte> unpacked = default)
    {
        _size = piece.Size;
        _tree = piece.Tree;
        Pivot = piece.Pivot;
        Radius = piece.Radius;
        _memory = memory;
        _unpacked = unpacked;
    }

    /// <summary>The piece voxel the lift's centre was in.</summary>
    public VoxelCoord Pivot { get; }

    /// <summary>The piece's radius around its pivot, in voxels.</summary>
    public int Radius { get; }

    /// <summary>The material at piece voxel (x, y, z); air outside the piece's box.</summary>
    public byte Read(int x, int y, int z)
    {
        if ((uint)x >= (uint)_size.X || (uint)y >= (uint)_size.Y || (uint)z >= (uint)_size.Z)
        {
            return VoxelConstants.Air;
        }

        return _unpacked.IsEmpty
            ? ChunkTree.ReadVoxel(
                _memory ?? throw new InvalidOperationException("A piece view needs the piece's memory."),
                _tree,
                x,
                y,
                z
            )
            : _unpacked[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)];
    }
}

/// <summary>
/// Where a piece edit (<see cref="VoxelEditShape.Piece"/>) puts its piece. A level-0 point is turned back into the piece
/// with a rotation matrix built from the edit's turn in integers (the quaternion's products, divided by its squared
/// length only at the end, rounding down), so every machine finds the same piece voxel for it.
/// </summary>
internal readonly ref struct PiecePlacement
{
    private readonly long _m00;
    private readonly long _m01;
    private readonly long _m02;
    private readonly long _m10;
    private readonly long _m11;
    private readonly long _m12;
    private readonly long _m20;
    private readonly long _m21;
    private readonly long _m22;
    private readonly long _lengthSquared;
    private readonly long _reachSquared;
    private readonly VoxelCoord _origin;
    private readonly PieceView _piece;

    public PiecePlacement(in VoxelEdit edit, in PieceView piece)
    {
        var (x, y, z, w) = VoxelEdit.UnpackTurn(edit.Turn);
        long xx = (long)x * x,
            yy = (long)y * y,
            zz = (long)z * z,
            ww = (long)w * w,
            xy = (long)x * y,
            xz = (long)x * z,
            yz = (long)y * z,
            wx = (long)w * x,
            wy = (long)w * y,
            wz = (long)w * z;
        // The rotation of the quaternion, times its squared length.
        _m00 = ww + xx - yy - zz;
        _m01 = 2 * (xy - wz);
        _m02 = 2 * (xz + wy);
        _m10 = 2 * (xy + wz);
        _m11 = ww - xx + yy - zz;
        _m12 = 2 * (yz - wx);
        _m20 = 2 * (xz - wy);
        _m21 = 2 * (yz + wx);
        _m22 = ww - xx - yy + zz;
        _lengthSquared = xx + yy + zz + ww;
        // No voxel centre of the piece lies farther from the pivot than its radius (in half voxels, with a margin).
        var reach = 2L * piece.Radius + 2;
        _reachSquared = reach * reach;
        _origin = edit.Origin;
        _piece = piece;
    }

    /// <summary>The piece's material at level-0 point (x, y, z) (a voxel's centre); air where the piece holds none.</summary>
    public byte MaterialAt(int x, int y, int z)
    {
        // The point's centre relative to the pivot, in half voxels, turned back by the transpose of the rotation.
        long dx = 2L * (x - _origin.X) + 1,
            dy = 2L * (y - _origin.Y) + 1,
            dz = 2L * (z - _origin.Z) + 1;
        if (dx * dx + dy * dy + dz * dz > _reachSquared)
        {
            return VoxelConstants.Air;
        }

        var pivot = _piece.Pivot;
        var lx = FloorDivide(_m00 * dx + _m10 * dy + _m20 * dz, _lengthSquared) + 2L * pivot.X;
        var ly = FloorDivide(_m01 * dx + _m11 * dy + _m21 * dz, _lengthSquared) + 2L * pivot.Y;
        var lz = FloorDivide(_m02 * dx + _m12 * dy + _m22 * dz, _lengthSquared) + 2L * pivot.Z;
        return _piece.Read((int)(lx >> 1), (int)(ly >> 1), (int)(lz >> 1));
    }

    private static long FloorDivide(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
