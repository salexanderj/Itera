using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// Reads one object's runs of blocked cells in one column of the mover's cells (the world's level-0 voxels): the cells
/// whose centres, turned into the object, land on its solid voxels that are not passable, each run with the height of the
/// object's surface over the column's centre and that voxel's material. <see cref="ObjectColumns"/> reads the placed
/// objects through it, and navigation the objects as generated (<c>Navigation.NavObjects</c>), so the two agree. The cells
/// come from the kind's columns' runs (<see cref="VoxelObjectKind.ColumnRuns"/>, made once a run and shared by every
/// reader): an object as its kind is takes them whole, and a damaged one reads, through its damage, only the cells over
/// its kind's solid voxels. Any thread, one at a time.
/// </summary>
internal sealed class ObjectColumnReader
{
    private readonly MaterialSet _passable;
    private readonly int _cell;

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
    /// damage with <paramref name="damaged"/>: then over its kind's solid voxels, passable ones too, since edits only
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

        var runs = kind.ColumnRuns(in _passable);
        var index = vx + kind.Size.X * vz;
        // Whole runs of cells over the kind's runs, the cells between two runs that meet joined.
        int lo = int.MinValue,
            hi = int.MinValue;
        foreach (var run in damaged ? runs.Solid(index) : runs.Blocking(index))
        {
            if (!CellsOf(kind, anchor, run, out var from, out var to))
            {
                continue;
            }

            if (lo != int.MinValue && from == hi + 1)
            {
                hi = to;
                continue;
            }

            if (lo != int.MinValue)
            {
                Add(voxels, kind, anchor, vx, vz, damaged, lo, hi, column);
            }

            lo = from;
            hi = to;
        }

        if (lo != int.MinValue)
        {
            Add(voxels, kind, anchor, vx, vz, damaged, lo, hi, column);
        }
    }

    /// <summary>
    /// The cells from <paramref name="from"/> to <paramref name="to"/> whose centres land in the run of the object's voxel
    /// column; false when none does.
    /// </summary>
    private bool CellsOf(VoxelObjectKind kind, VoxelCoord anchor, VoxelRun run, out int from, out int to)
    {
        // Cell y's centre, y * cell + cell / 2, lies in voxel floor((centre - anchor.Y + the kind's anchor) / unit).
        var unit = kind.UnitCentimetres;
        var shift = (long)anchor.Y - kind.AnchorCentimetres.Y - _cell / 2;
        from = (int)-DeterministicMath.FloorDivide(-((long)run.Low * unit + shift), _cell);
        to = (int)DeterministicMath.FloorDivide((long)(run.High + 1) * unit - 1 + shift, _cell);
        return from <= to;
    }

    /// <summary>
    /// Adds the cells from <paramref name="lo"/> to <paramref name="hi"/>: as one span for an object as its kind is, else
    /// each run of them whose centres land on a solid voxel of its damage that is not passable.
    /// </summary>
    private void Add(
        IObjectVoxelSource voxels,
        VoxelObjectKind kind,
        VoxelCoord anchor,
        int vx,
        int vz,
        bool damaged,
        int lo,
        int hi,
        List<ObjectSpan> column
    )
    {
        if (!damaged)
        {
            column.Add(Span(voxels, kind, anchor, vx, vz, lo, hi));
            return;
        }

        var half = _cell / 2.0;
        var runStart = int.MinValue;
        for (var y = lo; y <= hi + 1; y++)
        {
            var solid = y <= hi && Solid(voxels, vx, CellVoxelY(kind, anchor, y, half), vz);
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
    /// The span of cells lo to hi of one object's column (vx, vz): its surface over the highest solid voxel of the column
    /// under the top cell's top, in cells, and that voxel's material. The surface lies where the density
    /// (<see cref="VoxelDensity"/>) crosses one half between the centres of that voxel and the voxel above, as the mesher
    /// draws it and the world's ground is read, so a smooth shape (a ramp) reads as its slope, not as steps a voxel high.
    /// A voxel above that blocks too gives the voxel's top, and a passable one counts as empty. It is kept from the top
    /// cell's bottom to half a cell over its top, as near as the ground's surface lies to its top.
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
            voxels.Voxel(vx, vy, vz, out var material, out var low);
            if (material != VoxelConstants.Air && !_passable.Contains(material))
            {
                // In the object's voxels from its box's floor: the voxel's top, or the density's crossing.
                double over = vy + 1;
                voxels.Voxel(vx, vy + 1, vz, out var above, out var high);
                if (above == VoxelConstants.Air || _passable.Contains(above))
                {
                    high = above == VoxelConstants.Air ? high : VoxelDensity.Empty;
                    over = vy + 0.5 + (low - 127.5) / (low - (double)high);
                }

                var surface = (over * unit - kind.AnchorCentimetres.Y + anchor.Y) / _cell;
                return new ObjectSpan(lo, hi, (float)Math.Clamp(surface, hi, hi + 1.5), material);
            }
        }

        // The top cell's centre holds a solid voxel, so one lies in it; past rounding, its top.
        voxels.Voxel(vx, CellVoxelY(kind, anchor, hi, _cell / 2.0), vz, out var centre, out _);
        return new ObjectSpan(lo, hi, hi + 1, centre);
    }
}

/// <summary>One run of blocked cells of a column: lowest and highest cell, the surface's height in cells, its material.</summary>
internal readonly record struct ObjectSpan(int Lo, int Hi, float Surface, byte Material);
