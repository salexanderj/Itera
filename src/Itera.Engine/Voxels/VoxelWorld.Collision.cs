using System.Buffers.Binary;
using System.Numerics;

namespace Itera.Engine.Voxels;

/// <summary>
/// Receives the blocking voxels of a box from <see cref="VoxelWorld.WalkBlocking{TVisitor}(int, int, int, int, int, int, in MaterialSet, ref TVisitor)"/>,
/// in the voxels of the level walked (level 0, or level 2 for a giant's colliders): whole boxes where
/// the tree holds one blocking material, and single bricks where it holds mixed voxels. Everything handed over is already
/// cut to the box walked.
/// </summary>
internal interface IBlockingVisitor
{
    /// <summary>Every voxel from (x0, y0, z0) to (x1, y1, z1), inclusive, blocks.</summary>
    void Box(int x0, int y0, int z0, int x1, int y1, int z1);

    /// <summary>
    /// The brick whose lowest corner is voxel (x, y, z) holds blocking voxels where <paramref name="mask"/> has a bit:
    /// eight words, word z for layer z, bit x + 8y.
    /// </summary>
    void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask);

    /// <summary>True when nothing more the walk could hand over would change the answer, so it stops.</summary>
    bool Finished { get; }
}

/// <summary>
/// The level-0 cells (voxel columns) under an upright circle: the cells whose square comes nearer than the radius to the
/// centre. Cells the circle only touches are left out, so a circle held at its radius from a wall does not stand on it.
/// Coordinates are in level-0 voxels. Cells are numbered within <see cref="Far"/> of the middle: a circle farther out
/// (no voxel is there) keeps to cells at that distance, so no bound wraps round an int (feet past about -21,475 km would give a box from <see cref="int.MinValue"/> to
/// <see cref="int.MaxValue"/>).
/// </summary>
internal readonly struct CircleFootprint
{
    /// <summary>The farthest cell from the middle, on x and z: far outside the world, and far inside an int's range.</summary>
    public const int Far = 1 << 30;

    public CircleFootprint(double centreX, double centreZ, double radius)
    {
        CentreX = centreX;
        CentreZ = centreZ;
        Radius = radius;
        MinX = Cell(Math.Floor(centreX - radius));
        MaxX = Cell(Math.Ceiling(centreX + radius) - 1);
        MinZ = Cell(Math.Floor(centreZ - radius));
        MaxZ = Cell(Math.Ceiling(centreZ + radius) - 1);
    }

    public double CentreX { get; }

    public double CentreZ { get; }

    public double Radius { get; }

    /// <summary>Lowest x of a cell that may be under the circle.</summary>
    public int MinX { get; }

    /// <summary>Highest x of a cell that may be under the circle.</summary>
    public int MaxX { get; }

    /// <summary>Lowest z of a cell that may be under the circle.</summary>
    public int MinZ { get; }

    /// <summary>Highest z of a cell that may be under the circle.</summary>
    public int MaxZ { get; }

    /// <summary>The cells of row <paramref name="z"/> under the circle, from x0 to x1 inclusive; false when there are none.</summary>
    public bool Row(int z, out int x0, out int x1)
    {
        var dz =
            CentreZ < z ? z - CentreZ
            : CentreZ > z + 1 ? CentreZ - (z + 1)
            : 0;
        if (dz >= Radius)
        {
            x0 = 0;
            x1 = -1;
            return false;
        }

        var half = Math.Sqrt(Radius * Radius - dz * dz);
        x0 = Cell(Math.Floor(CentreX - half));
        x1 = Cell(Math.Ceiling(CentreX + half) - 1);
        return x0 <= x1;
    }

    /// <summary>A whole number of voxels as a cell, kept within <see cref="Far"/>; NaN as the middle's.</summary>
    private static int Cell(double voxels) => double.IsNaN(voxels) ? 0 : (int)Math.Clamp(voxels, -Far, Far);
}

