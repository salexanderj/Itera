namespace Itera.Engine.Voxels;

/// <summary>
/// Applies <see cref="VoxelEdit"/>s to voxels. A model's voxels (characters, items; <see cref="VoxelGrid.Unit"/>)
/// change by sample points: at level k a voxel is tested at the centimetre its grid samples it at
/// (<see cref="GridLevel.SampleAt"/>), so the result depends only on integers and every machine agrees. The world's
/// change by the share of each voxel a shape covers, with their densities (VoxelEditing.Densities.cs). Workers use
/// <see cref="ApplyToRegion(in VoxelEdit, in PieceView, GridLevel, VoxelCoord, int, Span{byte}, Span{byte})"/> on a
/// world's dense chunks; the main thread uses <see cref="ApplyToWorldChunk"/> on a world's stored ones and
/// <see cref="ApplyToChunk(VoxelMemory, VoxelGrid, ChildState, ChunkKey, in VoxelEdit, in PieceView, Span{int}, out int)"/>
/// on a model's. A piece edit also needs a view of its piece; other edits take the default view.
/// </summary>
internal static partial class VoxelEditing
{
    /// <summary>
    /// Applies an edit to a dense cube of <paramref name="size"/>^3 voxels at <paramref name="level"/> whose lowest voxel
    /// is <paramref name="origin"/> (in voxels of that level). Index <c>x + size*y + size*size*z</c>. Returns true when
    /// any voxel changed.
    /// </summary>
    public static bool ApplyToRegion(
        in VoxelEdit edit,
        GridLevel level,
        VoxelCoord origin,
        int size,
        Span<byte> voxels
    ) => ApplyToRegion(in edit, default, level, origin, size, voxels);

