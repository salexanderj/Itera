using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// Reads one object's runs of blocked cells in one column of the mover's cells (the world's level-0 voxels): the cells
/// whose centres, turned into the object, land on its solid voxels that are not passable, each run with the height of the
/// object's surface over the column's centre and that voxel's material. <see cref="ObjectColumns"/> reads the placed
/// objects through it, and navigation the objects as generated (<c>Navigation.NavObjects</c>), so the two agree. It
/// keeps a table of each kind's columns' lowest and highest blocking voxels, made at the kind's first read, so only the
/// cells that can hold them are read; one thread at a time.
/// </summary>
internal sealed class ObjectColumnReader
{
    private readonly MaterialSet _passable;
    private readonly int _cell;
    private readonly Dictionary<VoxelObjectKindId, ColumnRange[]> _ranges = [];
    private readonly Dictionary<VoxelObjectKindId, ColumnRange[]> _solidRanges = [];

    /// <param name="passable">The materials characters pass through.</param>
    /// <param name="cellCentimetres">The edge of the mover's cells: the world's level-0 voxels.</param>
    public ObjectColumnReader(MaterialSet passable, int cellCentimetres)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCentimetres, 1);
        _passable = passable;
        _cell = cellCentimetres;
    }

    /// <summary>The edge of the cells, in centimetres.</summary>
    public int CellCentimetres => _cell;

    /// <summary>
    /// Adds to <paramref name="column"/>, lowest first, the runs of cells the object blocks in column
    /// (<paramref name="x"/>, <paramref name="z"/>) of the cells, read through <paramref name="voxels"/> (its kind, or its
    /// damage with <paramref name="damaged"/>: then within its kind's solid voxels, passable ones too, since edits only
    /// take voxels away but a paint may make a passable one blocking).
    /// </summary>
    public void AddColumn(
        in ObjectPlacement placement,
        VoxelObjectKind kind,
        IObjectVoxelSource voxels,
        bool damaged,
        int x,
        int z,
        List<ObjectSpan> column
    )
    {
        var anchor = placement.Anchor;
        var unit = kind.UnitCentimetres;
        var half = _cell / 2.0;
        // The column's centre, turned into the object's voxels.
        var (ox, oz) = ObjectTurns.ToObject(
            placement.Heading,
            (long)Math.Floor(x * (double)_cell + half) - anchor.X,
            (long)Math.Floor(z * (double)_cell + half) - anchor.Z
        );
        var vx = (int)DeterministicMath.FloorDivide(ox + kind.AnchorCentimetres.X, unit);
        var vz = (int)DeterministicMath.FloorDivide(oz + kind.AnchorCentimetres.Z, unit);
        if ((uint)vx >= (uint)kind.Size.X || (uint)vz >= (uint)kind.Size.Z)
        {
            return;
        }

        // Only the cells that can hold the column's solid voxels.
        var range = RangesOf(placement.Kind, kind, passableToo: damaged)[vx + kind.Size.X * vz];
        if (range.Low > range.High)
        {
            return;
        }

        var y0 = (int)DeterministicMath.FloorDivide((long)anchor.Y + kind.BottomCentimetres, _cell);
        var y1 = (int)DeterministicMath.FloorDivide((long)anchor.Y + kind.TopCentimetres - 1, _cell);
        var from = (int)
            Math.Max(
                y0,
                DeterministicMath.FloorDivide((long)range.Low * unit - kind.AnchorCentimetres.Y + anchor.Y, _cell)
            );
        var to = (int)
            Math.Min(
                y1,
                DeterministicMath.FloorDivide(
                    (long)(range.High + 1) * unit - 1 - kind.AnchorCentimetres.Y + anchor.Y,
                    _cell
                )
            );
        var runStart = int.MinValue;
        for (var y = from; y <= to + 1; y++)
        {
            var solid = y <= to && Solid(voxels, vx, CellVoxelY(kind, anchor, y, half), vz);
            if (solid && runStart == int.MinValue)
            {
                runStart = y;
            }
            else if (!solid && runStart != int.MinValue)
            {
                column.Add(Span(voxels, kind, anchor, vx, vz, runStart, y - 1));
                runStart = int.MinValue;
            }
        }
    }

    // The voxel of the object's column that holds cell y's centre.
    private int CellVoxelY(VoxelObjectKind kind, VoxelCoord anchor, int y, double half) =>
        (int)
            DeterministicMath.FloorDivide(
                (long)Math.Floor(y * (double)_cell + half) - anchor.Y + kind.AnchorCentimetres.Y,
                kind.UnitCentimetres
            );

    private bool Solid(IObjectVoxelSource voxels, int vx, int vy, int vz)
    {
        voxels.Voxel(vx, vy, vz, out var material, out _);
        return material != VoxelConstants.Air && !_passable.Contains(material);
    }

    /// <summary>
    /// The span of cells lo to hi of one object's column (vx, vz): its surface the top of the highest solid voxel of the
    /// column under the top cell's top, in cells, and that voxel's material.
    /// </summary>
    private ObjectSpan Span(
        IObjectVoxelSource voxels,
        VoxelObjectKind kind,
        VoxelCoord anchor,
        int vx,
        int vz,
        int lo,
        int hi
    )
    {
        var unit = kind.UnitCentimetres;
        // The object's voxels from the top cell's top down to its bottom.
        var top = (int)
            DeterministicMath.FloorDivide((long)(hi + 1) * _cell - 1 - anchor.Y + kind.AnchorCentimetres.Y, unit);
        var bottom = (int)DeterministicMath.FloorDivide((long)hi * _cell - anchor.Y + kind.AnchorCentimetres.Y, unit);
        for (var vy = top; vy >= bottom; vy--)
        {
            voxels.Voxel(vx, vy, vz, out var material, out _);
            if (material != VoxelConstants.Air && !_passable.Contains(material))
            {
                var surface = ((long)(vy + 1) * unit - kind.AnchorCentimetres.Y + anchor.Y) / (double)_cell;
                return new ObjectSpan(lo, hi, (float)Math.Clamp(surface, hi, hi + 1), material);
            }
        }

        // The top cell's centre holds a solid voxel, so one lies in it; past rounding, its top.
        voxels.Voxel(vx, CellVoxelY(kind, anchor, hi, _cell / 2.0), vz, out var centre, out _);
        return new ObjectSpan(lo, hi, hi + 1, centre);
    }

    /// <summary>
    /// The lowest and highest solid voxel that is not passable (with <paramref name="passableToo"/>, any solid voxel) of
    /// each of the kind's voxel columns (index <c>x + Size.X * z</c>; Low over High for none), made from its parts' bricks
    /// at its first read: so a column reads only the cells an object's column can block.
    /// </summary>
    private ColumnRange[] RangesOf(VoxelObjectKindId id, VoxelObjectKind kind, bool passableToo)
    {
        var table = passableToo ? _solidRanges : _ranges;
        if (table.TryGetValue(id, out var found))
        {
            return found;
        }

        var size = kind.Size;
        var ranges = new ColumnRange[size.X * size.Z];
        Array.Fill(ranges, new ColumnRange(short.MaxValue, short.MinValue));
        const int border = ObjectPart.BorderVoxels;
        const int brick = VoxelConstants.BrickSize;
        foreach (var part in kind.Parts)
        {
            var level = part.Levels[0];
            for (var b = 0; b < level.Bricks.Length; b++)
            {
                var packed = level.Bricks[b];
                // The brick's corner in the kind's voxels; only the part's own voxels count (its border is another's).
                int bx = ((packed & 15) << 3) - border + part.Offset.X,
                    by = (((packed >> 4) & 15) << 3) - border + part.Offset.Y,
                    bz = (((packed >> 8) & 15) << 3) - border + part.Offset.Z;
                var payload = level.PayloadStart[b];
                for (var j = 0; j < brick * brick * brick; j++)
                {
                    int x = bx + (j & 7),
                        y = by + ((j >> 3) & 7),
                        z = bz + (j >> 6);
                    var material = level.Uniform[b] ? level.Payload[payload] : level.Payload[payload + j];
                    if (
                        material == VoxelConstants.Air
                        || (!passableToo && _passable.Contains(material))
                        || x < part.Offset.X
                        || y < part.Offset.Y
                        || z < part.Offset.Z
                        || x >= part.Offset.X + part.OwnSize.X
                        || y >= part.Offset.Y + part.OwnSize.Y
                        || z >= part.Offset.Z + part.OwnSize.Z
                    )
                    {
                        continue;
                    }

                    ref var range = ref ranges[x + size.X * z];
                    range = new ColumnRange((short)Math.Min(range.Low, y), (short)Math.Max(range.High, y));
                }
            }
        }

        table[id] = ranges;
        return ranges;
    }

    /// <summary>A kind's voxel column's lowest and highest solid voxel that blocks.</summary>
    private readonly record struct ColumnRange(short Low, short High);
}

/// <summary>One run of blocked cells of a column: lowest and highest cell, the surface's height in cells, its material.</summary>
internal readonly record struct ObjectSpan(int Lo, int Hi, float Surface, byte Material);
