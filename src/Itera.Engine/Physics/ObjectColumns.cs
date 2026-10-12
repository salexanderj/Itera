using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// The voxel objects a world places (<see cref="ObjectWorld"/>) as the character mover reads its cells: in columns of
/// its cells (the world's level-0 voxels), each a few spans of blocked cells, one for each run of cells whose centres,
/// turned into an object, land on its solid voxels that are not passable (<see cref="IVoxelPassability"/>: a tree's
/// leaves, where the game says so). A span keeps the height of its object's surface over the column's centre (the top of
/// the highest solid voxel of the object's column there under the span's top) and that voxel's material, so the mover
/// stands on a rock as on the ground and reads its material and tags. It answers what the voxel world and the mesh
/// colliders answer (<see cref="HighestTop"/>, <see cref="LowestBottom"/>, <see cref="BlockedCells"/>,
/// <see cref="ColumnTops"/>, <see cref="SurfaceOver"/>, <see cref="TopAt"/>), with the same values for none, so the mover
/// takes them as the ground's. The spans are made a chunk of 32 by 32 columns at a time, the first time a query reads
/// it, and kept until the objects change (<see cref="ObjectWorld.Version"/>) or one under it is damaged
/// (<see cref="ObjectDamage.ReadChanges"/>), at most <see cref="MostChunks"/>; a chunk
/// reads only the cells that can hold each object's column's solid voxels (a table of each kind's columns' lowest and
/// highest, made at the kind's first chunk). Making a chunk allocates until the cache holds that many and their arrays
/// have grown to the largest; every other read allocates nothing. Main thread.
/// </summary>
internal sealed class ObjectColumns
{
    /// <summary>The most chunks kept.</summary>
    public const int MostChunks = 256;

    private const int ChunkBits = 5;
    private const int ChunkSide = 1 << ChunkBits;

    private readonly ObjectWorld _objects;
    private readonly VoxelObjectKinds _kinds;
    private readonly MaterialSet _passable;
    private readonly int _cell;
    private readonly Dictionary<long, Chunk> _chunks = new(MostChunks);
    private readonly Stack<Chunk> _free = new();
    private readonly List<ObjectPlacement> _near = [];
    private readonly List<ObjectSpan>[] _columns = NewColumns();
    private readonly Dictionary<VoxelObjectKindId, ColumnRange[]> _ranges = [];
    private readonly Dictionary<VoxelObjectKindId, ColumnRange[]> _solidRanges = [];
    private readonly ObjectDamage? _damage;
    private readonly ObjectDamageChange[] _damageChanges = new ObjectDamageChange[64];
    private readonly List<long> _stale = [];
    private long _damageSeen;
    private int _version = -1;
    private long _reads;
    private Chunk? _last;
    private long _lastKey;

