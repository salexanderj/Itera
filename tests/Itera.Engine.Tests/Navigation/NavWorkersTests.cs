using Itera.Engine.Navigation;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// <see cref="NavWorkers"/>: every item of a set runs once, on any worker, each with its own number; a set asked to stop
/// starts no item; a worker's exception reaches the caller once every worker is done; and a quarter of the processors
/// work.
/// </summary>
public sealed class NavWorkersTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 500)]
    [InlineData(2, 1)]
    [InlineData(2, 500)]
    [InlineData(5, 3)]
    public void Every_item_runs_once_on_a_worker_of_the_set(int helpers, int items)
    {
        using var workers = new NavWorkers(helpers, () => false);
        var work = new Counting(items);

        workers.Run(items, work);
        workers.Run(items, work);

        Assert.All(work.Runs, runs => Assert.Equal(2, runs));
        Assert.All(work.Workers, worker => Assert.InRange(worker, 0, workers.Count - 1));
    }

    [Fact]
    public void A_set_asked_to_stop_starts_no_item()
    {
        using var workers = new NavWorkers(1, () => true);
        var work = new Counting(10);

        workers.Run(10, work);

        Assert.All(work.Runs, runs => Assert.Equal(0, runs));
    }

    [Fact]
    public void A_workers_exception_reaches_the_caller_and_the_next_set_runs()
    {
        using var workers = new NavWorkers(2, () => false);

        Assert.Throws<InvalidOperationException>(() => workers.Run(100, new Throwing()));
        var work = new Counting(100);
        workers.Run(100, work);
        Assert.All(work.Runs, runs => Assert.Equal(1, runs));
    }

    [Theory]
    [InlineData(16, 3)]
    [InlineData(12, 2)]
    [InlineData(8, 1)]
    [InlineData(4, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 0)]
    public void A_quarter_of_the_processors_work(int processors, int helpers) =>
        Assert.Equal(helpers, NavWorkers.HelpersFor(processors));

    private sealed class Counting(int items) : INavWork
    {
        public int[] Runs { get; } = new int[items];

        public int[] Workers { get; } = new int[items];

        public void Run(int item, int worker)
        {
            Interlocked.Increment(ref Runs[item]);
            Workers[item] = worker;
        }
    }

    private sealed class Throwing : INavWork
    {
        public void Run(int item, int worker)
        {
            if (item == 37)
            {
                throw new InvalidOperationException("item 37");
            }
        }
    }
}
