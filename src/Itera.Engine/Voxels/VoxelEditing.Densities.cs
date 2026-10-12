namespace Itera.Engine.Voxels;

// The world's edits, which keep each voxel's density (VoxelDensity): a sphere, capsule, or box clear, fill, set, or clear
// of some materials changes a voxel by the share of its cube the shape covers, not by its sample point, so a shape
// smaller than a voxel still marks it and a dig's wall lies where the shape's does. A voxel is measured on at most 10
// sub-rows each way, so a shape that meets none of them (under about a tenth of a voxel: 7 cm on 1 m voxels) changes
// nothing. Models keep the sample points.
internal static partial class VoxelEditing
{
    /// <summary>The most sub-rows a voxel is measured on along y and along z.</summary>
    internal const int MostSubRows = 10;

    /// <summary>
    /// The sub-rows a voxel of <paramref name="edge"/> centimetres is measured on along y and along z: one a centimetre,
    /// at most <see cref="MostSubRows"/>. Along x a row is measured to the centimetre.
    /// </summary>
    internal static int SubRows(int edge) => Math.Min(edge, MostSubRows);

    /// <summary>
    /// The centimetre of sub-row <paramref name="row"/> of <paramref name="rows"/> in a voxel whose low side is at
    /// <paramref name="low"/>: the middle of the row's share of the edge, rounded down. With one a centimetre, every
    /// centimetre.
    /// </summary>
    internal static long SubRowAt(long low, int edge, int rows, int row) => low + (2 * row + 1) * edge / (2 * rows);

    /// <summary>
    /// The parts a voxel of <paramref name="edge"/> centimetres is measured in: its sub-rows along y and z, each a
    /// centimetre long along x. At 10 cm and above (a 10 cm voxel at level 0), 100 sub-rows of the edge; on a grid of 1
    /// cm, the voxel's cubic centimetres.
    /// </summary>
    internal static int Parts(int edge)
    {
        var rows = SubRows(edge);
        return rows * rows * edge;
    }

    /// <summary>
    /// True when the world changes a voxel by how much of it <paramref name="edit"/> covers, and its density with it: a
    /// sphere, capsule, or box clear, fill, set, or clear of some materials. A lift, a paint, and a piece put back keep to
    /// sample points, so a lift's piece holds whole voxels.
    /// </summary>
    internal static bool ChangesByShare(in VoxelEdit edit) =>
        edit.Shape != VoxelEditShape.Piece
        && edit.Operation
            is VoxelEditOperation.Clear
                or VoxelEditOperation.Fill
                or VoxelEditOperation.Set
                or VoxelEditOperation.ClearMaterials;

    /// <summary>
    /// Changes one voxel by <paramref name="covered"/> of its <paramref name="total"/> parts:
    /// <list type="bullet">
    /// <item>A clear (and a clear of some materials, on a voxel of one of them) takes that share of what is solid in
    /// it: its density falls by the share of itself, rounded, and it becomes air below one half.</item>
    /// <item>A fill adds that share of what is empty in it: its density rises by the share of the rest, and air becomes
    /// the edit's material at one half.</item>
    /// <item>A set adds as a fill does, and the voxel becomes the edit's material when at least half of it is covered
    /// or it becomes solid; a set of air is a clear.</item>
    /// </list>
    /// Returns the density the voxel lost (0 for a fill or set). Integer maths only, so every machine agrees.
    /// </summary>
    internal static int ChangeByShare(in VoxelEdit edit, ref byte material, ref byte density, int covered, int total)
    {
        var operation = edit.Operation;
        if (operation == VoxelEditOperation.Set && edit.Material == VoxelConstants.Air)
        {
            operation = VoxelEditOperation.Clear;
        }

        switch (operation)
        {
            case VoxelEditOperation.Clear:
            case VoxelEditOperation.ClearMaterials when material != VoxelConstants.Air && edit.ClearsMaterial(material):
            {
                var removed = (int)(((long)density * covered + total / 2) / total);
                density = (byte)(density - removed);
                if (density < VoxelDensity.Solid)
                {
                    material = VoxelConstants.Air;
                }

                return removed;
            }

            case VoxelEditOperation.Fill:
            case VoxelEditOperation.Set:
            {
                if (edit.Material == VoxelConstants.Air)
                {
                    return 0;
                }

                var added = (int)(((long)(VoxelDensity.Full - density) * covered + total / 2) / total);
                density = (byte)(density + added);
                if (
                    (operation == VoxelEditOperation.Set && 2L * covered >= total)
                    || (material == VoxelConstants.Air && density >= VoxelDensity.Solid)
                )
                {
                    material = edit.Material;
                }

                return 0;
            }

            default:
                return 0;
        }
    }

