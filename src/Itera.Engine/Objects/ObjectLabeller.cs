using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>Which of a cut object's pieces stay (<see cref="ObjectLabeller"/>); the rest fall.</summary>
public enum ObjectRoots : byte
{
    /// <summary>
    /// The pieces that hold a root voxel: a solid voxel at the object's foot, whose bottom lies at or below its anchor (a
    /// tree's trunk in the ground, a rock's base). A kind with no such voxel keeps its largest piece instead.
    /// </summary>
    Ground = 0,

    /// <summary>The largest piece (by voxels; of two as large, the one whose first voxel comes first): an object standing on nothing of its own (a prop).</summary>
    Largest = 1,
}

/// <summary>
/// One piece of a damaged object to fall (<see cref="ObjectLabeller"/>): its first solid voxel in scan order (x fastest,
/// then y, then z), the seed its removal floods from, and its bounds, in the kind's voxels, inclusive.
/// </summary>
internal readonly record struct FallingPiece(VoxelCoord Seed, VoxelCoord Min, VoxelCoord Max);

/// <summary>
/// A damaged object's voxels as they were when a label was asked for: its kind's, with a copy of its changed bricks
/// over them (<see cref="DamagedObject"/>), read on the labeller's thread while the object changes on the main thread.
/// </summary>
internal sealed class LabelSnapshot : IObjectVoxelSource
{
    private const int BrickBytes = 2 * VoxelConstants.BrickSize * VoxelConstants.BrickSize * VoxelConstants.BrickSize;
    private readonly Dictionary<int, int> _brickAt = [];
    private byte[] _payload = [];

    public ObjectPlacement Placement { get; private set; }

    public VoxelObjectKind Kind { get; private set; } = null!;

    /// <summary>The object's <see cref="DamagedObject.Revision"/> when copied.</summary>
    public int Revision { get; private set; }

    /// <summary>The copy of the object it was taken from: an answer is for that copy alone, not a later one.</summary>
    public DamagedObject? Source { get; private set; }

    /// <summary>The pieces to fall, filled on the labeller's thread.</summary>
    public List<FallingPiece> Falling { get; } = [];

    /// <summary>What the labeller's thread threw while labelling it (then nothing falls), or null.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// The world's copies of objects (null without), whose voxels only the falling pieces fill are cleared
    /// (<see cref="Clears"/>).
    /// </summary>
    public ObjectCopies? Copies { get; private set; }

    /// <summary>The run's kinds, for the neighbours'.</summary>
    public VoxelObjectKinds? Kinds { get; private set; }

    /// <summary>The objects whose reach meets the object's (itself among them), whose copies a clear keeps. Main thread.</summary>
    public List<ObjectPlacement> Neighbours { get; } = [];

    /// <summary>The clears of the falling pieces' far copies (<see cref="VoxelEditOperation.ClearCopies"/>), filled on the thread.</summary>
    public List<VoxelEdit> Clears { get; } = [];

    /// <summary>Drops what keeps the copy and its neighbours alive while the snapshot waits in the pool.</summary>
    internal void Forget()
    {
        Source = null;
        Failure = null;
        Neighbours.Clear();
    }

    /// <summary>Copies the object's changed bricks, with the world's copies and the kinds. Main thread.</summary>
    public void Copy(DamagedObject damaged, ObjectCopies? copies = null, VoxelObjectKinds? kinds = null)
    {
        Placement = damaged.Placement;
        Kind = damaged.Kind;
        Revision = damaged.Revision;
        Source = damaged;
        Copies = copies;
        Kinds = kinds;
        Failure = null;
        Falling.Clear();
        Clears.Clear();
        _brickAt.Clear();
        var bricks = damaged.Bricks;
        if (_payload.Length < bricks.Length * BrickBytes)
        {
            _payload = new byte[Math.Max(bricks.Length * BrickBytes, 2 * _payload.Length)];
        }

        for (var i = 0; i < bricks.Length; i++)
        {
            damaged.BrickVoxelsOf(bricks[i]).CopyTo(_payload.AsSpan(i * BrickBytes));
            _brickAt[bricks[i]] = i * BrickBytes;
        }
    }

