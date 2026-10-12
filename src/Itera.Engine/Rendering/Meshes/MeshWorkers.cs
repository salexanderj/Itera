using System.Collections.Concurrent;
using Itera.Engine.Meshing;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// One block to mesh: its voxels with their apron (read on the main thread, <see cref="VoxelWorld.TryReadBlock(int, int, int, int, Span{byte}, Span{byte})"/>, or
/// from a model's level), and the mesh a worker makes of them. Reused: the main thread keeps a pool.
/// </summary>
internal sealed class MeshJob
{
    public readonly byte[] Samples = new byte[SurfaceNets.SampleCount];

    /// <summary>
    /// The samples' densities, read with them for a block of the world or of a model that keeps densities (an object
    /// kind's); other models' blocks have only materials.
    /// </summary>
    public readonly byte[] Densities = new byte[SurfaceNets.SampleCount];

    /// <summary>True when <see cref="Densities"/> holds the block's densities (a block of the world, or of a model that keeps them).</summary>
    public bool HasDensities;

    /// <summary>
    /// For a stitched mesh (<see cref="Sides"/> not none): the coarser level's block that holds this one, its samples
    /// and densities. Only the stitch jobs' pool has them.
    /// </summary>
    public readonly byte[]? CoarseSamples;

    public readonly byte[]? CoarseDensities;

    /// <summary>The sides a stitched mesh is made for; <see cref="TransitionSides.None"/> for a plain one.</summary>
    public TransitionSides Sides;

    /// <summary>The block's place in the coarser block, 0 or 16 coarser voxels along each axis.</summary>
    public VoxelCoord CoarseOffset;

    /// <summary>The stitch's <see cref="MeshStitch.CoarseChanges"/> when the coarser block was read.</summary>
    public int CoarseVersion;

    public readonly MeshBuffers Mesh = new();

    /// <summary>The column the block is in.</summary>
    public ColumnKey Column;

    /// <summary>
    /// The column object the job was started for: a column of the same key made since (a resync, another match, an
    /// unload and load) is another column, and the job is stale for it.
    /// </summary>
    public MeshColumn? Owner;

    /// <summary>The block's place in its column (<see cref="MeshColumn"/>).</summary>
    public int Index;

    /// <summary>The block's version when its voxels were read; a newer one makes the mesh stale.</summary>
    public int Version;

    /// <summary>The model the block is of, or null for a block of the world.</summary>
    public ModelMesh? Model;

    /// <summary>The model's revision when its voxels were read; a restart since makes the mesh stale.</summary>
    public int Revision;

    /// <summary>The model's tree (its level from its base) the block is of.</summary>
    public int Tree;

    /// <summary>The block's place in the model's tree (<see cref="GpuData.MeshModelDraw.Block"/>).</summary>
    public uint Block;

    /// <summary>Triangles made, or -1 when the mesher failed.</summary>
    public int Triangles;

    /// <summary>A job of the plain pool, or with <paramref name="stitches"/> one of the stitch jobs' pool.</summary>
    public MeshJob(bool stitches = false)
    {
        if (stitches)
        {
            CoarseSamples = new byte[SurfaceNets.SampleCount];
            CoarseDensities = new byte[SurfaceNets.SampleCount];
        }
    }

    /// <summary>True for a stitched mesh's job.</summary>
    public bool IsStitch => Sides != TransitionSides.None;
}

/// <summary>
/// Threads that mesh blocks (<see cref="SurfaceNets"/>, one each) for <see cref="WorldMeshes"/>: the main thread queues
/// jobs and takes them back finished. Nothing else is shared. A job that throws comes back with
/// <see cref="MeshJob.Triangles"/> -1 and the exception in <see cref="TakeFault"/>.
/// </summary>
internal sealed class MeshWorkers : IDisposable
{
    private readonly ConcurrentQueue<MeshJob> _queued = new();
    private readonly ConcurrentQueue<MeshJob> _done = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread[] _threads;
    private volatile bool _stopping;
    private Exception? _fault;
    private bool _disposed;

    /// <param name="threads">Worker threads, at least 1.</param>
    public MeshWorkers(int threads)
    {
        _threads = new Thread[Math.Max(1, threads)];
        for (var i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Run) { IsBackground = true, Name = $"Mesh worker {i + 1}" };
            _threads[i].Start();
        }
    }

    /// <summary>Worker threads.</summary>
    public int Count => _threads.Length;

    /// <summary>Queues a job. Main thread.</summary>
    public void Enqueue(MeshJob job)
    {
        _queued.Enqueue(job);
        _signal.Release();
    }

    /// <summary>Takes a finished job, if any. Main thread.</summary>
    public bool TryTake(out MeshJob job) => _done.TryDequeue(out job!);

    /// <summary>The first exception a job threw since the last call, if any. Main thread.</summary>
    public Exception? TakeFault() => Interlocked.Exchange(ref _fault, null);

    /// <summary>Stops the threads after their current jobs. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _signal.Release(_threads.Length);
        foreach (var thread in _threads)
        {
            thread.Join();
        }

        _signal.Dispose();
    }

    private void Run()
    {
        var mesher = new SurfaceNets();
        while (true)
        {
            _signal.Wait();
            if (_stopping)
            {
                return;
            }

            if (!_queued.TryDequeue(out var job))
            {
                continue;
            }

            try
            {
                job.Triangles = job.IsStitch
                    ? mesher.MeshTransition(
                        job.Samples,
                        job.Densities,
                        job.CoarseSamples,
                        job.CoarseDensities,
                        job.CoarseOffset,
                        job.Sides,
                        job.Mesh
                    )
                    : mesher.Mesh(job.Samples, job.HasDensities ? job.Densities : ReadOnlySpan<byte>.Empty, job.Mesh);
            }
            catch (Exception exception)
            {
                job.Triangles = -1;
                Interlocked.CompareExchange(ref _fault, exception, null);
            }

            _done.Enqueue(job);
        }
    }
}
