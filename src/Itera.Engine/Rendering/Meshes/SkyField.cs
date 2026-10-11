using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The sky field (docs/rendering-redesign.md, stage 1.5): how much of the open sky each cell of a box round the camera
/// sees, one byte a cell, for <c>mesh.frag</c> to scale the sky's and the ground's light by. Cells are voxels of the
/// level nearest 64 cm (<see cref="LevelFor"/>), or, with fewer levels, voxels of the coarsest level read every few. A cell of air under
/// open sky (nothing solid above it up to the box's top) sees all of it, 255; light then spreads through air to the six
/// neighbours, keeping <see cref="Keep"/>/256 of itself each cell, so a tunnel darkens a few metres in and a cave far from
/// its mouth is dark. A solid cell takes its brightest neighbour's value, so a surface, which lies between a solid cell
/// and an air one, samples the air in front of it. Cells outside the resident columns count as air.
/// <para>
/// <see cref="BeginRead"/> lists the box's blocks and <see cref="ContinueRead"/> copies them on the main thread, a few
/// each frame within a time limit (each block's own voxels, no apron; a block of one material or air is one fill);
/// <see cref="Flood()"/> then runs on a worker; the owner then uploads <see cref="Light"/>. One build at a time.
/// </para>
/// </summary>
internal sealed class SkyField
{
    /// <summary>Cells along x and z: 160 m of the game's 80 cm cells (128 m at 64 cm).</summary>
    public const int SizeXZ = 200;

    /// <summary>Cells along y: 80 m of the game's 80 cm cells (64 m at 64 cm).</summary>
    public const int SizeY = 100;

    /// <summary>Cells in the box.</summary>
    public const int Cells = SizeXZ * SizeY * SizeXZ;

    /// <summary>
    /// The size the cells are meant to be, in centimetres: they are the voxels of the level nearest it
    /// (<see cref="LevelFor"/>).
    /// </summary>
    public const int CellCentimetres = 64;

    /// <summary>
    /// The level whose voxels are the cells in a world of <paramref name="grid"/>: 6 (64 cm) on a grid of 1 cm, 3 (80 cm)
    /// on one of 10 cm.
    /// </summary>
    public static int LevelFor(VoxelGrid grid) => grid.LevelNearest(CellCentimetres);

    /// <summary>The edge of a cell in metres, in a world of <paramref name="grid"/>.</summary>
    public static float CellMetresFor(VoxelGrid grid) => grid.EdgeMetres(LevelFor(grid));

    /// <summary>What a cell keeps of its brightest neighbour's sky, out of 256: 0.85 a cell, so about a third 4 m in.</summary>
    public const int Keep = 218;

    /// <summary>The box's corner snaps to multiples of this many cells, so the field stays put as the camera moves a little.</summary>
    private const int Snap = 8;

    private readonly byte[] _solid = new byte[Cells];
    private readonly List<int>[] _buckets = new List<int>[256];
    private readonly byte[] _voxels = new byte[VoxelWorld.OwnBlockVoxels];
    private readonly List<ColumnKey> _resident = [];
    private readonly List<BlockKey> _blocks = [];
    private int _nextBlock;
    private VoxelGrid _grid = VoxelGrid.Unit;
    private int _level;
    private int _stride;
    private int _x0;
    private int _y0;
    private int _z0;