    public void Voxel(int x, int y, int z, out byte material, out byte density)
    {
        var size = Kind.Size;
        if ((uint)x >= (uint)size.X || (uint)y >= (uint)size.Y || (uint)z >= (uint)size.Z)
        {
            material = VoxelConstants.Air;
            density = VoxelDensity.Empty;
            return;
        }

        if (_brickAt.TryGetValue(DamagedObject.BrickKey(x, y, z), out var at))
        {
            var j = (x & 7) + 8 * ((y & 7) + 8 * (z & 7));
            material = _payload[at + j];
            density = _payload[at + BrickBytes / 2 + j];
            return;
        }

        Kind.Voxel(x, y, z, out material, out density);
    }
}

/// <summary>
/// Finds, on a thread of its own, the pieces of a damaged object that fall (<see cref="ObjectDamage"/> asks after an edit
/// takes voxels from an object, on a session's host or an offline game only): its solid voxels split into pieces joined
/// across faces (any material, leaves too), and by its kind's rule (<see cref="ObjectRoots"/>) those that stay and those
/// that fall. A request carries a snapshot of the object (<see cref="LabelSnapshot"/>), so the main thread keeps
/// changing it; the main thread takes the answers (<see cref="TryTake"/>) and removes the pieces that fall when the
/// object has not changed since. The kinds' tables are made before (<see cref="VoxelObjectKind.MakeTables"/>). Every
/// decision is the host's alone: peers follow the removals it logs.
/// </summary>
internal sealed class ObjectLabeller : IDisposable
{
    // Room made up front, so a first answer taken or snapshot returned on the main thread allocates nothing (when the
    // first one comes is the thread's timing).
    private const int PoolRoom = 16;
    private readonly Queue<LabelSnapshot> _asked = new(PoolRoom);
    private readonly Queue<LabelSnapshot> _answered = new(PoolRoom);
    private readonly Stack<LabelSnapshot> _spare = new(PoolRoom);

    // A plain object: the thread waits on it (Monitor.Wait), which System.Threading.Lock does not offer.
    private readonly object _lock = new();
    private readonly Stack<int> _flood = new();
    private ulong[] _seen = [];
    private ulong[] _falling = [];
    private Thread? _thread;
    private bool _stopping;
    private bool _disposed;

    /// <summary>The snapshots asked for and not yet answered or taken.</summary>
    public int Pending { get; private set; }

    /// <summary>
    /// True to keep the thread from starting: the snapshots asked for wait for <see cref="LabelPending"/> on the caller's
    /// thread. For tests that decide when an answer comes.
    /// </summary>
    internal bool Manual { get; set; }

    /// <summary>Labels every snapshot asked for on the calling thread (with <see cref="Manual"/>).</summary>
    internal void LabelPending()
    {
        if (!Manual)
        {
            throw new InvalidOperationException("The labeller labels on the caller's thread only when manual.");
        }

        while (true)
        {
            LabelSnapshot snapshot;
            lock (_lock)
            {
                if (!_asked.TryDequeue(out snapshot!))
                {
                    return;
                }
            }

            Label(snapshot);
            lock (_lock)
            {
                _answered.Enqueue(snapshot);
            }
        }
    }

    /// <summary>Starts the thread. Main thread.</summary>
    public void Start()
    {
        if (_thread is not null || Manual)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Itera object labeller" };
        _thread.Start();
    }

    /// <summary>A snapshot to fill and ask with, reused. Main thread.</summary>
    public LabelSnapshot Rent()
    {
        lock (_lock)
        {
            return _spare.Count > 0 ? _spare.Pop() : new LabelSnapshot();
        }
    }

    /// <summary>Asks for the pieces of the snapshot's object that fall. Main thread.</summary>
    public void Ask(LabelSnapshot snapshot)
    {
        lock (_lock)
        {
            _asked.Enqueue(snapshot);
            Pending++;
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>An answered snapshot, if any; <see cref="Return"/> it when done. Main thread.</summary>
    public bool TryTake(out LabelSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_answered.TryDequeue(out snapshot!))
            {
                Pending--;
                return true;
            }

            return false;
        }
    }

