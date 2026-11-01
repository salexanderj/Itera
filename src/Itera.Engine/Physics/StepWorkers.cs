using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Physics;

/// <summary>
/// The threads a fixed step's parallel passes run on (<c>PlayerMovementSystem</c>'s moves): <see cref="Run"/> runs a
/// piece of work on each of <see cref="Count"/> threads at once, the calling thread as worker 0 and helper threads as the
/// rest, and returns once every one is done. Each worker keeps scratch of its own by its index, and takes the next item
/// of the pass from a counter it shares (<see cref="Interlocked.Increment(ref int)"/>), so each item's result is the same
/// whichever thread takes it. The helpers wait between passes. Run-wide (<c>Engine:Physics:StepThreads</c>); one calling
/// thread at a time, the main thread; allocates nothing once made.
/// </summary>
public sealed class StepWorkers : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly Thread[] _helpers;
    private Action<int>? _work;
    private int _pass;
    private int _working;
    private long _allocated;
    private bool _disposed;
    private ExceptionDispatchInfo? _fault;

    /// <param name="threads">The threads a pass runs on, the calling one among them: at least 1.</param>
    public StepWorkers(int threads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        _helpers = new Thread[threads - 1];
        for (var i = 0; i < _helpers.Length; i++)
        {
            var worker = i + 1;
            _helpers[i] = new Thread(() => Help(worker)) { Name = $"Itera step {worker + 1}", IsBackground = true };
            _helpers[i].Start();
        }
    }

    /// <param name="options">The engine's options: <c>Engine:Physics:StepThreads</c>, 0 for <see cref="ThreadCount"/>.</param>
    public StepWorkers(IOptions<EngineOptions> options)
        : this(Threads(options)) { }

    /// <summary>The threads a pass runs on, the calling one among them.</summary>
    public int Count => _helpers.Length + 1;

    /// <summary>The passes run so far: a test reads it to see a system's work went to the workers.</summary>
    public long Passes { get; private set; }

    /// <summary>
    /// For tests: the bytes the work allocated on every worker's thread, the calling one's too, over every pass so far.
    /// A test of a system on these threads reads it, since a test's own count is of its thread alone.
    /// </summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocated);

    /// <summary>
    /// A pass's threads for <paramref name="processors"/> logical processors: a quarter of them, 1 to 6, the calling one
    /// among them, as the physics takes for its own (the voxel workers take half the processors).
    /// </summary>
    public static int ThreadCount(int processors) => Math.Clamp(processors / 4, 1, 6);

    /// <summary>
    /// Runs <paramref name="work"/> on every worker at once, each given its index (0, the calling thread, to
    /// <see cref="Count"/> - 1), and returns once all are done. Rethrows the first exception a worker met. Main thread;
    /// not from within a pass.
    /// </summary>
    public void Run(Action<int> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_work is not null)
        {
            throw new InvalidOperationException("A pass of the step's workers runs already.");
        }

        _fault = null;
        Passes++;
        if (_helpers.Length > 0)
        {
            lock (_lock)
            {
                _work = work;
                _working = _helpers.Length;
                _pass++;
                Monitor.PulseAll(_lock);
            }
        }
        else
        {
            _work = work;
        }

        try
        {
            Work(work, 0);
        }
        finally
        {
            if (_helpers.Length > 0)
            {
                lock (_lock)
                {
                    while (_working > 0)
                    {
                        Monitor.Wait(_lock);
                    }
                }
            }

            _work = null;
        }

        _fault?.Throw();
    }

    /// <summary>Stops the helpers and waits for them, up to a few seconds.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Monitor.PulseAll(_lock);
        }

        foreach (var thread in _helpers)
        {
            thread.Join(StopTimeout);
        }
    }

    private static int Threads(IOptions<EngineOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var threads = options.Value.Physics.StepThreads;
        return threads > 0 ? threads : ThreadCount(Environment.ProcessorCount);
    }

    /// <summary>A helper: waits for each pass, works on it, and says when it is done.</summary>
    private void Help(int worker)
    {
        var seen = 0;
        while (true)
        {
            Action<int>? work;
            lock (_lock)
            {
                while (_pass == seen && !_disposed)
                {
                    Monitor.Wait(_lock);
                }

                if (_disposed)
                {
                    return;
                }

                seen = _pass;
                work = _work;
            }

            try
            {
                if (work is not null)
                {
                    Work(work, worker);
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _fault ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (--_working == 0)
                    {
                        Monitor.PulseAll(_lock);
                    }
                }
            }
        }
    }

    /// <summary>One worker's part of a pass, its allocations counted.</summary>
    private void Work(Action<int> work, int worker)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            work(worker);
        }
        catch (Exception ex) when (worker == 0)
        {
            lock (_lock)
            {
                _fault ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
        finally
        {
            Interlocked.Add(ref _allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
