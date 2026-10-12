using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// An object's voxels summed for world voxels of one size, for its far copy (<see cref="ObjectCopies"/>): on blocks half
/// that size across over its box (in its own voxels from its box's corner), each block's mean density (0 to 255: how much
/// of the block the object fills) and the commonest material of its solid voxels (air where it holds none). A world voxel
/// reads the blocks at its eight half-size corners' middles, turned into the object, so its copy keeps the object's size.
/// Made by <see cref="ObjectShareBuilder"/>, for a kind from its parts' voxels once a world asks, or for an object's
/// copy as it stands from a snapshot of its voxels (<see cref="ObjectLabeller"/>), alike. Read-only once built.
/// </summary>
internal sealed class ObjectShareGrid
{
    /// <summary>What <see cref="AroundAt"/> says of a block: nothing filled within a block of it.</summary>
    public const byte Empty = 0;

    /// <summary>What <see cref="AroundAt"/> says of a block: some filled and some not within a block of it.</summary>
    public const byte Edge = 1;

    /// <summary>What <see cref="AroundAt"/> says of a block: every block within a block of it full.</summary>
    public const byte Full = 2;

    private readonly byte[] _shares;
    private readonly byte[] _materials;
    private readonly byte[] _around;
    private readonly byte[] _near;
    private readonly int _nearX;
    private readonly int _nearY;
    private readonly int _nearZ;
    private readonly int _reach;
    private readonly int _sizeX;
    private readonly int _sizeY;
    private readonly int _sizeZ;

    internal ObjectShareGrid(
        byte[] shares,
        byte[] materials,
        int sizeX,
        int sizeY,
        int sizeZ,
        int block,
        int edge,
        VoxelCoord boxCentimetres,
        VoxelCoord anchor
    )
    {
        _shares = shares;
        _materials = materials;
        _sizeX = sizeX;
        _sizeY = sizeY;
        _sizeZ = sizeZ;
        _around = Around(shares, sizeX, sizeY, sizeZ);
        // A voxel's samples lie within two blocks of its middle's where a block is half a voxel; else within three.
        _reach = 2 * block == edge ? 2 : 3;
        (_near, _nearX, _nearY, _nearZ) = Near(shares, sizeX, sizeY, sizeZ, _reach);
        BoxCentimetres = boxCentimetres;
        Anchor = anchor;
        BlockCentimetres = block;
        EdgeCentimetres = edge;
        // The box from the anchor, and a world voxel round it: what any heading turns the copy within.
        long x0 = -anchor.X - (long)edge,
            x1 = boxCentimetres.X - (long)anchor.X + edge,
            z0 = -anchor.Z - (long)edge,
            z1 = boxCentimetres.Z - (long)anchor.Z + edge;
        var most = 0L;
        foreach (var x in (ReadOnlySpan<long>)[x0, x1])
        {
            foreach (var z in (ReadOnlySpan<long>)[z0, z1])
            {
                most = Math.Max(most, x * x + z * z);
            }
        }

        ReachAcrossCentimetres = Maths.DeterministicMath.SquareRoot(most) + 1;
        LowCentimetres = -anchor.Y - edge;
        HighCentimetres = boxCentimetres.Y - anchor.Y + edge;
    }

    /// <summary>A block's edge, in centimetres: whole voxels of the object.</summary>
    public int BlockCentimetres { get; }

    /// <summary>The object's box, in centimetres.</summary>
    public VoxelCoord BoxCentimetres { get; }

    /// <summary>The object's anchor, in centimetres from its box's corner.</summary>
    public VoxelCoord Anchor { get; }

    /// <summary>
    /// False when no block within two of the pair of blocks holding (<paramref name="x"/>, <paramref name="y"/>,
    /// <paramref name="z"/>) (centimetres in the object's box from its corner; three where a block is not half a world
    /// voxel) holds any share: a world voxel whose middle is there reads no block that holds any, whatever its heading
    /// (its samples lie within 0.53 of its edge of its middle along each axis).
    /// </summary>
    public bool AnyNear(long x, long y, long z)
    {
        var pair = 2L * BlockCentimetres;
        var pad = _reach / 2 + 1;
        var gx = Maths.DeterministicMath.FloorDivide(x, pair) + pad;
        var gy = Maths.DeterministicMath.FloorDivide(y, pair) + pad;
        var gz = Maths.DeterministicMath.FloorDivide(z, pair) + pad;
        return (ulong)gx < (ulong)_nearX
            && (ulong)gy < (ulong)_nearY
            && (ulong)gz < (ulong)_nearZ
            && _near[gx + _nearX * (gy + (long)_nearY * gz)] != 0;
    }

