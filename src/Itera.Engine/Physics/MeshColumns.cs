using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// The mesh colliders (<see cref="MeshColliders"/>) as the character mover reads its cells: in columns of its cells
/// (<c>cellsPerMetre</c> a metre, as the voxel world's level-0 voxels), each a few spans, one for each triangle that
/// crosses the column's square: the lowest and highest cell the triangle blocks inside the square, its height at the
/// square's centre (a wall's highest point over the square), and its tags. Only the cells a triangle passes through block: a closed mesh has no
/// inside. A triangle that lies on a cell's boundary blocks the cell behind it (away from its front, the side its
/// counter-clockwise winding faces): a floor exactly at y = k blocks the cell under it and a ceiling there the cell over
/// it, so a closed mesh of a voxel box blocks the box's outer voxels. It answers what the voxel world answers
/// (<see cref="HighestTop"/>, <see cref="LowestBottom"/>, <see cref="BlockedCells"/>, <see cref="ColumnTops"/>,
/// <see cref="SurfaceOver"/>), with the same values for none, so the mover takes the higher, the lower, or either of the
/// two. A column's surface is the plane at its centre, so it
/// lies anywhere in the column's top cell or under it, where the voxels' lies within half a cell of their top (the
/// mover reads surfaces, not tops, for both). The spans are made a chunk of 32 by 32 columns at a time, the first time a
/// query reads it, and kept until the colliders change, at most <see cref="MostChunks"/>: past that, the chunk read
/// longest ago is made again for the new one (bodies far apart whose windows need more than that at once make chunks
/// again at every read). Making a chunk allocates until the cache holds that many and their arrays have grown to the
/// largest; every other read allocates nothing. Main thread.
/// </summary>
internal sealed class MeshColumns(MeshColliders colliders, double cellsPerMetre)
{
    private const int ChunkBits = 5;
    private const int ChunkSide = 1 << ChunkBits;
    private const int ChunkColumns = ChunkSide * ChunkSide;

    /// <summary>
    /// The most chunks kept: about 1 to 30 KB each, and room for the widest body's window at 1 cm (13 by 13 chunks) and
    /// a few bodies apart.
    /// </summary>
    internal const int MostChunks = 256;

    /// <summary>
    /// How far a triangle is moved behind its front before it is cut into columns, in cells: one on a cell's boundary
    /// then lies in the cell behind it. It moves only along an axis it reaches less than this along (it lies flat at a
    /// cell's boundary or near one: a floor, a ceiling, or a wall along x or z), so a slope's, an overhang's, or a
    /// diagonal wall's edge on a boundary stays out of the cell past it. At 1 cm cells a float's step in metres reaches about a third
    /// of this 500 m from the middle, where a face on a boundary may block the cell in front of it instead.
    /// </summary>
    private const float BehindCells = 1e-2f;

    /// <summary>How far past a cell's boundary a triangle's part must reach to count as in that cell, in cells.</summary>
    private const float Epsilon = 1e-3f;

    /// <summary>A triangle steeper than this (its normal's y below it) is a wall: it has no height at a column's centre.</summary>
    private const float WallNormalY = 1e-3f;

    private readonly Dictionary<long, Chunk> _chunks = new(MostChunks);
    private readonly Stack<Chunk> _free = new();
    private readonly List<int> _triangles = [];
    private readonly List<(int Column, ColumnSpan Span)> _built = [];
    private int _version = -1;
    private Chunk? _last;
    private long _lastKey;
    private long _reads;

    /// <summary>True when there is no collider: every query answers none.</summary>
    public bool Empty => colliders.Count == 0;

    /// <summary>The cells per metre the columns are made in.</summary>
    public double CellsPerMetre => cellsPerMetre;

    /// <summary>The chunks of columns kept now, at most <see cref="MostChunks"/>.</summary>
    internal int ChunkCount => _chunks.Count;

