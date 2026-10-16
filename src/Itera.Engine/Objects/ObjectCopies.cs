using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// A world's objects as coarse copies in its own voxels, from the hand-over level up (<c>Engine:Objects:HandOverLevel</c>),
/// at their own size: for each kind and each of those levels whose voxels are no wider than the kind's
/// <see cref="VoxelObjectKind.CopiesUpToCentimetres"/>, a grid of how much of each block half a world voxel across the
/// kind fills (<see cref="ObjectShareGrid"/>); the column jobs find the objects near a column (<see cref="Find"/>) and
/// write into its chunks' air how much of each world voxel each fills at its heading (<see cref="Shares"/>), its density,
/// solid of the commonest material from a half, a thin upright part kept a voxel wide (<see cref="KeepThin"/>), after the
/// generator's fill and before the edits (<see cref="Stamp"/>). Built on the main thread before the workers start; read
/// by them all at once. Integers only, so every peer's copies are the same.
/// </summary>
internal sealed class ObjectCopies
{
    /// <summary>
    /// The version of the rule that writes copies (the lattice, its margins, the turns, the air they fill): part of the
    /// kinds' fingerprint, so builds whose copies differ do not join each other. Raise it with any change to the copies.
    /// </summary>
    public const int RuleVersion = 2;

    /// <summary>The least share (of 255) a voxel of a thin upright part has to be kept: a 64th.</summary>
    public const byte ThinShare = 4;

    /// <summary>The share a kept voxel of a thin upright part is given: solid, a little over half.</summary>
    public const byte KeptShare = 160;

    [ThreadStatic]
    private static byte[]? t_shares;

    [ThreadStatic]
    private static byte[]? t_materials;

    [ThreadStatic]
    private static byte[]? t_marks;

    private readonly IObjectPlacer _placer;
    private readonly VoxelObjectKinds _kinds;
    private readonly VoxelGrid _grid;
    private readonly ObjectShareGrid?[][] _copies;
    private readonly int[] _reaches;

