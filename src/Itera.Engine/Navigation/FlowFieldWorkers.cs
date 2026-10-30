using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Itera.Engine.Navigation;

/// <summary>
/// Builds a set of flow fields at once (<see cref="Build"/>): the calling thread and its helper threads each take the next
/// field not yet started, each with a <see cref="FlowFieldBuilder"/> of its own, so a set takes about as long as its
/// longest field. Each field comes out as one builder alone would build it, whichever thread takes it. The helpers wait
/// between sets, below normal priority so Bepu's workers keep the processors during the physics step. One calling thread
/// at a time; allocates nothing once made.
/// </summary>
internal sealed class FlowFieldWorkers : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly Thread[] _threads;
    private readonly FlowFieldBuilder[] _builders;
    private readonly Func<bool> _stopping;
    private NavGrid? _grid;
    private NavFieldSpec[] _specs = [];
    private byte[][] _walkable = [];
    private FlowField[] _fields = [];
    private double[] _milliseconds = [];
    private int _next;
    private int _set;
    private int _working;
    private bool _disposed;
    private ExceptionDispatchInfo? _fault;

    /// <param name="helpers">Helper threads besides the calling one; none builds every field on the calling thread.</param>
    /// <param name="stopping">True once no more fields should be started: a set stops between its fields.</param>
    public FlowFieldWorkers(int helpers, Func<bool> stopping)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(helpers);
        _stopping = stopping;
        _threads = new Thread[helpers];
        _builders = new FlowFieldBuilder[helpers + 1];
        for (var i = 0; i < _builders.Length; i++)
        {
            _builders[i] = new FlowFieldBuilder();
        }

        for (var i = 0; i < helpers; i++)
        {
            var builder = i + 1;
            _threads[i] = new Thread(() => Help(builder))
            {
                Name = $"Itera navigation {builder + 1}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _threads[i].Start();
        }
    }

    /// <summary>Helper threads besides the calling one.</summary>
    public int Helpers => _threads.Length;

    /// <summary>
    /// The helpers for <paramref name="fields"/> fields on a machine of <paramref name="processors"/> logical processors:
    /// a quarter of the processors build at once, the calling thread among them, and never more than there are fields.
    /// </summary>
    public static int HelpersFor(int fields, int processors) => Math.Clamp(processors / 4, 1, Math.Max(1, fields)) - 1;

    /// <summary>
    /// Builds each of <paramref name="specs"/> over <paramref name="grid"/> into the field of the same index, with the
    /// walkable cells of the same index (<see cref="NavSize.Derive"/>), and writes how long each took. Returns once every
    /// field is built, or once the builds stopped between fields. Rethrows the first exception a helper met.
    /// </summary>
    public void Build(NavGrid grid, NavFieldSpec[] specs, byte[][] walkable, FlowField[] fields, double[] milliseconds)
    {
        lock (_lock)
        {
            _grid = grid;
            _specs = specs;
            _walkable = walkable;
            _fields = fields;
            _milliseconds = milliseconds;
            _next = 0;
            _working = _threads.Length;
            _set++;
            Monitor.PulseAll(_lock);
        }

        Work(0);
        lock (_lock)
        {
            while (_working > 0)
            {
                Monitor.Wait(_lock);
            }
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

        foreach (var thread in _threads)
        {
            thread.Join(StopTimeout);
        }
    }

    /// <summary>A helper: waits for each set, works on it, and says when it is done.</summary>
    private void Help(int builder)
    {
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
                Work(builder);
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

    /// <summary>Builds the next field not yet started with builder <paramref name="builder"/>, until none is left.</summary>
    private void Work(int builder)
    {
        while (!_stopping())
        {
            var field = Interlocked.Increment(ref _next) - 1;
            if (field >= _specs.Length)
            {
                return;
            }

            var start = Stopwatch.GetTimestamp();
            _builders[builder].Build(_grid!, _specs[field], _walkable[field], _fields[field]);
            _milliseconds[field] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }
}