    /// <summary>
    /// Applies an edit, with its piece when it is a piece edit, to a dense cube of <paramref name="size"/>^3 voxels at
    /// <paramref name="level"/> whose lowest voxel is <paramref name="origin"/> (in voxels of that level). Index
    /// <c>x + size*y + size*size*z</c>. Returns true when any voxel changed. Any thread: pieces never change. A sphere,
    /// capsule, or box covers one span of each row, found without testing its voxels one by one (<see cref="RowSpan"/>: a 50 cm dig at level 0 went from about 0.8 ms per layer to about 0.05 ms), and a set, clear, or
    /// lift fills the span at once.
    /// </summary>
    public static bool ApplyToRegion(
        in VoxelEdit edit,
        in PieceView piece,
        GridLevel level,
        VoxelCoord origin,
        int size,
        Span<byte> voxels
    )
    {
        if (!TryGetRange(in edit, level, origin, size, out var min, out var end))
        {
            return false;
        }

        if (edit.Shape == VoxelEditShape.Piece)
        {
            return PlaceInRegion(new PiecePlacement(in edit, in piece), level, origin, min, end, size, voxels);
        }

        // A set, clear, or lift gives every covered voxel the same material.
        var whole = edit.Operation is VoxelEditOperation.Set or VoxelEditOperation.Clear or VoxelEditOperation.Lift;
        var value = edit.Apply(VoxelConstants.Air);
        var changed = false;
        for (var z = min.Z; z < end.Z; z++)
        {
            var sampleZ = level.SampleAt(origin.Z + z);
            for (var y = min.Y; y < end.Y; y++)
            {
                var sampleY = level.SampleAt(origin.Y + y);
                if (!RowSpan(in edit, level, origin.X, sampleY, sampleZ, min.X, end.X, out var x0, out var x1))
                {
                    continue;
                }

                var span = voxels.Slice((z * size + y) * size + x0, x1 - x0);
                if (whole)
                {
                    if (span.ContainsAnyExcept(value))
                    {
                        span.Fill(value);
                        changed = true;
                    }

                    continue;
                }

                for (var x = 0; x < span.Length; x++)
                {
                    var current = span[x];
                    var next = edit.Apply(current);
                    if (next != current)
                    {
                        span[x] = next;
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// The local x range [x0, x1), within [min, end), of the row at sample point (<paramref name="sampleY"/>,
    /// <paramref name="sampleZ"/>) whose voxels' sample points a sphere, capsule, or box edit covers: exactly the voxels
    /// <see cref="VoxelEdit.Contains"/> accepts. For a sphere the covered level-0 x are those with
    /// <c>dx * dx &lt;= r * r - dy * dy - dz * dz</c>, found by an integer square root. False when the row has none.
    /// </summary>
    private static bool RowSpan(
        in VoxelEdit edit,
        GridLevel level,
        int originX,
        int sampleY,
        int sampleZ,
        int min,
        int end,
        out int x0,
        out int x1
    )
    {
        if (!ShapeRow(in edit, sampleY, sampleZ, out var first, out var last))
        {
            x0 = x1 = 0;
            return false;
        }

        // The voxels whose sample points lie in [first, last].
        x0 = (int)Math.Max(min, level.FirstSampleAtOrAfter(first) - originX);
        x1 = (int)Math.Min(end, level.FirstSampleAtOrAfter(last + 1) - originX);
        return x0 < x1;
    }

    /// <summary>
    /// The range [first, last] of whole centimetres x at which the points (x, <paramref name="y"/>, <paramref name="z"/>)
    /// lie in a sphere, capsule, or box edit: exactly those <see cref="VoxelEdit.Contains"/> accepts. For a sphere they
    /// are the x with <c>dx * dx &lt;= r * r - dy * dy - dz * dz</c>, found by an integer square root. False when the
    /// row has none.
    /// </summary>
    private static bool ShapeRow(in VoxelEdit edit, long y, long z, out long first, out long last)
    {
        first = last = 0;
        if (edit.Shape == VoxelEditShape.Sphere)
        {
            var dy = y - edit.Origin.Y;
            var dz = z - edit.Origin.Z;
            long r = edit.Radius;
            var rest = r * r - dy * dy - dz * dz;
            if (rest < 0)
            {
                return false;
            }

            var reach = (long)Math.Sqrt(rest);
            while (reach * reach > rest)
            {
                reach--;
            }

            while ((reach + 1) * (reach + 1) <= rest)
            {
                reach++;
            }

            first = edit.Origin.X - reach;
            last = edit.Origin.X + reach;
            return true;
        }

        if (edit.Shape == VoxelEditShape.Capsule)
        {
            return y >= int.MinValue
                && y <= int.MaxValue
                && z >= int.MinValue
                && z <= int.MaxValue
                && CapsuleRow(in edit, (int)y, (int)z, out first, out last);
        }

        if (
            y < edit.Origin.Y
            || y >= (long)edit.Origin.Y + edit.Extent.Y
            || z < edit.Origin.Z
            || z >= (long)edit.Origin.Z + edit.Extent.Z
        )
        {
            return false;
        }

        first = edit.Origin.X;
        last = (long)edit.Origin.X + edit.Extent.X - 1;
        return true;
    }

    /// <summary>
    /// The level-0 x range [first, last] of the row (<paramref name="y"/>, <paramref name="z"/>) that a capsule covers,
    /// by <see cref="VoxelEdit.Contains"/> alone, so every machine agrees. A capsule is convex, so the covered x form one
    /// interval: it is worked out in doubles for a radius a hair wider (<see cref="CapsuleSpan"/>), which holds every
    /// covered x, and its ends are then moved onto the first and last x that <see cref="VoxelEdit.Contains"/> accepts, a
    /// step or two each. False when the row has none.
    /// </summary>
    private static bool CapsuleRow(in VoxelEdit edit, int y, int z, out long first, out long last)
    {
        first = last = 0;
        edit.GetBounds(out var min, out var end);
        if (y < min.Y || y >= end.Y || z < min.Z || z >= end.Z)
        {
            return false;
        }

        if (!CapsuleSpan(in edit, y, z, edit.Radius + 0.01, out var low, out var high))
        {
            return false;
        }

        var from = Math.Max(min.X, (long)Math.Floor(low));
        var to = Math.Min(end.X - 1L, (long)Math.Ceiling(high));
        while (from <= to && !edit.Contains((int)from, y, z))
        {
            from++;
        }

        if (from > to)
        {
            return false;
        }

        while (!edit.Contains((int)to, y, z))
        {
            to--;
        }

        // Past what the doubles found, should they have fallen short.
        while (from > min.X && edit.Contains((int)(from - 1), y, z))
        {
            from--;
        }

        while (to < end.X - 1L && edit.Contains((int)(to + 1), y, z))
        {
            to++;
        }

        first = from;
        last = to;
        return true;
    }

    /// <summary>
    /// The range [low, high] of x at which the row at (<paramref name="y"/>, <paramref name="z"/>) lies within
    /// <paramref name="radius"/> of a capsule edit's segment, in doubles with + - * / and sqrt only; false when it misses.
    /// The union of where the row meets the two end spheres and the cylinder between the end planes.
    /// </summary>
    private static bool CapsuleSpan(
        in VoxelEdit edit,
        double y,
        double z,
        double radius,
        out double low,
        out double high
    )
    {
        double ax = edit.Origin.X,
            ay = edit.Origin.Y,
            az = edit.Origin.Z;
        double dx = edit.Extent.X,
            dy = edit.Extent.Y,
            dz = edit.Extent.Z;
        var radiusSquared = radius * radius;
        low = double.PositiveInfinity;
        high = double.NegativeInfinity;
        AddSphereSpan(ax, y - ay, z - az, radiusSquared, ref low, ref high);
        AddSphereSpan(ax + dx, y - (ay + dy), z - (az + dz), radiusSquared, ref low, ref high);
        var length = dx * dx + dy * dy + dz * dz;
        if (length <= 0)
        {
            return low <= high;
        }

        // With u = x - ax, the point's share along the segment is (u dx + k) / length, between the end planes when
        // that is between 0 and 1.
        var v = y - ay;
        var w = z - az;
        var k = v * dy + w * dz;
        var from = double.NegativeInfinity;
        var to = double.PositiveInfinity;
        if (dx > 0)
        {
            from = -k / dx;
            to = (length - k) / dx;
        }
        else if (dx < 0)
        {
            from = (length - k) / dx;
            to = -k / dx;
        }
        else if (k < 0 || k > length)
        {
            return low <= high;
        }

        // The squared distance from the axis times length: a u^2 + b u + c + radius^2 length, with a >= 0.
        var a = dy * dy + dz * dz;
        var b = -2 * dx * k;
        var c = length * (v * v + w * w) - k * k - radiusSquared * length;
        if (a > 0)
        {
            var discriminant = b * b - 4 * a * c;
            if (discriminant < 0)
            {
                return low <= high;
            }

            var root = Math.Sqrt(discriminant);
            from = Math.Max(from, (-b - root) / (2 * a));
            to = Math.Min(to, (-b + root) / (2 * a));
        }
        else if (c > 0)
        {
            return low <= high;
        }

        if (from <= to)
        {
            low = Math.Min(low, ax + from);
            high = Math.Max(high, ax + to);
        }

        return low <= high;
    }

    private static void AddSphereSpan(
        double centreX,
        double dy,
        double dz,
        double radiusSquared,
        ref double low,
        ref double high
    )
    {
        var room = radiusSquared - dy * dy - dz * dz;
        if (room >= 0)
        {
            var half = Math.Sqrt(room);
            low = Math.Min(low, centreX - half);
            high = Math.Max(high, centreX + half);
        }
    }

    /// <summary>
    /// Applies an edit to a stored chunk and returns its new state. Only the bricks the edit reaches are decoded; a
    /// brick that ends up different is encoded again. The cell index (<c>x + 16y + 256z</c>) of every changed brick
    /// is written to <paramref name="changedBricks"/> (4096 entries). Any thread on a chunk it alone holds (the main thread
    /// for live edits, a worker replaying the log into a column it generates); it may pass the memory budget, because an
    /// edit must never fail.
    /// </summary>
    public static ChildState ApplyToChunk(
        VoxelMemory memory,
        VoxelGrid grid,
        ChildState chunk,
        ChunkKey key,
        in VoxelEdit edit,
        Span<int> changedBricks,
        out int changedCount
    ) => ApplyToChunk(memory, grid, chunk, key, in edit, default, changedBricks, out changedCount);

    /// <summary>
    /// Applies an edit, with its piece when it is a piece edit, to a stored chunk and returns its new state (see the
    /// overload without a piece).
    /// </summary>
    public static ChildState ApplyToChunk(
        VoxelMemory memory,
        VoxelGrid grid,
        ChildState chunk,
        ChunkKey key,
        in VoxelEdit edit,
        in PieceView piece,
        Span<int> changedBricks,
        out int changedCount
    ) => ApplyToChunk(memory, grid, chunk, key, in edit, in piece, changedBricks, out changedCount, densities: false);

    /// <summary>
    /// <see cref="ApplyToChunk(VoxelMemory, VoxelGrid, ChildState, ChunkKey, in VoxelEdit, in PieceView, Span{int}, out int)"/>
    /// by sample points, keeping each voxel's density when <paramref name="densities"/> is true (a world's lift, paint,
    /// or piece put back: <see cref="ApplyToWorldChunk"/>).
    /// </summary>
    private static ChildState ApplyToChunk(
        VoxelMemory memory,
        VoxelGrid grid,
        ChildState chunk,
        ChunkKey key,
        in VoxelEdit edit,
        in PieceView piece,
        Span<int> changedBricks,
        out int changedCount,
        bool densities
    )
    {
        changedCount = 0;
        var level = new GridLevel(grid, key.Level);
        if (chunk.IsUniformOrEmpty && edit.LeavesUniform(chunk.Material))
        {
            return chunk;
        }

        var chunkOrigin = key.LevelOrigin;
        if (!TryGetRange(in edit, level, chunkOrigin, VoxelConstants.ChunkSize, out var min, out var end))
        {
            return chunk;
        }

        Span<byte> voxels = stackalloc byte[VoxelConstants.BrickVoxels];
        Span<byte> brickDensities = densities ? stackalloc byte[VoxelConstants.BrickVoxels] : default;
        for (var bz = min.Z >> 3; bz <= (end.Z - 1) >> 3; bz++)
        {
            for (var by = min.Y >> 3; by <= (end.Y - 1) >> 3; by++)
            {
                for (var bx = min.X >> 3; bx <= (end.X - 1) >> 3; bx++)
                {
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

                    // A clear or set that covers the whole brick makes it air or one material without reading it, and so
                    // does a fill or a clear of some materials that covers a brick of one material (a 5 m cube built
                    // into the air, or taken away again).
                    if (
                        (
                            edit.Operation is VoxelEditOperation.Clear or VoxelEditOperation.Set
                            || (
                                old.IsUniformOrEmpty
                                && edit.Operation is VoxelEditOperation.Fill or VoxelEditOperation.ClearMaterials
                            )
                        ) && Covers(in edit, level, brickOrigin)
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

                    BrickCodec.Fill(memory, old, voxels);
                    if (densities)
                    {
                        BrickCodec.FillDensities(memory, old, brickDensities);
                        if (
                            !ApplyBySamples(
                                in edit,
                                in piece,
                                level,
                                brickOrigin,
                                VoxelConstants.BrickSize,
                                voxels,
                                brickDensities
                            )
                        )
                        {
                            continue;
                        }
                    }
                    else if (!ApplyToRegion(in edit, in piece, level, brickOrigin, VoxelConstants.BrickSize, voxels))
                    {
                        continue;
                    }

                    var brick = BrickCodec.Encode(memory, voxels, brickDensities, mayExceedBudget: true, out _);
                    chunk = ChunkTree.SetBrick(memory, chunk, bx, by, bz, brick);
                    changedBricks[changedCount++] = bx | (by << 4) | (bz << 8);
                }
            }
        }

        return chunk;
    }

    /// <summary>
    /// Adds to <paramref name="counts"/> (one entry per material, 256) the solid voxels of a stored chunk that a sphere,
    /// capsule, or box edit covers: exactly the voxels <see cref="ApplyToChunk(VoxelMemory, VoxelGrid, ChildState, ChunkKey, in VoxelEdit, Span{int}, out int)"/>
    /// tests, so what a clear there would remove. A chunk, a 32-voxel cell, or a brick of one material is counted row span
    /// by row span without decoding, and a covered one at once (a 5 m cube covers some 240,000 bricks, most of them in
    /// cells of one material); a mixed brick is decoded. Changes nothing; allocates nothing.
    /// </summary>
    public static void CountInChunk(
        VoxelMemory memory,
        VoxelGrid grid,
        ChildState chunk,
        ChunkKey key,
        in VoxelEdit edit,
        Span<int> counts
    )
    {
        var level = new GridLevel(grid, key.Level);
        if (edit.Shape == VoxelEditShape.Piece)
        {
            throw new ArgumentException("Only sphere, capsule, and box edits are counted.", nameof(edit));
        }

        if (counts.Length < 256)
        {
            throw new ArgumentException("The counts hold one entry per material.", nameof(counts));
        }

        var chunkOrigin = key.LevelOrigin;
        if (
            chunk.IsEmpty
            || !TryGetRange(in edit, level, chunkOrigin, VoxelConstants.ChunkSize, out var min, out var end)
        )
        {
            return;
        }

        if (chunk.IsUniformOrEmpty)
        {
            counts[chunk.Material] += Covered(in edit, level, chunkOrigin, VoxelConstants.ChunkSize);
            return;
        }

        Span<byte> voxels = stackalloc byte[VoxelConstants.BrickVoxels];
        for (var cz = min.Z >> CellShift; cz <= (end.Z - 1) >> CellShift; cz++)
        {
            for (var cy = min.Y >> CellShift; cy <= (end.Y - 1) >> CellShift; cy++)
            {
                for (var cx = min.X >> CellShift; cx <= (end.X - 1) >> CellShift; cx++)
                {
                    var cell = ChunkTree.GetCell(memory, chunk, cx, cy, cz);
                    if (cell.IsEmpty)
                    {
                        continue;
                    }

                    if (cell.IsUniformOrEmpty)
                    {
                        var cellOrigin = new VoxelCoord(
                            chunkOrigin.X + (cx << CellShift),
                            chunkOrigin.Y + (cy << CellShift),
                            chunkOrigin.Z + (cz << CellShift)
                        );
                        counts[cell.Material] += Covered(in edit, level, cellOrigin, CellSize);
                        continue;
                    }

                    // The cell's bricks within the range.
                    const int perCell = CellSize >> VoxelConstants.BrickShift;
                    for (
                        var bz = Math.Max(min.Z >> 3, cz * perCell);
                        bz <= Math.Min((end.Z - 1) >> 3, cz * perCell + perCell - 1);
                        bz++
                    )
                    {
                        for (
                            var by = Math.Max(min.Y >> 3, cy * perCell);
                            by <= Math.Min((end.Y - 1) >> 3, cy * perCell + perCell - 1);
                            by++
                        )
                        {
                            for (
                                var bx = Math.Max(min.X >> 3, cx * perCell);
                                bx <= Math.Min((end.X - 1) >> 3, cx * perCell + perCell - 1);
                                bx++
                            )
                            {
                                CountBrick(memory, chunk, chunkOrigin, bx, by, bz, level, in edit, voxels, counts);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>The side of a chunk's 32-voxel cell (<see cref="ChunkTree"/>), and its shift.</summary>
    private const int CellShift = 5;

    private const int CellSize = 1 << CellShift;

    /// <summary>Adds the solid voxels of the chunk's brick (bx, by, bz) that the edit covers to <paramref name="counts"/>.</summary>
    private static void CountBrick(
        VoxelMemory memory,
        ChildState chunk,
        VoxelCoord chunkOrigin,
        int bx,
        int by,
        int bz,
        GridLevel level,
        in VoxelEdit edit,
        Span<byte> voxels,
        Span<int> counts
    )
    {
        var brick = ChunkTree.GetBrick(memory, chunk, bx, by, bz);
        if (brick.IsEmpty)
        {
            return;
        }

        var brickOrigin = new VoxelCoord(
            chunkOrigin.X + (bx << 3),
            chunkOrigin.Y + (by << 3),
            chunkOrigin.Z + (bz << 3)
        );
        if (brick.IsUniformOrEmpty)
        {
            counts[brick.Material] += Covered(in edit, level, brickOrigin, VoxelConstants.BrickSize);
            return;
        }

        BrickCodec.Fill(memory, brick, voxels);
        CountInRegion(in edit, level, brickOrigin, VoxelConstants.BrickSize, voxels, counts);
    }

    /// <summary>How many voxels of the cube of <paramref name="size"/> from <paramref name="origin"/> the edit covers: all at once when it covers its corners.</summary>
    private static int Covered(in VoxelEdit edit, GridLevel level, VoxelCoord origin, int size) =>
        Covers(in edit, level, origin, size) ? size * size * size : CoveredInRegion(in edit, level, origin, size);

    /// <summary>How many voxels of a dense cube a sphere, capsule, or box edit covers, found row span by row span.</summary>
    private static int CoveredInRegion(in VoxelEdit edit, GridLevel level, VoxelCoord origin, int size)
    {
        if (!TryGetRange(in edit, level, origin, size, out var min, out var end))
        {
            return 0;
        }

        var covered = 0;
        for (var z = min.Z; z < end.Z; z++)
        {
            var sampleZ = level.SampleAt(origin.Z + z);
            for (var y = min.Y; y < end.Y; y++)
            {
                var sampleY = level.SampleAt(origin.Y + y);
                if (RowSpan(in edit, level, origin.X, sampleY, sampleZ, min.X, end.X, out var x0, out var x1))
                {
                    covered += x1 - x0;
                }
            }
        }

        return covered;
    }

    /// <summary>Adds the solid voxels of a dense cube that a sphere, capsule, or box edit covers to <paramref name="counts"/>.</summary>
    private static void CountInRegion(
        in VoxelEdit edit,
        GridLevel level,
        VoxelCoord origin,
        int size,
        ReadOnlySpan<byte> voxels,
        Span<int> counts
    )
    {
        if (!TryGetRange(in edit, level, origin, size, out var min, out var end))
        {
            return;
        }

        for (var z = min.Z; z < end.Z; z++)
        {
            var sampleZ = level.SampleAt(origin.Z + z);
            for (var y = min.Y; y < end.Y; y++)
            {
                var sampleY = level.SampleAt(origin.Y + y);
                if (!RowSpan(in edit, level, origin.X, sampleY, sampleZ, min.X, end.X, out var x0, out var x1))
                {
                    continue;
                }

                foreach (var voxel in voxels.Slice((z * size + y) * size + x0, x1 - x0))
                {
                    if (voxel != VoxelConstants.Air)
                    {
                        counts[voxel]++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// True when every voxel of the brick at <paramref name="brickOrigin"/> (voxels of <paramref name="level"/>) lies in
    /// a sphere, capsule, or box edit: all three are convex, so it is enough that the sample points of the brick's eight corner
    /// voxels do.
    /// </summary>
    private static bool Covers(in VoxelEdit edit, GridLevel level, VoxelCoord brickOrigin) =>
        Covers(in edit, level, brickOrigin, VoxelConstants.BrickSize);

    /// <summary>True when the edit covers every voxel of the cube of <paramref name="size"/> from <paramref name="brickOrigin"/>: its eight corners, as every shape is convex.</summary>
    private static bool Covers(in VoxelEdit edit, GridLevel level, VoxelCoord brickOrigin, int size)
    {
        if (edit.Shape == VoxelEditShape.Piece)
        {
            return false;
        }

        var last = size - 1;
        for (var corner = 0; corner < 8; corner++)
        {
            if (
                !edit.Contains(
                    level.SampleAt(brickOrigin.X + ((corner & 1) != 0 ? last : 0)),
                    level.SampleAt(brickOrigin.Y + ((corner & 2) != 0 ? last : 0)),
                    level.SampleAt(brickOrigin.Z + ((corner & 4) != 0 ? last : 0))
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Puts a piece's voxels into the air of a dense cube's local range [min, end).</summary>
    private static bool PlaceInRegion(
        in PiecePlacement placement,
        GridLevel level,
        VoxelCoord origin,
        VoxelCoord min,
        VoxelCoord end,
        int size,
        Span<byte> voxels
    )
    {
        var changed = false;
        for (var z = min.Z; z < end.Z; z++)
        {
            var sampleZ = level.SampleAt(origin.Z + z);
            for (var y = min.Y; y < end.Y; y++)
            {
                var sampleY = level.SampleAt(origin.Y + y);
                var row = voxels.Slice((z * size + y) * size, size);
                for (var x = min.X; x < end.X; x++)
                {
                    if (row[x] != VoxelConstants.Air)
                    {
                        continue;
                    }

                    var material = placement.MaterialAt(level.SampleAt(origin.X + x), sampleY, sampleZ);
                    if (material != VoxelConstants.Air)
                    {
                        row[x] = material;
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>True when the edit's covered box overlaps the box [min, end), both in centimetres.</summary>
    public static bool Overlaps(in VoxelEdit edit, VoxelCoord min, VoxelCoord end)
    {
        edit.GetBounds(out var editMin, out var editEnd);
        return editMin.X < end.X
            && editEnd.X > min.X
            && editMin.Y < end.Y
            && editEnd.Y > min.Y
            && editMin.Z < end.Z
            && editEnd.Z > min.Z;
    }

    /// <summary>
    /// The voxels of a cube (origin and size in voxels of <paramref name="level"/>) whose sample points can lie in
    /// the edit's bounds, as local [min, end). False when there are none.
    /// </summary>
    private static bool TryGetRange(
        in VoxelEdit edit,
        GridLevel level,
        VoxelCoord origin,
        int size,
        out VoxelCoord min,
        out VoxelCoord end
    )
    {
        edit.GetBounds(out var editMin, out var editEnd);
        min = new VoxelCoord(
            Math.Max(0, level.FirstSampleAtOrAfter(editMin.X) - origin.X),
            Math.Max(0, level.FirstSampleAtOrAfter(editMin.Y) - origin.Y),
            Math.Max(0, level.FirstSampleAtOrAfter(editMin.Z) - origin.Z)
        );
        end = new VoxelCoord(
            Math.Min(size, level.FirstSampleAtOrAfter(editEnd.X) - origin.X),
            Math.Min(size, level.FirstSampleAtOrAfter(editEnd.Y) - origin.Y),
            Math.Min(size, level.FirstSampleAtOrAfter(editEnd.Z) - origin.Z)
        );
        return min.X < end.X && min.Y < end.Y && min.Z < end.Z;
    }
}
