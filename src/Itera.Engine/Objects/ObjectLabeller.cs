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
    private readonly Queue<LabelSnapshot> _asked = new();
    private readonly Queue<LabelSnapshot> _answered = new();
    private readonly Stack<LabelSnapshot> _spare = new();

    // A plain object: the thread waits on it (Monitor.Wait), which System.Threading.Lock does not offer.
    private readonly object _lock = new();
    private readonly Stack<int> _flood = new();
    private readonly List<int> _kept = [];
    private ulong[] _seen = [];
    private ulong[] _falling = [];
    private long[] _sums = [];
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
    /// The clears of the far copies only the falling pieces fill: at each level the kind is copied at, each world voxel
    /// in reach of the pieces whose copy holds the object's material, that no voxel the object keeps lies within the copy
    /// rule's reach of (<see cref="ObjectCopyGrid"/>: the blocks round its sample point's), and no other object's copy
    /// fills, is cleared, in boxes of runs along x and z of one layer.
    /// </summary>
    private void ClearCopies(LabelSnapshot snapshot)
    {
        var kind = snapshot.Kind;
        var size = kind.Size;
        var count = size.X * size.Y * size.Z;
        var copies = snapshot.Copies!;
        var placement = snapshot.Placement;
        var unit = kind.UnitCentimetres;
        var offset = kind.AnchorCentimetres;

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

        // The voxels it keeps.
        _kept.Clear();
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    var at = x + size.X * (y + size.Y * z);
                    if ((_falling[at >> 6] & (1UL << (at & 63))) != 0)
                    {
                        continue;
                    }

                    snapshot.Voxel(x, y, z, out var material, out _);
                    if (material != VoxelConstants.Air)
                    {
                        _kept.Add(at);
                    }
                }
            }
        }

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

        if (materialCount == 0)
        {
            return;
        }

        // The pieces' box, in the kind's centimetres from its anchor.
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

        var grid = copies.Grid;
        for (var level = copies.FirstLevel; level <= copies.LastLevel; level++)
        {
            if (copies.GridFor(placement.Kind, level) is not { } copy)
            {
                continue;
            }

            ClearLevel(
                snapshot,
                copy,
                grid,
                level,
                new VoxelCoord(x0, y0, z0),
                new VoxelCoord(x1, y1, z1),
                materials[..materialCount]
            );
        }
    }

    /// <summary>The clears of one level's copies, from the pieces' box in the kind's voxels.</summary>
    private void ClearLevel(
        LabelSnapshot snapshot,
        ObjectCopyGrid copy,
        VoxelGrid grid,
        int level,
        VoxelCoord low,
        VoxelCoord high,
        ReadOnlySpan<byte> materials
    )
    {
        var kind = snapshot.Kind;
        var size = kind.Size;
        var unit = kind.UnitCentimetres;
        var offset = kind.AnchorCentimetres;
        var placement = snapshot.Placement;
        var anchor = placement.Anchor;
        var edge = grid.Edge(level);
        var block = copy.BlockCentimetres;
        var per = block / unit;
        int blocksX = (size.X + per - 1) / per,
            blocksY = (size.Y + per - 1) / per,
            blocksZ = (size.Z + per - 1) / per;

        // The voxels it keeps in each block, summed over every box from block (0, 0, 0).
        var cells = (blocksX + 1) * (blocksY + 1) * (blocksZ + 1);
        if (_sums.Length < cells)
        {
            _sums = new long[cells];
        }
        else
        {
            Array.Clear(_sums, 0, cells);
        }

        int sx = blocksX + 1,
            sy = blocksY + 1;
        foreach (var at in _kept)
        {
            int x = at % size.X,
                y = at / size.X % size.Y,
                z = at / (size.X * size.Y);
            _sums[x / per + 1 + sx * (y / per + 1 + sy * (z / per + 1))]++;
        }

        for (var z = 1; z <= blocksZ; z++)
        {
            for (var y = 1; y <= blocksY; y++)
            {
                for (var x = 1; x <= blocksX; x++)
                {
                    var i = x + sx * (y + sy * z);
                    _sums[i] +=
                        _sums[i - 1]
                        + _sums[i - sx]
                        + _sums[i - sx * sy]
                        - _sums[i - 1 - sx]
                        - _sums[i - 1 - sx * sy]
                        - _sums[i - sx - sx * sy]
                        + _sums[i - 1 - sx - sx * sy];
                }
            }
        }

        // The world voxels in reach of the pieces: their box turned into the world, and the copy's reach round it.
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

        var across = ObjectCopyGrid.HalfAcross(edge) + block;
        var up = (edge + 1) / 2 + block;
        var ix0 = (int)Maths.DeterministicMath.FloorDivide(anchor.X + wx0 - across, edge);
        var ix1 = (int)Maths.DeterministicMath.FloorDivide(anchor.X + wx1 + across, edge);
        var iz0 = (int)Maths.DeterministicMath.FloorDivide(anchor.Z + wz0 - across, edge);
        var iz1 = (int)Maths.DeterministicMath.FloorDivide(anchor.Z + wz1 + across, edge);
        var iy0 = (int)Maths.DeterministicMath.FloorDivide((long)anchor.Y + low.Y * unit - offset.Y - up, edge);
        var iy1 = (int)Maths.DeterministicMath.FloorDivide((long)anchor.Y + (high.Y + 1) * unit - offset.Y + up, edge);
        var level0 = new GridLevel(grid, level);
        int mA = copy.MarginAcross,
            mU = copy.MarginUp;
        for (var y = iy0; y <= iy1; y++)
        {
            var py = level0.SampleAt(y);
            for (var z = iz0; z <= iz1; z++)
            {
                var pz = level0.SampleAt(z);
                var runStart = int.MinValue;
                for (var x = ix0; x <= ix1 + 1; x++)
                {
                    var cleared =
                        x <= ix1
                        && Cleared(
                            snapshot,
                            copy,
                            level,
                            level0.SampleAt(x),
                            py,
                            pz,
                            sx,
                            sy,
                            blocksX,
                            blocksY,
                            blocksZ,
                            mA,
                            mU
                        );
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

    /// <summary>
    /// True when the world voxel sampled at (px, py, pz), absolute centimetres, holds the object's copy, the object keeps no
    /// voxel within the copy rule's reach of it, and no other object's copy fills it.
    /// </summary>
    private bool Cleared(
        LabelSnapshot snapshot,
        ObjectCopyGrid copy,
        int level,
        int px,
        int py,
        int pz,
        int sx,
        int sy,
        int blocksX,
        int blocksY,
        int blocksZ,
        int mA,
        int mU
    )
    {
        var kind = snapshot.Kind;
        var offset = kind.AnchorCentimetres;
        var placement = snapshot.Placement;
        var anchor = placement.Anchor;
        var (ox, oz) = ObjectTurns.ToObject(placement.Heading, px - (long)anchor.X, pz - (long)anchor.Z);
        long kx = ox + offset.X,
            ky = py - (long)anchor.Y + offset.Y,
            kz = oz + offset.Z;
        if (copy.At(kx, ky, kz) == VoxelConstants.Air)
        {
            return false;
        }

        // The voxels it keeps in the blocks round the sample point's.
        var block = copy.BlockCentimetres;
        var bx = Maths.DeterministicMath.FloorDivide(kx, block);
        var by = Maths.DeterministicMath.FloorDivide(ky, block);
        var bz = Maths.DeterministicMath.FloorDivide(kz, block);
        var x0 = (int)Math.Clamp(bx - mA, 0, blocksX);
        var x1 = (int)Math.Clamp(bx + mA + 1, 0, blocksX);
        var y0 = (int)Math.Clamp(by - mU, 0, blocksY);
        var y1 = (int)Math.Clamp(by + mU + 1, 0, blocksY);
        var z0 = (int)Math.Clamp(bz - mA, 0, blocksZ);
        var z1 = (int)Math.Clamp(bz + mA + 1, 0, blocksZ);
        if (x0 < x1 && y0 < y1 && z0 < z1)
        {
            var kept =
                _sums[x1 + sx * (y1 + sy * z1)]
                - _sums[x0 + sx * (y1 + sy * z1)]
                - _sums[x1 + sx * (y0 + sy * z1)]
                - _sums[x1 + sx * (y1 + sy * z0)]
                + _sums[x0 + sx * (y0 + sy * z1)]
                + _sums[x0 + sx * (y1 + sy * z0)]
                + _sums[x1 + sx * (y0 + sy * z0)]
                - _sums[x0 + sx * (y0 + sy * z0)];
            if (kept > 0)
            {
                return false;
            }
        }

        // Another object's copy there keeps it.
        var copies = snapshot.Copies!;
        var kinds = snapshot.Kinds!;
        foreach (var other in snapshot.Neighbours)
        {
            if (other.Id == placement.Id || copies.GridFor(other.Kind, level) is not { } theirs)
            {
                continue;
            }

            var otherKind = kinds[other.Kind];
            var (tx, tz) = ObjectTurns.ToObject(other.Heading, px - (long)other.Anchor.X, pz - (long)other.Anchor.Z);
            if (
                theirs.At(
                    tx + otherKind.AnchorCentimetres.X,
                    py - (long)other.Anchor.Y + otherKind.AnchorCentimetres.Y,
                    tz + otherKind.AnchorCentimetres.Z
                ) != VoxelConstants.Air
            )
            {
                return false;
            }
        }

        return true;
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