    /// <summary>The block holding centimetre <paramref name="centimetres"/> of the object's box along one axis.</summary>
    public long BlockOf(long centimetres) => Maths.DeterministicMath.FloorDivide(centimetres, BlockCentimetres);

    /// <summary>
    /// <see cref="Read"/> of block (<paramref name="bx"/>, <paramref name="by"/>, <paramref name="bz"/>) (<see cref="BlockOf"/>).
    /// </summary>
    public byte ReadBlock(long bx, long by, long bz, out byte material, out byte around)
    {
        if ((ulong)bx < (ulong)_sizeX && (ulong)by < (ulong)_sizeY && (ulong)bz < (ulong)_sizeZ)
        {
            var index = bx + _sizeX * (by + (long)_sizeY * bz);
            material = _materials[index];
            around = _around[index];
            return _shares[index];
        }

        material = VoxelConstants.Air;
        around =
            bx < -1 || by < -1 || bz < -1 || bx > _sizeX || by > _sizeY || bz > _sizeZ ? Empty
            : AnyFilledWithin(
                Math.Clamp(bx, 0, _sizeX - 1),
                Math.Clamp(by, 0, _sizeY - 1),
                Math.Clamp(bz, 0, _sizeZ - 1)
            )
                ? Edge
            : Empty;
        return 0;
    }

    /// <summary>
    /// The share and material of the block holding (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>)
    /// (<see cref="ShareAt"/>) and what lies within a block of it (<see cref="AroundAt"/>), from one lookup.
    /// </summary>
    public byte Read(long x, long y, long z, out byte material, out byte around)
    {
        var bx = Maths.DeterministicMath.FloorDivide(x, BlockCentimetres);
        var by = Maths.DeterministicMath.FloorDivide(y, BlockCentimetres);
        var bz = Maths.DeterministicMath.FloorDivide(z, BlockCentimetres);
        if ((ulong)bx < (ulong)_sizeX && (ulong)by < (ulong)_sizeY && (ulong)bz < (ulong)_sizeZ)
        {
            var index = bx + _sizeX * (by + (long)_sizeY * bz);
            material = _materials[index];
            around = _around[index];
            return _shares[index];
        }

        material = VoxelConstants.Air;
        around = AroundAt(x, y, z);
        return 0;
    }

    // Pairs of blocks, pad pairs past the box on each side (pad = reach / 2 + 1), holding whether any block within reach
    // of the pair's two holds a share. Pair g holds blocks 2(g - pad) and the next.
    private static (byte[] Near, int X, int Y, int Z) Near(byte[] shares, int sizeX, int sizeY, int sizeZ, int reach)
    {
        var pad = reach / 2 + 1;
        int nx = (sizeX + 1) / 2 + 2 * pad,
            ny = (sizeY + 1) / 2 + 2 * pad,
            nz = (sizeZ + 1) / 2 + 2 * pad;
        var near = new byte[(long)nx * ny * nz];
        for (var z = 0; z < sizeZ; z++)
        {
            for (var y = 0; y < sizeY; y++)
            {
                for (var x = 0; x < sizeX; x++)
                {
                    if (shares[x + sizeX * (y + (long)sizeY * z)] == 0)
                    {
                        continue;
                    }

                    // The pairs whose blocks, widened by reach, hold block (x, y, z): blocks 2(g - pad) - reach to
                    // 2(g - pad) + 1 + reach.
                    int lowX = Pair(x - 1 - reach, pad),
                        highX = Pair(x + reach, pad);
                    int lowY = Pair(y - 1 - reach, pad),
                        highY = Pair(y + reach, pad);
                    int lowZ = Pair(z - 1 - reach, pad),
                        highZ = Pair(z + reach, pad);
                    for (var gz = Math.Max(0, lowZ); gz <= Math.Min(nz - 1, highZ); gz++)
                    {
                        for (var gy = Math.Max(0, lowY); gy <= Math.Min(ny - 1, highY); gy++)
                        {
                            for (var gx = Math.Max(0, lowX); gx <= Math.Min(nx - 1, highX); gx++)
                            {
                                near[gx + nx * (gy + (long)ny * gz)] = 1;
                            }
                        }
                    }
                }
            }
        }

        return (near, nx, ny, nz);

        // The pair holding block b.
        static int Pair(int b, int pad) => (int)Maths.DeterministicMath.FloorDivide(b, 2) + pad;
    }

    /// <summary>The world voxels' edge it is for, in centimetres.</summary>
    public int EdgeCentimetres { get; }

    /// <summary>How far across from the anchor the copy reaches, at any heading, rounded up.</summary>
    public int ReachAcrossCentimetres { get; }