    /// <summary>The colliders' version (<see cref="MeshColliders.Version"/>): it changes whenever one is added or removed.</summary>
    internal int Version => colliders.Version;

    /// <summary>True when the chunk that holds column (x, z) is kept now.</summary>
    internal bool Keeps(int x, int z) =>
        _version == colliders.Version && _chunks.ContainsKey(((long)(x >> ChunkBits) << 32) | (uint)(z >> ChunkBits));

    /// <summary>
    /// The top (one above the highest cell) of the highest cell a triangle blocks from y0 to y1 (inclusive) under
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
    /// The bottom (its y) of the lowest cell a triangle blocks from y0 to y1 (inclusive) under
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
    /// (<paramref name="width"/> cells, at most 128, by <c>rows.Length</c>) where a triangle blocks a cell from y0 to
    /// y1 (inclusive); it adds to the bits already set.
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
    /// the top of the highest cell a triangle blocks from y0 to y1, or <see cref="int.MinValue"/>.
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
    /// The height, in cells, of the surface of the ground whose top is <paramref name="top"/> in column (x, z): the highest
    /// height at the column's centre of the triangles that block the cell under the top (no higher than the top, and lower
    /// than that cell where a slope rises across the column), a wall's its highest point over the column; NaN when no
    /// triangle blocks that cell.
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