    /// <param name="objects">The match's placed objects.</param>
    /// <param name="kinds">The run's object kinds.</param>
    /// <param name="passability">The materials characters pass through.</param>
    /// <param name="cellCentimetres">The edge of the mover's cells: the world's level-0 voxels.</param>
    /// <param name="damage">What edits did to the objects, whose voxels are read through it; none without.</param>
    public ObjectColumns(
        ObjectWorld objects,
        VoxelObjectKinds kinds,
        IVoxelPassability passability,
        int cellCentimetres,
        ObjectDamage? damage = null
    )
    {
        _damage = damage;
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(passability);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCentimetres, 1);
        _objects = objects;
        _kinds = kinds;
        _passable = MaterialSet.Passable(passability);
        _cell = cellCentimetres;
    }

    /// <summary>True when there is no object near anything loaded: every query answers none.</summary>
    public bool Empty => _objects.Count == 0;

    /// <summary>The chunks of columns kept now.</summary>
    internal int ChunkCount => _chunks.Count;

    /// <summary>
    /// True when the objects that may stand in the cells from (<paramref name="x0"/>, <paramref name="z0"/>) to
    /// (<paramref name="x1"/>, <paramref name="z1"/>) are all known: the areas under them within the map, widened by the
    /// widest kind's reach, are filled (<see cref="ObjectWorld.IsKnown"/>). Always with no placer.
    /// </summary>
    public bool Known(int x0, int z0, int x1, int z1) =>
        _objects.IsKnown((long)x0 * _cell, (long)(x1 + 1) * _cell - 1, (long)z0 * _cell, (long)(z1 + 1) * _cell - 1);

    /// <summary>
    /// The top (one above the highest cell) of the highest cell an object blocks from y0 to y1 (inclusive) under
    /// <paramref name="footprint"/>, or <see cref="int.MinValue"/> when none does.
    /// </summary>
    public int HighestTop(in CircleFootprint footprint, int y0, int y1)
    {
        var best = int.MinValue;
        if (Empty)
        {
            return best;
        }

        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                best = Math.Max(best, ColumnTop(x, z, y0, y1));
            }
        }

        return best;
    }

    /// <summary>
    /// The bottom (its y) of the lowest cell an object blocks from y0 to y1 (inclusive) under
    /// <paramref name="footprint"/>, or <see cref="int.MaxValue"/> when none does.
    /// </summary>
    public int LowestBottom(in CircleFootprint footprint, int y0, int y1)
    {
        var best = int.MaxValue;
        if (Empty)
        {
            return best;
        }

        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                foreach (ref readonly var span in Spans(x, z))
                {
                    if (span.Hi >= y0 && span.Lo <= y1)
                    {
                        best = Math.Min(best, Math.Max(span.Lo, y0));
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Sets bit <c>x - windowX</c> of row <c>z - windowZ</c> of <paramref name="rows"/> for each cell of the window
    /// (<paramref name="width"/> cells, at most 128, by <c>rows.Length</c>) where an object blocks a cell from y0 to y1
    /// (inclusive); it adds to the bits already set.
    /// </summary>
    public void BlockedCells(int windowX, int windowZ, int width, int y0, int y1, Span<UInt128> rows)
    {
        if (Empty)
        {
            return;
        }

        for (var row = 0; row < rows.Length; row++)
        {
            var bits = UInt128.Zero;
            for (var i = 0; i < width; i++)
            {
                if (ColumnTop(windowX + i, windowZ + row, y0, y1) != int.MinValue)
                {
                    bits |= UInt128.One << i;
                }
            }

            rows[row] |= bits;
        }
    }

    /// <summary>
    /// For each column of the window (<paramref name="width"/> by <paramref name="depth"/>, index <c>x + width * z</c>),
    /// the top of the highest cell an object blocks from y0 to y1, or <see cref="int.MinValue"/>.
    /// </summary>
    public void ColumnTops(int windowX, int windowZ, int width, int depth, int y0, int y1, Span<int> tops)
    {
        for (var z = 0; z < depth; z++)
        {
            for (var x = 0; x < width; x++)
            {
                tops[x + width * z] = Empty ? int.MinValue : ColumnTop(windowX + x, windowZ + z, y0, y1);
            }
        }
    }

    /// <summary>
    /// The height, in cells, of the surface of the object whose top is <paramref name="top"/> in column (x, z): its
    /// surface over the column's centre, in the cell under the top; NaN when no object blocks that cell.
    /// </summary>
    public double SurfaceOver(int x, int top, int z)
    {
        var surface = double.NaN;
        foreach (ref readonly var span in Spans(x, z))
        {
            if (span.Lo > top - 1 || span.Hi < top - 1)
            {
                continue;
            }

            var height = Math.Min(span.Surface, top);
            surface = double.IsNaN(surface) ? height : Math.Max(surface, height);
        }

        return surface;
    }

    /// <summary>
    /// The highest cell of column (x, z) from y0 to y1 an object blocks, with the voxel material of the object's surface
    /// there; false for none.
    /// </summary>
    public bool TopAt(int x, int z, int y0, int y1, out int top, out byte material)
    {
        top = int.MinValue;
        material = VoxelConstants.Air;
        foreach (ref readonly var span in Spans(x, z))
        {
            if (span.Hi >= y0 && span.Lo <= y1 && Math.Min(span.Hi, y1) > top)
            {
                top = Math.Min(span.Hi, y1);
                material = span.Material;
            }
        }

        return material != VoxelConstants.Air;
    }

    /// <summary>The top of the highest cell blocked from y0 to y1 in column (x, z), or <see cref="int.MinValue"/>.</summary>
    private int ColumnTop(int x, int z, int y0, int y1)
    {
        var best = int.MinValue;
        foreach (ref readonly var span in Spans(x, z))
        {
            if (span.Hi >= y0 && span.Lo <= y1)
            {
                best = Math.Max(best, Math.Min(span.Hi, y1) + 1);
            }
        }

        return best;
    }

    /// <summary>The spans of column (x, z), its chunk made first when it is not yet.</summary>
    private ReadOnlySpan<ObjectSpan> Spans(int x, int z)
    {
        if (_objects.Version != _version)
        {
            _version = _objects.Version;
            foreach (var chunk in _chunks.Values)
            {
                _free.Push(chunk);
            }

            _chunks.Clear();
            _last = null;
        }

        if (_damage is not null && _damage.Version != _damageSeen)
        {
            FollowDamage();
        }

        var cx = x >> ChunkBits;
        var cz = z >> ChunkBits;
        var key = ((long)cx << 32) | (uint)cz;
        if (_last is null || _lastKey != key)
        {
            if (!_chunks.TryGetValue(key, out var found))
            {
                found = Build(cx, cz, Unused());
                _chunks[key] = found;
            }

            found.LastRead = ++_reads;
            _last = found;
            _lastKey = key;
        }

        var built = _last;
        var column = (x & (ChunkSide - 1)) | ((z & (ChunkSide - 1)) << ChunkBits);
        var start = built.Offsets[column];
        return built.Spans.AsSpan(start, built.Offsets[column + 1] - start);
    }

    /// <summary>
    /// Drops the chunks under each object whose voxels changed since the last read (every chunk when the changes ran
    /// ahead of it): they are made again at their next read.
    /// </summary>
    private void FollowDamage()
    {
        while (_damage!.Version != _damageSeen)
        {
            var read = _damage.ReadChanges(_damageSeen, _damageChanges, out var lost);
            if (lost)
            {
                _damageSeen = _damage.Version;
                foreach (var chunk in _chunks.Values)
                {
                    _free.Push(chunk);
                }

                _chunks.Clear();
                _last = null;
                return;
            }

            for (var i = 0; i < read; i++)
            {
                ref readonly var change = ref _damageChanges[i];
                _damageSeen = change.Version;
                // The chunks whose columns the object's reach meets.
                var side = (long)ChunkSide * _cell;
                long cx0 = DeterministicMath.FloorDivide(change.MinX, side),
                    cx1 = DeterministicMath.FloorDivide(change.MaxX, side),
                    cz0 = DeterministicMath.FloorDivide(change.MinZ, side),
                    cz1 = DeterministicMath.FloorDivide(change.MaxZ, side);
                _stale.Clear();
                foreach (var key in _chunks.Keys)
                {
                    long cx = key >> 32,
                        cz = (int)(uint)key;
                    if (cx >= cx0 && cx <= cx1 && cz >= cz0 && cz <= cz1)
                    {
                        _stale.Add(key);
                    }
                }

                foreach (var key in _stale)
                {
                    _free.Push(_chunks[key]);
                    _chunks.Remove(key);
                }
            }

            _last = null;
        }
    }

    /// <summary>A chunk to make: a freed one, a new one while fewer than <see cref="MostChunks"/> are kept, else the one read longest ago.</summary>
    private Chunk Unused()
    {
        if (_free.Count > 0)
        {
            return _free.Pop();
        }

        if (_chunks.Count < MostChunks)
        {
            return new Chunk();
        }

        var oldestKey = 0L;
        Chunk? oldest = null;
        foreach (var (key, chunk) in _chunks)
        {
            if (oldest is null || chunk.LastRead < oldest.LastRead)
            {
                oldestKey = key;
                oldest = chunk;
            }
        }

        _chunks.Remove(oldestKey);
        return oldest!;
    }

    /// <summary>The spans of every column of chunk (cx, cz), from the objects that reach into it.</summary>
    private Chunk Build(int cx, int cz, Chunk chunk)
    {
        int x0 = cx << ChunkBits,
            z0 = cz << ChunkBits;
        foreach (var column in _columns)
        {
            column.Clear();
        }

        _near.Clear();
        _objects.Near(
            (int)((long)x0 * _cell),
            (int)((long)(x0 + ChunkSide) * _cell - 1),
            (int)((long)z0 * _cell),
            (int)((long)(z0 + ChunkSide) * _cell - 1),
            _near
        );
        foreach (var placement in _near)
        {
            AddObject(in placement, x0, z0);
        }

        var count = 0;
        foreach (var column in _columns)
        {
            count += column.Count;
        }

        if (chunk.Spans.Length < count)
        {
            chunk.Spans = new ObjectSpan[Math.Max(count, 2 * chunk.Spans.Length)];
        }

        var at = 0;
        for (var i = 0; i < _columns.Length; i++)
        {
            chunk.Offsets[i] = at;
            foreach (var span in _columns[i])
            {
                chunk.Spans[at++] = span;
            }
        }

        chunk.Offsets[_columns.Length] = at;
        return chunk;
    }

    /// <summary>Adds an object's runs of blocked cells to each column of the chunk at (x0, z0) its reach meets.</summary>
    private void AddObject(in ObjectPlacement placement, int x0, int z0)
    {
        var kind = _kinds[placement.Kind];
        var anchor = placement.Anchor;
        var unit = kind.UnitCentimetres;
        var reach = kind.ReachCentimetres;
        // The columns and cells its box can meet.
        var cx0 = (int)Math.Max(x0, DeterministicMath.FloorDivide((long)anchor.X - reach, _cell));
        var cx1 = (int)Math.Min(x0 + ChunkSide - 1, DeterministicMath.FloorDivide((long)anchor.X + reach, _cell));
        var cz0 = (int)Math.Max(z0, DeterministicMath.FloorDivide((long)anchor.Z - reach, _cell));
        var cz1 = (int)Math.Min(z0 + ChunkSide - 1, DeterministicMath.FloorDivide((long)anchor.Z + reach, _cell));
        var y0 = (int)DeterministicMath.FloorDivide((long)anchor.Y + kind.BottomCentimetres, _cell);
        var y1 = (int)DeterministicMath.FloorDivide((long)anchor.Y + kind.TopCentimetres - 1, _cell);
        var half = _cell / 2.0;
        // A damaged object's voxels through its damage, within its kind's solid voxels, passable ones too: edits only
        // take voxels away, but a paint may make a passable one blocking.
        var damaged = _damage?.Find(placement.Id);
        IObjectVoxelSource voxels = (IObjectVoxelSource?)damaged ?? kind;
        var ranges = RangesOf(placement.Kind, kind, passableToo: damaged is not null);
        for (var z = cz0; z <= cz1; z++)
        {
            for (var x = cx0; x <= cx1; x++)
            {
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
                    continue;
                }

                // Only the cells that can hold the column's solid voxels.
                var range = ranges[vx + kind.Size.X * vz];
                if (range.Low > range.High)
                {
                    continue;
                }

                var from = (int)
                    Math.Max(
                        y0,
                        DeterministicMath.FloorDivide(
                            (long)range.Low * unit - kind.AnchorCentimetres.Y + anchor.Y,
                            _cell
                        )
                    );
                var to = (int)
                    Math.Min(
                        y1,
                        DeterministicMath.FloorDivide(
                            (long)(range.High + 1) * unit - 1 - kind.AnchorCentimetres.Y + anchor.Y,
                            _cell
                        )
                    );
                var column = _columns[(x - x0) | ((z - z0) << ChunkBits)];
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
    /// at its first read: so a chunk reads only the cells an object's column can block.
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

    private static List<ObjectSpan>[] NewColumns()
    {
        var columns = new List<ObjectSpan>[ChunkSide * ChunkSide];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = [];
        }

        return columns;
    }

    /// <summary>One run of blocked cells of a column: lowest and highest cell, the surface's height in cells, its material.</summary>
    private readonly record struct ObjectSpan(int Lo, int Hi, float Surface, byte Material);

    /// <summary>A kind's voxel column's lowest and highest solid voxel that blocks.</summary>
    private readonly record struct ColumnRange(short Low, short High);

    private sealed class Chunk
    {
        public readonly int[] Offsets = new int[ChunkSide * ChunkSide + 1];
        public ObjectSpan[] Spans = new ObjectSpan[64];
        public long LastRead;
    }
}