    /// <summary>The lowest centimetre of the copy from the anchor.</summary>
    public int LowCentimetres { get; }

    /// <summary>The centimetre past the highest of the copy from the anchor.</summary>
    public int HighCentimetres { get; }

    /// <summary>
    /// What lies within a block of the block holding (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>)
    /// (centimetres in the object's box from its corner): <see cref="Empty"/>, <see cref="Edge"/>, or <see cref="Full"/>;
    /// past the box, empty, its border's blocks counting what lies past it as empty.
    /// </summary>
    public byte AroundAt(long x, long y, long z)
    {
        var bx = Maths.DeterministicMath.FloorDivide(x, BlockCentimetres);
        var by = Maths.DeterministicMath.FloorDivide(y, BlockCentimetres);
        var bz = Maths.DeterministicMath.FloorDivide(z, BlockCentimetres);
        // A block just past the box has its border's blocks within one.
        if (bx < -1 || by < -1 || bz < -1 || bx > _sizeX || by > _sizeY || bz > _sizeZ)
        {
            return Empty;
        }

        if ((ulong)bx >= (ulong)_sizeX || (ulong)by >= (ulong)_sizeY || (ulong)bz >= (ulong)_sizeZ)
        {
            return AnyFilledWithin(
                Math.Clamp(bx, 0, _sizeX - 1),
                Math.Clamp(by, 0, _sizeY - 1),
                Math.Clamp(bz, 0, _sizeZ - 1)
            )
                ? Edge
                : Empty;
        }

        return _around[bx + _sizeX * (by + (long)_sizeY * bz)];
    }

    // True when a block within one of (bx, by, bz) holds any share.
    private bool AnyFilledWithin(long bx, long by, long bz)
    {
        for (var z = Math.Max(0, bz - 1); z <= Math.Min(_sizeZ - 1, bz + 1); z++)
        {
            for (var y = Math.Max(0, by - 1); y <= Math.Min(_sizeY - 1, by + 1); y++)
            {
                for (var x = Math.Max(0, bx - 1); x <= Math.Min(_sizeX - 1, bx + 1); x++)
                {
                    if (_shares[x + _sizeX * (y + (long)_sizeY * z)] != 0)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // Each block's neighbourhood within one: empty, edge, or full; past the box counts as empty.
    private static byte[] Around(byte[] shares, int sizeX, int sizeY, int sizeZ)
    {
        var around = new byte[shares.Length];
        for (var z = 0; z < sizeZ; z++)
        {
            for (var y = 0; y < sizeY; y++)
            {
                for (var x = 0; x < sizeX; x++)
                {
                    bool any = false,
                        all = true;
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx,
                                    ny = y + dy,
                                    nz = z + dz;
                                var share =
                                    (uint)nx < (uint)sizeX && (uint)ny < (uint)sizeY && (uint)nz < (uint)sizeZ
                                        ? shares[nx + sizeX * (ny + (long)sizeY * nz)]
                                        : (byte)0;
                                any |= share != 0;
                                all &= share == VoxelDensity.Full;
                            }
                        }
                    }

                    around[x + sizeX * (y + (long)sizeY * z)] =
                        all ? Full
                        : any ? Edge
                        : Empty;
                }
            }
        }

        return around;
    }

    /// <summary>
    /// The mean density of the block holding (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>),
    /// centimetres in the object's box from its corner, and its material; 0 and air outside the box.
    /// </summary>
    public byte ShareAt(long x, long y, long z, out byte material)
    {
        var bx = Maths.DeterministicMath.FloorDivide(x, BlockCentimetres);
        var by = Maths.DeterministicMath.FloorDivide(y, BlockCentimetres);
        var bz = Maths.DeterministicMath.FloorDivide(z, BlockCentimetres);
        if ((ulong)bx >= (ulong)_sizeX || (ulong)by >= (ulong)_sizeY || (ulong)bz >= (ulong)_sizeZ)
        {
            material = VoxelConstants.Air;
            return 0;
        }

        var index = bx + _sizeX * (by + (long)_sizeY * bz);
        material = _materials[index];
        return _shares[index];
    }
}

/// <summary>
/// Sums an object's voxels into <see cref="ObjectShareGrid"/>s for world voxels of a first edge and each twice as wide
/// after it: fed its voxels one at a time (<see cref="Add"/>, any order, each once), then <see cref="Finish"/>. The kind's
/// grids and a snapshot's are made by this one rule, so a copy written from the kind and one worked out from an undamaged
/// object's voxels are the same. Integers only.
/// </summary>
internal sealed class ObjectShareBuilder
{
    private readonly VoxelCoord _size;
    private readonly int _unit;
    private readonly int _per;
    private readonly int _blocksX;
    private readonly int _blocksY;
    private readonly int _blocksZ;
    private readonly long[] _densities;
    private readonly int[][] _counts;
    private readonly int[] _slot = new int[256];
    private readonly byte[] _materials;
    private readonly int _edge;

