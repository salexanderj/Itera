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
/// takes each object's columns from its kind's columns' runs, a damaged one's read through its damage over its kind's
/// solid voxels (<see cref="ObjectColumnReader"/>, which navigation's reading of the objects as generated shares). Making a
/// chunk allocates until the cache holds that many and
/// their arrays have grown to the largest; every other read allocates nothing. Main thread: the mover and navigation
/// share it (<c>VoxelCharacterMover.Objects</c>).
/// </summary>
internal sealed class ObjectColumns
{
    /// <summary>The most chunks kept.</summary>
    public const int MostChunks = 256;

    private const int ChunkBits = 5;
    private const int ChunkSide = 1 << ChunkBits;

    private readonly ObjectWorld _objects;
    private readonly VoxelObjectKinds _kinds;
    private readonly ObjectColumnReader _reader;
    private readonly int _cell;
    private readonly Dictionary<long, Chunk> _chunks = new(MostChunks);
    private readonly Stack<Chunk> _free = new();
    private readonly List<ObjectPlacement> _near = [];
    private readonly List<ObjectSpan>[] _columns = NewColumns();
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
        _reader = new ObjectColumnReader(MaterialSet.Passable(passability), cellCentimetres);
        _cell = cellCentimetres;
    }

    /// <summary>True when there is no object near anything loaded: every query answers none.</summary>
    public bool Empty => _objects.Count == 0;

    /// <summary>The chunks of columns kept now.</summary>
    internal int ChunkCount => _chunks.Count;

    /// <summary>The edge of its cells, in centimetres: the mover's.</summary>
    public int CellCentimetres => _cell;

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
                // The chunks whose columns the change's box meets: what an edit changed, or the object's whole reach.
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
        var reach = kind.ReachCentimetres;
        // The columns its box can meet.
        var cx0 = (int)Math.Max(x0, DeterministicMath.FloorDivide((long)anchor.X - reach, _cell));
        var cx1 = (int)Math.Min(x0 + ChunkSide - 1, DeterministicMath.FloorDivide((long)anchor.X + reach, _cell));
        var cz0 = (int)Math.Max(z0, DeterministicMath.FloorDivide((long)anchor.Z - reach, _cell));
        var cz1 = (int)Math.Min(z0 + ChunkSide - 1, DeterministicMath.FloorDivide((long)anchor.Z + reach, _cell));
        // A damaged object's voxels through its damage.
        var damaged = _damage?.Find(placement.Id);
        IObjectVoxelSource voxels = (IObjectVoxelSource?)damaged ?? kind;
        for (var z = cz0; z <= cz1; z++)
        {
            for (var x = cx0; x <= cx1; x++)
            {
                _reader.AddColumn(
                    in placement,
                    kind,
                    voxels,
                    damaged is not null,
                    x,
                    z,
                    _columns[(x - x0) | ((z - z0) << ChunkBits)]
                );
            }
        }
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

    private sealed class Chunk
    {
        public readonly int[] Offsets = new int[ChunkSide * ChunkSide + 1];
        public ObjectSpan[] Spans = new ObjectSpan[64];
        public long LastRead;
    }
}
