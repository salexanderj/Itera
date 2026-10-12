using Itera.Engine.Maths;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Objects;

/// <summary>
/// A world's objects as coarse copies in its own voxels, from the hand-over level up (<c>Engine:Objects:HandOverLevel</c>):
/// for each kind and each of those levels whose voxels are no wider than the kind's <see cref="VoxelObjectKind.CopiesUpToCentimetres"/>,
/// a grid of the materials a world voxel there takes (<see cref="ObjectCopyGrid"/>); the column jobs find the objects near
/// a column (<see cref="Find"/>) and write their copies into its chunks' air (<see cref="Stamp"/>), after the generator's
/// fill and before the edits. Built on the main thread before the workers start; read by them all at once. Integers
/// only, so every peer's copies are the same.
/// </summary>
internal sealed class ObjectCopies
{
    /// <summary>
    /// The version of the rule that writes copies (the lattice, its margins, the turns, the air they fill): part of the
    /// kinds' fingerprint, so builds whose copies differ do not join each other. Raise it with any change to the copies.
    /// </summary>
    public const int RuleVersion = 1;

    private readonly IObjectPlacer _placer;
    private readonly VoxelObjectKinds _kinds;
    private readonly VoxelGrid _grid;
    private readonly ObjectCopyGrid?[][] _copies;
    private readonly int[] _reaches;

    private ObjectCopies(
        IObjectPlacer placer,
        VoxelObjectKinds kinds,
        VoxelGrid grid,
        ObjectCopyGrid?[][] copies,
        int first,
        int last,
        MaterialSet materials,
        int[] reaches
    )
    {
        _reaches = reaches;
        _placer = placer;
        _kinds = kinds;
        _grid = grid;
        _copies = copies;
        FirstLevel = first;
        LastLevel = last;
        Materials = materials;
    }

    /// <summary>The finest level holding copies.</summary>
    public int FirstLevel { get; }

    /// <summary>The coarsest level holding copies; below <see cref="FirstLevel"/> when no kind is copied.</summary>
    public int LastLevel { get; }

    /// <summary>Every material of every kind: what the copies are made of.</summary>
    public MaterialSet Materials { get; }

    /// <summary>
    /// The copies of <paramref name="kinds"/> for a world of <paramref name="grid"/> with <paramref name="levels"/>
    /// levels, from <paramref name="handOverLevel"/> up; null with no placer or no kind. Seals the kinds. Main thread.
    /// </summary>
    public static ObjectCopies? Create(
        IObjectPlacer? placer,
        VoxelObjectKinds? kinds,
        VoxelGrid grid,
        int handOverLevel,
        int levels
    )
    {
        if (placer is null || kinds is null || kinds.Count == 0)
        {
            return null;
        }

        kinds.Seal(grid);
        var copies = new ObjectCopyGrid?[kinds.Count][];
        var first = int.MaxValue;
        var last = -1;
        var materials = default(MaterialSet);
        var reaches = new int[levels];
        for (var k = 0; k < kinds.Count; k++)
        {
            var kind = kinds[new VoxelObjectKindId((ushort)k)];
            materials = materials.Union(kind.Materials);
            copies[k] = new ObjectCopyGrid?[levels];
            for (var level = handOverLevel; level < levels; level++)
            {
                if (grid.Edge(level) > kind.CopiesUpToCentimetres)
                {
                    break;
                }

                var copy = kind.CopyFor(grid.Edge(level));
                copies[k][level] = copy;
                reaches[level] = Math.Max(reaches[level], copy.ReachAcrossCentimetres);
                first = Math.Min(first, level);
                last = Math.Max(last, level);
            }
        }

        return new ObjectCopies(
            placer,
            kinds,
            grid,
            copies,
            first == int.MaxValue ? 0 : first,
            last,
            materials,
            reaches
        );
    }

    /// <summary>True when some kind has a copy at <paramref name="level"/>.</summary>
    public bool CopiesAt(int level) => level >= FirstLevel && level <= LastLevel;

