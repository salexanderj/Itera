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

    /// <summary>The pieces to fall, filled on the labeller's thread.</summary>
    public List<FallingPiece> Falling { get; } = [];

    /// <summary>Copies the object's changed bricks. Main thread.</summary>
    public void Copy(DamagedObject damaged)
    {
        Placement = damaged.Placement;
        Kind = damaged.Kind;
        Revision = damaged.Revision;
        Falling.Clear();
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
    private ulong[] _seen = [];
    private Thread? _thread;
    private bool _stopping;
    private bool _disposed;

    /// <summary>The snapshots asked for and not yet answered or taken.</summary>
    public int Pending { get; private set; }

    /// <summary>Starts the thread. Main thread.</summary>
    public void Start()
    {
        if (_thread is not null)
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

    /// <summary>Gives a snapshot back for reuse. Main thread.</summary>
    public void Return(LabelSnapshot snapshot)
    {
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

            Label(snapshot);
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
