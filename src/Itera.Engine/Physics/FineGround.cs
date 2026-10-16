using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// The voxel terrain read in cells finer than its level-0 voxels (<c>Engine:Physics:TerrainCellCentimetres</c>), for a
/// character mover that meets objects, solid models, and mesh colliders at their own size over coarse ground: each
/// level-0 voxel is <see cref="Factor"/> cells across. A run of blocking voxels in a column is solid in the fine cells
/// from its bottom (whole) up to its surface: the density's over its highest voxel (<see cref="VoxelWorld.SurfaceOver"/>,
/// from half a voxel under its top to half over it), blended between the voxel columns (a neighbour's run whose surface
/// lies within the ramp of it joins, the nearest of them) with no seam between columns that join (<see cref="Blended"/>:
/// where every neighbour joins, the bilinear blend between the columns' centres the mover reads in coarse cells), and a
/// fine cell is solid where its centre lies under that surface. So a slope of coarse voxels reads as the ramp the mover
/// walks in coarse cells, a ledge or a wall stays sharp at the voxel's side, and every fine column's surface lies within
/// half a cell of its top, as on a grid that fine (<see cref="SurfaceOver"/> gives it exactly).
/// Answers the mover's ground queries in fine cells; each loads the voxel columns round the cells it asks about (a
/// column more on each side, for the blend) and the layers within the ramp and a voxel of them. Main thread; allocates
/// nothing.
/// </summary>
internal sealed class FineGround
{
    /// <summary>The most runs of blocking voxels kept for one column within a query's layers; more are joined.</summary>
    private const int MaxRuns = 8;

    /// <summary>The widest window of cells a query reads across x, and the deepest along z (the mover's).</summary>
    private const int MostWidth = 128;

    private const int MostDepth = 512;

    private const int SurfaceCacheBits = 12;

    private readonly VoxelWorld _world;
    private readonly int _factor;

    // The columns loaded for the query: a box of voxel columns, and the layers from _y0 to _y1.
    private int _x0;
    private int _z0;
    private int _width;
    private int _depth;
    private int _y0;
    private int _y1;
    private int _loadStamp;
    private int _stamp = 1;
    private bool _loaded;

    // Each column's runs: [_starts, _ends) in voxels, and their surfaces (NaN until read for this load).
    private readonly int[] _counts;
    private readonly int[] _starts;
    private readonly int[] _ends;
    private readonly double[] _surfaces;
    private readonly int[] _surfaceStamps;
    private readonly double[] _controls;
    private readonly int[] _controlStamps;
    private readonly UInt128[] _bits;

    // Surfaces read within one move or query, by voxel column and top (SurfaceOver reads two voxels' densities).
    private readonly long[] _cachedKeys = new long[1 << SurfaceCacheBits];
    private readonly double[] _cachedSurfaces = new double[1 << SurfaceCacheBits];
    private readonly int[] _cachedStamps = new int[1 << SurfaceCacheBits];

    private MaterialSet _passable;