    /// <summary>
    /// Adds to <paramref name="found"/> the objects whose copy at <paramref name="column"/>'s level reaches into the column:
    /// the placer's objects near it, those of kinds copied at the level whose box, widened by the copy's reach, meets the
    /// column. Worker threads, each with its own list.
    /// </summary>
    public void Find(ColumnKey column, ulong seed, List<ObjectPlacement> found)
    {
        found.Clear();
        var width = (long)_grid.ColumnCentimetres(column.Level);
        var minX = column.X * width;
        var minZ = column.Z * width;
        // The farthest any kind's copy at this level reaches from its anchor.
        var reach = _reaches[column.Level];
        _placer.Find(
            seed,
            (int)(minX - reach),
            (int)(minX + width + reach),
            (int)(minZ - reach),
            (int)(minZ + width + reach),
            found
        );
        var kept = 0;
        for (var i = 0; i < found.Count; i++)
        {
            var placement = found[i];
            if (placement.Kind.Value >= _copies.Length || (uint)placement.Heading >= ObjectPlacement.HeadingSteps)
            {
                throw new InvalidOperationException(
                    $"The world's placer gave object {placement.Id.Value} kind {placement.Kind.Value} (of {_copies.Length}) and heading {placement.Heading}."
                );
            }

            if (_copies[placement.Kind.Value][column.Level] is not { } copy)
            {
                continue;
            }

            var across = copy.ReachAcrossCentimetres;
            if (
                placement.Anchor.X + across >= minX
                && placement.Anchor.X - across < minX + width
                && placement.Anchor.Z + across >= minZ
                && placement.Anchor.Z - across < minZ + width
            )
            {
                found[kept++] = placement;
            }
        }

        found.RemoveRange(kept, found.Count - kept);
    }

    /// <summary>
    /// The lowest and highest centimetre the copies of <paramref name="placements"/> at <paramref name="level"/> reach,
    /// or (1, 0) for none.
    /// </summary>
    public (long Low, long High) Heights(int level, ReadOnlySpan<ObjectPlacement> placements)
    {
        long low = 1,
            high = 0;
        foreach (ref readonly var placement in placements)
        {
            var (bottom, top) = HeightsOf(level, in placement);
            (low, high) = low > high ? (bottom, top) : (Math.Min(low, bottom), Math.Max(high, top));
        }

        return (low, high);
    }