    /// <summary>Gives a snapshot back for reuse, dropping its copy's reference. Main thread.</summary>
    public void Return(LabelSnapshot snapshot)
    {
        snapshot.Forget();
        lock (_lock)
        {
            _spare.Push(snapshot);
        }
    }

    /// <summary>Finds, on the calling thread, the pieces of the snapshot's object that fall (for tests and the thread).</summary>
    internal void Label(LabelSnapshot snapshot)
    {
        var kind = snapshot.Kind;
        var size = kind.Size;
        var count = size.X * size.Y * size.Z;
        if (_seen.Length * 64L < count)
        {
            _seen = new ulong[(count + 63) / 64];
        }
        else
        {
            Array.Clear(_seen, 0, (count + 63) / 64);
        }

        snapshot.Falling.Clear();
        var roots = kind.Roots;
        // A root voxel's bottom lies at or below the anchor: layers 0 to the anchor's height in voxels.
        var rootTop = kind.AnchorCentimetres.Y / kind.UnitCentimetres;
        var largest = -1L;
        var largestIndex = -1;
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    var at = x + size.X * (y + size.Y * z);
                    if ((_seen[at >> 6] & (1UL << (at & 63))) != 0)
                    {
                        continue;
                    }

                    snapshot.Voxel(x, y, z, out var material, out _);
                    if (material == VoxelConstants.Air)
                    {
                        continue;
                    }

                    var (voxels, rooted, min, max) = Flood(snapshot, size, at, rootTop);
                    var piece = new FallingPiece(new VoxelCoord(x, y, z), min, max);
                    if (roots == ObjectRoots.Ground)
                    {
                        if (!rooted)
                        {
                            snapshot.Falling.Add(piece);
                        }

                        continue;
                    }

                    // The largest stays: the others fall, the first of two as large staying.
                    snapshot.Falling.Add(piece);
                    if (voxels > largest)
                    {
                        largest = voxels;
                        largestIndex = snapshot.Falling.Count - 1;
                    }
                }
            }
        }

        if (roots == ObjectRoots.Largest && largestIndex >= 0)
        {
            snapshot.Falling.RemoveAt(largestIndex);
        }

        if (snapshot.Falling.Count > 0 && snapshot.Copies is not null && snapshot.Kinds is not null)
        {
            ClearCopies(snapshot);
        }
    }

    /// <summary>
    /// The clears of the far copies only the falling pieces fill: at each level the kind is copied at, the object's shares
    /// of the world voxels as its copy was written (<see cref="ObjectCopies.Shares"/> and <see cref="ObjectCopies.KeepThin"/>
    /// from the kind's grid, which the column jobs read) and as it would be written without the pieces (from a grid of the
    /// snapshot's voxels less theirs, <see cref="ObjectShareBuilder"/>, by the kind's rule); each world voxel within a voxel
    /// of the pieces' box solid as written, not without them, and solid in no neighbour's copy, is cleared, in boxes of runs
    /// along x of one row. (A voxel an earlier cut cleared may be cleared again.)
    /// </summary>
    private void ClearCopies(LabelSnapshot snapshot)
    {
        var kind = snapshot.Kind;
        var size = kind.Size;
        var count = size.X * size.Y * size.Z;
        var copies = snapshot.Copies!;
        var placement = snapshot.Placement;

        // The voxels that fall, by the pieces' floods from their seeds again.
        if (_falling.Length * 64L < count)
        {
            _falling = new ulong[(count + 63) / 64];
        }
        else
        {
            Array.Clear(_falling, 0, (count + 63) / 64);
        }

        Array.Clear(_seen, 0, (count + 63) / 64);
        foreach (var piece in snapshot.Falling)
        {
            var start = piece.Seed.X + size.X * (piece.Seed.Y + size.Y * piece.Seed.Z);
            if ((_seen[start >> 6] & (1UL << (start & 63))) == 0)
            {
                Flood(snapshot, size, start, -1);
            }
        }

        (_seen, _falling) = (_falling, _seen);

        // The materials a clear names: the kind's, those a set holds.
        Span<byte> materials = stackalloc byte[VoxelEdit.MaterialSetSize];
        var materialCount = 0;
        for (var m = 1; m < VoxelEdit.MaterialSetSize; m++)
        {
            if (kind.Materials.Contains((byte)m))
            {
                materials[materialCount++] = (byte)m;
            }
        }

        // The levels the kind is copied at.
        var first = -1;
        var levels = 0;
        for (var level = copies.FirstLevel; level <= copies.LastLevel; level++)
        {
            if (copies.GridFor(placement.Kind, level) is not null)
            {
                first = first < 0 ? level : first;
                levels++;
            }
        }

        if (materialCount == 0 || levels == 0)
        {
            return;
        }

        // The object's share grids without the pieces, from its voxels as they stand; as written, the kind's.
        var grid = copies.Grid;
        var without = new ObjectShareBuilder(size, kind.UnitCentimetres, grid.Edge(first), kind.Materials);
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    var at = x + size.X * (y + size.Y * z);
                    if ((_falling[at >> 6] & (1UL << (at & 63))) == 0)
                    {
                        snapshot.Voxel(x, y, z, out var material, out var density);
                        without.Add(x, y, z, material, density);
                    }
                }
            }
        }

        var withoutGrids = without.Finish(levels, kind.AnchorCentimetres);

        // The pieces' box, in the kind's voxels.
        int x0 = int.MaxValue,
            y0 = int.MaxValue,
            z0 = int.MaxValue,
            x1 = int.MinValue,
            y1 = int.MinValue,
            z1 = int.MinValue;
        foreach (var piece in snapshot.Falling)
        {
            (x0, y0, z0) = (Math.Min(x0, piece.Min.X), Math.Min(y0, piece.Min.Y), Math.Min(z0, piece.Min.Z));
            (x1, y1, z1) = (Math.Max(x1, piece.Max.X), Math.Max(y1, piece.Max.Y), Math.Max(z1, piece.Max.Z));
        }

        for (var i = 0; i < levels; i++)
        {
            ClearLevel(
                snapshot,
                copies.GridFor(placement.Kind, first + i)!,
                withoutGrids[i],
                first + i,
                new VoxelCoord(x0, y0, z0),
                new VoxelCoord(x1, y1, z1),
                materials[..materialCount]
            );
        }
    }

    /// <summary>
    /// The clears of one level's copies: the world voxels within a voxel of the pieces' box (in the kind's voxels) the
    /// object's copy holds as written (<paramref name="with"/>, the kind's grid) and not without them
    /// (<paramref name="without"/>), and no neighbour's copy holds.
    /// </summary>
    private static void ClearLevel(
        LabelSnapshot snapshot,
        ObjectShareGrid with,
        ObjectShareGrid without,
        int level,
        VoxelCoord low,
        VoxelCoord high,
        ReadOnlySpan<byte> materials
    )
    {
        var kind = snapshot.Kind;
        var unit = kind.UnitCentimetres;
        var offset = kind.AnchorCentimetres;
        var placement = snapshot.Placement;
        var anchor = placement.Anchor;
        var copies = snapshot.Copies!;
        var edge = copies.Grid.Edge(level);

        // The object's shares over its whole range, as its copy is written (so the thin parts' rule agrees).
        var (rx, ry, rz, sx, sy, sz) = ObjectCopies.RangeOf(with, in placement, edge);
        var count = sx * sy * sz;
        var before = new byte[count];
        var after = new byte[count];
        var beforeMaterials = new byte[count];
        var afterMaterials = new byte[count];
        var marks = new byte[count];
        ObjectCopies.Shares(with, offset, in placement, edge, rx, ry, rz, sx, sy, sz, before, beforeMaterials);
        ObjectCopies.KeepThin(before, beforeMaterials, sx, sy, sz, marks);
        ObjectCopies.Shares(without, offset, in placement, edge, rx, ry, rz, sx, sy, sz, after, afterMaterials);
        ObjectCopies.KeepThin(after, afterMaterials, sx, sy, sz, marks);

        // The world voxels in reach of the pieces: their box turned into the world, and a voxel round it.
        long wx0 = long.MaxValue,
            wx1 = long.MinValue,
            wz0 = long.MaxValue,
            wz1 = long.MinValue;
        for (var corner = 0; corner < 4; corner++)
        {
            var ox = (long)((corner & 1) == 0 ? low.X : high.X + 1) * unit - offset.X;
            var oz = (long)((corner & 2) == 0 ? low.Z : high.Z + 1) * unit - offset.Z;
            var (wx, wz) = ObjectTurns.FromObject(placement.Heading, ox, oz);
            (wx0, wx1) = (Math.Min(wx0, wx), Math.Max(wx1, wx));
            (wz0, wz1) = (Math.Min(wz0, wz), Math.Max(wz1, wz));
        }

        var ix0 = Math.Max(rx, (int)Maths.DeterministicMath.FloorDivide(anchor.X + wx0, edge) - 1);
        var ix1 = Math.Min(rx + sx - 1, (int)Maths.DeterministicMath.FloorDivide(anchor.X + wx1, edge) + 1);
        var iz0 = Math.Max(rz, (int)Maths.DeterministicMath.FloorDivide(anchor.Z + wz0, edge) - 1);
        var iz1 = Math.Min(rz + sz - 1, (int)Maths.DeterministicMath.FloorDivide(anchor.Z + wz1, edge) + 1);
        var iy0 = Math.Max(
            ry,
            (int)Maths.DeterministicMath.FloorDivide((long)anchor.Y + low.Y * unit - offset.Y, edge) - 1
        );
        var iy1 = Math.Min(
            ry + sy - 1,
            (int)Maths.DeterministicMath.FloorDivide((long)anchor.Y + (high.Y + 1) * unit - offset.Y, edge) + 1
        );
        // The neighbours' copies at this level, each over its part of the examined box and a voxel round it (all the thin
        // parts' rule reads), where its range meets the box.
        var neighbours =
            new List<(int X, int Y, int Z, int SizeX, int SizeY, int SizeZ, byte[] Shares, byte[] Materials)>();
        var kinds = snapshot.Kinds!;
        foreach (var other in snapshot.Neighbours)
        {
            if (other.Id == placement.Id || copies.GridFor(other.Kind, level) is not { } theirs)
            {
                continue;
            }

            var range = ObjectCopies.RangeOf(theirs, in other, edge);
            int nx0 = Math.Max(range.X, ix0 - 1),
                ny0 = Math.Max(range.Y, iy0 - 1),
                nz0 = Math.Max(range.Z, iz0 - 1),
                nsx = Math.Min(range.X + range.SizeX, ix1 + 2) - nx0,
                nsy = Math.Min(range.Y + range.SizeY, iy1 + 2) - ny0,
                nsz = Math.Min(range.Z + range.SizeZ, iz1 + 2) - nz0;
            if (nsx <= 0 || nsy <= 0 || nsz <= 0)
            {
                continue;
            }

            var cells = nsx * nsy * nsz;
            var shares = new byte[cells];
            var theirMaterials = new byte[cells];
            var theirMarks = new byte[cells];
            ObjectCopies.Shares(
                theirs,
                kinds[other.Kind].AnchorCentimetres,
                in other,
                edge,
                nx0,
                ny0,
                nz0,
                nsx,
                nsy,
                nsz,
                shares,
                theirMaterials
            );
            ObjectCopies.KeepThin(shares, theirMaterials, nsx, nsy, nsz, theirMarks);
            neighbours.Add((nx0, ny0, nz0, nsx, nsy, nsz, shares, theirMaterials));
        }

        for (var y = iy0; y <= iy1; y++)
        {
            for (var z = iz0; z <= iz1; z++)
            {
                var runStart = int.MinValue;
                for (var x = ix0; x <= ix1 + 1; x++)
                {
                    var cleared = false;
                    if (x <= ix1)
                    {
                        var at = x - rx + sx * (y - ry + sy * (z - rz));
                        cleared =
                            before[at] >= VoxelDensity.Solid
                            && beforeMaterials[at] != VoxelConstants.Air
                            && after[at] < VoxelDensity.Solid
                            && !HeldByNeighbour(neighbours, x, y, z);
                    }

                    if (cleared && runStart == int.MinValue)
                    {
                        runStart = x;
                    }
                    else if (!cleared && runStart != int.MinValue)
                    {
                        snapshot.Clears.Add(
                            VoxelEdit.ClearCopies(
                                level,
                                new VoxelCoord(runStart * edge, y * edge, z * edge),
                                new VoxelCoord((x - runStart) * edge, edge, edge),
                                materials
                            )
                        );
                        runStart = int.MinValue;
                    }
                }
            }
        }
    }

    /// <summary>True when a neighbour's copy holds world voxel (x, y, z) solid (from half, of a material, as it is written).</summary>
    private static bool HeldByNeighbour(
        List<(int X, int Y, int Z, int SizeX, int SizeY, int SizeZ, byte[] Shares, byte[] Materials)> neighbours,
        int x,
        int y,
        int z
    )
    {
        foreach (var (nx, ny, nz, sx, sy, sz, shares, materials) in neighbours)
        {
            int lx = x - nx,
                ly = y - ny,
                lz = z - nz;
            if (
                (uint)lx < (uint)sx
                && (uint)ly < (uint)sy
                && (uint)lz < (uint)sz
                && shares[lx + sx * (ly + sy * lz)] >= VoxelDensity.Solid
                && materials[lx + sx * (ly + sy * lz)] != VoxelConstants.Air
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One piece from voxel index <paramref name="start"/>: its voxels, whether it holds a root voxel, and its bounds.</summary>
    private (long Voxels, bool Rooted, VoxelCoord Min, VoxelCoord Max) Flood(
        LabelSnapshot snapshot,
        VoxelCoord size,
        int start,
        int rootTop
    )
    {
        _flood.Clear();
        _seen[start >> 6] |= 1UL << (start & 63);
        _flood.Push(start);
        long voxels = 0;
        var rooted = false;
        int x0 = int.MaxValue,
            y0 = int.MaxValue,
            z0 = int.MaxValue,
            x1 = int.MinValue,
            y1 = int.MinValue,
            z1 = int.MinValue;
        while (_flood.Count > 0)
        {
            var at = _flood.Pop();
            int x = at % size.X,
                y = at / size.X % size.Y,
                z = at / (size.X * size.Y);
            voxels++;
            rooted |= y <= rootTop;
            (x0, y0, z0) = (Math.Min(x0, x), Math.Min(y0, y), Math.Min(z0, z));
            (x1, y1, z1) = (Math.Max(x1, x), Math.Max(y1, y), Math.Max(z1, z));
            Reach(snapshot, size, x - 1, y, z);
            Reach(snapshot, size, x + 1, y, z);
            Reach(snapshot, size, x, y - 1, z);
            Reach(snapshot, size, x, y + 1, z);
            Reach(snapshot, size, x, y, z - 1);
            Reach(snapshot, size, x, y, z + 1);
        }

        return (voxels, rooted, new VoxelCoord(x0, y0, z0), new VoxelCoord(x1, y1, z1));
    }

    private void Reach(LabelSnapshot snapshot, VoxelCoord size, int x, int y, int z)
    {
        if ((uint)x >= (uint)size.X || (uint)y >= (uint)size.Y || (uint)z >= (uint)size.Z)
        {
            return;
        }

        var at = x + size.X * (y + size.Y * z);
        if ((_seen[at >> 6] & (1UL << (at & 63))) != 0)
        {
            return;
        }

        snapshot.Voxel(x, y, z, out var material, out _);
        if (material == VoxelConstants.Air)
        {
            return;
        }

        _seen[at >> 6] |= 1UL << (at & 63);
        _flood.Push(at);
    }

    private void Run()
    {
        while (true)
        {
            LabelSnapshot snapshot;
            lock (_lock)
            {
                while (_asked.Count == 0 && !_stopping)
                {
                    Monitor.Wait(_lock);
                }

                if (_stopping)
                {
                    return;
                }

                snapshot = _asked.Dequeue();
            }

            try
            {
                Label(snapshot);
            }
            catch (Exception exception)
            {
                // No answer of pieces rather than a thread that ends the game: the object stays whole, and the main
                // thread logs it.
                snapshot.Falling.Clear();
                snapshot.Clears.Clear();
                snapshot.Failure = exception;
            }

            lock (_lock)
            {
                _answered.Enqueue(snapshot);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_lock)
        {
            _stopping = true;
            Monitor.Pulse(_lock);
        }

        _thread?.Join();
    }
}
