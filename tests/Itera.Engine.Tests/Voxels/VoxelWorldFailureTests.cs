using System.Numerics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// Streaming's failure paths at the <see cref="VoxelWorld"/> level, each ending with the native memory back where it
/// settles (these paths free voxel memory under failure, and no test held them): a generator that throws
/// on a later layer ends the world's next update naming the column, and the world still disposes; a column unloaded
/// while its job waits in the generator frees what the job made; a budget that runs out on a later layer frees the
/// layers before it; and a reset while columns are still loading leaves the memory of a world that never had one.
/// </summary>
public sealed class VoxelWorldFailureTests : IDisposable
{
    private readonly List<VoxelWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    [Fact]
    public void A_generator_that_throws_on_a_later_layer_ends_the_update_naming_the_column_and_the_world_disposes()
    {
        var generator = new LayeredGenerator { ThrowFromLayer = 2 };
        var world = Create(generator);
        world.SetCenters([Vector3.Zero]);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        InvalidOperationException? failure = null;
        while (failure is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The worker never failed.");
            try
            {
                world.Update(0);
            }
            catch (InvalidOperationException exception)
            {
                failure = exception;
            }

            Thread.Sleep(1);
        }

        // Named by the column it failed on, the generator's own exception inside.
        Assert.Contains("column", failure.Message, StringComparison.Ordinal);
        Assert.Equal("a layer that fails", failure.InnerException?.Message);
        var disposing = new Thread(world.Dispose);
        disposing.Start();
        Assert.True(disposing.Join(TimeSpan.FromSeconds(10)), "The world did not dispose after the fault.");
        Assert.Equal(0, world.UsedBytes);
    }

    [Fact]
    public void A_column_unloaded_while_its_job_waits_in_the_generator_frees_what_the_job_made()
    {
        using var gate = new ManualResetEventSlim(false);
        var generator = new LayeredGenerator { Gate = gate, WaitFromLayer = 1 };
        var world = Create(generator);
        world.SetCenters([Vector3.Zero]);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Volatile.Read(ref generator.Waiting) == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "No job reached the gate.");
                world.Update(0);
                Thread.Sleep(1);
            }

            Assert.True(world.UsedBytes > 0, "The waiting job had made no layer.");
            world.SetCenters([]);
            world.Update(0);
        }
        finally
        {
            gate.Set();
        }

        Settle(world, empty: true);
        Assert.Equal(0, world.ResidentColumns);
        Assert.Equal(0, world.UsedBytes);
    }

    [Fact]
    public void A_budget_that_runs_out_on_a_later_layer_frees_the_layers_before_it()
    {
        var generator = new LayeredGenerator { Noisy = true };
        var world = Create(generator, budgetMegabytes: 8, radius: 4);
        world.SetCenters([Vector3.Zero]);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        // Memory falls back as each failed job frees its layers, so the column is tried again: wait for failures only.
        while (world.FailedJobCount < 3)
        {
            Assert.True(DateTime.UtcNow < deadline, "No job ran out of memory.");
            world.Update(0);
            Thread.Sleep(1);
        }

        Assert.True(generator.MostLayerFilled > 0, "Every job failed on its first layer.");
        world.SetCenters([]);
        Settle(world, empty: true);

        Assert.Equal(0, world.ResidentColumns);
        Assert.Equal(0, world.UsedBytes);
    }

    [Fact]
    public void A_reset_while_columns_load_leaves_the_memory_of_a_world_that_had_none()
    {
        var fresh = Create(new SlopeGenerator());
        fresh.SetCenters([Vector3.Zero]);
        Settle(fresh);
        var world = Create(new SlopeGenerator());
        world.SetCenters([Vector3.Zero]);
        world.Update(0);
        Assert.True(world.PendingColumns > 0);

        world.Reset();
        Settle(world);

        Assert.Equal(fresh.ResidentColumns, world.ResidentColumns);
        Assert.Equal(fresh.UsedBytes, world.UsedBytes);
        Assert.Equal(fresh.StoredBricks, world.StoredBricks);
        world.SetCenters([]);
        Settle(world, empty: true);
        Assert.Equal(0, world.UsedBytes);
    }

    private VoxelWorld Create(IChunkGenerator generator, int budgetMegabytes = 64, double radius = 3)
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = radius,
            Levels = 2,
            MemoryBudgetMegabytes = budgetMegabytes,
            WorkerThreads = 2,
            Seed = 7,
        };
        var world = new VoxelWorld(settings, 0, generator, NullLogger<VoxelWorld>.Instance);
        world.Initialize();
        _worlds.Add(world);
        return world;
    }

    /// <summary>
    /// Updates until nothing loads, frees, or runs on a worker, and every column wanted is resident (or, with
    /// <paramref name="empty"/>, none is), twice running.
    /// </summary>
    private static void Settle(VoxelWorld world, bool empty = false)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var quiet = 0;
        while (quiet < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "The voxel world did not settle.");
            world.Update(0);
            var done = empty ? world.ResidentColumns == 0 : world.WantedColumnsResident;
            var idle = world.PendingColumns == 0 && world.PendingFrees == 0 && world.JobsInFlight == 0;
            quiet = idle && done ? quiet + 1 : 0;
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Every layer of every column mixed: stone below a layer's middle and air above (cheap), or noise (dear, for the
    /// budget). Its <see cref="Fill"/> may wait at a gate from a layer up, or throw from a layer up.
    /// </summary>
    private sealed class LayeredGenerator : IChunkGenerator
    {
        public int Waiting;

        public ManualResetEventSlim? Gate { get; init; }

        public int WaitFromLayer { get; init; } = int.MaxValue;

        public int ThrowFromLayer { get; init; } = int.MaxValue;

        public bool Noisy { get; init; }

        /// <summary>The highest layer of a column any fill reached.</summary>
        public int MostLayerFilled => Volatile.Read(ref _mostLayer);

        private int _mostLayer;

        public string Id => "test.layered";

        public int Version => 1;

        public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) =>
            layers.Fill(ChunkFill.Mixed);

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels)
        {
            var layer = chunk.Y - VoxelGrid.Unit.MinChunkYAt(chunk.Level);
            int seen;
            while (layer > (seen = Volatile.Read(ref _mostLayer)))
            {
                Interlocked.CompareExchange(ref _mostLayer, layer, seen);
            }

            if (layer >= ThrowFromLayer)
            {
                throw new InvalidOperationException("a layer that fails");
            }

            if (Gate is not null && layer >= WaitFromLayer)
            {
                Interlocked.Increment(ref Waiting);
                Gate.Wait();
            }

            var span = voxels[..VoxelConstants.ChunkVoxels];
            if (!Noisy)
            {
                span[..(span.Length / 2)].Fill(3);
                span[(span.Length / 2)..].Clear();
                return;
            }

            var hash = (uint)(chunk.X * 73_856_093) ^ (uint)(chunk.Y * 19_349_663) ^ (uint)(chunk.Z * 83_492_791);
            for (var i = 0; i < span.Length; i++)
            {
                hash ^= hash << 13;
                hash ^= hash >> 17;
                hash ^= hash << 5;
                span[i] = (byte)(1 + (hash & 3));
            }
        }
    }
}
