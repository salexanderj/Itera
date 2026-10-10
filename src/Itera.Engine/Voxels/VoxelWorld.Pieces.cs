using System.Globalization;

namespace Itera.Engine.Voxels;

// Lifts and pieces: a lifted piece of voxels, a host's lift with its runs, the pieces the log keeps, and a thrown ball's
// piece made from nothing.
public sealed partial class VoxelWorld
{
    /// <summary>
    /// Lifts the voxels a <see cref="VoxelEditOperation.Lift"/> edit covers out of the world, read from the world itself
    /// (tests, and a game's own lifts of what is there; a thrown ball is not lifted, but made from nothing,
    /// <see cref="VoxelModels.MakeBall"/>). They are kept as a piece
    /// named by the edit's sequence, which this returns, and cleared as <see cref="VoxelEditOperation.Clear"/> would. The
    /// piece holds the level-0 voxels, so the level-0 columns the edit reaches must be resident; when one is not, nothing
    /// happens and this returns false. <paramref name="maker"/> is as <see cref="Apply"/>'s. Throws
    /// <see cref="ArgumentException"/> for an invalid edit.
    /// </summary>
    public bool TryLift(in VoxelEdit edit, out long sequence, int maker = VoxelEdit.NoMaker)
    {
        ThrowIfFollowingHost();
        // Checked before the piece is kept: a throw after AddPiece left a piece with no lift, and saves unreadable.
        ThrowIfBadMaker(maker);

        sequence = 0;
        var sequenced = edit with { Sequence = _edits.Count + 1 };
        var problem = sequenced.Validate();
        if (problem is not null || edit.Operation != VoxelEditOperation.Lift)
        {
            throw new ArgumentException(
                "Invalid lift: " + (problem ?? "the operation is not Lift") + ".",
                nameof(edit)
            );
        }

        sequenced.GetBounds(out var min, out var end);
        Grid.GetVoxelBounds(in sequenced, out var voxelMin, out var voxelEnd);
        if (!ColumnsResident(voxelMin.X, voxelMin.Z, voxelEnd.X - 1, voxelEnd.Z - 1))
        {
            return false;
        }

        // The piece is what the log says is there, waiting large edits included. It is read a centimetre at a time,
        // whatever the world's voxels (phase 5 changes how pieces are kept).
        CatchUpLevel0(voxelMin, voxelEnd);
        var size = new VoxelCoord(end.X - min.X, end.Y - min.Y, end.Z - min.Z);
        var pivot = new VoxelCoord(size.X >> 1, size.Y >> 1, size.Z >> 1);
        var scratch = _pieceScratch ??= new byte[VoxelConstants.ChunkVoxels];
        _unpackedPiece = 0;
        ClearBox(scratch, size);
        var solid = 0;
        var farthest = 0L;
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    if (sequenced.Contains(min.X + x, min.Y + y, min.Z + z))
                    {
                        TryGetMaterial(new VoxelCoord(min.X + x, min.Y + y, min.Z + z), out var material);
                        scratch[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = material;
                        if (material != VoxelConstants.Air)
                        {
                            solid++;
                            farthest = Math.Max(farthest, VoxelPiece.FarthestCornerSquared(pivot, x, y, z));
                        }
                    }
                }
            }
        }

        ThrowIfNoRoom(in sequenced);
        var tree = ChunkTree.Encode(Memory, scratch, size, mayExceedBudget: true, out _);
        _edits.AddPiece(new VoxelPiece(sequenced.Sequence, min, size, tree, solid, VoxelPiece.RadiusOf(farthest)));
        ApplyChecked(in sequenced, maker);
        sequence = sequenced.Sequence;
        return true;
    }

    /// <summary>
    /// Applies a lift from the host with the piece it lifted, as <paramref name="runs"/> of one material over the lift's
    /// box (x fastest, then y, then z; see <see cref="CollectPieceRuns"/>), made by <paramref name="maker"/> (see
    /// <see cref="Apply"/>). The edit must be the next one. A client cannot lift the piece itself: the host's columns
    /// decide what it holds, and the client's may not be loaded. Throws <see cref="ArgumentException"/> for an invalid
    /// edit or runs that do not fill the box; nothing changes then.
    /// </summary>
    public void ApplyHostLift(
        in VoxelEdit edit,
        ReadOnlySpan<(byte Material, int Length)> runs,
        int maker = VoxelEdit.NoMaker
    )
    {
        if (edit.Operation != VoxelEditOperation.Lift || edit.Sequence != _edits.Count + 1)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Edit {edit.Sequence} is not a lift with the next sequence ({_edits.Count + 1})."
                ),
                nameof(edit)
            );
        }

        var problem = edit.Validate();
        if (problem is not null)
        {
            throw new ArgumentException($"Invalid edit: {problem}.", nameof(edit));
        }

        edit.GetBounds(out var min, out var end);
        var size = new VoxelCoord(end.X - min.X, end.Y - min.Y, end.Z - min.Z);
        problem = PieceRuns.Check(size, runs);
        if (problem is not null)
        {
            throw new ArgumentException($"Piece {edit.Sequence}: {problem}.", nameof(runs));
        }

        ThrowIfBadMaker(maker);

        ThrowIfNoRoom(in edit);
        AddStoredPiece(edit.Sequence, min, size, runs, _pieceScratch ??= new byte[VoxelConstants.ChunkVoxels]);
        ApplyChecked(in edit, maker);
    }

    /// <summary>
    /// Lifts with the piece's voxels given as <paramref name="runs"/> instead of read from this world (tests; a thrown
    /// ball was made this way until 2026-10-07, when it became a body made from nothing, <see cref="VoxelModels.MakeBall"/>). Gives
    /// the edit the next sequence, as <see cref="TryLift"/> does, and returns it. Throws
    /// <see cref="ArgumentException"/> for an invalid lift or runs that do not fill its box, and
    /// <see cref="InvalidOperationException"/> on a world that follows a host. <paramref name="maker"/> is as
    /// <see cref="Apply"/>'s.
    /// </summary>
    public long LiftWithRuns(
        in VoxelEdit edit,
        ReadOnlySpan<(byte Material, int Length)> runs,
        int maker = VoxelEdit.NoMaker
    )
    {
        ThrowIfFollowingHost();
        var sequenced = edit with { Sequence = _edits.Count + 1 };
        ApplyHostLift(in sequenced, runs, maker);
        return sequenced.Sequence;
    }

    /// <summary>
    /// True when a voxel <paramref name="edit"/> covers (a sphere or a box) holds anything but air, in the resident
    /// level-0 columns; columns not resident count as air. Large
    /// edits still waiting there are applied first. Walks only the solid parts of the trees
    /// (<see cref="WalkBlocking{TVisitor}(int, int, int, int, int, int, in MaterialSet, ref TVisitor)"/>), so a ball's space in open air costs little however big it is: a 73 cm
    /// ball's 200,000 voxels read one by one took about 45 ms. Main thread; allocates nothing.
    /// </summary>
    public bool AnySolidIn(in VoxelEdit edit)
    {
        if (edit.Shape is not (VoxelEditShape.Sphere or VoxelEditShape.Box))
        {
            throw new ArgumentException("Only a sphere or a box covers voxels to look in.", nameof(edit));
        }

        Grid.GetVoxelBounds(in edit, out var min, out var end);
        CatchUpLevel0(min, end);
        var visitor = new SolidInEdit(edit, Grid);
        WalkBlocking(min.X, min.Y, min.Z, end.X - 1, end.Y - 1, end.Z - 1, MaterialSet.None, ref visitor);
        return visitor.Found;
    }

    /// <summary>
    /// Writes the piece that lift <paramref name="sequence"/> took into <paramref name="runs"/> (cleared first), as runs
    /// of one material over its box, x fastest, then y, then z: what a save holds and what the host sends with the lift.
    /// Returns false when no lift has that sequence.
    /// </summary>
    public bool CollectPieceRuns(long sequence, List<(byte Material, int Length)> runs)
    {
        runs.Clear();
        if (!_edits.TryGetPiece(sequence, out var piece))
        {
            return false;
        }

        PieceRuns.Collect(in piece, Unpacked(in piece), runs);
        return true;
    }

    /// <summary>
    /// Writes what a lift would take into <paramref name="runs"/> (cleared first), as runs of one material over its box,
    /// x fastest, then y, then z, without changing the world: the tests' measure of what is there. Returns
    /// false, with nothing written, when a level-0 column the
    /// lift reaches is not resident. Throws <see cref="ArgumentException"/> for an edit that is not a valid lift.
    /// </summary>
    internal bool TryCollectRuns(in VoxelEdit edit, List<(byte Material, int Length)> runs)
    {
        runs.Clear();
        var problem = (edit with { Sequence = _edits.Count + 1 }).Validate();
        if (problem is not null || edit.Operation != VoxelEditOperation.Lift)
        {
            throw new ArgumentException(
                "Invalid lift: " + (problem ?? "the operation is not Lift") + ".",
                nameof(edit)
            );
        }

        edit.GetBounds(out var min, out var end);
        Grid.GetVoxelBounds(in edit, out var voxelMin, out var voxelEnd);
        if (!ColumnsResident(voxelMin.X, voxelMin.Z, voxelEnd.X - 1, voxelEnd.Z - 1))
        {
            return false;
        }

        CatchUpLevel0(voxelMin, voxelEnd);
        var run = 0;
        byte current = 0;
        for (var z = min.Z; z < end.Z; z++)
        {
            for (var y = min.Y; y < end.Y; y++)
            {
                for (var x = min.X; x < end.X; x++)
                {
                    byte material = VoxelConstants.Air;
                    if (edit.Contains(x, y, z))
                    {
                        TryGetMaterial(new VoxelCoord(x, y, z), out material);
                    }

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
        return true;
    }

    /// <summary>
    /// Stores a piece read from a saved log, or sent by the host with its lift, in the voxel memory and keeps it for
    /// its lift: its runs are unpacked into the chunk scratch (x fastest, then y, then z, within its box) and encoded,
    /// one piece at a time.
    /// </summary>
    private void AddStoredPiece(
        long sequence,
        VoxelCoord min,
        VoxelCoord size,
        ReadOnlySpan<(byte Material, int Length)> runs,
        byte[] scratch
    )
    {
        _unpackedPiece = 0;
        ClearBox(scratch, size);
        var pivot = new VoxelCoord(size.X >> 1, size.Y >> 1, size.Z >> 1);
        var solid = 0;
        var farthest = 0L;
        var index = 0;
        foreach (var (material, length) in runs)
        {
            for (var n = 0; n < length; n++, index++)
            {
                if (material == VoxelConstants.Air)
                {
                    continue;
                }

                var x = index % size.X;
                var y = index / size.X % size.Y;
                var z = index / (size.X * size.Y);
                scratch[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = material;
                solid++;
                farthest = Math.Max(farthest, VoxelPiece.FarthestCornerSquared(pivot, x, y, z));
            }
        }

        var tree = ChunkTree.Encode(Memory, scratch, size, mayExceedBudget: true, out _);
        _edits.AddPiece(new VoxelPiece(sequence, min, size, tree, solid, VoxelPiece.RadiusOf(farthest)));
    }

    /// <summary>
    /// Clears the rows of the chunk scratch a piece of <paramref name="size"/> takes: encoding reads only bricks that reach
    /// into it and counts the rest as air, so the other 2 MB need no clearing (a 25 cm ball would otherwise clear and
    /// encode the whole chunk).
    /// </summary>
    internal static void ClearBox(byte[] scratch, VoxelCoord size)
    {
        // Whole bricks: a brick that reaches into the box is read whole, its voxels outside the box then counted as air.
        var width = Math.Min(VoxelConstants.ChunkSize, (size.X + 7) & ~7);
        var height = Math.Min(VoxelConstants.ChunkSize, (size.Y + 7) & ~7);
        var depth = Math.Min(VoxelConstants.ChunkSize, (size.Z + 7) & ~7);
        for (var z = 0; z < depth; z++)
        {
            for (var y = 0; y < height; y++)
            {
                scratch.AsSpan(VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z), width).Clear();
            }
        }
    }

    /// <summary>
    /// <paramref name="piece"/>'s box decoded into the piece scratch, which keeps the last piece decoded for a put-back
    /// or its runs; outside the box the scratch holds anything.
    /// </summary>
    private byte[] Unpacked(in VoxelPiece piece)
    {
        var scratch = _pieceScratch ??= new byte[VoxelConstants.ChunkVoxels];
        if (_unpackedPiece != piece.Sequence)
        {
            ChunkTree.Decode(Memory, piece.Tree, scratch, piece.Size);
            _unpackedPiece = piece.Sequence;
        }

        return scratch;
    }

    /// <summary>The piece lifted by edit <paramref name="sequence"/>.</summary>
    internal bool TryGetPiece(long sequence, out VoxelPiece piece) => _edits.TryGetPiece(sequence, out piece);

    /// <summary>
    /// Stops at the first solid voxel an edit covers (<see cref="AnySolidIn"/>): a voxel the walk names by its level-0
    /// index is in the edit when its sample point (<paramref name="grid"/>) is.
    /// </summary>
    private struct SolidInEdit(VoxelEdit edit, VoxelGrid grid) : IBlockingVisitor
    {
        private readonly VoxelEdit _edit = edit;
        private readonly VoxelGrid _grid = grid;

        public bool Found { get; private set; }

        public readonly bool Finished => Found;

        public void Box(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            // The box's voxel nearest the edit's origin is in the edit when any of its voxels is (a sphere's test is by
            // distance from its origin; a box's by overlap, which the nearest voxel also decides).
            var origin = _grid.ToVoxel(_edit.Origin);
            Found |= _edit.Contains(
                _grid.SampleAt(Math.Clamp(origin.X, x0, x1), 0),
                _grid.SampleAt(Math.Clamp(origin.Y, y0, y1), 0),
                _grid.SampleAt(Math.Clamp(origin.Z, z0, z1), 0)
            );
        }

        public void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask)
        {
            for (var layer = 0; layer < mask.Length && !Found; layer++)
            {
                var bits = mask[layer];
                while (bits != 0 && !Found)
                {
                    var bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    Found = _edit.Contains(
                        _grid.SampleAt(x + (bit & 7), 0),
                        _grid.SampleAt(y + (bit >> 3), 0),
                        _grid.SampleAt(z + layer, 0)
                    );
                }
            }
        }
    }
}
