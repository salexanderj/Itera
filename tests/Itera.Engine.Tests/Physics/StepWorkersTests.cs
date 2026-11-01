using System.Diagnostics;
using Itera.Engine.Physics;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

public sealed class StepWorkersTests
{
    [Fact]
    public void A_pass_runs_on_every_worker_that_comes_while_the_caller_works_and_each_item_is_taken_once()
    {
        using var workers = new StepWorkers(4);
        var caller = Environment.CurrentManagedThreadId;
        var taken = new int[1000];
        var ran = new int[workers.Count];
        var threads = new int[workers.Count];
        var next = -1;
        // Each worker waits until all have started, the caller too, so every helper comes and the pass is on four threads
        // at once.
        using var started = new CountdownEvent(workers.Count);
        for (var pass = 0; pass < 20; pass++)
        {
            next = -1;
            started.Reset();
            workers.Run(worker =>
            {
                ran[worker]++;
                threads[worker] = Environment.CurrentManagedThreadId;
                started.Signal();
                Assert.True(started.Wait(TestTime.Limit(10)));
                for (var i = Interlocked.Increment(ref next); i < taken.Length; i = Interlocked.Increment(ref next))
                {
                    Interlocked.Increment(ref taken[i]);
                }
            });
        }

        Assert.Equal((4, 20L), (workers.Count, workers.Passes));
        Assert.All(taken, count => Assert.Equal(20, count));
        Assert.All(ran, count => Assert.Equal(20, count));
        Assert.Equal(caller, threads[0]);
        Assert.Equal(4, threads.Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_workers_exception_comes_back_from_the_pass_and_the_next_pass_runs(int thrower)
    {
        using var workers = new StepWorkers(3);
        var ran = new int[workers.Count];
        var (first, second) = (0, 0);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            workers.Run(worker =>
            {
                Meet(ref first, workers.Count);
                Interlocked.Increment(ref ran[worker]);
                if (worker == thrower)
                {
                    throw new InvalidOperationException("worker " + worker);
                }
            })
        );
        workers.Run(worker =>
        {
            Meet(ref second, workers.Count);
            Interlocked.Increment(ref ran[worker]);
        });

        Assert.Equal("worker " + thrower, thrown.Message);
        Assert.All(ran, count => Assert.Equal(2, count));
    }

    [Fact]
    public void One_thread_runs_the_pass_on_the_caller_and_a_pass_inside_a_pass_is_refused()
    {
        using var workers = new StepWorkers(1);
        var thread = 0;
        var inner = false;

        workers.Run(worker =>
        {
            thread = Environment.CurrentManagedThreadId;
            inner = Assert.Throws<InvalidOperationException>(() => workers.Run(_ => { })) is not null;
        });

        Assert.Equal(1, workers.Count);
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
        Assert.True(inner);
    }

    [Fact]
    public void A_pass_allocates_nothing_and_what_the_work_allocates_is_counted_on_every_thread()
    {
        using var workers = new StepWorkers(3);
        var sum = new long[workers.Count];
        Action<int> work = worker => sum[worker]++;
        workers.Run(work);
        var counted = workers.AllocatedBytes;

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            workers.Run(work);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var helpers = workers.AllocatedBytes - counted;
        var arrays = new byte[workers.Count][];
        var met = 0;
        counted = workers.AllocatedBytes;
        workers.Run(worker =>
        {
            Meet(ref met, workers.Count);
            arrays[worker] = worker == 2 ? new byte[4096] : [];
        });

        Assert.Equal(0, allocated);
        Assert.Equal(0, helpers);
        Assert.InRange(workers.AllocatedBytes - counted, 4096, 8192);
    }

    [Fact]
    public void The_option_sets_the_threads_and_0_takes_half_the_processors_2_to_6()
    {
        using var three = new StepWorkers(Options.Create(new EngineOptions { Physics = { StepThreads = 3 } }));
        using var otherwise = new StepWorkers(Options.Create(new EngineOptions()));

        Assert.Equal(3, three.Count);
        Assert.Equal(StepWorkers.ThreadCount(Environment.ProcessorCount), otherwise.Count);
        Assert.Equal(
            (1, 2, 2, 3, 4, 6, 6),
            (
                StepWorkers.ThreadCount(1),
                StepWorkers.ThreadCount(2),
                StepWorkers.ThreadCount(4),
                StepWorkers.ThreadCount(6),
                StepWorkers.ThreadCount(8),
                StepWorkers.ThreadCount(12),
                StepWorkers.ThreadCount(64)
            )
        );
    }

    [Fact]
    public void A_helper_that_comes_after_the_callers_part_skips_the_pass_so_no_work_runs_once_its_pass_returned()
    {
        using var workers = new StepWorkers(4);
        var current = -1;
        var late = 0;
        for (var pass = 0; pass < 2000; pass++)
        {
            var mine = pass;
            Volatile.Write(ref current, pass);
            workers.Run(worker =>
            {
                if (Volatile.Read(ref current) != mine)
                {
                    Interlocked.Increment(ref late);
                }
            });
            Volatile.Write(ref current, -1);
        }

        // The caller's part is short, so many helpers come after it; none runs a pass that has returned.
        Assert.Equal(0, late);
        Assert.Equal(2000, workers.Passes);
    }

    [Fact]
    public void A_pool_stopped_during_a_pass_ends_the_pass_and_refuses_the_next()
    {
        var workers = new StepWorkers(3);
        var ran = 0;

        workers.Run(worker =>
        {
            Interlocked.Increment(ref ran);
            if (worker == 0)
            {
                workers.Dispose();
            }
        });

        Assert.InRange(ran, 1, 3);
        Assert.Throws<ObjectDisposedException>(() => workers.Run(_ => { }));
    }

    [Fact]
    public void Disposed_twice_it_stops_and_refuses_a_pass()
    {
        var workers = new StepWorkers(2);
        workers.Dispose();
        workers.Dispose();

        Assert.Throws<ObjectDisposedException>(() => workers.Run(_ => { }));
    }

    /// <summary>
    /// Waits in a pass until <paramref name="count"/> workers have come to it, spinning so it allocates nothing: the
    /// caller's part lasts until every helper has joined.
    /// </summary>
    private static void Meet(ref int met, int count)
    {
        Interlocked.Increment(ref met);
        var limit = Stopwatch.GetTimestamp() + (long)(TestTime.Limit(10).TotalSeconds * Stopwatch.Frequency);
        var spin = default(SpinWait);
        while (Volatile.Read(ref met) < count)
        {
            Assert.True(Stopwatch.GetTimestamp() < limit, $"{Volatile.Read(ref met)} of {count} workers came.");
            spin.SpinOnce();
        }
    }
}
