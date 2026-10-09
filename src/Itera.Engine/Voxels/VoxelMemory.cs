using System.Numerics;
using System.Runtime.InteropServices;

namespace Itera.Engine.Voxels;

/// <summary>The kinds of fixed-size block <see cref="VoxelMemory"/> hands out.</summary>
internal enum VoxelPool
{
    /// <summary>A brick: 64-byte occupancy mask, with a <see cref="BrickRecord"/> in the side slab.</summary>
    Bricks,

    /// <summary>Palette and 1-bit indices of one brick.</summary>
    Index1,

    /// <summary>Palette and 2-bit indices of one brick.</summary>
    Index2,

    /// <summary>Palette and 4-bit indices of one brick.</summary>
    Index4,

    /// <summary>One material byte per voxel of one brick.</summary>
    Index8,

    /// <summary>A <see cref="NodeRecord"/>.</summary>
    Nodes,

    /// <summary>A node child list of up to 8 entries.</summary>
    List8,

    /// <summary>A node child list of up to 16 entries.</summary>
    List16,

    /// <summary>A node child list of up to 32 entries.</summary>
    List32,

    /// <summary>A node child list of up to 64 entries.</summary>
    List64,

    /// <summary>One density byte per voxel of one brick (<see cref="VoxelDensity"/>).</summary>
    Density,
}

/// <summary>
/// Native memory for voxel storage: one pool of fixed-size blocks per <see cref="VoxelPool"/>. Each pool takes slabs
/// of about 1 MB from the OS when it runs out of free blocks, keeps a free list inside the freed blocks, and returns
/// slabs only in <see cref="Dispose"/>, so a steady state allocates nothing. New slabs stop at the budget: allocation
/// then fails unless the caller may exceed it (edits must never fail; streaming may). Allocation and freeing may run on
/// any thread. Each thread keeps up to <see cref="CacheBlocks"/> free blocks of each pool, taken from the pool and
/// given back <see cref="CacheBatch"/> at a time under one lock: a column job allocates and frees a block about 4,000
/// times, and with 8 workers taking the lock for each, a job waited 1.8 ms of its 7.7 ms for it (2026-09-29).
/// <see cref="LiveBlocks"/> and <see cref="UsedBytes"/> count what callers hold, not what the caches hold.
/// <see cref="Pointer"/> takes no lock: a handle's memory never moves, and a handle reaches another thread only through
/// a hand-off that synchronises.
/// </summary>
internal sealed unsafe class VoxelMemory : IDisposable
{
    /// <summary>Palette bytes at the start of an index block.</summary>
    public const int PaletteBytes = 16;

    /// <summary>The most free blocks of one pool a thread keeps.</summary>
    public const int CacheBlocks = 64;

    /// <summary>How many blocks a thread takes from a pool, or gives back to it, at a time.</summary>
    public const int CacheBatch = 32;

    private const int SlabTargetBytes = 1 << 20;
    private const int NoBlock = -1;

    private readonly object _lock = new();
    private readonly Pool[] _pools;
    private readonly long _budgetBytes;
    private readonly ThreadLocal<BlockCache> _caches;

    // Every thread's cache, for the counts; added to under the lock.
    private readonly List<BlockCache> _allCaches = [];
    private long _reservedBytes;

    /// <summary>Bytes every voxel memory of the process holds now, for checks that an ended match freed its world.</summary>
    private static long s_liveReservedBytes;
    private volatile bool _disposed;

    /// <param name="budgetBytes">Slab bytes that allocations which may not exceed the budget stop at.</param>
    public VoxelMemory(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        _budgetBytes = budgetBytes;
        _pools =
        [
            new Pool(VoxelConstants.BrickMaskWords * sizeof(ulong), sizeof(BrickRecord)),
            new Pool(PaletteBytes + VoxelConstants.BrickVoxels / 8, 0),
            new Pool(PaletteBytes + VoxelConstants.BrickVoxels / 4, 0),
            new Pool(PaletteBytes + VoxelConstants.BrickVoxels / 2, 0),
            new Pool(VoxelConstants.BrickVoxels, 0),
            new Pool(sizeof(NodeRecord), 0),
            new Pool(8 * sizeof(uint), 0),
            new Pool(16 * sizeof(uint), 0),
            new Pool(32 * sizeof(uint), 0),
            new Pool(64 * sizeof(uint), 0),
            new Pool(VoxelConstants.BrickVoxels, 0),
        ];
        _caches = new ThreadLocal<BlockCache>(NewCache);
    }

