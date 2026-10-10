using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// Models that are solid to <see cref="VoxelCharacterMover"/> without being loose bodies (a head floating above the ground), read as
/// level-0 voxels by the rule loose bodies are (<see cref="LooseBodyVoxels"/>): a world voxel blocks when its centre,
/// taken into the model, lands on one of the model's voxels. A solid is its columns (<see cref="SolidColumns"/>), the
/// level of its voxels, and the point it turns about (in its own voxels), placed each step by
/// <see cref="SetPose"/>: that point in the world (absolute) and a turn about the vertical axis only. With the turn about y alone,
/// a world column lands in one column of the model and a world voxel's height in one of its heights, so each query reads
/// one column of spans per world cell instead of sampling bricks. <see cref="LooseBodyVoxels"/> holds these and adds
/// them to its queries, so the mover reads both. Main thread; allocates nothing once a solid is added.
/// </summary>
public sealed class SolidModelVoxels
{
    private readonly List<Solid?> _solids = [];
    private readonly int _worldVoxelCentimetres;

    /// <param name="worldVoxelCentimetres">The edge of the world's level-0 voxels (the mover's cells), in centimetres.</param>
    public SolidModelVoxels(int worldVoxelCentimetres = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(worldVoxelCentimetres, 1);
        _worldVoxelCentimetres = worldVoxelCentimetres;
    }