            var height = Math.Min(span.Lo + (double)span.Surface, top);
            surface = double.IsNaN(surface) ? height : Math.Max(surface, height);
        }

        return surface;
    }

    /// <summary>The tags of the highest triangle blocking a cell of column (x, z) from y0 to y1; 0 for none.</summary>
    public uint TagsAt(int x, int z, int y0, int y1) => TopAt(x, z, y0, y1, out _, out var tags, out _) ? tags : 0;

    /// <summary>
    /// The highest cell of column (x, z) from y0 to y1 a triangle blocks, with the triangle's tags and its collider
    /// (<see cref="MeshColliders"/>); false for none.
    /// </summary>
    public bool TopAt(int x, int z, int y0, int y1, out int top, out uint tags, out int collider)
    {
        top = int.MinValue;
        tags = 0;
        collider = -1;
        foreach (ref readonly var span in Spans(x, z))
        {
            if (span.Hi >= y0 && span.Lo <= y1 && Math.Min(span.Hi, y1) > top)
            {
                top = Math.Min(span.Hi, y1);
                tags = span.Tags;
                collider = span.Collider;
            }
        }

        return collider >= 0;
    }

    /// <summary>The physics material of collider <paramref name="collider"/>, read now (a game may change it).</summary>
    public PhysicsMaterialId MaterialOf(int collider) => colliders.SettingsOf(collider).Material;

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
    private ReadOnlySpan<ColumnSpan> Spans(int x, int z)
    {
        if (colliders.Version != _version)
        {
            _version = colliders.Version;
            foreach (var chunk in _chunks.Values)
            {
                _free.Push(chunk);
            }

            _chunks.Clear();
            _last = null;
        }

        var cx = x >> ChunkBits;
        var cz = z >> ChunkBits;
        var key = ((long)cx << 32) | (uint)cz;
        // A query reads its columns in order: most reads are of the chunk read last.
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
    /// A chunk to make: a freed one, a new one while fewer than <see cref="MostChunks"/> are kept, else the one read
    /// longest ago, taken out of the cache.
    /// </summary>
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

    /// <summary>The spans of every column of chunk (cx, cz), from the triangles over it, written into <paramref name="chunk"/>.</summary>
    private Chunk Build(int cx, int cz, Chunk chunk)
    {
        int x0 = cx << ChunkBits,
            z0 = cz << ChunkBits;
        var metres = 1 / cellsPerMetre;
        colliders.Triangles(x0 * metres, z0 * metres, (x0 + ChunkSide) * metres, (z0 + ChunkSide) * metres, _triangles);
        _built.Clear();
        // A triangle cut by four sides has at most 7 corners; room for more where rounding makes a corner cross twice.
        Span<Vector3> polygon = stackalloc Vector3[16];
        Span<Vector3> scratch = stackalloc Vector3[16];
        foreach (var triangle in _triangles)
        {
            // In cells from the chunk's corner across and from a whole cell at the collider's anchor up, so the small
            // shift behind the front is not lost to rounding at any place or height.
            colliders.Triangle(
                triangle,
                out var ma,
                out var mb,
                out var mc,
                out var anchor,
                out var tags,
                out var collider
            );
            var y0 = (int)Math.Floor(anchor.Y / (double)VoxelConstants.CentimetresPerMetre * cellsPerMetre);
            var a = Local(ma, anchor, x0, y0, z0);
            var b = Local(mb, anchor, x0, y0, z0);
            var c = Local(mc, anchor, x0, y0, z0);
            var normal = Vector3.Cross(b - a, c - a);
            var length = normal.Length();
            if (!(length > 0))
            {
                continue;
            }

            normal /= length;
            var wall = MathF.Abs(normal.Y) < WallNormalY;
            var size = Vector3.Max(a, Vector3.Max(b, c)) - Vector3.Min(a, Vector3.Min(b, c));
            var back =
                new Vector3(
                    size.X < BehindCells ? MathF.Sign(normal.X) : 0,
                    size.Y < BehindCells ? MathF.Sign(normal.Y) : 0,
                    size.Z < BehindCells ? MathF.Sign(normal.Z) : 0
                ) * BehindCells;
            Vector3 sa = a - back,
                sb = b - back,
                sc = c - back;
            var min = Vector3.Min(sa, Vector3.Min(sb, sc));
            var max = Vector3.Max(sa, Vector3.Max(sb, sc));
            // At least the column a triangle with no width across lies in (a wall within Epsilon of a boundary).
            var firstX = (int)MathF.Floor(min.X + Epsilon);
            var firstZ = (int)MathF.Floor(min.Z + Epsilon);
            int fromX = Math.Max(0, firstX),
                toX = Math.Min(ChunkSide - 1, Math.Max(firstX, (int)MathF.Ceiling(max.X - Epsilon) - 1)),
                fromZ = Math.Max(0, firstZ),
                toZ = Math.Min(ChunkSide - 1, Math.Max(firstZ, (int)MathF.Ceiling(max.Z - Epsilon) - 1));
            for (var z = fromZ; z <= toZ; z++)
            {
                for (var x = fromX; x <= toX; x++)
                {
                    if (!Clip(sa, sb, sc, x, z, polygon, scratch, out var lo, out var hi))
                    {
                        continue;
                    }

                    // A wall's highest point over the square.
                    var surface = hi;
                    if (!wall)
                    {
                        // The triangle's own plane (not moved) at the column's centre, kept to what of it lies over
                        // the square.
                        var height = a.Y - (normal.X * (x + 0.5f - a.X) + normal.Z * (z + 0.5f - a.Z)) / normal.Y;
                        surface = Math.Clamp(height, lo - BehindCells, hi + BehindCells);
                    }

                    var cellLo = (int)MathF.Floor(lo + Epsilon);
                    var cellHi = Math.Max(cellLo, (int)MathF.Ceiling(hi - Epsilon) - 1);
                    _built.Add(
                        (
                            x | (z << ChunkBits),
                            new ColumnSpan(y0 + cellLo, y0 + cellHi, surface - cellLo, tags, collider)
                        )
                    );
                }
            }
        }

        // Grouped by column, as offsets into one array.
        Array.Clear(chunk.Offsets);
        foreach (var (column, _) in _built)
        {
            chunk.Offsets[column + 1]++;
        }

        for (var i = 1; i <= ChunkColumns; i++)
        {
            chunk.Offsets[i] += chunk.Offsets[i - 1];
        }

        if (chunk.Spans.Length < _built.Count)
        {
            chunk.Spans = new ColumnSpan[Math.Max(_built.Count, chunk.Spans.Length * 2)];
        }

        Span<int> filled = stackalloc int[ChunkColumns];
        foreach (var (column, span) in _built)
        {
            chunk.Spans[chunk.Offsets[column] + filled[column]++] = span;
        }

        return chunk;
    }

    /// <summary>
    /// A point given in metres from <paramref name="anchor"/> (absolute centimetres), in cells from the absolute cell
    /// (x0, y0, z0): a chunk's corner across and a whole cell near the anchor up.
    /// </summary>
    private Vector3 Local(Vector3 metres, VoxelCoord anchor, int x0, int y0, int z0)
    {
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        return new(
            (float)((anchor.X / perMetre + metres.X) * cellsPerMetre - x0),
            (float)((anchor.Y / perMetre + metres.Y) * cellsPerMetre - y0),
            (float)((anchor.Z / perMetre + metres.Z) * cellsPerMetre - z0)
        );
    }

    /// <summary>
    /// The part of triangle a, b, c over the square of column (x, z) (Sutherland and Hodgman's clip against its four
    /// sides): false when none is, or when it only touches the square (it reaches less than <see cref="Epsilon"/> across
    /// it), else its lowest and highest y.
    /// </summary>
    private static bool Clip(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        int x,
        int z,
        Span<Vector3> polygon,
        Span<Vector3> scratch,
        out float lo,
        out float hi
    )
    {
        lo = hi = 0;
        polygon[0] = a;
        polygon[1] = b;
        polygon[2] = c;
        var count = 3;
        count = ClipSide(polygon, count, scratch, axis: 0, x, keepAbove: true);
        count = ClipSide(scratch, count, polygon, axis: 0, x + 1, keepAbove: false);
        count = ClipSide(polygon, count, scratch, axis: 2, z, keepAbove: true);
        count = ClipSide(scratch, count, polygon, axis: 2, z + 1, keepAbove: false);
        if (count == 0)
        {
            return false;
        }

        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        for (var i = 0; i < count; i++)
        {
            low = Vector3.Min(low, polygon[i]);
            high = Vector3.Max(high, polygon[i]);
        }

        lo = low.Y;
        hi = high.Y;
        return high.X - low.X + (high.Z - low.Z) > Epsilon;
    }

    /// <summary>Keeps the part of a polygon on one side of the plane where x (axis 0) or z (axis 2) is <paramref name="at"/>.</summary>
    private static int ClipSide(
        ReadOnlySpan<Vector3> input,
        int count,
        Span<Vector3> output,
        int axis,
        float at,
        bool keepAbove
    )
    {
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var current = input[i];
            var next = input[(i + 1) % count];
            var c = axis == 0 ? current.X : current.Z;
            var n = axis == 0 ? next.X : next.Z;
            var currentIn = keepAbove ? c >= at : c <= at;
            var nextIn = keepAbove ? n >= at : n <= at;
            if (currentIn && written < output.Length)
            {
                output[written++] = current;
            }

            if (currentIn != nextIn && c != n && written < output.Length)
            {
                output[written++] = Vector3.Lerp(current, next, (at - c) / (n - c));
            }
        }

        return written;
    }

    /// <summary>
    /// One triangle's part over a column: the lowest and highest cell it blocks, its height at the centre in cells over
    /// the lowest (a wall's highest point; kept from it, so a float holds it to a ten-thousandth of a cell at any height),
    /// and its tags.
    /// </summary>
    private readonly record struct ColumnSpan(int Lo, int Hi, float Surface, uint Tags, int Collider);

    /// <summary>The spans of a chunk's columns: column i's from <c>Offsets[i]</c> to <c>Offsets[i + 1]</c>.</summary>
    private sealed class Chunk
    {
        /// <summary>When it was last read (a count of chunk changes).</summary>
        public long LastRead { get; set; }

        public int[] Offsets { get; } = new int[ChunkColumns + 1];

        public ColumnSpan[] Spans { get; set; } = new ColumnSpan[64];
    }
}