    public SkyField()
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = [];
        }
    }

    /// <summary>The light of each cell once <see cref="Flood()"/> has run: index x + sizeX (y + sizeY z).</summary>
    public byte[] Light { get; } = new byte[Cells];

    /// <summary>The low corner of cell (0, 0, 0), absolute (whole cells), set by <see cref="BeginRead"/>.</summary>
    public WorldPoint Corner { get; private set; }

    /// <summary>The middle of the box, absolute, set by <see cref="BeginRead"/>.</summary>
    public WorldPoint Centre { get; private set; }

    /// <summary>Each cell's material as the read left it (air 0), indexed as <see cref="Light"/>. For tests.</summary>
    public ReadOnlySpan<byte> Solid => _solid;

    /// <summary>The level whose voxels the last read's cells are (<see cref="LevelFor"/>).</summary>
    public int CellLevel { get; private set; } = LevelFor(VoxelGrid.Unit);

    /// <summary>The edge of the last read's cells, in metres.</summary>
    public float CellMetres => _grid.EdgeMetres(CellLevel);

    /// <summary>Blocks of the last read that were one material or air, each filled without reading its voxels.</summary>
    public int UniformBlocks { get; private set; }

    /// <summary>Blocks of the last read whose voxels were read.</summary>
    public int StoredBlocks { get; private set; }

    /// <summary>
    /// Reads which cells of the box round <paramref name="centre"/> are solid from <paramref name="world"/> whole
    /// (<see cref="BeginRead"/>, then <see cref="ContinueRead"/> with no time limit). Main thread.
    /// </summary>
    public void Read(VoxelWorld world, in WorldPoint centre, int coarsestLevel)
    {
        BeginRead(world, centre, coarsestLevel);
        ContinueRead(world, long.MaxValue);
    }

    /// <summary>
    /// Starts a read of the box round <paramref name="centre"/>: sets <see cref="Corner"/> and <see cref="Centre"/>,
    /// clears the cells, and lists the blocks of <paramref name="world"/>'s level <see cref="CellLevel"/> (or its coarsest
    /// level if that is finer, every few voxels a cell) in the resident columns that cover the box. Blocks of columns
    /// that load later are not read (the box's cells there stay air); a column that unloads before its blocks are read
    /// leaves them air too. Main thread; allocates nothing once the lists have grown.
    /// </summary>
    public void BeginRead(VoxelWorld world, in WorldPoint centre, int coarsestLevel)
    {
        ArgumentNullException.ThrowIfNull(world);
        _grid = world.Grid;
        CellLevel = LevelFor(_grid);
        _level = Math.Min(CellLevel, coarsestLevel);
        _stride = 1 << (CellLevel - _level);
        double cellCentimetres = _grid.Edge(CellLevel);
        // The corner in cells, absolute: the camera's cell less half the box, snapped.
        var cx = (int)Math.Floor(centre.X * VoxelConstants.CentimetresPerMetre / cellCentimetres) - SizeXZ / 2;
        var cy = (int)Math.Floor(centre.Y * VoxelConstants.CentimetresPerMetre / cellCentimetres) - SizeY / 2;
        var cz = (int)Math.Floor(centre.Z * VoxelConstants.CentimetresPerMetre / cellCentimetres) - SizeXZ / 2;
        cx = FloorTo(cx, Snap);
        cy = FloorTo(cy, Snap);
        cz = FloorTo(cz, Snap);
        var metres = cellCentimetres / VoxelConstants.CentimetresPerMetre;
        Corner = new WorldPoint(cx * metres, cy * metres, cz * metres);
        Centre = Corner.Plus(SizeXZ * 0.5 * metres, SizeY * 0.5 * metres, SizeXZ * 0.5 * metres);
        Array.Clear(_solid);
        UniformBlocks = 0;
        StoredBlocks = 0;

        // The box in the level's voxels, and the blocks that cover it.
        const int edge = VoxelWorld.BlockEdge;
        _x0 = cx * _stride;
        _y0 = cy * _stride;
        _z0 = cz * _stride;
        var x1 = _x0 + SizeXZ * _stride;
        var y1 = _y0 + SizeY * _stride;
        var z1 = _z0 + SizeXZ * _stride;
        // Only the resident columns' blocks: the rest of the box stays air.
        _blocks.Clear();
        _nextBlock = 0;
        _resident.Clear();
        world.CollectResidentColumns(_resident);
        const int column = VoxelConstants.ChunkSize;
        foreach (var key in _resident)
        {
            var cx0 = key.X * column;
            var cz0 = key.Z * column;
            if (key.Level != _level || cx0 >= x1 || cx0 + column <= _x0 || cz0 >= z1 || cz0 + column <= _z0)
            {
                continue;
            }

            for (var bz = Math.Max(cz0, FloorTo(_z0, edge)) / edge; bz * edge < Math.Min(z1, cz0 + column); bz++)
            {
                for (var by = FloorTo(_y0, edge) / edge; by * edge < y1; by++)
                {
                    for (
                        var bx = Math.Max(cx0, FloorTo(_x0, edge)) / edge;
                        bx * edge < Math.Min(x1, cx0 + column);
                        bx++
                    )
                    {
                        _blocks.Add(new BlockKey(bx, by, bz));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reads the listed blocks of <see cref="BeginRead"/>'s box into its cells, one after another, until all are read
    /// or the <see cref="System.Diagnostics.Stopwatch"/> passes <paramref name="untilTicks"/> (at least one block is
    /// read). Returns true when all are read. A block that is all one material or air is one fill; the others' voxels
    /// are read without an apron. Main thread; allocates nothing.
    /// </summary>
    public bool ContinueRead(VoxelWorld world, long untilTicks)
    {
        ArgumentNullException.ThrowIfNull(world);
        const int edge = VoxelWorld.BlockEdge;
        while (_nextBlock < _blocks.Count)
        {
            var block = _blocks[_nextBlock++];
            if (world.TryReadOwnBlock(_level, block.X, block.Y, block.Z, _voxels, out var cell))
            {
                if (cell.IsUniformOrEmpty)
                {
                    FillBlock(block.X * edge, block.Y * edge, block.Z * edge, cell.Material);
                    UniformBlocks++;
                }
                else
                {
                    CopyBlock(block.X * edge, block.Y * edge, block.Z * edge);
                    StoredBlocks++;
                }
            }

            if (System.Diagnostics.Stopwatch.GetTimestamp() >= untilTicks)
            {
                break;
            }
        }

        return _nextBlock >= _blocks.Count;
    }

    /// <summary>How long the last <see cref="Flood()"/> took, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public long FloodTicks { get; private set; }

    /// <summary>Spreads the sky's light through the box's air (<see cref="Flood(ReadOnlySpan{byte}, Span{byte}, int, int, int, List{int}[])"/>). Any one thread.</summary>
    public void Flood()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Flood(_solid, Light, SizeXZ, SizeY, SizeXZ, _buckets);
        FloodTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
    }

    /// <summary>
    /// Fills <paramref name="light"/> from <paramref name="solid"/> (non-zero for solid), both index x + sx (y + sy z):
    /// 255 in each column's air from the top down to its first solid cell, then through air to the six neighbours,
    /// keeping <see cref="Keep"/>/256 a cell, brightest first; then each solid cell takes its brightest air neighbour's.
    /// <paramref name="buckets"/>, 256 empty lists, hold the cells to spread from by value; they are left empty.
    /// </summary>
    internal static void Flood(ReadOnlySpan<byte> solid, Span<byte> light, int sx, int sy, int sz, List<int>[] buckets)
    {
        light.Clear();
        var plane = sx * sy;
        for (var z = 0; z < sz; z++)
        {
            for (var x = 0; x < sx; x++)
            {
                for (var y = sy - 1; y >= 0; y--)
                {
                    var index = x + sx * (y + sy * z);
                    if (solid[index] != 0)
                    {
                        break;
                    }

                    light[index] = 255;
                    buckets[255].Add(index);
                }
            }
        }

        // Brightest first: a cell is spread from at the value it ends with, once (later, dimmer entries are stale).
        for (var value = 255; value > 0; value--)
        {
            var bucket = buckets[value];
            var next = (byte)(value * Keep >> 8);
            for (var i = 0; i < bucket.Count; i++)
            {
                var index = bucket[i];
                if (light[index] != value || next == 0)
                {
                    continue;
                }

                var x = index % sx;
                var y = index / sx % sy;
                var z = index / plane;
                if (x > 0)
                {
                    Spread(solid, light, index - 1, next, buckets);
                }

                if (x < sx - 1)
                {
                    Spread(solid, light, index + 1, next, buckets);
                }

                if (y > 0)
                {
                    Spread(solid, light, index - sx, next, buckets);
                }

                if (y < sy - 1)
                {
                    Spread(solid, light, index + sx, next, buckets);
                }

                if (z > 0)
                {
                    Spread(solid, light, index - plane, next, buckets);
                }

                if (z < sz - 1)
                {
                    Spread(solid, light, index + plane, next, buckets);
                }
            }

            bucket.Clear();
        }

        // A solid cell takes its brightest air neighbour's light: surfaces sample the air in front of them.
        for (var z = 0; z < sz; z++)
        {
            for (var y = 0; y < sy; y++)
            {
                for (var x = 0; x < sx; x++)
                {
                    var index = x + sx * (y + sy * z);
                    if (solid[index] == 0)
                    {
                        continue;
                    }

                    byte best = 0;
                    best = Brighter(solid, light, best, x > 0, index - 1);
                    best = Brighter(solid, light, best, x < sx - 1, index + 1);
                    best = Brighter(solid, light, best, y > 0, index - sx);
                    best = Brighter(solid, light, best, y < sy - 1, index + sx);
                    best = Brighter(solid, light, best, z > 0, index - plane);
                    best = Brighter(solid, light, best, z < sz - 1, index + plane);
                    light[index] = best;
                }
            }
        }
    }

    private static void Spread(ReadOnlySpan<byte> solid, Span<byte> light, int index, byte value, List<int>[] buckets)
    {
        if (solid[index] == 0 && light[index] < value)
        {
            light[index] = value;
            buckets[value].Add(index);
        }
    }

    private static byte Brighter(
        ReadOnlySpan<byte> solid,
        ReadOnlySpan<byte> light,
        byte best,
        bool inside,
        int index
    ) => inside && solid[index] == 0 && light[index] > best ? light[index] : best;

    private static int FloorTo(int value, int step) =>
        value >= 0 ? value / step * step : -((-value + step - 1) / step * step);

    /// <summary>
    /// Copies the voxels of the block whose first voxel is (<paramref name="bx"/>, <paramref name="by"/>,
    /// <paramref name="bz"/>), read into <see cref="_voxels"/>, every stride-th, into the cells of the box. The box's
    /// corner and the block's are multiples of the stride.
    /// </summary>
    private void CopyBlock(int bx, int by, int bz)
    {
        const int edge = VoxelWorld.BlockEdge;
        var stride = _stride;
        var (xFrom, xTo) = Inside(bx, _x0, SizeXZ, stride);
        var (yFrom, yTo) = Inside(by, _y0, SizeY, stride);
        var (zFrom, zTo) = Inside(bz, _z0, SizeXZ, stride);
        for (var vz = zFrom; vz < zTo; vz += stride)
        {
            var z = (bz + vz - _z0) / stride;
            for (var vy = yFrom; vy < yTo; vy += stride)
            {
                var y = (by + vy - _y0) / stride;
                var row = SizeXZ * (y + SizeY * z) - _x0 / stride;
                var from = edge * (vy + edge * vz);
                for (var vx = xFrom; vx < xTo; vx += stride)
                {
                    _solid[row + (bx + vx) / stride] = _voxels[from + vx];
                }
            }
        }
    }

    /// <summary>
    /// Fills the cells of the box that the block whose first voxel is (<paramref name="bx"/>, <paramref name="by"/>,
    /// <paramref name="bz"/>) covers with <paramref name="material"/>; air leaves them as they were cleared.
    /// </summary>
    private void FillBlock(int bx, int by, int bz, byte material)
    {
        if (material == VoxelConstants.Air)
        {
            return;
        }

        var stride = _stride;
        var (xFrom, xTo) = Inside(bx, _x0, SizeXZ, stride);
        var (yFrom, yTo) = Inside(by, _y0, SizeY, stride);
        var (zFrom, zTo) = Inside(bz, _z0, SizeXZ, stride);
        var first = (bx + xFrom - _x0) / stride;
        var count = (xTo - xFrom + stride - 1) / stride;
        for (var vz = zFrom; vz < zTo; vz += stride)
        {
            var z = (bz + vz - _z0) / stride;
            for (var vy = yFrom; vy < yTo; vy += stride)
            {
                var y = (by + vy - _y0) / stride;
                _solid.AsSpan(first + SizeXZ * (y + SizeY * z), count).Fill(material);
            }
        }
    }

    /// <summary>The block's voxels along one axis that fall in the box: from, to (exclusive).</summary>
    private static (int From, int To) Inside(int block, int box, int cells, int stride) =>
        (Math.Max(0, box - block), Math.Min(VoxelWorld.BlockEdge, box + cells * stride - block));

    /// <summary>A block of the read's level: its coordinates in blocks.</summary>
    private readonly record struct BlockKey(int X, int Y, int Z);
}