    /// <summary>True when a copy of <paramref name="placements"/> reaches into <paramref name="chunk"/>'s height.</summary>
    public bool Touches(ChunkKey chunk, ReadOnlySpan<ObjectPlacement> placements)
    {
        var height = (long)_grid.ColumnCentimetres(chunk.Level);
        var bottom = (long)chunk.Y * height;
        foreach (ref readonly var placement in placements)
        {
            var (low, high) = HeightsOf(chunk.Level, in placement);
            if (high >= bottom && low < bottom + height)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the copies of <paramref name="placements"/> into the air of <paramref name="chunk"/>'s voxels (128^3 at index
    /// <c>x + 128y + 16384z</c>), each voxel the material its sample point reads in its kind's copy at the chunk's level,
    /// whole (<see cref="VoxelDensity.Full"/>). Returns how many voxels it wrote.
    /// </summary>
    public int Stamp(ChunkKey chunk, ReadOnlySpan<ObjectPlacement> placements, Span<byte> voxels, Span<byte> densities)
    {
        const int size = VoxelConstants.ChunkSize;
        var level = new GridLevel(_grid, chunk.Level);
        var first = chunk.LevelOrigin;
        var written = 0;
        foreach (ref readonly var placement in placements)
        {
            var copy = _copies[placement.Kind.Value][chunk.Level]!;
            var kind = _kinds[placement.Kind];
            var anchor = placement.Anchor;
            var across = copy.ReachAcrossCentimetres;
            var (low, high) = HeightsOf(chunk.Level, in placement);
            var (zStart, zEnd) = VoxelRows.Rows(
                Clamp(anchor.Z - (long)across),
                Clamp(anchor.Z + (long)across),
                first.Z,
                level
            );
            var (yStart, yEnd) = VoxelRows.Rows(Clamp(low), Clamp(high), first.Y, level);
            var (xStart, xEnd) = VoxelRows.Rows(
                Clamp(anchor.X - (long)across),
                Clamp(anchor.X + (long)across),
                first.X,
                level
            );
            // ObjectTurns.ToObject, with what stays fixed along a row taken out of it.
            long cos = ObjectTurns.Cos(placement.Heading),
                sin = ObjectTurns.Sin(placement.Heading);
            for (var z = zStart; z < zEnd; z++)
            {
                long dz = level.SampleAt(first.Z + z) - (long)anchor.Z;
                long dzSin = dz * sin,
                    dzCos = dz * cos;
                for (var y = yStart; y < yEnd; y++)
                {
                    var oy = level.SampleAt(first.Y + y) - (long)anchor.Y + kind.AnchorCentimetres.Y;
                    for (var x = xStart; x < xEnd; x++)
                    {
                        long dx = level.SampleAt(first.X + x) - (long)anchor.X;
                        var ox = (dx * cos + dzSin) >> ObjectTurns.Shift;
                        var oz = (dzCos - dx * sin) >> ObjectTurns.Shift;
                        var material = copy.At(ox + kind.AnchorCentimetres.X, oy, oz + kind.AnchorCentimetres.Z);
                        var index = x + size * (y + size * z);
                        if (material != VoxelConstants.Air && voxels[index] == VoxelConstants.Air)
                        {
                            voxels[index] = material;
                            densities[index] = VoxelDensity.Full;
                            written++;
                        }
                    }
                }
            }
        }

        return written;
    }

    // The centimetres a placement's copy at the level reaches from bottom to top, inclusive.
    private (long Low, long High) HeightsOf(int level, in ObjectPlacement placement)
    {
        var copy = _copies[placement.Kind.Value][level]!;
        return ((long)placement.Anchor.Y + copy.LowCentimetres, (long)placement.Anchor.Y + copy.HighCentimetres - 1);
    }

    private static int Clamp(long centimetres) => (int)Math.Clamp(centimetres, int.MinValue / 2, int.MaxValue / 2);
}

/// <summary>
/// A kind's copy for world voxels of one size: on a lattice of blocks over the kind's box (in the kind's voxels from its
/// box's corner), the material a world voxel whose sample point lies in a block takes, at any heading: the commonest of
/// the kind's solid voxels a world voxel there could touch (those within half its diagonal across, half its edge up and
/// down), or air where it could touch none. So every world voxel the object touches is solid, and a thin trunk is kept;
/// the copy reaches past the object by up to about a world voxel. Read-only once built.
/// </summary>
internal sealed class ObjectCopyGrid
{
    private readonly byte[] _materials;
    private readonly int _sizeX;
    private readonly int _sizeY;
    private readonly int _sizeZ;
    private readonly int _marginAcross;
    private readonly int _marginUp;
    private readonly int _block;

    private ObjectCopyGrid(
        byte[] materials,
        int sizeX,
        int sizeY,
        int sizeZ,
        int marginAcross,
        int marginUp,
        int block,
        VoxelCoord anchor
    )
    {
        _materials = materials;
        _sizeX = sizeX;
        _sizeY = sizeY;
        _sizeZ = sizeZ;
        _marginAcross = marginAcross;
        _marginUp = marginUp;
        _block = block;
        // The lattice's box from the anchor: what any heading turns its cells within.
        long x0 = -marginAcross * (long)block - anchor.X,
            x1 = (sizeX - marginAcross) * (long)block - anchor.X,
            z0 = -marginAcross * (long)block - anchor.Z,
            z1 = (sizeZ - marginAcross) * (long)block - anchor.Z;
        var most = 0L;
        foreach (var x in (ReadOnlySpan<long>)[x0, x1])
        {
            foreach (var z in (ReadOnlySpan<long>)[z0, z1])
            {
                most = Math.Max(most, x * x + z * z);
            }
        }

        ReachAcrossCentimetres = DeterministicMath.SquareRoot(most) + 1;
        LowCentimetres = -marginUp * block - anchor.Y;
        HighCentimetres = (sizeY - marginUp) * block - anchor.Y;
    }

    /// <summary>How far across from the kind's anchor any of the copy's voxels lies, at any heading, rounded up.</summary>
    public int ReachAcrossCentimetres { get; }

    /// <summary>The lowest centimetre of the copy from the kind's anchor.</summary>
    public int LowCentimetres { get; }

    /// <summary>The centimetre past the highest of the copy from the kind's anchor.</summary>
    public int HighCentimetres { get; }

    /// <summary>Half the diagonal of a square <paramref name="edge"/> across, rounded up.</summary>
    public static int HalfAcross(int edge) => DeterministicMath.SquareRoot((long)edge * edge / 2) + 1;

    /// <summary>
    /// The material at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>), centimetres in the kind's box
    /// from its corner; air outside the lattice.
    /// </summary>
    public byte At(long x, long y, long z)
    {
        var bx = DeterministicMath.FloorDivide(x, _block) + _marginAcross;
        var by = DeterministicMath.FloorDivide(y, _block) + _marginUp;
        var bz = DeterministicMath.FloorDivide(z, _block) + _marginAcross;
        return (ulong)bx < (ulong)_sizeX && (ulong)by < (ulong)_sizeY && (ulong)bz < (ulong)_sizeZ
            ? _materials[bx + _sizeX * (by + (long)_sizeY * bz)]
            : VoxelConstants.Air;
    }

    /// <summary>The copy of <paramref name="kind"/> for world voxels <paramref name="edge"/> centimetres across.</summary>
    public static ObjectCopyGrid Build(VoxelObjectKind kind, int edge)
    {
        var unit = kind.UnitCentimetres;
        var halfAcross = HalfAcross(edge);
        var halfUp = (edge + 1) / 2;
        // Blocks a quarter of the world's voxel, in whole voxels of the kind.
        var per = Math.Max(1, edge / 4 / unit);
        var block = per * unit;
        var blocksX = (kind.Size.X + per - 1) / per;
        var blocksY = (kind.Size.Y + per - 1) / per;
        var blocksZ = (kind.Size.Z + per - 1) / per;

        // Each material's voxels in each block.
        var materials = kind.Materials;
        var list = new List<byte>();
        for (var m = 1; m < 256; m++)
        {
            if (materials.Contains((byte)m))
            {
                list.Add((byte)m);
            }
        }

        var slot = new int[256];
        for (var i = 0; i < list.Count; i++)
        {
            slot[list[i]] = i;
        }

        var cells = (long)blocksX * blocksY * blocksZ;
        var counts = new int[list.Count][];
        for (var i = 0; i < list.Count; i++)
        {
            counts[i] = new int[cells];
        }

        var voxels = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        foreach (var part in kind.Parts)
        {
            part.Decode(0, voxels, densities);
            for (var z = 0; z < part.Size.Z; z++)
            {
                for (var y = 0; y < part.Size.Y; y++)
                {
                    for (var x = 0; x < part.Size.X; x++)
                    {
                        var material = voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)];
                        if (material == VoxelConstants.Air)
                        {
                            continue;
                        }

                        var b =
                            (part.Offset.X + x) / per
                            + blocksX * ((part.Offset.Y + y) / per + (long)blocksY * ((part.Offset.Z + z) / per));
                        counts[slot[material]][b]++;
                    }
                }
            }
        }

        // The blocks a world voxel whose sample point lies in a block may touch: within this many blocks of it.
        var marginAcross = (halfAcross + block - 1) / block;
        var marginUp = (halfUp + block - 1) / block;
        var sizeX = blocksX + 2 * marginAcross;
        var sizeY = blocksY + 2 * marginUp;
        var sizeZ = blocksZ + 2 * marginAcross;
        var sums = new long[list.Count][];
        for (var i = 0; i < list.Count; i++)
        {
            sums[i] = PrefixSums(counts[i], blocksX, blocksY, blocksZ);
        }

        var copy = new byte[(long)sizeX * sizeY * sizeZ];
        for (var lz = 0; lz < sizeZ; lz++)
        {
            for (var ly = 0; ly < sizeY; ly++)
            {
                for (var lx = 0; lx < sizeX; lx++)
                {
                    // The block this lattice cell stands for, and the blocks within the margins of it.
                    int bx = lx - marginAcross,
                        by = ly - marginUp,
                        bz = lz - marginAcross;
                    var best = 0L;
                    byte material = VoxelConstants.Air;
                    for (var i = 0; i < list.Count; i++)
                    {
                        var count = BoxSum(
                            sums[i],
                            blocksX,
                            blocksY,
                            blocksZ,
                            bx - marginAcross,
                            bx + marginAcross,
                            by - marginUp,
                            by + marginUp,
                            bz - marginAcross,
                            bz + marginAcross
                        );
                        if (count > best)
                        {
                            best = count;
                            material = list[i];
                        }
                    }

                    copy[lx + sizeX * (ly + (long)sizeY * lz)] = material;
                }
            }
        }

        return new ObjectCopyGrid(copy, sizeX, sizeY, sizeZ, marginAcross, marginUp, block, kind.AnchorCentimetres);
    }