    /// <summary>The budget in bytes.</summary>
    public long BudgetBytes => _budgetBytes;

    /// <summary>Bytes of slabs taken from the OS, across all pools.</summary>
    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    /// <summary>Bytes every voxel memory of the process holds now (slabs reserved and not yet freed by Dispose).</summary>
    public static long LiveReservedBytes => Interlocked.Read(ref s_liveReservedBytes);

    /// <summary>Bytes of blocks in use, across all pools; not those the threads' caches hold.</summary>
    public long UsedBytes
    {
        get
        {
            lock (_lock)
            {
                long used = 0;
                for (var i = 0; i < _pools.Length; i++)
                {
                    var pool = _pools[i];
                    used += (long)(pool.Live - CachedLocked(i)) * (pool.BlockSize + pool.SideSize);
                }

                return used;
            }
        }
    }

    /// <summary>Blocks in use in one pool; not those the threads' caches hold.</summary>
    public int LiveBlocks(VoxelPool pool)
    {
        lock (_lock)
        {
            return _pools[(int)pool].Live - CachedLocked((int)pool);
        }
    }

    /// <summary>Size in bytes of one block of <paramref name="pool"/>, not counting its side record.</summary>
    public int BlockSize(VoxelPool pool) => _pools[(int)pool].BlockSize;

    /// <summary>
    /// Takes a block. Returns false when the pool needs a new slab and that slab would pass the budget, unless
    /// <paramref name="mayExceedBudget"/> is true. The block's contents are undefined.
    /// </summary>
    public bool TryAllocate(VoxelPool pool, bool mayExceedBudget, out int handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cache = _caches.Value!;
        var index = (int)pool;
        if (cache.Count[index] == 0 && !Refill(cache, index, mayExceedBudget))
        {
            handle = NoBlock;
            return false;
        }

        handle = cache.Handles[index][--cache.Count[index]];
#if DEBUG
        _pools[index].MarkLive(handle);
#endif
        return true;
    }

    /// <summary>Takes a block, going past the budget if needed. For edits, which must not fail.</summary>
    public int Allocate(VoxelPool pool)
    {
        TryAllocate(pool, mayExceedBudget: true, out var handle);
        return handle;
    }

    /// <summary>Returns a block to its pool (through this thread's cache).</summary>
    public void Free(VoxelPool pool, int handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cache = _caches.Value!;
        var index = (int)pool;
#if DEBUG
        _pools[index].MarkFree(handle);
#endif
        if (cache.Count[index] == CacheBlocks)
        {
            GiveBack(cache, index);
        }

        cache.Handles[index][cache.Count[index]++] = handle;
    }

    /// <summary>
    /// Takes up to <see cref="CacheBatch"/> blocks from the pool into the thread's empty cache: free blocks first, then new
    /// ones from the slabs, adding slabs within the budget unless <paramref name="mayExceedBudget"/>. False when none.
    /// </summary>
    private bool Refill(BlockCache cache, int index, bool mayExceedBudget)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var p = _pools[index];
            var handles = cache.Handles[index];
            var count = 0;
            while (count < CacheBatch)
            {
                if (p.FreeHead != NoBlock)
                {
                    handles[count++] = p.FreeHead;
                    p.FreeHead = *(int*)p.Pointer(p.FreeHead);
                    continue;
                }

                if (p.Next == p.Capacity)
                {
                    var slabBytes = (long)p.BlocksPerSlab * (p.BlockSize + p.SideSize);
                    // A new slab only for the block asked for, not to fill the cache, and within the budget unless the
                    // caller may exceed it.
                    if (count > 0 || (!mayExceedBudget && _reservedBytes + slabBytes > _budgetBytes))
                    {
                        break;
                    }

                    p.AddSlab();
                    Interlocked.Add(ref _reservedBytes, slabBytes);
                    Interlocked.Add(ref s_liveReservedBytes, slabBytes);
                }

                handles[count++] = p.Next++;
            }