/// <summary>
/// A footprint with its rows worked out once, for queries that test many bricks against it: row z's cells, as
/// <see cref="CircleFootprint.Row"/> gives them. A footprint deeper than <see cref="MaxTableRows"/> works each row out as
/// it goes.
/// </summary>
internal readonly ref struct FootprintRows
{
    /// <summary>The most rows a table holds: a footprint 5 m across.</summary>
    public const int MaxTableRows = 512;

    private readonly CircleFootprint _footprint;
    private readonly ReadOnlySpan<int> _ranges;

    /// <param name="footprint">The footprint.</param>
    /// <param name="table">Room for the rows: <see cref="TableLength"/> ints.</param>
    public FootprintRows(in CircleFootprint footprint, Span<int> table)
    {
        _footprint = footprint;
        var depth = footprint.MaxZ - footprint.MinZ + 1;
        if (depth <= 0 || table.Length < 2 * depth)
        {
            _ranges = default;
            return;
        }

        for (var row = 0; row < depth; row++)
        {
            footprint.Row(footprint.MinZ + row, out table[2 * row], out table[2 * row + 1]);
        }

        _ranges = table[..(2 * depth)];
    }

    public int MinZ => _footprint.MinZ;

    public int MaxZ => _footprint.MaxZ;

    /// <summary>How many ints the table of <paramref name="footprint"/>'s rows takes: 0 when it is too deep for one.</summary>
    public static int TableLength(in CircleFootprint footprint)
    {
        var depth = footprint.MaxZ - footprint.MinZ + 1;
        return depth > 0 && depth <= MaxTableRows ? 2 * depth : 0;
    }

    /// <summary>The cells of row <paramref name="z"/> under the circle, from x0 to x1 inclusive; false when there are none.</summary>
    public bool Row(int z, out int x0, out int x1)
    {
        if (_ranges.IsEmpty)
        {
            return _footprint.Row(z, out x0, out x1);
        }

        // Rows outside the footprint's box hold no cells.
        var row = z - _footprint.MinZ;
        if ((uint)row >= (uint)(_ranges.Length / 2))
        {
            x0 = 0;
            x1 = -1;
            return false;
        }

        x0 = _ranges[2 * row];
        x1 = _ranges[2 * row + 1];
        return x0 <= x1;
    }
}

public sealed partial class VoxelWorld
{
    /// <summary>
    /// True when every level-0 column that holds a voxel from x0 to x1 and z0 to z1 (inclusive, level-0 voxels) is
    /// resident, so its voxels can be read. Columns outside the map count as resident: they hold nothing.
    /// </summary>
    internal bool ColumnsResident(int x0, int z0, int x1, int z1) => ColumnsResident(0, x0, z0, x1, z1);

