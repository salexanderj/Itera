using Itera.Engine.Physics;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

public sealed class StepWorkersTests
{
    [Fact]
    public void A_pass_runs_on_every_worker_at_once_and_each_item_is_taken_once()
    {
        using var workers = new StepWorkers(4);
        var caller = Environment.CurrentManagedThreadId;
        var taken = new int[1000];
        var ran = new int[workers.Count];
        var threads = new int[workers.Count];
        var next = -1;
        // Each worker waits until all have started, so the pass is on four threads at once.
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

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            workers.Run(worker =>
            {
                Interlocked.Increment(ref ran[worker]);
                if (worker == thrower)
                {
                    throw new InvalidOperationException("worker " + worker);
                }
            })
        );
        workers.Run(worker => Interlocked.Increment(ref ran[worker]));

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
        counted = workers.AllocatedBytes;
        workers.Run(worker => arrays[worker] = worker == 2 ? new byte[4096] : []);

        Assert.Equal(0, allocated);
        Assert.Equal(0, helpers);
        Assert.InRange(workers.AllocatedBytes - counted, 4096, 8192);
    }

    [Fact]
    public void The_option_sets_the_threads_and_0_takes_a_quarter_of_the_processors()
    {
        using var three = new StepWorkers(Options.Create(new EngineOptions { Physics = { StepThreads = 3 } }));
        using var otherwise = new StepWorkers(Options.Create(new EngineOptions()));

        Assert.Equal(3, three.Count);
        Assert.Equal(StepWorkers.ThreadCount(Environment.ProcessorCount), otherwise.Count);
        Assert.Equal((1, 3, 6), (StepWorkers.ThreadCount(4), StepWorkers.ThreadCount(12), StepWorkers.ThreadCount(64)));
    }

    [Fact]
    public void Disposed_twice_it_stops_and_refuses_a_pass()
    {
        var workers = new StepWorkers(2);
        workers.Dispose();
        workers.Dispose();

        Assert.Throws<ObjectDisposedException>(() => workers.Run(_ => { }));
    }
}