            p.Live += count;
            cache.Count[index] = count;
            return count > 0;
        }
    }

    /// <summary>Gives <see cref="CacheBatch"/> of the thread's cached blocks back to the pool's free list.</summary>
    private void GiveBack(BlockCache cache, int index)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var p = _pools[index];
            var handles = cache.Handles[index];
            for (var i = 0; i < CacheBatch; i++)
            {
                var handle = handles[--cache.Count[index]];
                *(int*)p.Pointer(handle) = p.FreeHead;
                p.FreeHead = handle;
            }

            p.Live -= CacheBatch;
        }
    }

    /// <summary>Blocks of pool <paramref name="index"/> the threads' caches hold. Under the lock.</summary>
    private int CachedLocked(int index)
    {
        var cached = 0;
        foreach (var cache in _allCaches)
        {
            cached += Volatile.Read(ref cache.Count[index]);
        }

        return cached;
    }

    private BlockCache NewCache()
    {
        var cache = new BlockCache(_pools.Length);
        lock (_lock)
        {
            _allCaches.Add(cache);
        }

        return cache;
    }

    /// <summary>The block's memory. Valid until the block is freed.</summary>
    public byte* Pointer(VoxelPool pool, int handle) => _pools[(int)pool].Pointer(handle);

    /// <summary>The block's side record (bricks only). Valid until the block is freed.</summary>
    public byte* SidePointer(VoxelPool pool, int handle) => _pools[(int)pool].SidePointer(handle);

    /// <summary>A brick's occupancy mask: 8 words, word z for layer z, bit <c>x + 8y</c>.</summary>
    public ulong* BrickMask(int brick) => (ulong*)_pools[(int)VoxelPool.Bricks].Pointer(brick);

    /// <summary>A brick's record.</summary>
    public BrickRecord* Brick(int brick) => (BrickRecord*)_pools[(int)VoxelPool.Bricks].SidePointer(brick);

    /// <summary>A brick's density block: one byte per voxel, index <c>x + 8y + 64z</c>.</summary>
    public byte* Densities(int block) => _pools[(int)VoxelPool.Density].Pointer(block);

    /// <summary>A node's record.</summary>
    public NodeRecord* Node(int node) => (NodeRecord*)_pools[(int)VoxelPool.Nodes].Pointer(node);

    /// <summary>The first entry of a child list block of class <paramref name="listClass"/> (0 to 3).</summary>
    public uint* List(int listClass, int list) => (uint*)_pools[(int)VoxelPool.List8 + listClass].Pointer(list);

    /// <summary>Frees every slab. Handles must not be used afterwards.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var pool in _pools)
            {
                pool.FreeSlabs();
            }

            Interlocked.Add(ref s_liveReservedBytes, -_reservedBytes);
            _reservedBytes = 0;
            _allCaches.Clear();
        }

        _caches.Dispose();
    }

    /// <summary>One thread's free blocks of each pool, taken from the pools in batches.</summary>
    private sealed class BlockCache(int pools)
    {
        public int[][] Handles { get; } = [.. Enumerable.Range(0, pools).Select(_ => new int[CacheBlocks])];

        public int[] Count { get; } = new int[pools];
    }

    /// <summary>One size class. Not thread safe; <see cref="VoxelMemory"/> holds its lock around every change.</summary>
    private sealed class Pool
    {
        private readonly int _shift;
        private readonly int _mask;
        private nint[] _slabs = new nint[16];
        private nint[] _sideSlabs = new nint[16];
#if DEBUG
        // A bit per block, set while it is handed out: a block freed twice, or handed out twice, throws (a double free would hand one
        // block to two owners). One bitmap a slab, so growing never moves
        // a bit another thread is changing; native, as the slabs are, so growing allocates no more managed memory than in
        // a Release build.
        private nint[] _live = new nint[16];
#endif
        private int _slabCount;

        public Pool(int blockSize, int sideSize)
        {
            BlockSize = blockSize;
            SideSize = sideSize;
            // The largest power of two that fits the target, so a handle splits into slab and index with a shift.
            BlocksPerSlab = 1 << BitOperations.Log2((uint)(SlabTargetBytes / blockSize));
            _shift = BitOperations.Log2((uint)BlocksPerSlab);
            _mask = BlocksPerSlab - 1;
        }

        public int BlockSize { get; }

        public int SideSize { get; }

        public int BlocksPerSlab { get; }

        public int Capacity => _slabCount * BlocksPerSlab;

        public int Next { get; set; }

        public int FreeHead { get; set; } = NoBlock;

        public int Live { get; set; }

        public byte* Pointer(int handle) => (byte*)_slabs[handle >> _shift] + (long)(handle & _mask) * BlockSize;

#if DEBUG
        /// <summary>Marks a block handed out; throws when it was already (Debug).</summary>
        public void MarkLive(int handle)
        {
            var words = (ulong*)Volatile.Read(ref _live)[handle >> _shift];
            var bit = 1UL << (handle & _mask & 63);
            var old = Interlocked.Or(ref words[(handle & _mask) >> 6], bit);
            if ((old & bit) != 0)
            {
                throw new InvalidOperationException($"Voxel block {handle} was handed out while still in use.");
            }
        }

        /// <summary>Marks a block free; throws when it was not handed out (Debug).</summary>
        public void MarkFree(int handle)
        {
            var live = Volatile.Read(ref _live);
            var slab = handle >> _shift;
            if (handle < 0 || slab >= live.Length || live[slab] == 0)
            {
                throw new InvalidOperationException($"Voxel block {handle} was freed but never made.");
            }

            var words = (ulong*)live[slab];
            var bit = 1UL << (handle & _mask & 63);
            var old = Interlocked.And(ref words[(handle & _mask) >> 6], ~bit);
            if ((old & bit) == 0)
            {
                throw new InvalidOperationException($"Voxel block {handle} was freed while not in use (freed twice).");
            }
        }
#endif

        public byte* SidePointer(int handle) => (byte*)_sideSlabs[handle >> _shift] + (long)(handle & _mask) * SideSize;

        public void AddSlab()
        {
            if (_slabCount == _slabs.Length)
            {
                // Readers index these arrays without the lock. They keep reading the old arrays until they see the
                // new ones, and the old ones still hold every slab a handle they have can point into.
                var slabs = new nint[_slabs.Length * 2];
                var sideSlabs = new nint[_sideSlabs.Length * 2];
                Array.Copy(_slabs, slabs, _slabCount);
                Array.Copy(_sideSlabs, sideSlabs, _slabCount);
                Volatile.Write(ref _slabs, slabs);
                Volatile.Write(ref _sideSlabs, sideSlabs);
#if DEBUG
                var live = new nint[_live.Length * 2];
                Array.Copy(_live, live, _slabCount);
                Volatile.Write(ref _live, live);
#endif
            }

#if DEBUG
            _live[_slabCount] = (nint)NativeMemory.AllocZeroed((nuint)((BlocksPerSlab + 63) / 64), sizeof(ulong));
#endif

            _slabs[_slabCount] = (nint)NativeMemory.AlignedAlloc((nuint)((long)BlocksPerSlab * BlockSize), 64);
            if (SideSize > 0)
            {
                _sideSlabs[_slabCount] = (nint)NativeMemory.AlignedAlloc((nuint)((long)BlocksPerSlab * SideSize), 64);
            }

            _slabCount++;
        }

        public void FreeSlabs()
        {
            for (var i = 0; i < _slabCount; i++)
            {
                NativeMemory.AlignedFree((void*)_slabs[i]);
                if (SideSize > 0)
                {
                    NativeMemory.AlignedFree((void*)_sideSlabs[i]);
                }
#if DEBUG
                NativeMemory.Free((void*)_live[i]);
                _live[i] = 0;
#endif
            }

            _slabCount = 0;
            Next = 0;
            Live = 0;
            FreeHead = NoBlock;
        }
    }
}