    // Sums of the counts over every box from block (0, 0, 0) to (x, y, z) exclusive: (blocks + 1)^3 entries.
    private static long[] PrefixSums(int[] counts, int sizeX, int sizeY, int sizeZ)
    {
        int strideY = sizeX + 1,
            strideZ = (sizeX + 1) * (sizeY + 1);
        var sums = new long[(long)strideZ * (sizeZ + 1)];
        for (var z = 1; z <= sizeZ; z++)
        {
            for (var y = 1; y <= sizeY; y++)
            {
                for (var x = 1; x <= sizeX; x++)
                {
                    var count = counts[x - 1 + sizeX * (y - 1 + (long)sizeY * (z - 1))];
                    var i = x + strideY * y + (long)strideZ * z;
                    sums[i] =
                        count
                        + sums[i - 1]
                        + sums[i - strideY]
                        + sums[i - strideZ]
                        - sums[i - 1 - strideY]
                        - sums[i - 1 - strideZ]
                        - sums[i - strideY - strideZ]
                        + sums[i - 1 - strideY - strideZ];
                }
            }
        }

        return sums;
    }

    // The counts over blocks x0..x1, y0..y1, z0..z1 (inclusive), clipped to the box.
    private static long BoxSum(
        long[] sums,
        int sizeX,
        int sizeY,
        int sizeZ,
        int x0,
        int x1,
        int y0,
        int y1,
        int z0,
        int z1
    )
    {
        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        z0 = Math.Max(z0, 0);
        x1 = Math.Min(x1, sizeX - 1);
        y1 = Math.Min(y1, sizeY - 1);
        z1 = Math.Min(z1, sizeZ - 1);
        if (x0 > x1 || y0 > y1 || z0 > z1)
        {
            return 0;
        }

        int strideY = sizeX + 1,
            strideZ = (sizeX + 1) * (sizeY + 1);
        long Sum(int x, int y, int z) => sums[x + strideY * y + (long)strideZ * z];
        return Sum(x1 + 1, y1 + 1, z1 + 1)
            - Sum(x0, y1 + 1, z1 + 1)
            - Sum(x1 + 1, y0, z1 + 1)
            - Sum(x1 + 1, y1 + 1, z0)
            + Sum(x0, y0, z1 + 1)
            + Sum(x0, y1 + 1, z0)
            + Sum(x1 + 1, y0, z0)
            - Sum(x0, y0, z0);
    }
}