    /// <param name="size">The object's box, in its voxels.</param>
    /// <param name="unit">Its voxels' edge, in centimetres.</param>
    /// <param name="edge">The finest world voxels' edge the grids are for, in centimetres.</param>
    /// <param name="materials">Its materials (any solid voxel's among them).</param>
    public ObjectShareBuilder(VoxelCoord size, int unit, int edge, MaterialSet materials)
    {
        _size = size;
        _unit = unit;
        _edge = edge;
        // Blocks half the finest world voxel, in whole voxels of the object.
        _per = Math.Max(1, edge / 2 / unit);
        _blocksX = (size.X + _per - 1) / _per;
        _blocksY = (size.Y + _per - 1) / _per;
        _blocksZ = (size.Z + _per - 1) / _per;
        var list = new List<byte>();
        Array.Fill(_slot, -1);
        for (var m = 1; m < 256; m++)
        {
            if (materials.Contains((byte)m))
            {
                _slot[m] = list.Count;
                list.Add((byte)m);
            }
        }

        _materials = [.. list];
        var blocks = (long)_blocksX * _blocksY * _blocksZ;
        _densities = new long[blocks];
        _counts = new int[_materials.Length][];
        for (var i = 0; i < _counts.Length; i++)
        {
            _counts[i] = new int[blocks];
        }
    }

    /// <summary>Adds voxel (x, y, z) of the object's box: its material and density.</summary>
    public void Add(int x, int y, int z, byte material, byte density)
    {
        var block = x / _per + _blocksX * (y / _per + (long)_blocksY * (z / _per));
        _densities[block] += density;
        if (material != VoxelConstants.Air && _slot[material] >= 0)
        {
            _counts[_slot[material]][block]++;
        }
    }

    /// <summary>
    /// The grids for <paramref name="count"/> world voxel sizes from the first, each twice the last, for an object whose
    /// anchor is <paramref name="anchor"/> centimetres from its box's corner.
    /// </summary>
    public ObjectShareGrid[] Finish(int count, VoxelCoord anchor)
    {
        var grids = new ObjectShareGrid[count];
        var box = new VoxelCoord(_size.X * _unit, _size.Y * _unit, _size.Z * _unit);
        var densities = _densities;
        var counts = _counts;
        int bx = _blocksX,
            by = _blocksY,
            bz = _blocksZ;
        var per = _per;
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                // Each block of this size the sum of the eight of the last under it.
                (densities, counts, bx, by, bz) = Halve(densities, counts, bx, by, bz);
                per *= 2;
            }

            var volume = (long)per * per * per;
            var blocks = (long)bx * by * bz;
            var shares = new byte[blocks];
            var materials = new byte[blocks];
            for (var b = 0L; b < blocks; b++)
            {
                shares[b] = (byte)Math.Min(255, densities[b] / volume);
                var best = 0;
                for (var m = 0; m < counts.Length; m++)
                {
                    if (counts[m][b] > best)
                    {
                        best = counts[m][b];
                        materials[b] = _materials[m];
                    }
                }
            }

            grids[i] = new ObjectShareGrid(shares, materials, bx, by, bz, per * _unit, _edge << i, box, anchor);
        }

        return grids;
    }

    private static (long[] Densities, int[][] Counts, int X, int Y, int Z) Halve(
        long[] densities,
        int[][] counts,
        int bx,
        int by,
        int bz
    )
    {
        int hx = (bx + 1) / 2,
            hy = (by + 1) / 2,
            hz = (bz + 1) / 2;
        var halfDensities = new long[(long)hx * hy * hz];
        var halfCounts = new int[counts.Length][];
        for (var m = 0; m < counts.Length; m++)
        {
            halfCounts[m] = new int[halfDensities.Length];
        }

        for (var z = 0; z < bz; z++)
        {
            for (var y = 0; y < by; y++)
            {
                for (var x = 0; x < bx; x++)
                {
                    var from = x + bx * (y + (long)by * z);
                    var to = x / 2 + hx * (y / 2 + (long)hy * (z / 2));
                    halfDensities[to] += densities[from];
                    for (var m = 0; m < counts.Length; m++)
                    {
                        halfCounts[m][to] += counts[m][from];
                    }
                }
            }
        }

        return (halfDensities, halfCounts, hx, hy, hz);
    }
}