    /// <summary>
    /// Calls <paramref name="visitor"/> for every voxel of a dense cube of <paramref name="size"/>^3 voxels at
    /// <paramref name="level"/> from <paramref name="origin"/> (in voxels of that level) that a sphere, capsule, or box
    /// edit covers any part of, with its index (<c>x + size*y + size*size*z</c>) and how many of its
    /// <see cref="Parts"/> are covered: its sub-rows' centimetres along x that lie in the shape.
    /// </summary>
    internal static void VisitShares<TVisitor>(
        in VoxelEdit edit,
        GridLevel level,
        VoxelCoord origin,
        int size,
        ref TVisitor visitor
    )
        where TVisitor : IShareVisitor, allows ref struct =>
        VisitShares(in edit, level, origin, new VoxelCoord(size, size, size), ref visitor);

    /// <summary>
    /// <see cref="VisitShares{TVisitor}(in VoxelEdit, GridLevel, VoxelCoord, int, ref TVisitor)"/> over a box of
    /// <paramref name="size"/> voxels, index <c>x + size.X * (y + size.Y * z)</c>. Each row of voxels is measured once
    /// for all its voxels; a voxel inside every sub-row's range is whole without counting.
    /// </summary>
    internal static void VisitShares<TVisitor>(
        in VoxelEdit edit,
        GridLevel level,
        VoxelCoord origin,
        VoxelCoord size,
        ref TVisitor visitor
    )
        where TVisitor : IShareVisitor, allows ref struct
    {
        if (!TryGetBoxRange(in edit, level, origin, size, out var min, out var end))
        {
            return;
        }

        var edge = level.Edge;
        var rows = SubRows(edge);
        var total = rows * rows * edge;
        Span<long> firsts = stackalloc long[MostSubRows * MostSubRows];
        Span<long> lasts = stackalloc long[MostSubRows * MostSubRows];
        Span<int> weights = stackalloc int[MostSubRows * MostSubRows];
        for (var z = min.Z; z < end.Z; z++)
        {
            var lowZ = (long)(origin.Z + z) * edge;
            for (var y = min.Y; y < end.Y; y++)
            {
                var lowY = (long)(origin.Y + y) * edge;
                var spans = RowSpans(in edit, lowY, lowZ, edge, rows, firsts, lasts, weights);
                if (spans == 0)
                {
                    continue;
                }

                // The row's reach, and the range every sub-row covers.
                long reachFirst = long.MaxValue,
                    reachLast = long.MinValue,
                    wholeFirst = long.MinValue,
                    wholeLast = long.MaxValue;
                var weight = 0;
                for (var s = 0; s < spans; s++)
                {
                    reachFirst = Math.Min(reachFirst, firsts[s]);
                    reachLast = Math.Max(reachLast, lasts[s]);
                    wholeFirst = Math.Max(wholeFirst, firsts[s]);
                    wholeLast = Math.Min(wholeLast, lasts[s]);
                    weight += weights[s];
                }

                if (weight < rows * rows)
                {
                    wholeFirst = long.MaxValue;
                }

                var x0 = (int)Math.Max(min.X, FloorDiv(reachFirst, edge) - origin.X);
                var x1 = (int)Math.Min(end.X, FloorDiv(reachLast, edge) - origin.X + 1);
                var row = (z * size.Y + y) * size.X;
                for (var x = x0; x < x1; x++)
                {
                    var low = (long)(origin.X + x) * edge;
                    var high = low + edge - 1;
                    int covered;
                    if (low >= wholeFirst && high <= wholeLast)
                    {
                        covered = total;
                    }
                    else
                    {
                        long sum = 0;
                        for (var s = 0; s < spans; s++)
                        {
                            var length = Math.Min(lasts[s], high) - Math.Max(firsts[s], low) + 1;
                            if (length > 0)
                            {
                                sum += length * weights[s];
                            }
                        }

                        covered = (int)sum;
                    }

                    if (covered > 0)
                    {
                        visitor.Visit(row + x, covered, total);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The x ranges the sub-rows of the voxel row whose low corner is (<paramref name="lowY"/>, <paramref name="lowZ"/>)
    /// cover, each with how many sub-rows share it: one range for a box, which every sub-row inside it shares, one for
    /// each covered sub-row of a sphere or capsule. Returns how many.
    /// </summary>
    private static int RowSpans(
        in VoxelEdit edit,
        long lowY,
        long lowZ,
        int edge,
        int rows,
        Span<long> firsts,
        Span<long> lasts,
        Span<int> weights
    )
    {
        if (edit.Shape == VoxelEditShape.Box)
        {
            int insideY = 0,
                insideZ = 0;
            for (var j = 0; j < rows; j++)
            {
                var y = SubRowAt(lowY, edge, rows, j);
                insideY += y >= edit.Origin.Y && y < (long)edit.Origin.Y + edit.Extent.Y ? 1 : 0;
                var z = SubRowAt(lowZ, edge, rows, j);
                insideZ += z >= edit.Origin.Z && z < (long)edit.Origin.Z + edit.Extent.Z ? 1 : 0;
            }

            if (insideY == 0 || insideZ == 0 || edit.Extent.X <= 0)
            {
                return 0;
            }

            firsts[0] = edit.Origin.X;
            lasts[0] = (long)edit.Origin.X + edit.Extent.X - 1;
            weights[0] = insideY * insideZ;
            return 1;
        }

        if (edit.Shape == VoxelEditShape.Sphere)
        {
            // Of a sphere's sub-rows the one nearest its centre covers the most: none when it covers nothing.
            var nearY = Math.Clamp(edit.Origin.Y, SubRowAt(lowY, edge, rows, 0), SubRowAt(lowY, edge, rows, rows - 1));
            var nearZ = Math.Clamp(edit.Origin.Z, SubRowAt(lowZ, edge, rows, 0), SubRowAt(lowZ, edge, rows, rows - 1));
            if (!ShapeRow(in edit, nearY, nearZ, out _, out _))
            {
                return 0;
            }
        }

        if (edit.Shape == VoxelEditShape.Capsule)
        {
            // Every sub-row lies within half the row's diagonal (under 0.71 of its edge) of the row's middle line: a
            // capsule wider by that which misses the middle line misses them all.
            var middleY = lowY + edge / 2.0;
            var middleZ = lowZ + edge / 2.0;
            if (!CapsuleSpan(in edit, middleY, middleZ, edit.Radius + 0.71 * edge + 0.01, out _, out _))
            {
                return 0;
            }
        }

        var count = 0;
        for (var k = 0; k < rows; k++)
        {
            var z = SubRowAt(lowZ, edge, rows, k);
            for (var j = 0; j < rows; j++)
            {
                if (ShapeRow(in edit, SubRowAt(lowY, edge, rows, j), z, out var first, out var last))
                {
                    firsts[count] = first;
                    lasts[count] = last;
                    weights[count] = 1;
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// The voxels of a cube (origin and size in voxels of <paramref name="level"/>) whose boxes meet the edit's bounds,
    /// as local [min, end). False when there are none.
    /// </summary>
    private static bool TryGetBoxRange(
        in VoxelEdit edit,
        GridLevel level,
        VoxelCoord origin,
        int size,
        out VoxelCoord min,
        out VoxelCoord end
    ) => TryGetBoxRange(in edit, level, origin, new VoxelCoord(size, size, size), out min, out end);

    /// <summary>
    /// The voxels of a box of <paramref name="size"/> voxels of <paramref name="level"/> from <paramref name="origin"/>
    /// whose boxes meet the edit's bounds, as local [min, end). False when there are none.
    /// </summary>
    private static bool TryGetBoxRange(
        in VoxelEdit edit,
        GridLevel level,
        VoxelCoord origin,
        VoxelCoord size,
        out VoxelCoord min,
        out VoxelCoord end
    )
    {
        edit.GetBounds(out var editMin, out var editEnd);
        var edge = level.Edge;
        min = new VoxelCoord(
            (int)Math.Max(0, FloorDiv(editMin.X, edge) - origin.X),
            (int)Math.Max(0, FloorDiv(editMin.Y, edge) - origin.Y),
            (int)Math.Max(0, FloorDiv(editMin.Z, edge) - origin.Z)
        );
        end = new VoxelCoord(
            (int)Math.Min(size.X, FloorDiv(editEnd.X - 1L, edge) + 1 - origin.X),
            (int)Math.Min(size.Y, FloorDiv(editEnd.Y - 1L, edge) + 1 - origin.Y),
            (int)Math.Min(size.Z, FloorDiv(editEnd.Z - 1L, edge) + 1 - origin.Z)
        );
        return min.X < end.X && min.Y < end.Y && min.Z < end.Z;
    }

    /// <summary>
    /// True when a sphere, capsule, or box edit covers every part of every voxel of the cube of <paramref name="size"/>
    /// from <paramref name="origin"/> (voxels of <paramref name="level"/>): the shapes are convex, so it is enough that
    /// it holds the cube's outermost measured points, its first and last centimetre along x and its first and last
    /// sub-rows along y and z.
    /// </summary>
    private static bool CoversWhole(in VoxelEdit edit, GridLevel level, VoxelCoord origin, int size)
    {
        var edge = level.Edge;
        var rows = SubRows(edge);
        long lowX = (long)origin.X * edge,
            highX = (long)(origin.X + size) * edge - 1;
        long lowY = SubRowAt((long)origin.Y * edge, edge, rows, 0),
            highY = SubRowAt((long)(origin.Y + size - 1) * edge, edge, rows, rows - 1);
        long lowZ = SubRowAt((long)origin.Z * edge, edge, rows, 0),
            highZ = SubRowAt((long)(origin.Z + size - 1) * edge, edge, rows, rows - 1);
        for (var corner = 0; corner < 8; corner++)
        {
            var x = (corner & 1) != 0 ? highX : lowX;
            var y = (corner & 2) != 0 ? highY : lowY;
            var z = (corner & 4) != 0 ? highZ : lowZ;
            if (
                x is < int.MinValue or > int.MaxValue
                || y is < int.MinValue or > int.MaxValue
                || z is < int.MinValue or > int.MaxValue
                || !edit.Contains((int)x, (int)y, (int)z)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static long FloorDiv(long value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    /// <summary>
    /// Applies a world edit to a dense cube of <paramref name="size"/>^3 voxels and their <paramref name="densities"/>
    /// at <paramref name="level"/> from <paramref name="origin"/> (see
    /// <see cref="ApplyToRegion(in VoxelEdit, in PieceView, GridLevel, VoxelCoord, int, Span{byte})"/>): a clear, fill,
    /// set, or clear of some materials by the share it covers (<see cref="ChangeByShare"/>), a lift, paint, or piece by
    /// sample points, a voxel that turns solid then full and one that turns to air empty. Returns true when any voxel or
    /// density changed. Any thread: pieces never change.
    /// </summary>
    public static bool ApplyToRegion(
        in VoxelEdit edit,
        in PieceView piece,
        GridLevel level,
        VoxelCoord origin,
        int size,
        Span<byte> voxels,
        Span<byte> densities
    )
    {
        if (!edit.AppliesAtLevel(level.Level))
        {
            return false;
        }

        if (!ChangesByShare(in edit))
        {
            return ApplyBySamples(in edit, in piece, level, origin, size, voxels, densities);
        }

        var change = new ShareChange(in edit, voxels, densities);
        VisitShares(in edit, level, origin, size, ref change);
        return change.Changed;
    }

    /// <summary>
    /// Applies a world edit to a stored chunk and returns its new state, as
    /// <see cref="ApplyToChunk(VoxelMemory, VoxelGrid, ChildState, ChunkKey, in VoxelEdit, in PieceView, Span{int}, out int)"/>
    /// does for a model, but keeping each voxel's density (<see cref="ApplyToRegion(in VoxelEdit, in PieceView, GridLevel, VoxelCoord, int, Span{byte}, Span{byte})"/>).
    /// Only the bricks the edit reaches are decoded; a brick it covers whole becomes air or one material without being
    /// read.
    /// </summary>
    public static ChildState ApplyToWorldChunk(
        VoxelMemory memory,
        VoxelGrid grid,
        ChildState chunk,
        ChunkKey key,
        in VoxelEdit edit,
        in PieceView piece,
        Span<int> changedBricks,
        out int changedCount
    )
    {
        changedCount = 0;
        if (!edit.AppliesAtLevel(key.Level))
        {
            return chunk;
        }

        if (!ChangesByShare(in edit))
        {
            return ApplyToChunk(
                memory,
                grid,
                chunk,
                key,
                in edit,
                in piece,
                changedBricks,
                out changedCount,
                densities: true
            );
        }

        var level = new GridLevel(grid, key.Level);
        if (chunk.IsUniformOrEmpty && edit.LeavesUniform(chunk.Material))
        {
            return chunk;
        }

        var chunkOrigin = key.LevelOrigin;
        if (!TryGetBoxRange(in edit, level, chunkOrigin, VoxelConstants.ChunkSize, out var min, out var end))
        {
            return chunk;
        }

        // A strip of bricks along x at a time: each row of voxels is measured once for the strip, not once a brick.
        var firstBrick = min.X >> 3;
        var bricks = ((end.X - 1) >> 3) - firstBrick + 1;
        var strip = new VoxelCoord(
            bricks * VoxelConstants.BrickSize,
            VoxelConstants.BrickSize,
            VoxelConstants.BrickSize
        );
        Span<byte> voxels = stackalloc byte[StripVoxels];
        Span<byte> densities = stackalloc byte[StripVoxels];
        Span<byte> brickVoxels = stackalloc byte[VoxelConstants.BrickVoxels];
        Span<byte> brickDensities = stackalloc byte[VoxelConstants.BrickVoxels];
        for (var bz = min.Z >> 3; bz <= (end.Z - 1) >> 3; bz++)
        {
            for (var by = min.Y >> 3; by <= (end.Y - 1) >> 3; by++)
            {
                var read = 0;
                for (var i = 0; i < bricks; i++)
                {
                    var bx = firstBrick + i;
                    var old = ChunkTree.GetBrick(memory, chunk, bx, by, bz);
                    if (old.IsUniformOrEmpty && edit.LeavesUniform(old.Material))
                    {
                        continue;
                    }

                    var brickOrigin = new VoxelCoord(
                        chunkOrigin.X + (bx << 3),
                        chunkOrigin.Y + (by << 3),
                        chunkOrigin.Z + (bz << 3)
                    );

                    // A clear or set that covers the whole brick makes it air or one material without reading it, and
                    // so does a fill or a clear of some materials that covers a brick of one material.
                    if (
                        (
                            edit.Operation is VoxelEditOperation.Clear or VoxelEditOperation.Set
                            || (
                                old.IsUniformOrEmpty
                                && edit.Operation is VoxelEditOperation.Fill or VoxelEditOperation.ClearMaterials
                            )
                        ) && CoversWhole(in edit, level, brickOrigin, VoxelConstants.BrickSize)
                    )
                    {
                        var whole = ChildState.Uniform(edit.Apply(old.Material));
                        if (old != whole)
                        {
                            chunk = ChunkTree.SetBrick(memory, chunk, bx, by, bz, whole);
                            changedBricks[changedCount++] = bx | (by << 4) | (bz << 8);
                        }

                        continue;
                    }

                    BrickCodec.Fill(memory, old, brickVoxels);
                    BrickCodec.FillDensities(memory, old, brickDensities);
                    ToStrip(brickVoxels, voxels, i, bricks);
                    ToStrip(brickDensities, densities, i, bricks);
                    read |= 1 << i;
                }

                if (read == 0)
                {
                    continue;
                }

                var change = new StripChange(in edit, voxels, densities, strip.X, read);
                VisitShares(
                    in edit,
                    level,
                    new VoxelCoord(
                        chunkOrigin.X + (firstBrick << 3),
                        chunkOrigin.Y + (by << 3),
                        chunkOrigin.Z + (bz << 3)
                    ),
                    strip,
                    ref change
                );
                for (var i = 0; i < bricks; i++)
                {
                    if ((change.Changed & (1 << i)) == 0)
                    {
                        continue;
                    }

                    FromStrip(voxels, brickVoxels, i, bricks);
                    FromStrip(densities, brickDensities, i, bricks);
                    var bx = firstBrick + i;
                    var brick = BrickCodec.Encode(memory, brickVoxels, brickDensities, mayExceedBudget: true, out _);
                    chunk = ChunkTree.SetBrick(memory, chunk, bx, by, bz, brick);
                    changedBricks[changedCount++] = bx | (by << 4) | (bz << 8);
                }
            }
        }

        return chunk;
    }

    /// <summary>
    /// Adds to <paramref name="removed"/> (one entry per material, 256) what a sphere, capsule, or box clear (or a clear
    /// of some materials) would take from a stored world chunk: for each voxel the density it would lose
    /// (<see cref="ChangeByShare"/>) times its edge cubed, so the sum over 255 is in cubic centimetres. Entry 0 takes
    /// what it takes from air voxels' densities, the solid of a neighbour's surface that reaches into them. An empty
    /// or uniform chunk, cell, or brick is measured without decoding. Changes nothing; allocates nothing.
    /// </summary>
    public static void CountInWorldChunk(
        VoxelMemory memory,
        VoxelGrid grid,
        ChildState chunk,
        ChunkKey key,
        in VoxelEdit edit,
        Span<long> removed
    )
    {
        if (edit.Shape == VoxelEditShape.Piece)
        {
            throw new ArgumentException("Only sphere, capsule, and box edits are counted.", nameof(edit));
        }

        if (removed.Length < 256)
        {
            throw new ArgumentException("The counts hold one entry per material.", nameof(removed));
        }

        var level = new GridLevel(grid, key.Level);
        var chunkOrigin = key.LevelOrigin;
        if (
            chunk.IsEmpty
            || !TryGetBoxRange(in edit, level, chunkOrigin, VoxelConstants.ChunkSize, out var min, out var end)
        )
        {
            return;
        }

        var edge = (long)level.Edge;
        var volume = edge * edge * edge;
        if (chunk.IsUniformOrEmpty)
        {
            var uniform = new ShareCount(in edit, chunk.Material, removed, volume);
            VisitShares(in edit, level, chunkOrigin, VoxelConstants.ChunkSize, ref uniform);
            return;
        }

        // A strip of bricks along x at a time, as an edit does: empty ones read as air, which loses nothing.
        var firstBrick = min.X >> 3;
        var bricks = ((end.X - 1) >> 3) - firstBrick + 1;
        var strip = new VoxelCoord(
            bricks * VoxelConstants.BrickSize,
            VoxelConstants.BrickSize,
            VoxelConstants.BrickSize
        );
        Span<byte> voxels = stackalloc byte[StripVoxels];
        Span<byte> densities = stackalloc byte[StripVoxels];
        Span<byte> brickVoxels = stackalloc byte[VoxelConstants.BrickVoxels];
        Span<byte> brickDensities = stackalloc byte[VoxelConstants.BrickVoxels];
        for (var bz = min.Z >> 3; bz <= (end.Z - 1) >> 3; bz++)
        {
            for (var by = min.Y >> 3; by <= (end.Y - 1) >> 3; by++)
            {
                var any = false;
                for (var i = 0; i < bricks; i++)
                {
                    var brick = ChunkTree.GetBrick(memory, chunk, firstBrick + i, by, bz);
                    any |= !brick.IsEmpty;
                    BrickCodec.Fill(memory, brick, brickVoxels);
                    BrickCodec.FillDensities(memory, brick, brickDensities);
                    ToStrip(brickVoxels, voxels, i, bricks);
                    ToStrip(brickDensities, densities, i, bricks);
                }

                if (!any)
                {
                    continue;
                }

                var count = new ShareCount(
                    in edit,
                    voxels[..(strip.X * strip.Y * strip.Z)],
                    densities[..(strip.X * strip.Y * strip.Z)],
                    removed,
                    volume
                );
                VisitShares(
                    in edit,
                    level,
                    new VoxelCoord(
                        chunkOrigin.X + (firstBrick << 3),
                        chunkOrigin.Y + (by << 3),
                        chunkOrigin.Z + (bz << 3)
                    ),
                    strip,
                    ref count
                );
            }
        }
    }

    /// <summary>Voxels in a strip of a chunk's bricks along x: 16 bricks of 8^3.</summary>
    private const int StripVoxels = VoxelConstants.BricksPerChunkEdge * VoxelConstants.BrickVoxels;

    /// <summary>
    /// Copies brick <paramref name="brick"/> of a strip of <paramref name="bricks"/> (index <c>x + 8y + 64z</c>) into
    /// the strip (index <c>x + 8 bricks (y + 8z)</c>).
    /// </summary>
    private static void ToStrip(ReadOnlySpan<byte> voxels, Span<byte> strip, int brick, int bricks)
    {
        var width = bricks * VoxelConstants.BrickSize;
        for (var row = 0; row < VoxelConstants.BrickSize * VoxelConstants.BrickSize; row++)
        {
            voxels
                .Slice(row * VoxelConstants.BrickSize, VoxelConstants.BrickSize)
                .CopyTo(strip.Slice(row * width + brick * VoxelConstants.BrickSize, VoxelConstants.BrickSize));
        }
    }

    /// <summary>Copies brick <paramref name="brick"/> of a strip out of it (<see cref="ToStrip"/>).</summary>
    private static void FromStrip(ReadOnlySpan<byte> strip, Span<byte> voxels, int brick, int bricks)
    {
        var width = bricks * VoxelConstants.BrickSize;
        for (var row = 0; row < VoxelConstants.BrickSize * VoxelConstants.BrickSize; row++)
        {
            strip
                .Slice(row * width + brick * VoxelConstants.BrickSize, VoxelConstants.BrickSize)
                .CopyTo(voxels.Slice(row * VoxelConstants.BrickSize, VoxelConstants.BrickSize));
        }
    }

    /// <summary>
    /// A world edit that keeps to sample points (a lift, a paint, a piece put back) on a dense cube with its densities:
    /// a voxel that changes between air and solid takes the new material's default density.
    /// </summary>
    private static bool ApplyBySamples(
        in VoxelEdit edit,
        in PieceView piece,
        GridLevel level,
        VoxelCoord origin,
        int size,
        Span<byte> voxels,
        Span<byte> densities
    )
    {
        if (!TryGetRange(in edit, level, origin, size, out var min, out var end))
        {
            return false;
        }

        var changed = false;
        var placement = edit.Shape == VoxelEditShape.Piece ? new PiecePlacement(in edit, in piece) : default;
        for (var z = min.Z; z < end.Z; z++)
        {
            var sampleZ = level.SampleAt(origin.Z + z);
            for (var y = min.Y; y < end.Y; y++)
            {
                var sampleY = level.SampleAt(origin.Y + y);
                var row = (z * size + y) * size;
                int x0,
                    x1;
                if (edit.Shape == VoxelEditShape.Piece)
                {
                    (x0, x1) = (min.X, end.X);
                }
                else if (!RowSpan(in edit, level, origin.X, sampleY, sampleZ, min.X, end.X, out x0, out x1))
                {
                    continue;
                }

                for (var x = x0; x < x1; x++)
                {
                    var current = voxels[row + x];
                    var next =
                        edit.Shape == VoxelEditShape.Piece
                            ? current == VoxelConstants.Air
                                ? placement.MaterialAt(level.SampleAt(origin.X + x), sampleY, sampleZ)
                                : current
                            : edit.Apply(current);
                    if (next == current)
                    {
                        continue;
                    }

                    voxels[row + x] = next;
                    if ((next == VoxelConstants.Air) != (current == VoxelConstants.Air))
                    {
                        densities[row + x] = VoxelDensity.Default(next);
                    }

                    changed = true;
                }
            }
        }

        return changed;
    }
}

/// <summary>
/// What <see cref="VoxelEditing.VisitShares{TVisitor}(in VoxelEdit, GridLevel, VoxelCoord, VoxelCoord, ref TVisitor)"/>
/// calls for each voxel an edit covers a part of.
/// </summary>
internal interface IShareVisitor
{
    /// <summary>The voxel at <paramref name="index"/> of the cube has <paramref name="covered"/> of its <paramref name="total"/> parts covered.</summary>
    void Visit(int index, int covered, int total);
}

/// <summary>Changes each voxel visited by the share covered (<see cref="VoxelEditing.ChangeByShare"/>).</summary>
internal ref struct ShareChange(in VoxelEdit edit, Span<byte> voxels, Span<byte> densities) : IShareVisitor
{
    private readonly VoxelEdit _edit = edit;
    private readonly Span<byte> _voxels = voxels;
    private readonly Span<byte> _densities = densities;

    /// <summary>True once any voxel or density changed.</summary>
    public bool Changed { get; private set; }

    public void Visit(int index, int covered, int total)
    {
        var material = _voxels[index];
        var density = _densities[index];
        VoxelEditing.ChangeByShare(in _edit, ref material, ref density, covered, total);
        if (material != _voxels[index] || density != _densities[index])
        {
            _voxels[index] = material;
            _densities[index] = density;
            Changed = true;
        }
    }
}

/// <summary>
/// Changes each voxel visited in a strip of bricks by the share covered, only in the bricks read (a bit each in
/// <c>read</c>), and marks the bricks that changed in <see cref="Changed"/>.
/// </summary>
internal ref struct StripChange(in VoxelEdit edit, Span<byte> voxels, Span<byte> densities, int width, int read)
    : IShareVisitor
{
    private readonly VoxelEdit _edit = edit;
    private readonly Span<byte> _voxels = voxels;
    private readonly Span<byte> _densities = densities;
    private readonly int _width = width;
    private readonly int _read = read;

    /// <summary>A bit for each brick of the strip that changed.</summary>
    public int Changed { get; private set; }

    public void Visit(int index, int covered, int total)
    {
        var brick = (index % _width) >> VoxelConstants.BrickShift;
        if ((_read & (1 << brick)) == 0)
        {
            return;
        }

        var material = _voxels[index];
        var density = _densities[index];
        VoxelEditing.ChangeByShare(in _edit, ref material, ref density, covered, total);
        if (material != _voxels[index] || density != _densities[index])
        {
            _voxels[index] = material;
            _densities[index] = density;
            Changed |= 1 << brick;
        }
    }
}

/// <summary>
/// Adds what a clear would take from each voxel visited to the count of its material, in density steps times the
/// voxel's volume: of a cube of one material at full density, or of decoded voxels and densities.
/// </summary>
internal ref struct ShareCount : IShareVisitor
{
    private readonly VoxelEdit _edit;
    private readonly byte _material;
    private readonly ReadOnlySpan<byte> _voxels;
    private readonly ReadOnlySpan<byte> _densities;
    private readonly Span<long> _removed;
    private readonly long _volume;

    /// <summary>Counts a cube that is all <paramref name="material"/>.</summary>
    public ShareCount(in VoxelEdit edit, byte material, Span<long> removed, long volume)
    {
        _edit = edit;
        _material = material;
        _removed = removed;
        _volume = volume;
    }

    /// <summary>Counts a decoded cube of voxels and densities.</summary>
    public ShareCount(
        in VoxelEdit edit,
        ReadOnlySpan<byte> voxels,
        ReadOnlySpan<byte> densities,
        Span<long> removed,
        long volume
    )
    {
        _edit = edit;
        _voxels = voxels;
        _densities = densities;
        _removed = removed;
        _volume = volume;
    }

    public void Visit(int index, int covered, int total)
    {
        var material = _voxels.IsEmpty ? _material : _voxels[index];
        var density = _voxels.IsEmpty ? VoxelDensity.Default(material) : _densities[index];
        var original = material;
        var lost = VoxelEditing.ChangeByShare(in _edit, ref material, ref density, covered, total);
        if (lost > 0)
        {
            _removed[original] += lost * _volume;
        }
    }
}
