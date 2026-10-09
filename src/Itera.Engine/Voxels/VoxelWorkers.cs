using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Itera.Engine.Voxels;

/// <summary>
/// Background threads that run <see cref="ColumnJob"/>s. The main thread queues jobs with <see cref="Enqueue"/> and
/// collects finished ones with <see cref="TryTakeFinished"/>; the queues' locks are the hand-off, so a job's contents are
/// visible to whichever side takes it. The threads run below normal priority so Bepu's workers keep the processors
/// during the physics step. Each thread has its own 4 MB of scratch for one dense chunk's materials and densities.
/// </summary>
internal sealed class VoxelWorkers : IDisposable
{
    private readonly IChunkGenerator _generator;
    private readonly VoxelMemory _memory;
    private readonly Thread[] _threads;
    private readonly Queue<ColumnJob> _queued = new();
    private readonly Queue<ColumnJob> _frees = new();
    private readonly Queue<ColumnJob> _finished = new();
    private readonly object _queuedLock = new();
    private readonly object _finishedLock = new();
    private volatile bool _stopping;
    private volatile ExceptionDispatchInfo? _fault;
    private int _running;

    /// <param name="generator">The world's generator; called from every worker at once.</param>
    /// <param name="memory">Where chunks are stored.</param>
    /// <param name="threadCount">Worker threads, at least one.</param>
    public VoxelWorkers(IChunkGenerator generator, VoxelMemory memory, int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);
        _generator = generator;
        _memory = memory;
        _threads = new Thread[threadCount];
    }

    /// <summary>Worker threads.</summary>
    public int ThreadCount => _threads.Length;

    /// <summary>Generation jobs queued or running, not yet taken back. Free jobs are not counted.</summary>
    public int InFlight { get; private set; }

    /// <summary>Free jobs queued or running, not yet taken back.</summary>
    public int FreesInFlight { get; private set; }

    /// <summary>True after a worker ended with an exception. Main thread: call <see cref="ThrowIfFaulted"/>.</summary>
    public bool IsFaulted => _fault is not null;

    /// <summary>Starts the threads. Main thread; once.</summary>
    public void Start()
    {
        if (_threads[0] is not null)
        {
            throw new InvalidOperationException("The voxel workers are started already.");
        }

        for (var i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Run)
            {
                Name = $"Itera voxels {i + 1}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _threads[i].Start();
        }
    }

    /// <summary>Queues a job. Main thread.</summary>
    public void Enqueue(ColumnJob job)
    {
        if (job.IsFree)
        {
            FreesInFlight++;
        }
        else
        {
            InFlight++;
        }

        lock (_queuedLock)
        {
            // Free jobs go first, so memory comes back before more is taken.
            (job.IsFree ? _frees : _queued).Enqueue(job);
            Monitor.Pulse(_queuedLock);
        }
    }

    /// <summary>Takes one finished job, if there is one. Main thread.</summary>
    public bool TryTakeFinished(out ColumnJob job)
    {
        lock (_finishedLock)
        {
            if (_finished.TryDequeue(out job!))
            {
                if (job.IsFree)
                {
                    FreesInFlight--;
                }
                else
                {
                    InFlight--;
                }

                return true;
            }

            return false;
        }
    }

    /// <summary>Rethrows the exception that ended a worker, if any. Main thread.</summary>
    public void ThrowIfFaulted() => _fault?.Throw();

    /// <summary>
    /// Asks every thread to stop after its current job and waits up to <paramref name="timeout"/> in total. Returns true
    /// when all have ended. Jobs still queued are dropped; their memory goes with <see cref="VoxelMemory"/>.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        lock (_queuedLock)
        {
            _stopping = true;
            Monitor.PulseAll(_queuedLock);
        }

        // A steady clock: a wall-clock change does not stretch or cut the wait.
        var start = Stopwatch.GetTimestamp();
        var stopped = true;
        foreach (var thread in _threads)
        {
            if (thread is null)
            {
                continue;
            }

            var left = timeout - Stopwatch.GetElapsedTime(start);
            stopped &= thread.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

        return stopped && Volatile.Read(ref _running) == 0;
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(2));

    private void Run()
    {
        Interlocked.Increment(ref _running);
        ColumnJob? job = null;
        try
        {
            var scratch = new byte[VoxelConstants.ChunkVoxels];
            var densities = new byte[VoxelConstants.ChunkVoxels];
            while (true)
            {
                lock (_queuedLock)
                {
                    while (_queued.Count == 0 && _frees.Count == 0 && !_stopping)
                    {
                        Monitor.Wait(_queuedLock);
                    }

                    if (_stopping)
                    {
                        return;
                    }

                    job = _frees.Count > 0 ? _frees.Dequeue() : _queued.Dequeue();
                }

                job.Run(_generator, _memory, scratch, densities);
                lock (_finishedLock)
                {
                    _finished.Enqueue(job);
                }

                job = null;
            }
        }
        catch (Exception ex)
        {
            // Named by the column it failed on, so the log says where.
            var failure = job is null
                ? ex
                : new InvalidOperationException($"A voxel worker failed on column {job.Key}: {ex.Message}", ex);
            _fault ??= ExceptionDispatchInfo.Capture(failure);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}
