using System.Runtime.ExceptionServices;

namespace Itera.Engine.Navigation;

/// <summary>A piece of the navigation's work that <see cref="NavWorkers"/> runs item by item, on any of its threads.</summary>
internal interface INavWork
{
    /// <summary>Does item <paramref name="item"/> on worker <paramref name="worker"/> (0 the calling thread), with that worker's scratch.</summary>
    void Run(int item, int worker);
}

/// <summary>
/// The navigation thread's helpers (<see cref="Run"/>): the calling thread and each helper take the next item not yet
/// started until none is left, so the items take about as long as the longest share. Each worker keeps scratch of its
/// own by its number, so each item comes out as it would on any thread. The helpers wait between sets, below normal
/// priority (<see cref="ThreadPriorities"/>, on Linux too) so the step's threads keep the processors. One calling thread
/// at a time; allocates nothing once made.
/// </summary>
internal sealed class NavWorkers : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly Thread[] _threads;
    private readonly Func<bool> _stopping;
    private INavWork? _work;
    private int _items;
    private int _next;
    private int _set;
    private int _working;
    private bool _disposed;
    private ExceptionDispatchInfo? _fault;

    /// <param name="helpers">Helper threads besides the calling one; none runs every item on the calling thread.</param>
    /// <param name="stopping">True once no more items should be started: a set stops between its items.</param>
    public NavWorkers(int helpers, Func<bool> stopping)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(helpers);
        _stopping = stopping;
        _threads = new Thread[helpers];
        for (var i = 0; i < helpers; i++)
        {
            var worker = i + 1;
            _threads[i] = new Thread(() => Help(worker))
            {
                Name = $"Itera navigation {worker + 1}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _threads[i].Start();
        }
    }

    /// <summary>The workers, the calling thread among them.</summary>
    public int Count => _threads.Length + 1;

    /// <summary>
    /// The helpers on a machine of <paramref name="processors"/> logical processors: a quarter of the processors work at
    /// once, the calling thread among them.
    /// </summary>
    public static int HelpersFor(int processors) => Math.Max(1, processors / 4) - 1;

    /// <summary>
    /// Runs items 0 to <paramref name="items"/> - 1 of <paramref name="work"/> on the calling thread and the helpers.
    /// Returns once every item is done, or once the set stopped between items. Rethrows the first exception a worker met.
    /// </summary>
    public void Run(int items, INavWork work)
    {
        if (items <= 0)
        {
            return;
        }

        if (items == 1 || _threads.Length == 0)
        {
            for (var item = 0; item < items && !_stopping(); item++)
            {
                work.Run(item, 0);
            }

            return;
        }

        lock (_lock)
        {
            _work = work;
            _items = items;
            _next = 0;
            _working = _threads.Length;
            _set++;
            Monitor.PulseAll(_lock);
        }

        try
        {
            Work(0);
        }
        catch (Exception ex)
        {
            // The helpers finish their items first, so no set ever overlaps the next.
            _fault ??= ExceptionDispatchInfo.Capture(ex);
        }

        lock (_lock)
        {
            while (_working > 0)
            {
                Monitor.Wait(_lock);
            }

            _work = null;
        }

        var fault = _fault;
        _fault = null;
        fault?.Throw();
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

        foreach (var thread in _threads)
        {
            thread.Join(StopTimeout);
        }
    }

    /// <summary>A helper: waits for each set, works on it, and says when it is done.</summary>
    private void Help(int worker)
    {
        // Below normal on Linux too, where .NET's priority changes nothing.
        ThreadPriorities.Lower();
        var seen = 0;
        while (true)
        {
            lock (_lock)
            {
                while (_set == seen && !_disposed)
                {
                    Monitor.Wait(_lock);
                }

                if (_disposed)
                {
                    return;
                }

                seen = _set;
            }

            try
            {
                Work(worker);
            }
            catch (Exception ex)
            {
                _fault ??= ExceptionDispatchInfo.Capture(ex);
            }

            lock (_lock)
            {
                if (--_working == 0)
                {
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    /// <summary>Does the next item not yet started on worker <paramref name="worker"/>, until none is left.</summary>
    private void Work(int worker)
    {
        var work = _work;
        while (work is not null && !_stopping())
        {
            var item = Interlocked.Increment(ref _next) - 1;
            if (item >= _items)
            {
                return;
            }

            work.Run(item, worker);
        }
    }
}