    private ObjectCopies(
        IObjectPlacer placer,
        VoxelObjectKinds kinds,
        VoxelGrid grid,
        ObjectShareGrid?[][] copies,
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
        var copies = new ObjectShareGrid?[kinds.Count][];
        var first = int.MaxValue;
        var last = -1;
        var materials = default(MaterialSet);
        var reaches = new int[levels];
        for (var k = 0; k < kinds.Count; k++)
        {
            var kind = kinds[new VoxelObjectKindId((ushort)k)];
            materials = materials.Union(kind.Materials);
            copies[k] = new ObjectShareGrid?[levels];
            var count = 0;
            while (handOverLevel + count < levels && grid.Edge(handOverLevel + count) <= kind.CopiesUpToCentimetres)
            {
                count++;
            }

            var grids = count > 0 ? kind.SharesFrom(grid.Edge(handOverLevel), count) : [];
            for (var level = handOverLevel; level < handOverLevel + count; level++)
            {
                var copy = grids[level - handOverLevel];
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

    /// <summary>True when <paramref name="kind"/> is copied at any level. Any thread.</summary>
    public bool IsCopied(VoxelObjectKindId kind)
    {
        for (var level = FirstLevel; level <= LastLevel; level++)
        {
            if (GridFor(kind, level) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The copy of <paramref name="kind"/> at <paramref name="level"/>, or null where it is not copied. Any thread.</summary>
    public ObjectShareGrid? GridFor(VoxelObjectKindId kind, int level) =>
        level >= FirstLevel && level <= LastLevel ? _copies[kind.Value][level] : null;

    /// <summary>The world's grid.</summary>
    public VoxelGrid Grid => _grid;

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
    /// <c>x + 128y + 16384z</c>): each world voxel's share of each object (<see cref="Shares"/>, its thin upright parts
    /// kept, <see cref="KeepThin"/>) from a half makes an air voxel solid of the object's material at that density, and
    /// under a half raises an air voxel's density to it (so the surface lies where the object's does); ground and what
    /// an object earlier in the list made solid stay. Each object's shares are worked out over the chunk's part of its
    /// range and a voxel round it (all <see cref="KeepThin"/> reads), as over its whole range. Returns how many voxels it
    /// changed (made solid, or raised). Any thread.
    /// </summary>
    public int Stamp(ChunkKey chunk, ReadOnlySpan<ObjectPlacement> placements, Span<byte> voxels, Span<byte> densities)
    {
        const int size = VoxelConstants.ChunkSize;
        var edge = _grid.Edge(chunk.Level);
        var first = chunk.LevelOrigin;
        var written = 0;
        foreach (ref readonly var placement in placements)
        {
            var copy = _copies[placement.Kind.Value][chunk.Level]!;
            var (x0, y0, z0, sx, sy, sz) = RangeOf(copy, in placement, edge);
            // The chunk's part of the range.
            int fx = Math.Max(x0, first.X),
                fy = Math.Max(y0, first.Y),
                fz = Math.Max(z0, first.Z),
                ex = Math.Min(x0 + sx, first.X + size),
                ey = Math.Min(y0 + sy, first.Y + size),
                ez = Math.Min(z0 + sz, first.Z + size);
            if (fx >= ex || fy >= ey || fz >= ez)
            {
                continue;
            }

            // The chunk's part and a voxel round it within the range, so a thin part's neighbours past the chunk count too.
            int cx0 = Math.Max(x0, fx - 1),
                cy0 = Math.Max(y0, fy - 1),
                cz0 = Math.Max(z0, fz - 1),
                csx = Math.Min(x0 + sx, ex + 1) - cx0,
                csy = Math.Min(y0 + sy, ey + 1) - cy0,
                csz = Math.Min(z0 + sz, ez + 1) - cz0;
            var count = csx * csy * csz;
            var shares = Scratch(ref t_shares, count);
            var materials = Scratch(ref t_materials, count);
            var marks = Scratch(ref t_marks, count);
            Shares(
                copy,
                _kinds[placement.Kind].AnchorCentimetres,
                in placement,
                edge,
                cx0,
                cy0,
                cz0,
                csx,
                csy,
                csz,
                shares,
                materials
            );
            KeepThin(shares, materials, csx, csy, csz, marks);
            (x0, y0, z0, sx, sy) = (cx0, cy0, cz0, csx, csy);
            for (var z = fz; z < ez; z++)
            {
                for (var y = fy; y < ey; y++)
                {
                    for (var x = fx; x < ex; x++)
                    {
                        var at = x - x0 + sx * (y - y0 + sy * (z - z0));
                        var share = shares[at];
                        var index = x - first.X + size * (y - first.Y + size * (z - first.Z));
                        if (share == 0 || voxels[index] != VoxelConstants.Air)
                        {
                            continue;
                        }

                        if (share >= VoxelDensity.Solid && materials[at] != VoxelConstants.Air)
                        {
                            voxels[index] = materials[at];
                            densities[index] = VoxelDensity.Fit(materials[at], share);
                            written++;
                        }
                        else if (share > densities[index])
                        {
                            densities[index] = VoxelDensity.Fit(VoxelConstants.Air, share);
                            written++;
                        }
                    }
                }
            }
        }

        return written;
    }

    /// <summary>
    /// The world voxels of one level a copy may fill round <paramref name="placement"/>: its box turned by its heading, and
    /// a voxel round it, as absolute voxel indices of its corner and the count along each axis.
    /// </summary>
    internal static (int X, int Y, int Z, int SizeX, int SizeY, int SizeZ) RangeOf(
        ObjectShareGrid copy,
        in ObjectPlacement placement,
        int edge
    )
    {
        var anchor = placement.Anchor;
        var box = copy.BoxCentimetres;
        var offset = copy.Anchor;
        long wx0 = long.MaxValue,
            wx1 = long.MinValue,
            wz0 = long.MaxValue,
            wz1 = long.MinValue;
        for (var corner = 0; corner < 4; corner++)
        {
            var (wx, wz) = ObjectTurns.FromObject(
                placement.Heading,
                ((corner & 1) == 0 ? 0 : box.X) - (long)offset.X,
                ((corner & 2) == 0 ? 0 : box.Z) - (long)offset.Z
            );
            (wx0, wx1) = (Math.Min(wx0, wx), Math.Max(wx1, wx));
            (wz0, wz1) = (Math.Min(wz0, wz), Math.Max(wz1, wz));
        }

        // A voxel past the turned box (the turns round to a centimetre or two).
        var x0 = (int)DeterministicMath.FloorDivide(anchor.X + wx0 - edge, edge);
        var x1 = (int)DeterministicMath.FloorDivide(anchor.X + wx1 + edge, edge);
        var z0 = (int)DeterministicMath.FloorDivide(anchor.Z + wz0 - edge, edge);
        var z1 = (int)DeterministicMath.FloorDivide(anchor.Z + wz1 + edge, edge);
        var y0 = (int)DeterministicMath.FloorDivide((long)anchor.Y + copy.LowCentimetres, edge);
        var y1 = (int)DeterministicMath.FloorDivide((long)anchor.Y + copy.HighCentimetres - 1, edge);
        return (x0, y0, z0, x1 - x0 + 1, y1 - y0 + 1, z1 - z0 + 1);
    }

    /// <summary>
    /// How much of each world voxel <paramref name="edge"/> centimetres across, from absolute voxel (x0, y0, z0),
    /// <paramref name="sizeX"/> by <paramref name="sizeY"/> by <paramref name="sizeZ"/> (index <c>x + sizeX (y + sizeY z)</c>),
    /// the object at <paramref name="placement"/> fills by <paramref name="grid"/> (its kind's, or a snapshot's), 0 to 255:
    /// the blocks at the middles of the voxel's eight half-size cubes, turned into the object
    /// (<see cref="ObjectTurns.ToObject"/>), read first; a voxel whose eight blocks have nothing, or all full, within a
    /// block of them (<see cref="ObjectShareGrid.AroundAt"/>) is that, and one on the object's edge is the mean of the
    /// blocks at the middles of its 64 quarter-size cubes (so a turned lattice does not alias); and the material of the fullest block of the first eight that holds one (the first of two as full), else
    /// air. <paramref name="anchor"/> is the kind's anchor in its box. Any thread; allocates nothing.
    /// </summary>
    internal static void Shares(
        ObjectShareGrid grid,
        VoxelCoord anchor,
        in ObjectPlacement placement,
        int edge,
        int x0,
        int y0,
        int z0,
        int sizeX,
        int sizeY,
        int sizeZ,
        Span<byte> shares,
        Span<byte> materials
    )
    {
        var at = placement.Anchor;
        long cos = ObjectTurns.Cos(placement.Heading),
            sin = ObjectTurns.Sin(placement.Heading);
        // ObjectTurns.ToObject of a voxel's corner plus a sample's offset: the corner's products and the offset's, added
        // before the shift, as ToObject's are, so the turned points are its own. Offsets 0 to 1 are the eighths' middles
        // (a quarter and three quarters in), 2 to 5 the quarter cubes'.
        Span<long> offsets = stackalloc long[6];
        offsets[0] = edge / 4;
        offsets[1] = edge * 3 / 4;
        for (var k = 0; k < 4; k++)
        {
            offsets[2 + k] = Quarter(edge, k);
        }

        Span<long> offsetCos = stackalloc long[6];
        Span<long> offsetSin = stackalloc long[6];
        Span<long> rows = stackalloc long[6];
        for (var k = 0; k < 6; k++)
        {
            offsetCos[k] = offsets[k] * cos;
            offsetSin[k] = offsets[k] * sin;
        }

        long middleCos = edge / 2 * cos,
            middleSin = edge / 2 * sin;
        for (var z = 0; z < sizeZ; z++)
        {
            long dz = (long)(z0 + z) * edge - at.Z;
            long zCos = dz * cos,
                zSin = dz * sin;
            for (var y = 0; y < sizeY; y++)
            {
                long oy = (long)(y0 + y) * edge - at.Y + anchor.Y;
                for (var k = 0; k < 6; k++)
                {
                    rows[k] = grid.BlockOf(oy + offsets[k]);
                }

                var middleRow = oy + edge / 2;
                for (var x = 0; x < sizeX; x++)
                {
                    long dx = (long)(x0 + x) * edge - at.X;
                    long xCos = dx * cos,
                        xSin = dx * sin;
                    var i = x + sizeX * (y + sizeY * z);
                    // Nothing of the object near the voxel's middle: none in it.
                    var mx = ((xCos + middleCos + zSin + middleSin) >> ObjectTurns.Shift) + anchor.X;
                    var mz = ((zCos + middleCos - xSin - middleSin) >> ObjectTurns.Shift) + anchor.Z;
                    if (!grid.AnyNear(mx, middleRow, mz))
                    {
                        shares[i] = 0;
                        materials[i] = VoxelConstants.Air;
                        continue;
                    }

                    int sum = 0,
                        best = 0;
                    bool empty = true,
                        full = true;
                    var material = VoxelConstants.Air;
                    for (var corner = 0; corner < 8; corner++)
                    {
                        int kx = corner & 1,
                            ky = (corner >> 1) & 1,
                            kz = corner >> 2;
                        var share = grid.ReadBlock(
                            grid.BlockOf(
                                ((xCos + offsetCos[kx] + zSin + offsetSin[kz]) >> ObjectTurns.Shift) + anchor.X
                            ),
                            rows[ky],
                            grid.BlockOf(
                                ((zCos + offsetCos[kz] - xSin - offsetSin[kx]) >> ObjectTurns.Shift) + anchor.Z
                            ),
                            out var held,
                            out var around
                        );
                        sum += share;
                        empty &= around == ObjectShareGrid.Empty;
                        full &= around == ObjectShareGrid.Full;
                        if (held != VoxelConstants.Air && share > best)
                        {
                            best = share;
                            material = held;
                        }
                    }

                    materials[i] = material;
                    if (empty || full)
                    {
                        // Nothing, or all full, within a block of every eighth: the voxel holds that.
                        shares[i] = empty ? (byte)0 : VoxelDensity.Full;
                        continue;
                    }

                    // On the object's edge: the 64 quarter-size cubes' middles.
                    sum = 0;
                    for (var cube = 0; cube < 64; cube++)
                    {
                        int qx = 2 + (cube & 3),
                            qy = 2 + ((cube >> 2) & 3),
                            qz = 2 + (cube >> 4);
                        sum += grid.ReadBlock(
                            grid.BlockOf(
                                ((xCos + offsetCos[qx] + zSin + offsetSin[qz]) >> ObjectTurns.Shift) + anchor.X
                            ),
                            rows[qy],
                            grid.BlockOf(
                                ((zCos + offsetCos[qz] - xSin - offsetSin[qx]) >> ObjectTurns.Shift) + anchor.Z
                            ),
                            out _,
                            out _
                        );
                    }

                    shares[i] = (byte)(sum / 64);
                }
            }
        }
    }

    // The middle of quarter k (0 to 3) of a voxel edge centimetres across, from its low side.
    private static int Quarter(int edge, int k) => (edge * (2 * k + 1)) / 8;

    /// <summary>
    /// Keeps a thin upright part (a trunk, a pole) at least a voxel wide in <paramref name="shares"/>: each voxel with a
    /// material whose share is at least <see cref="ThinShare"/>, with none of its eight neighbours across greater (ties
    /// all kept) and some share in the voxels above and below it, is made at least <see cref="KeptShare"/>, solid. Caps,
    /// rims, and broad parts are left as they are. <paramref name="marks"/> is scratch of the same size. Any thread;
    /// allocates nothing.
    /// </summary>
    internal static void KeepThin(
        Span<byte> shares,
        ReadOnlySpan<byte> materials,
        int sizeX,
        int sizeY,
        int sizeZ,
        Span<byte> marks
    )
    {
        var count = sizeX * sizeY * sizeZ;
        marks[..count].Clear();
        for (var z = 0; z < sizeZ; z++)
        {
            for (var y = 1; y < sizeY - 1; y++)
            {
                for (var x = 0; x < sizeX; x++)
                {
                    var i = x + sizeX * (y + sizeY * z);
                    var share = shares[i];
                    if (
                        share < ThinShare
                        || share >= KeptShare
                        || materials[i] == VoxelConstants.Air
                        || shares[i - sizeX] == 0
                        || shares[i + sizeX] == 0
                    )
                    {
                        continue;
                    }

                    var greatest = true;
                    for (var dz = -1; dz <= 1 && greatest; dz++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx,
                                nz = z + dz;
                            if (
                                (dx != 0 || dz != 0)
                                && (uint)nx < (uint)sizeX
                                && (uint)nz < (uint)sizeZ
                                && shares[nx + sizeX * (y + sizeY * nz)] > share
                            )
                            {
                                greatest = false;
                                break;
                            }
                        }
                    }

                    marks[i] = greatest ? (byte)1 : (byte)0;
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (marks[i] != 0)
            {
                shares[i] = KeptShare;
            }
        }
    }

    /// <summary>This thread's scratch of at least <paramref name="count"/> bytes.</summary>
    private static Span<byte> Scratch(ref byte[]? array, int count)
    {
        if (array is null || array.Length < count)
        {
            array = new byte[Math.Max(count, 2 * (array?.Length ?? 1024))];
        }

        return array.AsSpan(0, count);
    }

    // The centimetres a placement's copy at the level reaches from bottom to top, inclusive.
    private (long Low, long High) HeightsOf(int level, in ObjectPlacement placement)
    {
        var copy = _copies[placement.Kind.Value][level]!;
        return ((long)placement.Anchor.Y + copy.LowCentimetres, (long)placement.Anchor.Y + copy.HighCentimetres - 1);
    }
}