    /// <summary>
    /// <see cref="ColumnsResident(int, int, int, int)"/> for the columns of <paramref name="level"/>, with x0 to x1 and z0
    /// to z1 in that level's voxels.
    /// </summary>
    internal bool ColumnsResident(int level, int x0, int z0, int x1, int z1)
    {
        // Only the world's columns are walked (a box far outside the map
        // would walk up to 10^15 keys that cannot exist).
        var first = FirstVoxelXZ(level);
        var last = LastVoxelXZ(level);
        x0 = Math.Max(x0, first);
        z0 = Math.Max(z0, first);
        x1 = Math.Min(x1, last);
        z1 = Math.Min(z1, last);
        if (x0 > x1 || z0 > z1)
        {
            return true;
        }

        for (var cz = z0 >> VoxelConstants.ChunkShift; cz <= z1 >> VoxelConstants.ChunkShift; cz++)
        {
            for (var cx = x0 >> VoxelConstants.ChunkShift; cx <= x1 >> VoxelConstants.ChunkShift; cx++)
            {
                var key = new ColumnKey(level, cx, cz);
                if (
                    key.IsInBounds(Grid)
                    && (!_columns.TryGetValue(key, out var column) || column.State != ColumnState.Resident)
                )
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Hands <paramref name="visitor"/> every voxel from (x0, y0, z0) to (x1, y1, z1), inclusive, that is solid and not in
    /// <paramref name="passable"/>, as boxes and bricks. Columns that are not resident are skipped (check
    /// <see cref="ColumnsResident(int, int, int, int)"/> first). It walks the largest empty or uniform part of the tree it meets, so open air
    /// and solid ground cost little. Main thread; allocates nothing.
    /// </summary>
    internal void WalkBlocking<TVisitor>(
        int x0,
        int y0,
        int z0,
        int x1,
        int y1,
        int z1,
        in MaterialSet passable,
        ref TVisitor visitor
    )
        where TVisitor : struct, IBlockingVisitor, allows ref struct =>
        WalkBlocking(0, x0, y0, z0, x1, y1, z1, in passable, ref visitor);

    /// <summary>
    /// <see cref="WalkBlocking{TVisitor}(int, int, int, int, int, int, in MaterialSet, ref TVisitor)"/> in the columns of
    /// <paramref name="level"/>, with the corners and the voxels handed over in that level's voxels.
    /// </summary>
    internal unsafe void WalkBlocking<TVisitor>(
        int level,
        int x0,
        int y0,
        int z0,
        int x1,
        int y1,
        int z1,
        in MaterialSet passable,
        ref TVisitor visitor
    )
        where TVisitor : struct, IBlockingVisitor, allows ref struct
    {
        // Only the world's columns are walked, as in ColumnsResident.
        x0 = Math.Max(x0, FirstVoxelXZ(level));
        y0 = Math.Max(y0, Grid.MinChunkYAt(level) << VoxelConstants.ChunkShift);
        z0 = Math.Max(z0, FirstVoxelXZ(level));
        x1 = Math.Min(x1, LastVoxelXZ(level));
        y1 = Math.Min(y1, ((Grid.MaxChunkYAt(level) + 1) << VoxelConstants.ChunkShift) - 1);
        z1 = Math.Min(z1, LastVoxelXZ(level));
        if (x0 > x1 || y0 > y1 || z0 > z1)
        {
            return;
        }

        var memory = Memory;
        Span<ulong> mask = stackalloc ulong[VoxelConstants.BrickMaskWords];
        const int Shift = VoxelConstants.ChunkShift;
        const int Last = VoxelConstants.ChunkSize - 1;
        for (var cz = z0 >> Shift; cz <= z1 >> Shift; cz++)
        {
            for (var cx = x0 >> Shift; cx <= x1 >> Shift; cx++)
            {
                if (
                    !_columns.TryGetValue(new ColumnKey(level, cx, cz), out var column)
                    || column.State != ColumnState.Resident
                )
                {
                    continue;
                }

                for (var cy = y0 >> Shift; cy <= y1 >> Shift; cy++)
                {
                    var chunk = column.LayerAt(cy - Grid.MinChunkYAt(level));
                    if (chunk.IsEmpty)
                    {
                        continue;
                    }

                    int ox = cx << Shift,
                        oy = cy << Shift,
                        oz = cz << Shift;
                    // The walked box in the chunk's own coordinates.
                    int lx0 = Math.Max(x0 - ox, 0),
                        ly0 = Math.Max(y0 - oy, 0),
                        lz0 = Math.Max(z0 - oz, 0),
                        lx1 = Math.Min(x1 - ox, Last),
                        ly1 = Math.Min(y1 - oy, Last),
                        lz1 = Math.Min(z1 - oz, Last);
                    if (chunk.Kind == ChildKind.Uniform)
                    {
                        if (!passable.Contains(chunk.Material))
                        {
                            visitor.Box(ox + lx0, oy + ly0, oz + lz0, ox + lx1, oy + ly1, oz + lz1);
                            if (visitor.Finished)
                            {
                                return;
                            }
                        }

                        continue;
                    }

                    WalkNode(
                        memory,
                        chunk.Handle,
                        ox,
                        oy,
                        oz,
                        lx0,
                        ly0,
                        lz0,
                        lx1,
                        ly1,
                        lz1,
                        in passable,
                        mask,
                        ref visitor
                    );
                    if (visitor.Finished)
                    {
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Hands <paramref name="visitor"/> the blocking voxels of one chunk tree from (0, 0, 0) to (x1, y1, z1), inclusive,
    /// in the tree's own coordinates, as <see cref="WalkBlocking{TVisitor}(int, int, int, int, int, int, in MaterialSet, ref TVisitor)"/> does for the world: a lifted piece, for example. Any
    /// thread, as long as nothing frees the tree. Allocates nothing.
    /// </summary>
    internal static void WalkTree<TVisitor>(
        VoxelMemory memory,
        ChildState tree,
        int x1,
        int y1,
        int z1,
        in MaterialSet passable,
        ref TVisitor visitor
    )
        where TVisitor : struct, IBlockingVisitor, allows ref struct
    {
        const int Last = VoxelConstants.ChunkSize - 1;
        x1 = Math.Min(x1, Last);
        y1 = Math.Min(y1, Last);
        z1 = Math.Min(z1, Last);
        if (tree.IsEmpty || x1 < 0 || y1 < 0 || z1 < 0)
        {
            return;
        }

        if (tree.Kind == ChildKind.Uniform)
        {
            if (!passable.Contains(tree.Material))
            {
                visitor.Box(0, 0, 0, x1, y1, z1);
            }

            return;
        }

        Span<ulong> mask = stackalloc ulong[VoxelConstants.BrickMaskWords];
        WalkNode(memory, tree.Handle, 0, 0, 0, 0, 0, 0, x1, y1, z1, in passable, mask, ref visitor);
    }

    /// <summary>
    /// A 2D grid of the level-0 cells in a window that hold a blocking voxel anywhere from y0 to y1 (inclusive): row
    /// <c>z - windowZ</c> of <paramref name="rows"/> has bit <c>x - windowX</c> set for such a cell (x, z). The window is
    /// at most 128 cells wide and <c>rows.Length</c> deep. Main thread; allocates nothing.
    /// </summary>
    internal void BlockedCells(
        int windowX,
        int windowZ,
        int width,
        int y0,
        int y1,
        in MaterialSet passable,
        Span<UInt128> rows
    )
    {
        if ((uint)(width - 1) >= 128)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A window is 1 to 128 cells wide.");
        }

        rows.Clear();
        var visitor = new CellsVisitor(rows, windowX, windowZ);
        WalkBlocking(
            windowX,
            y0,
            windowZ,
            windowX + width - 1,
            y1,
            windowZ + rows.Length - 1,
            in passable,
            ref visitor
        );
    }

    /// <summary>
    /// For each level-0 column of the window from (<paramref name="windowX"/>, <paramref name="windowZ"/>),
    /// <paramref name="width"/> by <paramref name="depth"/> columns (index <c>x + width * z</c>), the top (its y, one
    /// above the voxel) of its highest blocking voxel from y0 to y1, or <see cref="int.MinValue"/> where none blocks. Main
    /// thread; allocates nothing.
    /// </summary>
    internal void ColumnTops(
        int windowX,
        int windowZ,
        int width,
        int depth,
        int y0,
        int y1,
        in MaterialSet passable,
        Span<int> tops
    )
    {
        tops[..(width * depth)].Fill(int.MinValue);
        var visitor = new ColumnTopsVisitor(tops, windowX, windowZ, width, depth);
        WalkBlocking(windowX, y0, windowZ, windowX + width - 1, y1, windowZ + depth - 1, in passable, ref visitor);
    }

    /// <summary>
    /// The height, in level-0 voxels, of the surface over a run of blocking voxels whose top is <paramref name="top"/>
    /// in column (<paramref name="x"/>, <paramref name="z"/>): where the density (<see cref="VoxelDensity"/>) crosses one
    /// half between the centres of the run's highest voxel and the voxel above, as the mesher draws it, so from half a
    /// voxel under the top to half a voxel over it; the top itself where both keep their materials' defaults. A voxel
    /// above that blocks too gives the top, and a passable one (foliage) counts as empty. Main thread; allocates nothing.
    /// </summary>
    internal double SurfaceOver(int x, int top, int z, in MaterialSet passable)
    {
        var below = new VoxelCoord(x, top - 1, z);
        var above = new VoxelCoord(x, top, z);
        if (
            !TryGetResident(ColumnKey.Containing(below, 0), out var column)
            || !column.TryReadVoxelAndDensity(Memory, below, out _, out var low, out var lowStored)
        )
        {
            return top;
        }

        column.TryReadVoxelAndDensity(Memory, above, out var material, out var high, out var highStored);
        if (!lowStored && !highStored)
        {
            return top;
        }

        if (material != VoxelConstants.Air)
        {
            if (!passable.Contains(material))
            {
                return top;
            }

            high = VoxelDensity.Empty;
        }

        return top - 0.5 + (low - 127.5) / (low - (double)high);
    }

    /// <summary>The lowest x and z, in <paramref name="level"/>'s voxels, of a column of that level inside the world.</summary>
    private int FirstVoxelXZ(int level) => Grid.MinChunkXZAt(level) << VoxelConstants.ChunkShift;

    /// <summary>The highest x and z, in <paramref name="level"/>'s voxels, of a column of that level inside the world.</summary>
    private int LastVoxelXZ(int level) => ((Grid.MaxChunkXZAt(level) + 1) << VoxelConstants.ChunkShift) - 1;

    /// <summary>
    /// The top (one above the highest voxel) of the highest blocking voxel from y0 to y1 (inclusive) under
    /// <paramref name="footprint"/>, or <see cref="int.MinValue"/> when there is none. Main thread; allocates nothing.
    /// </summary>
    internal int HighestTop(in CircleFootprint footprint, int y0, int y1, in MaterialSet passable)
    {
        Span<int> table = stackalloc int[FootprintRows.TableLength(in footprint)];
        var visitor = new HighestVisitor(new FootprintRows(in footprint, table), y1);
        WalkBlocking(footprint.MinX, y0, footprint.MinZ, footprint.MaxX, y1, footprint.MaxZ, in passable, ref visitor);
        return visitor.Top;
    }

    /// <summary>
    /// The bottom (its y) of the lowest blocking voxel from y0 to y1 (inclusive) under <paramref name="footprint"/>, or
    /// <see cref="int.MaxValue"/> when there is none. Main thread; allocates nothing.
    /// </summary>
    internal int LowestBottom(in CircleFootprint footprint, int y0, int y1, in MaterialSet passable)
    {
        Span<int> table = stackalloc int[FootprintRows.TableLength(in footprint)];
        var visitor = new LowestVisitor(new FootprintRows(in footprint, table), y0);
        WalkBlocking(footprint.MinX, y0, footprint.MinZ, footprint.MaxX, y1, footprint.MaxZ, in passable, ref visitor);
        return visitor.Bottom;
    }

    private static void WalkNode<TVisitor>(
        VoxelMemory memory,
        int root,
        int ox,
        int oy,
        int oz,
        int lx0,
        int ly0,
        int lz0,
        int lx1,
        int ly1,
        int lz1,
        in MaterialSet passable,
        scoped Span<ulong> mask,
        ref TVisitor visitor
    )
        where TVisitor : struct, IBlockingVisitor, allows ref struct
    {
        const int CellShift = VoxelConstants.CellShift;
        const int BrickShift = VoxelConstants.BrickShift;
        for (var cellZ = lz0 >> CellShift; cellZ <= lz1 >> CellShift; cellZ++)
        {
            for (var cellY = ly0 >> CellShift; cellY <= ly1 >> CellShift; cellY++)
            {
                for (var cellX = lx0 >> CellShift; cellX <= lx1 >> CellShift; cellX++)
                {
                    var cell = ChunkTree.GetChild(memory, root, cellX | (cellY << 2) | (cellZ << 4));
                    if (cell.IsEmpty)
                    {
                        continue;
                    }

                    int cx0 = cellX << CellShift,
                        cy0 = cellY << CellShift,
                        cz0 = cellZ << CellShift;
                    int bx0 = Math.Max(lx0, cx0),
                        by0 = Math.Max(ly0, cy0),
                        bz0 = Math.Max(lz0, cz0),
                        bx1 = Math.Min(lx1, cx0 + (1 << CellShift) - 1),
                        by1 = Math.Min(ly1, cy0 + (1 << CellShift) - 1),
                        bz1 = Math.Min(lz1, cz0 + (1 << CellShift) - 1);
                    if (cell.Kind == ChildKind.Uniform)
                    {
                        if (!passable.Contains(cell.Material))
                        {
                            visitor.Box(ox + bx0, oy + by0, oz + bz0, ox + bx1, oy + by1, oz + bz1);
                            if (visitor.Finished)
                            {
                                return;
                            }
                        }

                        continue;
                    }

                    for (var brickZ = bz0 >> BrickShift; brickZ <= bz1 >> BrickShift; brickZ++)
                    {
                        for (var brickY = by0 >> BrickShift; brickY <= by1 >> BrickShift; brickY++)
                        {
                            for (var brickX = bx0 >> BrickShift; brickX <= bx1 >> BrickShift; brickX++)
                            {
                                var brick = ChunkTree.GetChild(
                                    memory,
                                    cell.Handle,
                                    (brickX & 3) | ((brickY & 3) << 2) | ((brickZ & 3) << 4)
                                );
                                if (brick.IsEmpty)
                                {
                                    continue;
                                }

                                int px = brickX << BrickShift,
                                    py = brickY << BrickShift,
                                    pz = brickZ << BrickShift;
                                // The walked box within this brick, 0 to 7 on each axis.
                                int ix0 = Math.Max(bx0 - px, 0),
                                    iy0 = Math.Max(by0 - py, 0),
                                    iz0 = Math.Max(bz0 - pz, 0),
                                    ix1 = Math.Min(bx1 - px, 7),
                                    iy1 = Math.Min(by1 - py, 7),
                                    iz1 = Math.Min(bz1 - pz, 7);
                                if (brick.Kind == ChildKind.Uniform)
                                {
                                    if (!passable.Contains(brick.Material))
                                    {
                                        visitor.Box(
                                            ox + px + ix0,
                                            oy + py + iy0,
                                            oz + pz + iz0,
                                            ox + px + ix1,
                                            oy + py + iy1,
                                            oz + pz + iz1
                                        );
                                    }

                                    continue;
                                }

                                if (
                                    BlockingMask(memory, brick.Handle, in passable, mask)
                                    && Clip(mask, ix0, iy0, iz0, ix1, iy1, iz1)
                                )
                                {
                                    visitor.Brick(ox + px, oy + py, oz + pz, mask);
                                }
                            }
                        }
                    }

                    if (visitor.Finished)
                    {
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Writes the stored brick's blocking voxels into <paramref name="mask"/>: its occupancy, less the voxels of passable
    /// materials. Returns false when none blocks.
    /// </summary>
    internal static unsafe bool BlockingMask(VoxelMemory memory, int brick, in MaterialSet passable, Span<ulong> mask)
    {
        var occupied = new ReadOnlySpan<ulong>(memory.BrickMask(brick), VoxelConstants.BrickMaskWords);
        var record = memory.Brick(brick);
        var bits = record->Bits;

        // A brick stored for its densities alone holds no solid voxel.
        if (bits == 0 && record->MaterialOrCount == VoxelConstants.Air)
        {
            return false;
        }

        if (passable.IsEmpty)
        {
            occupied.CopyTo(mask);
            return true;
        }

        if (bits == 0)
        {
            if (passable.Contains(record->MaterialOrCount))
            {
                return false;
            }

            occupied.CopyTo(mask);
            return true;
        }

        byte* palette = null;
        if (bits < 8)
        {
            palette = memory.Pointer(BrickCodec.IndexPool(bits), record->Block);
            var passing = 0;
            for (var i = 0; i < record->MaterialOrCount; i++)
            {
                passing += passable.Contains(palette[i]) ? 1 : 0;
            }

            if (passing == record->MaterialOrCount)
            {
                return false;
            }

            if (passing == 0)
            {
                occupied.CopyTo(mask);
                return true;
            }
        }

        // Some materials pass and some block.
        var any = false;
        if (bits == 8)
        {
            var materials = memory.Pointer(VoxelPool.Index8, record->Block);
            for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
            {
                var blocking = 0UL;
                for (var word = occupied[w]; word != 0; word &= word - 1)
                {
                    var bit = BitOperations.TrailingZeroCount(word);
                    if (!passable.Contains(materials[(w << 6) + bit]))
                    {
                        blocking |= 1UL << bit;
                    }
                }

                mask[w] = blocking;
                any |= blocking != 0;
            }

            return any;
        }

        // Palette indices are packed low bit first, 64 / bits voxels to a little-endian word: each word is compared
        // with every blocking entry's index at once, field by field, and the fields that match are packed into bits.
        var fields = bits switch
        {
            1 => ulong.MaxValue,
            2 => 0x5555_5555_5555_5555UL,
            _ => 0x1111_1111_1111_1111UL,
        };
        Span<ulong> blockingIndices = stackalloc ulong[16];
        var count = 0;
        for (var i = 0; i < record->MaterialOrCount; i++)
        {
            if (!passable.Contains(palette[i]))
            {
                blockingIndices[count++] = (ulong)i * fields;
            }
        }

        var indices = new ReadOnlySpan<byte>(palette + VoxelMemory.PaletteBytes, VoxelConstants.BrickVoxels * bits / 8);
        var perWord = 64 / bits;
        for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
        {
            var blocking = 0UL;
            for (var k = 0; k < bits; k++)
            {
                var packed = BinaryPrimitives.ReadUInt64LittleEndian(indices[(((w * bits) + k) << 3)..]);
                var matches = 0UL;
                for (var i = 0; i < count; i++)
                {
                    matches |= FieldsEqual(packed ^ blockingIndices[i], bits) & fields;
                }

                blocking |= PackFields(matches, bits) << (k * perWord);
            }

            blocking &= occupied[w];
            mask[w] = blocking;
            any |= blocking != 0;
        }

        return any;
    }

    /// <summary>A bit at the lowest bit of each <paramref name="bits"/>-bit field of <paramref name="difference"/> that is 0.</summary>
    private static ulong FieldsEqual(ulong difference, int bits) =>
        bits switch
        {
            1 => ~difference,
            2 => ~(difference | (difference >> 1)),
            _ => ~(difference | (difference >> 1) | (difference >> 2) | (difference >> 3)),
        };

    /// <summary>
    /// Packs the lowest bit of each <paramref name="bits"/>-bit field of <paramref name="x"/> (the other bits 0) into the
    /// low 64 / <paramref name="bits"/> bits, in order.
    /// </summary>
    private static ulong PackFields(ulong x, int bits)
    {
        if (bits == 2)
        {
            x = (x | (x >> 1)) & 0x3333_3333_3333_3333UL;
            x = (x | (x >> 2)) & 0x0F0F_0F0F_0F0F_0F0FUL;
            x = (x | (x >> 4)) & 0x00FF_00FF_00FF_00FFUL;
            x = (x | (x >> 8)) & 0x0000_FFFF_0000_FFFFUL;
            return (x | (x >> 16)) & 0x0000_0000_FFFF_FFFFUL;
        }

        if (bits == 4)
        {
            x = (x | (x >> 3)) & 0x0303_0303_0303_0303UL;
            x = (x | (x >> 6)) & 0x000F_000F_000F_000FUL;
            x = (x | (x >> 12)) & 0x0000_00FF_0000_00FFUL;
            return (x | (x >> 24)) & 0x0000_0000_0000_FFFFUL;
        }

        return x;
    }

    /// <summary>Keeps only the mask's voxels from (x0, y0, z0) to (x1, y1, z1) within the brick; false when none is left.</summary>
    internal static bool Clip(Span<ulong> mask, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        var xBits = ((1UL << (x1 - x0 + 1)) - 1) << x0;
        var rows = (y1 == 7 ? ulong.MaxValue : (1UL << (8 * (y1 + 1))) - 1) & ~((1UL << (8 * y0)) - 1);
        var keep = rows & (xBits * 0x0101010101010101UL);
        var any = 0UL;
        for (var z = 0; z < VoxelConstants.BrickMaskWords; z++)
        {
            mask[z] = z >= z0 && z <= z1 ? mask[z] & keep : 0;
            any |= mask[z];
        }

        return any != 0;
    }

    /// <summary>Bits <paramref name="from"/> to <paramref name="to"/> inclusive, both within 0 to 127.</summary>
    private static UInt128 RangeBits(int from, int to) =>
        (to == 127 ? UInt128.MaxValue : (UInt128.One << (to + 1)) - 1) & ~((UInt128.One << from) - 1);

    /// <summary>Folds blocking voxels over y into the cells of a window.</summary>
    internal readonly ref struct CellsVisitor : IBlockingVisitor
    {
        private readonly Span<UInt128> _rows;
        private readonly int _windowX;
        private readonly int _windowZ;

        public CellsVisitor(Span<UInt128> rows, int windowX, int windowZ)
        {
            _rows = rows;
            _windowX = windowX;
            _windowZ = windowZ;
        }

        public bool Finished => false;

        public void Box(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            var bits = RangeBits(x0 - _windowX, x1 - _windowX);
            for (var z = z0; z <= z1; z++)
            {
                _rows[z - _windowZ] |= bits;
            }
        }

        public void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask)
        {
            var shift = x - _windowX;
            for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
            {
                var word = mask[w];
                if (word == 0)
                {
                    continue;
                }

                // Any voxel in the layer's eight rows marks its column.
                word |= word >> 32;
                word |= word >> 16;
                word |= word >> 8;
                var cells = (UInt128)(word & 0xFF);
                _rows[z + w - _windowZ] |= shift >= 0 ? cells << shift : cells >> -shift;
            }
        }
    }

    /// <summary>Finds the highest top of a blocking voxel under a footprint.</summary>
    internal ref struct HighestVisitor : IBlockingVisitor
    {
        public int Top;
        private readonly FootprintRows _footprint;
        private readonly int _highest;

        /// <param name="footprint">The footprint.</param>
        /// <param name="y1">The highest voxel walked: once one there is found, nothing is higher.</param>
        public HighestVisitor(FootprintRows footprint, int y1)
        {
            _footprint = footprint;
            _highest = y1 + 1;
            Top = int.MinValue;
        }

        public readonly bool Finished => Top >= _highest;

        public void Box(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            if (y1 + 1 > Top && Covers(in _footprint, x0, z0, x1, z1))
            {
                Top = y1 + 1;
            }
        }

        public void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask)
        {
            for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
            {
                var hits = mask[w] & RowSpread(in _footprint, x, z + w);
                if (hits != 0)
                {
                    Top = Math.Max(Top, y + ((63 - BitOperations.LeadingZeroCount(hits)) >> 3) + 1);
                }
            }
        }
    }

    /// <summary>Finds the top of the highest blocking voxel of each column of a window.</summary>
    internal ref struct ColumnTopsVisitor(Span<int> tops, int windowX, int windowZ, int width, int depth)
        : IBlockingVisitor
    {
        private readonly Span<int> _tops = tops;
        private readonly int _windowX = windowX;
        private readonly int _windowZ = windowZ;
        private readonly int _width = width;
        private readonly int _depth = depth;

        public readonly bool Finished => false;

        public void Box(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            for (var z = Math.Max(z0, _windowZ); z <= Math.Min(z1, _windowZ + _depth - 1); z++)
            {
                var row = _tops.Slice((z - _windowZ) * _width, _width);
                for (var x = Math.Max(x0, _windowX); x <= Math.Min(x1, _windowX + _width - 1); x++)
                {
                    ref var top = ref row[x - _windowX];
                    top = Math.Max(top, y1 + 1);
                }
            }
        }

        public void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask)
        {
            for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
            {
                var word = mask[w];
                var cz = z + w;
                if (word == 0 || cz < _windowZ || cz >= _windowZ + _depth)
                {
                    continue;
                }

                var row = _tops.Slice((cz - _windowZ) * _width, _width);
                for (var bx = 0; bx < VoxelConstants.BrickSize; bx++)
                {
                    var cx = x + bx;
                    // The column's bits: bx + 8y for y 0 to 7.
                    var bits = (word >> bx) & 0x0101010101010101UL;
                    if (bits == 0 || cx < _windowX || cx >= _windowX + _width)
                    {
                        continue;
                    }

                    ref var top = ref row[cx - _windowX];
                    top = Math.Max(top, y + ((63 - BitOperations.LeadingZeroCount(bits)) >> 3) + 1);
                }
            }
        }
    }

    /// <summary>Finds the lowest bottom of a blocking voxel under a footprint.</summary>
    internal ref struct LowestVisitor : IBlockingVisitor
    {
        public int Bottom;
        private readonly FootprintRows _footprint;
        private readonly int _lowest;

        /// <param name="footprint">The footprint.</param>
        /// <param name="y0">The lowest voxel walked: once one there is found, nothing is lower.</param>
        public LowestVisitor(FootprintRows footprint, int y0)
        {
            _footprint = footprint;
            _lowest = y0;
            Bottom = int.MaxValue;
        }

        public readonly bool Finished => Bottom <= _lowest;

        public void Box(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            if (y0 < Bottom && Covers(in _footprint, x0, z0, x1, z1))
            {
                Bottom = y0;
            }
        }

        public void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask)
        {
            for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
            {
                var hits = mask[w] & RowSpread(in _footprint, x, z + w);
                if (hits != 0)
                {
                    Bottom = Math.Min(Bottom, y + (BitOperations.TrailingZeroCount(hits) >> 3));
                }
            }
        }
    }

    /// <summary>True when a cell from (x0, z0) to (x1, z1) lies under the footprint.</summary>
    private static bool Covers(in FootprintRows footprint, int x0, int z0, int x1, int z1)
    {
        for (var z = Math.Max(z0, footprint.MinZ); z <= Math.Min(z1, footprint.MaxZ); z++)
        {
            if (footprint.Row(z, out var fx0, out var fx1) && Math.Max(fx0, x0) <= Math.Min(fx1, x1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The footprint's cells of row <paramref name="z"/> within the brick whose lowest x is <paramref name="brickX"/>, as
    /// a brick layer's mask: bit x + 8y for every y.
    /// </summary>
    private static ulong RowSpread(in FootprintRows footprint, int brickX, int z)
    {
        if (!footprint.Row(z, out var fx0, out var fx1))
        {
            return 0;
        }

        var from = Math.Max(fx0 - brickX, 0);
        var to = Math.Min(fx1 - brickX, 7);
        if (from > to)
        {
            return 0;
        }

        var xBits = ((1UL << (to - from + 1)) - 1) << from;
        return xBits * 0x0101010101010101UL;
    }
}
