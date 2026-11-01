using System.Runtime.ExceptionServices;
using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Physics;

/// <summary>
/// The threads a fixed step's parallel passes run on (<c>PlayerMovementSystem</c>'s moves, and a game's own systems'
/// passes): <see cref="Run"/> runs a piece of work on the calling thread, as worker 0, and on each helper thread that
/// comes to the pass while the caller's own part runs. A helper the busy processors have not run by then skips the pass,
/// so a pass never waits for one that has not started. Each worker keeps scratch of its own by its index, and takes the
/// next item of the pass from a counter it shares (<see cref="Interlocked.Increment(ref int)"/>), so each item's result is
/// the same whichever thread takes it, and no item waits on a worker that may not come. The helpers wait between passes.
/// Each worker's part is the zone <c>step workers.part</c>, and the caller's wait for the helpers in the pass
/// <c>step workers.wait</c>. Run-wide (<c>Engine:Physics:StepThreads</c>); one calling thread at a time, the main thread;
/// allocates nothing once made.
/// </summary>
public sealed class StepWorkers : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly ZoneId s_partZone = Zones.Name("step workers.part");
    private static readonly ZoneId s_waitZone = Zones.Name("step workers.wait");

    private readonly object _lock = new();
    private readonly Thread[] _helpers;
    private Action<int>? _work;
    private int _pass;
    private bool _open;
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

    /// <summary>The threads a pass may run on, the calling one among them.</summary>
    public int Count => _helpers.Length + 1;

    /// <summary>The passes run so far: a test reads it to see a system's work went to the workers.</summary>
    public long Passes { get; private set; }

    /// <summary>
    /// For tests: the bytes the work allocated on every worker's thread, the calling one's too, over every pass so far.
    /// A test of a system on these threads reads it, since a test's own count is of its thread alone.
    /// </summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocated);

    /// <summary>
    /// A pass's threads for <paramref name="processors"/> logical processors, the calling one among them: a quarter of
    /// them, as the physics takes for its own (the voxel workers take half the processors), but at least 2, so a machine
    /// of six or fewer gains too, and at most 6; 1 on a machine of one.
    /// </summary>
    public static int ThreadCount(int processors) => processors < 2 ? 1 : Math.Clamp(processors / 4, 2, 6);

    /// <summary>
    /// Runs <paramref name="work"/> on the calling thread, given index 0, and on each helper that comes while it runs,
    /// given its own (1 to <see cref="Count"/> - 1), at once; returns once the caller's part and each helper's in the pass
    /// are done. A helper that comes after the caller's part skips the pass, so the work must not leave anything to a
    /// worker by its index. Rethrows the first exception a worker met. Main thread; not from within a pass.
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
                _open = true;
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
                using var wait = Zones.Time(s_waitZone);
                lock (_lock)
                {
                    // Closed: a helper not in the pass yet stays out of it, and the caller waits for those in it alone.
                    _open = false;
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

    /// <summary>
    /// A helper: waits for each pass, joins it while it is open, works on it, and says when it is done. A pass that closed
    /// before it came, it skips; stopped, it ends, in a pass or not, since the caller waits only for the helpers in it.
    /// </summary>
    private void Help(int worker)
    {
        var seen = 0;
        while (true)
        {
            Action<int> work;
            lock (_lock)
            {
                while (true)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (_pass != seen)
                    {
                        seen = _pass;
                        if (_open)
                        {
                            break;
                        }
                    }

                    Monitor.Wait(_lock);
                }

                work = _work!;
                _working++;
            }

            try
            {
                Work(work, worker);
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

    /// <summary>One worker's part of a pass, timed as a zone and its allocations counted.</summary>
    private void Work(Action<int> work, int worker)
    {
        using var part = Zones.Time(s_partZone);
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