    /// <param name="world">The voxels.</param>
    /// <param name="cellCentimetres">The cells' edge, a whole divisor of the world's level-0 voxels, finer than them.</param>
    public FineGround(VoxelWorld world, int cellCentimetres)
    {
        var voxel = world.Grid.BaseCentimetres;
        if (cellCentimetres < 1 || cellCentimetres >= voxel || voxel % cellCentimetres != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellCentimetres),
                cellCentimetres,
                $"A fine cell is a whole divisor of the world's {voxel} cm voxels, finer than them."
            );
        }

        _world = world;
        _factor = voxel / cellCentimetres;
        var columns = (MostWidth / _factor + 4) * (MostDepth / _factor + 4);
        _counts = new int[columns];
        _starts = new int[columns * MaxRuns];
        _ends = new int[columns * MaxRuns];
        _surfaces = new double[columns * MaxRuns];
        _surfaceStamps = new int[columns * MaxRuns];
        _controls = new double[columns * MaxRuns * 8];
        _controlStamps = new int[columns * MaxRuns];
        _bits = new UInt128[columns];
    }

    /// <summary>The cells across a level-0 voxel.</summary>
    public int Factor => _factor;

    /// <summary>The slope limit's rise over one cell of the move or query, which the blend joins neighbours within.</summary>
    public double Ramp { get; private set; } = 1;

    /// <summary>
    /// Starts a move or query: its ramp, and the voxels read afresh (they may have changed since). The mover calls it as
    /// it forgets its own surfaces.
    /// </summary>
    public void Begin(double ramp, in MaterialSet passable)
    {
        Ramp = ramp;
        _passable = passable;
        _stamp++;
        if (_stamp == int.MaxValue)
        {
            Array.Clear(_cachedStamps);
            Array.Clear(_surfaceStamps);
            Array.Clear(_controlStamps);
            _stamp = 1;
        }

        _loaded = false;
    }

    /// <summary>True when the voxel columns under cells x0 to x1 and z0 to z1, and a column round them, are resident.</summary>
    public bool ColumnsResident(int x0, int z0, int x1, int z1) =>
        _world.ColumnsResident(Voxel(x0) - 1, Voxel(z0) - 1, Voxel(x1) + 1, Voxel(z1) + 1);

    /// <summary>
    /// As <see cref="VoxelWorld.BlockedCells"/> in fine cells: the cells of a window (at most 128 wide) that hold ground
    /// anywhere from y0 to y1.
    /// </summary>
    public void BlockedCells(int windowX, int windowZ, int width, int y0, int y1, Span<UInt128> rows)
    {
        if ((uint)(width - 1) >= MostWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A window is 1 to 128 cells wide.");
        }

        rows.Clear();
        var depth = rows.Length;
        Load(windowX, windowZ, windowX + width - 1, windowZ + depth - 1, y0, y1);
        for (var row = 0; row < depth; row++)
        {
            var z = windowZ + row;
            var vz = Voxel(z);
            for (var vx = Voxel(windowX); vx <= Voxel(windowX + width - 1); vx++)
            {
                var column = Column(vx, vz);
                // The window's cells in this voxel column.
                var from = Math.Max(vx * _factor, windowX);
                var to = Math.Min(vx * _factor + _factor - 1, windowX + width - 1);
                for (var run = 0; run < _counts[column]; run++)
                {
                    var bottom = _starts[column * MaxRuns + run] * _factor;
                    if (bottom > y1)
                    {
                        continue;
                    }

                    var (low, high) = TopBounds(column, run);
                    if (low > y0)
                    {
                        rows[row] |= Bits(from - windowX, to - windowX);
                        continue;
                    }

                    if (high <= y0)
                    {
                        continue;
                    }

                    for (var x = from; x <= to; x++)
                    {
                        if (Top(column, run, x, z) > y0)
                        {
                            rows[row] |= UInt128.One << (x - windowX);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// As <see cref="VoxelWorld.ColumnTops"/> in fine cells: each cell column's top (one above its highest cell of
    /// ground) from y0 to y1, or <see cref="int.MinValue"/>.
    /// </summary>
    public void ColumnTops(int windowX, int windowZ, int width, int depth, int y0, int y1, Span<int> tops)
    {
        tops[..(width * depth)].Fill(int.MinValue);
        Load(windowX, windowZ, windowX + width - 1, windowZ + depth - 1, y0, y1);
        for (var row = 0; row < depth; row++)
        {
            var z = windowZ + row;
            for (var i = 0; i < width; i++)
            {
                tops[i + width * row] = TopOf(windowX + i, z, y0, y1);
            }
        }
    }

    /// <summary>The top (one above the highest cell) of the ground from y0 to y1 under a footprint, or <see cref="int.MinValue"/>.</summary>
    public int HighestTop(in CircleFootprint footprint, int y0, int y1)
    {
        Load(footprint.MinX, footprint.MinZ, footprint.MaxX, footprint.MaxZ, y0, y1);
        var best = int.MinValue;
        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                best = Math.Max(best, TopOf(x, z, y0, y1));
            }
        }

        return best;
    }

    /// <summary>The bottom (its y) of the lowest cell of ground from y0 to y1 under a footprint, or <see cref="int.MaxValue"/>.</summary>
    public int LowestBottom(in CircleFootprint footprint, int y0, int y1)
    {
        Load(footprint.MinX, footprint.MinZ, footprint.MaxX, footprint.MaxZ, y0, y1);
        var best = int.MaxValue;
        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                var column = Column(Voxel(x), Voxel(z));
                for (var run = 0; run < _counts[column]; run++)
                {
                    var bottom = Math.Max(_starts[column * MaxRuns + run] * _factor, y0);
                    if (bottom <= y1 && bottom < best && Top(column, run, x, z) > bottom)
                    {
                        best = bottom;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The surface, in cells, over cell column (x, z)'s ground whose top is <paramref name="top"/>: the blended surface
    /// of the run whose top it is; the top itself where the cell at the top is ground too (the run goes on above what
    /// was read) or no run has that top.
    /// </summary>
    public double SurfaceOver(int x, int top, int z)
    {
        Load(x, z, x, z, top - 1, top);
        var column = Column(Voxel(x), Voxel(z));
        double surface = top;
        for (var run = 0; run < _counts[column]; run++)
        {
            var bottom = _starts[column * MaxRuns + run] * _factor;
            var runTop = Top(column, run, x, z);
            if (bottom <= top && runTop > top)
            {
                return top;
            }

            if (runTop == top)
            {
                surface = Blended(column, run, x, z) * _factor;
            }
        }

        return surface;
    }

    /// <summary>
    /// The highest cell of ground of cell column (x, z) from y0 to y1 (<paramref name="cell"/>, its y), and its voxel's
    /// material: the voxel holding the cell's, or where that is air (a surface over its run's highest voxel), the run's
    /// highest voxel's. False where none.
    /// </summary>
    public bool TopAt(int x, int z, int y0, int y1, out int cell, out byte material)
    {
        Load(x, z, x, z, y0, y1);
        var top = TopOf(x, z, y0, y1);
        material = VoxelConstants.Air;
        cell = int.MinValue;
        if (top == int.MinValue)
        {
            return false;
        }

        cell = top - 1;
        var voxel = new VoxelCoord(Voxel(x), Voxel(cell), Voxel(z));
        if (
            _world.TryGetVoxelMaterial(voxel, out material)
            && (material == VoxelConstants.Air || _passable.Contains(material))
        )
        {
            _world.TryGetVoxelMaterial(new VoxelCoord(voxel.X, voxel.Y - 1, voxel.Z), out material);
        }

        return material != VoxelConstants.Air;
    }

    /// <summary>The top (one above the highest cell) of cell column (x, z)'s ground from y0 to y1, or <see cref="int.MinValue"/>; loaded.</summary>
    private int TopOf(int x, int z, int y0, int y1)
    {
        var column = Column(Voxel(x), Voxel(z));
        var best = int.MinValue;
        for (var run = 0; run < _counts[column]; run++)
        {
            var bottom = _starts[column * MaxRuns + run] * _factor;
            if (bottom > y1)
            {
                continue;
            }

            var top = Math.Min(Top(column, run, x, z), y1 + 1);
            if (top > Math.Max(bottom, y0))
            {
                best = Math.Max(best, top);
            }
        }

        return best;
    }

    /// <summary>
    /// The top of run <paramref name="run"/> of a loaded column in cell column (x, z): one above the highest cell whose
    /// centre lies under its blended surface, at least a cell over its bottom.
    /// </summary>
    private int Top(int column, int run, int x, int z)
    {
        var surface = Blended(column, run, x, z) * _factor;
        var top = (int)Math.Floor(surface + 0.5);
        return Math.Max(top, _starts[column * MaxRuns + run] * _factor + 1);
    }

    /// <summary>
    /// The least and the greatest top run <paramref name="run"/> of a loaded column has in any of its cell columns: from
    /// the least and the greatest of its surface's points (<see cref="Blended"/>), between which the blend lies.
    /// </summary>
    private (int Low, int High) TopBounds(int column, int run)
    {
        var index = Controls(column, run);
        var own = Surface(column, run);
        double low = own,
            high = own;
        for (var i = 0; i < 8; i++)
        {
            low = Math.Min(low, _controls[index + i]);
            high = Math.Max(high, _controls[index + i]);
        }

        var bottom = _starts[column * MaxRuns + run] * _factor + 1;
        return (
            Math.Max((int)Math.Floor(low * _factor + 0.5), bottom),
            Math.Max((int)Math.Floor(high * _factor + 0.5), bottom)
        );
    }

    /// <summary>
    /// Run <paramref name="run"/>'s surface at the centre of cell column (x, z), in voxels: bilinear over the quarter of
    /// its voxel column the cell lies in, between the column's surface at its centre, the middles of the two sides
    /// nearest (the mean of the two columns' surfaces where the neighbour's joins, else its own), and the corner between
    /// them (the mean of the joined group of the four columns round it that holds it). Where every neighbour joins, that
    /// is the bilinear blend between the columns' centres the mover reads in coarse cells; where one does not (a ledge,
    /// a wall), the edge stays sharp; and two columns that join read the same surface along the side they share, so
    /// the ground has no seam there.
    /// </summary>
    private double Blended(int column, int run, int x, int z)
    {
        var index = Controls(column, run);
        var own = Surface(column, run);
        var vx = _x0 + column % _width;
        var vz = _z0 + column / _width;
        double u = (x + 0.5) / _factor - vx,
            v = (z + 0.5) / _factor - vz;
        var east = u >= 0.5;
        var south = v >= 0.5;
        double tx = Math.Abs(u - 0.5) * 2,
            tz = Math.Abs(v - 0.5) * 2;
        var alongX = _controls[index + (east ? 1 : 0)];
        var alongZ = _controls[index + (south ? 3 : 2)];
        var corner = _controls[index + 4 + (east ? 1 : 0) + (south ? 2 : 0)];
        return own * (1 - tx) * (1 - tz) + alongX * tx * (1 - tz) + alongZ * (1 - tx) * tz + corner * tx * tz;
    }

    /// <summary>
    /// The index into <see cref="_controls"/> of a loaded run's surface points, worked out once a load: the middles of
    /// its sides (-x, +x, -z, +z), then its corners (-x -z, +x -z, -x +z, +x +z).
    /// </summary>
    private int Controls(int column, int run)
    {
        var slot = column * MaxRuns + run;
        var index = slot * 8;
        if (_controlStamps[slot] == _loadStamp)
        {
            return index;
        }

        var own = Surface(column, run);
        var vx = _x0 + column % _width;
        var vz = _z0 + column / _width;
        _controls[index] = Middle(vx - 1, vz, own);
        _controls[index + 1] = Middle(vx + 1, vz, own);
        _controls[index + 2] = Middle(vx, vz - 1, own);
        _controls[index + 3] = Middle(vx, vz + 1, own);
        for (var corner = 0; corner < 4; corner++)
        {
            var dx = (corner & 1) == 0 ? -1 : 1;
            var dz = (corner & 2) == 0 ? -1 : 1;
            _controls[index + 4 + corner] = Corner(vx, vz, dx, dz, own);
        }

        _controlStamps[slot] = _loadStamp;
        return index;
    }

    /// <summary>The surface at the middle of the side a run of surface <paramref name="own"/> shares with column (x, z).</summary>
    private double Middle(int x, int z, double own)
    {
        var other = Nearest(x, z, own, Ramp);
        return double.IsNaN(other) ? own : (own + other) / 2;
    }

    /// <summary>
    /// The surface at the corner a run of surface <paramref name="own"/> in column (x, z) shares with the columns
    /// (<paramref name="dx"/>, <paramref name="dz"/>) of it: the mean of the group of the four columns' surfaces joined
    /// to it, each to one in the group within the ramp (a neighbour's run the nearest own within three times the ramp, as
    /// far as a chain of three joins reaches).
    /// </summary>
    private double Corner(int x, int z, int dx, int dz, double own)
    {
        Span<double> others =
        [
            Nearest(x + dx, z, own, 3 * Ramp),
            Nearest(x, z + dz, own, 3 * Ramp),
            Nearest(x + dx, z + dz, own, 3 * Ramp),
        ];
        Span<bool> joined = [false, false, false];
        double sum = own,
            count = 1;
        for (var pass = 0; pass < 3; pass++)
        {
            for (var i = 0; i < 3; i++)
            {
                if (joined[i] || double.IsNaN(others[i]))
                {
                    continue;
                }

                var joins = Math.Abs(others[i] - own) < Ramp;
                for (var j = 0; j < 3 && !joins; j++)
                {
                    joins = joined[j] && Math.Abs(others[i] - others[j]) < Ramp;
                }

                if (joins)
                {
                    joined[i] = true;
                    sum += others[i];
                    count++;
                }
            }
        }

        return sum / count;
    }

    /// <summary>
    /// The surface of voxel column (x, z)'s run nearest <paramref name="own"/>, where it lies within
    /// <paramref name="within"/> of it; else NaN (none there, a ledge, or a wall).
    /// </summary>
    private double Nearest(int x, int z, double own, double within)
    {
        x -= _x0;
        z -= _z0;
        if ((uint)x >= (uint)_width || (uint)z >= (uint)_depth)
        {
            return double.NaN;
        }

        var column = x + _width * z;
        var best = double.NaN;
        var nearest = within;
        for (var run = 0; run < _counts[column]; run++)
        {
            var surface = Surface(column, run);
            var difference = Math.Abs(surface - own);
            if (difference < nearest)
            {
                nearest = difference;
                best = surface;
            }
        }

        return best;
    }

    /// <summary>A loaded run's surface in voxels (<see cref="VoxelWorld.SurfaceOver"/> over its highest voxel), read once a load.</summary>
    private double Surface(int column, int run)
    {
        var index = column * MaxRuns + run;
        if (_surfaceStamps[index] == _loadStamp)
        {
            return _surfaces[index];
        }

        var x = _x0 + column % _width;
        var z = _z0 + column / _width;
        var top = _ends[index];
        var key = ((long)(x & 0x3FFFFF) << 42) | ((long)(z & 0x3FFFFF) << 20) | (long)(top & 0xFFFFF);
        var slot = (int)((x & 63) | ((z & 63) << 6));
        double surface;
        if (_cachedStamps[slot] == _stamp && _cachedKeys[slot] == key)
        {
            surface = _cachedSurfaces[slot];
        }
        else
        {
            surface = _world.SurfaceOver(x, top, z, in _passable);
            _cachedStamps[slot] = _stamp;
            _cachedKeys[slot] = key;
            _cachedSurfaces[slot] = surface;
        }

        _surfaces[index] = surface;
        _surfaceStamps[index] = _loadStamp;
        return surface;
    }

    /// <summary>
    /// Reads the runs of blocking voxels of the voxel columns under cells x0 to x1 and z0 to z1 and one round them, from a
    /// voxel and the ramp under y0 to as far over y1; nothing when what was last loaded in this move or query holds it.
    /// </summary>
    private void Load(int x0, int z0, int x1, int z1, int y0, int y1)
    {
        var margin = (int)Math.Ceiling(Ramp) + 1;
        int vx0 = Voxel(x0) - 1,
            vz0 = Voxel(z0) - 1,
            vx1 = Voxel(x1) + 1,
            vz1 = Voxel(z1) + 1,
            vy0 = Voxel(y0) - margin,
            vy1 = Voxel(y1) + margin;
        if (_loaded && vx0 >= _x0 && vz0 >= _z0 && vx1 < _x0 + _width && vz1 < _z0 + _depth && vy0 >= _y0 && vy1 <= _y1)
        {
            return;
        }

        var width = vx1 - vx0 + 1;
        var depth = vz1 - vz0 + 1;
        if (width * depth > _counts.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x1),
                width * depth,
                "The cells asked about span too many columns."
            );
        }

        (_x0, _z0, _width, _depth, _y0, _y1) = (vx0, vz0, width, depth, vy0, vy1);
        _loaded = true;
        _loadStamp++;
        if (_loadStamp == int.MaxValue)
        {
            Array.Clear(_surfaceStamps);
            Array.Clear(_controlStamps);
            _loadStamp = 1;
        }

        var columns = width * depth;
        _counts.AsSpan(0, columns).Clear();
        for (var from = vy0; from <= vy1; from += 128)
        {
            var to = Math.Min(from + 127, vy1);
            var bits = _bits.AsSpan(0, columns);
            bits.Clear();
            var visitor = new ColumnBitsVisitor(bits, vx0, vz0, width, depth, from);
            _world.WalkBlocking(vx0, from, vz0, vx1, to, vz1, in _passable, ref visitor);
            for (var column = 0; column < columns; column++)
            {
                AddRuns(column, bits[column], from, to - from + 1);
            }
        }
    }

    /// <summary>Adds the runs of a column's bits (layer <paramref name="from"/> at bit 0), joining one the last pass left open.</summary>
    private void AddRuns(int column, UInt128 bits, int from, int layers)
    {
        var at = 0;
        while (at < layers && bits != UInt128.Zero)
        {
            var skip = (int)UInt128.TrailingZeroCount(bits);
            if (skip >= layers)
            {
                return;
            }

            bits >>= skip;
            at += skip;
            var length = (int)UInt128.TrailingZeroCount(~bits);
            length = Math.Min(length, layers - at);
            var start = from + at;
            var end = start + length;
            var count = _counts[column];
            var last = column * MaxRuns + count - 1;
            if (count > 0 && _ends[last] == start)
            {
                _ends[last] = end;
            }
            else if (count == MaxRuns)
            {
                // Too many runs: the highest takes this one in, the gap between them filled.
                _ends[last] = end;
            }
            else
            {
                _starts[column * MaxRuns + count] = start;
                _ends[column * MaxRuns + count] = end;
                _counts[column] = count + 1;
            }

            at += length;
            bits = length >= 128 ? UInt128.Zero : bits >> length;
        }
    }

    /// <summary>The loaded column of voxel column (x, z).</summary>
    private int Column(int x, int z) => x - _x0 + _width * (z - _z0);

    /// <summary>The voxel holding cell <paramref name="cell"/>.</summary>
    private int Voxel(int cell) => DeterministicMath.FloorDivide(cell, _factor);

    /// <summary>Bits <paramref name="from"/> to <paramref name="to"/> inclusive, both within 0 to 127.</summary>
    private static UInt128 Bits(int from, int to) =>
        (to == 127 ? UInt128.MaxValue : (UInt128.One << (to + 1)) - 1) & ~((UInt128.One << from) - 1);

    /// <summary>Each column's blocking voxels in 128 layers from a base: bit y - base.</summary>
    private readonly ref struct ColumnBitsVisitor(
        Span<UInt128> bits,
        int windowX,
        int windowZ,
        int width,
        int depth,
        int baseY
    ) : IBlockingVisitor
    {
        private readonly Span<UInt128> _bits = bits;

        public bool Finished => false;

        public void Box(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            var layers = Bits(y0 - baseY, y1 - baseY);
            for (var z = Math.Max(z0, windowZ); z <= Math.Min(z1, windowZ + depth - 1); z++)
            {
                for (var x = Math.Max(x0, windowX); x <= Math.Min(x1, windowX + width - 1); x++)
                {
                    _bits[x - windowX + width * (z - windowZ)] |= layers;
                }
            }
        }

        public void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask)
        {
            for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
            {
                var word = mask[w];
                var cz = z + w;
                if (word == 0 || cz < windowZ || cz >= windowZ + depth)
                {
                    continue;
                }

                for (var bx = 0; bx < VoxelConstants.BrickSize; bx++)
                {
                    var cx = x + bx;
                    // The column's bits: bx + 8y for y 0 to 7.
                    var column = (word >> bx) & 0x0101010101010101UL;
                    if (column == 0 || cx < windowX || cx >= windowX + width)
                    {
                        continue;
                    }

                    var layers = UInt128.Zero;
                    for (var by = 0; by < VoxelConstants.BrickSize; by++)
                    {
                        if (((column >> (8 * by)) & 1) != 0)
                        {
                            layers |= UInt128.One << (y + by - baseY);
                        }
                    }

                    _bits[cx - windowX + width * (cz - windowZ)] |= layers;
                }
            }
        }
    }
}