    /// <summary>Solids added and not removed.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Adds a solid of <paramref name="columns"/> whose voxels are <paramref name="level"/>'s, turning about
    /// <paramref name="pivot"/> (in its own voxels, from its box's corner), and returns its id. It blocks nothing until
    /// <see cref="SetPose"/> places it.
    /// </summary>
    public int Add(SolidColumns columns, int level, Vector3 pivot)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, VoxelModels.MaxLevels - 1);
        // How many world voxels across one of the model's voxels is: 0.4 for 4 cm voxels in a world of 10 cm.
        var solid = new Solid(columns, VoxelModels.LevelCentimetres(level) / (float)_worldVoxelCentimetres, pivot);
        for (var i = 0; i < _solids.Count; i++)
        {
            if (_solids[i] is null)
            {
                _solids[i] = solid;
                Count++;
                return i;
            }
        }

        _solids.Add(solid);
        Count++;
        return _solids.Count - 1;
    }

    /// <summary>Removes solid <paramref name="id"/>; the id may be given to a later one.</summary>
    public void Remove(int id)
    {
        if (_solids[id] is null)
        {
            throw new ArgumentException("No such solid.", nameof(id));
        }

        _solids[id] = null;
        Count--;
    }

    /// <summary>
    /// Places solid <paramref name="id"/>: its pivot at the absolute point <paramref name="pivot"/> (a local position
    /// with the match's origin, <c>WorldOrigin.ToWorld</c>) and turned by <paramref name="yaw"/> radians about the
    /// vertical axis (as <see cref="Quaternion.CreateFromAxisAngle"/> about +y turns it). Call before the step's moves.
    /// </summary>
    public void SetPose(int id, in WorldPoint pivot, float yaw)
    {
        var perMetre = VoxelConstants.CentimetresPerMetre / (double)_worldVoxelCentimetres;
        SetPoseInCells(id, pivot.X * perMetre, pivot.Y * perMetre, pivot.Z * perMetre, yaw);
    }

    /// <summary>
    /// Places solid <paramref name="id"/> with its pivot at (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>) in absolute world level-0 voxels, in doubles (a float holds only whole voxels 150 km out at
    /// 1 cm).
    /// </summary>
    internal void SetPoseInCells(int id, double x, double y, double z, float yaw)
    {
        var solid = _solids[id] ?? throw new ArgumentException("No such solid.", nameof(id));
        solid.Place(x, y, z, yaw);
    }

    /// <summary>True when some placed solid's bounds reach the box from (x0, y0, z0) to (x1, y1, z1).</summary>
    internal bool Reaches(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        foreach (var solid in _solids)
        {
            if (solid is { Placed: true } && solid.Overlaps(x0, y0, z0, x1, y1, z1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The top (one above the highest voxel) of the highest solid voxel from y0 to y1 under <paramref name="footprint"/>,
    /// or <see cref="int.MinValue"/> when there is none.
    /// </summary>
    internal int HighestTop(in CircleFootprint footprint, int y0, int y1)
    {
        var top = int.MinValue;
        foreach (var solid in _solids)
        {
            if (
                solid is not { Placed: true }
                || !solid.Overlaps(footprint.MinX, y0, footprint.MinZ, footprint.MaxX, y1, footprint.MaxZ)
            )
            {
                continue;
            }

            int from = solid.LocalY(Math.Max(y0, solid.Min.Y)),
                to = solid.LocalY(Math.Min(y1, solid.Max.Y));
            int zFrom = Math.Max(footprint.MinZ, solid.Min.Z),
                zTo = Math.Min(footprint.MaxZ, solid.Max.Z);
            for (var z = zFrom; z <= zTo; z++)
            {
                if (!footprint.Row(z, out var x0, out var x1))
                {
                    continue;
                }

                for (int x = Math.Max(x0, solid.Min.X), last = Math.Min(x1, solid.Max.X); x <= last; x++)
                {
                    if (solid.Column(x, z, out var lx, out var lz))
                    {
                        var highest = solid.Columns.Highest(lx, lz, from, to);
                        if (highest >= 0)
                        {
                            top = Math.Max(top, Math.Min(y1, solid.HighestWorldY(highest)) + 1);
                        }
                    }
                }
            }
        }

        return top;
    }

    /// <summary>
    /// The bottom of the lowest solid voxel from y0 to y1 under <paramref name="footprint"/>, or <see cref="int.MaxValue"/>
    /// when there is none.
    /// </summary>
    internal int LowestBottom(in CircleFootprint footprint, int y0, int y1)
    {
        var bottom = int.MaxValue;
        foreach (var solid in _solids)
        {
            if (
                solid is not { Placed: true }
                || !solid.Overlaps(footprint.MinX, y0, footprint.MinZ, footprint.MaxX, y1, footprint.MaxZ)
            )
            {
                continue;
            }

            int from = solid.LocalY(Math.Max(y0, solid.Min.Y)),
                to = solid.LocalY(Math.Min(y1, solid.Max.Y));
            int zFrom = Math.Max(footprint.MinZ, solid.Min.Z),
                zTo = Math.Min(footprint.MaxZ, solid.Max.Z);
            for (var z = zFrom; z <= zTo; z++)
            {
                if (!footprint.Row(z, out var x0, out var x1))
                {
                    continue;
                }

                for (int x = Math.Max(x0, solid.Min.X), last = Math.Min(x1, solid.Max.X); x <= last; x++)
                {
                    if (solid.Column(x, z, out var lx, out var lz))
                    {
                        var lowest = solid.Columns.Lowest(lx, lz, from, to);
                        if (lowest >= 0)
                        {
                            bottom = Math.Min(bottom, Math.Max(y0, solid.LowestWorldY(lowest)));
                        }
                    }
                }
            }
        }

        return bottom;
    }

    /// <summary>
    /// Marks the cells of a window (<paramref name="width"/> cells from <paramref name="windowX"/>, a row of
    /// <paramref name="rows"/> for each z from <paramref name="windowZ"/>) that hold a solid voxel anywhere from y0 to
    /// y1, adding to what <paramref name="rows"/> holds.
    /// </summary>
    internal void BlockedCells(int windowX, int windowZ, int width, int y0, int y1, Span<UInt128> rows)
    {
        var windowX1 = windowX + width - 1;
        var windowZ1 = windowZ + rows.Length - 1;
        foreach (var solid in _solids)
        {
            if (solid is not { Placed: true } || !solid.Overlaps(windowX, y0, windowZ, windowX1, y1, windowZ1))
            {
                continue;
            }

            int from = solid.LocalY(Math.Max(y0, solid.Min.Y)),
                to = solid.LocalY(Math.Min(y1, solid.Max.Y));
            for (int z = Math.Max(windowZ, solid.Min.Z), zTo = Math.Min(windowZ1, solid.Max.Z); z <= zTo; z++)
            {
                for (int x = Math.Max(windowX, solid.Min.X), xTo = Math.Min(windowX1, solid.Max.X); x <= xTo; x++)
                {
                    if (solid.Column(x, z, out var lx, out var lz) && solid.Columns.Highest(lx, lz, from, to) >= 0)
                    {
                        rows[z - windowZ] |= UInt128.One << (x - windowX);
                    }
                }
            }
        }
    }

    private sealed class Solid(SolidColumns columns, float scale, Vector3 pivot)
    {
        public SolidColumns Columns { get; } = columns;

        public bool Placed { get; private set; }

        /// <summary>The world voxels the solid may cover, inclusive.</summary>
        public VoxelCoord Min { get; private set; }

        /// <inheritdoc cref="Min"/>
        public VoxelCoord Max { get; private set; }

        // The pivot in absolute world voxels; a cell's offset from it is exact in doubles, then small enough for floats.
        private double _pivotX;
        private double _pivotY;
        private double _pivotZ;
        private float _cos = 1;
        private float _sin;

        public void Place(double x, double y, double z, float yaw)
        {
            _pivotX = x;
            _pivotY = y;
            _pivotZ = z;
            _cos = MathF.Cos(yaw);
            _sin = MathF.Sin(yaw);
            // The box's corners turned into the world, and a voxel more for rounding.
            var size = new Vector3(Columns.Size.X, Columns.Size.Y, Columns.Size.Z);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (var corner = 0; corner < 8; corner++)
            {
                var local =
                    new Vector3(
                        (corner & 1) != 0 ? size.X : 0,
                        (corner & 2) != 0 ? size.Y : 0,
                        (corner & 4) != 0 ? size.Z : 0
                    ) - pivot;
                var turned = new Vector3(local.X * _cos + local.Z * _sin, local.Y, -local.X * _sin + local.Z * _cos);
                min = Vector3.Min(min, turned * scale);
                max = Vector3.Max(max, turned * scale);
            }

            Min = new VoxelCoord(
                (int)Math.Floor(x + min.X) - 1,
                (int)Math.Floor(y + min.Y) - 1,
                (int)Math.Floor(z + min.Z) - 1
            );
            Max = new VoxelCoord(
                (int)Math.Floor(x + max.X) + 1,
                (int)Math.Floor(y + max.Y) + 1,
                (int)Math.Floor(z + max.Z) + 1
            );
            Placed = true;
        }

        public bool Overlaps(int x0, int y0, int z0, int x1, int y1, int z1) =>
            Min.X <= x1 && Max.X >= x0 && Min.Y <= y1 && Max.Y >= y0 && Min.Z <= z1 && Max.Z >= z0;

        /// <summary>The model's column world cell (x, z)'s centre lands in; false outside its box.</summary>
        public bool Column(int x, int z, out int lx, out int lz)
        {
            float dx = (float)(x + 0.5 - _pivotX),
                dz = (float)(z + 0.5 - _pivotZ);
            // The turn undone: the inverse of (x cos + z sin, -x sin + z cos).
            var localX = (dx * _cos - dz * _sin) / scale + pivot.X;
            var localZ = (dx * _sin + dz * _cos) / scale + pivot.Z;
            lx = (int)MathF.Floor(localX);
            lz = (int)MathF.Floor(localZ);
            return (uint)lx < (uint)Columns.Size.X && (uint)lz < (uint)Columns.Size.Z;
        }

        /// <summary>The model's height world voxel height <paramref name="y"/>'s centre lands in (may lie outside its box).</summary>
        public int LocalY(int y) => (int)MathF.Floor((float)(y + 0.5 - _pivotY) / scale + pivot.Y);

        /// <summary>The lowest world voxel height whose centre lands in the model's height <paramref name="local"/>.</summary>
        public int LowestWorldY(int local)
        {
            var y = (int)Math.Ceiling((local - pivot.Y) * scale + _pivotY - 0.5);
            while (LocalY(y) >= local)
            {
                y--;
            }

            while (LocalY(y) < local)
            {
                y++;
            }

            return y;
        }

        /// <summary>The highest world voxel height whose centre lands in the model's height <paramref name="local"/>.</summary>
        public int HighestWorldY(int local) => LowestWorldY(local + 1) - 1;
    }
}
