using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed class VoxelWorkersTests : IDisposable
{
    private const ulong Seed = 17;
    private readonly SlopeGenerator _generator = new();
    private readonly VoxelMemory _memory = new(64 << 20);

    public void Dispose() => _memory.Dispose();

    [Fact]
    public void A_job_builds_the_same_voxels_as_direct_generation()
    {
        var job = RunJob(new ColumnKey(0, 1, -2));

        Assert.Equal(ColumnJobResult.Done, job.Result);
        var expected = new byte[VoxelConstants.ChunkVoxels];
        var actual = new byte[VoxelConstants.ChunkVoxels];
        for (var layer = 0; layer < job.Layers.Length; layer++)
        {
            _generator.Fill(job.Key.Chunk(layer, VoxelGrid.Unit), Seed, expected);
            ChunkTree.Decode(_memory, job.Layers[layer], actual);
            // A span comparison: Assert.Equal compared the 2 MB arrays element by element.
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Layer {layer} differs.");
        }

        Assert.Equal(ChildState.Uniform(SlopeGenerator.Stone), job.Layers[0]);
        Assert.Equal(ChildState.Empty, job.Layers[^1]);
        Assert.InRange(job.FilledLayers, 1, 3);
    }

    [Fact]
    public void Jobs_replay_their_edits_in_order()
    {
        var key = new ColumnKey(0, 0, 0);
        var job = new ColumnJob();
        job.Reset(key, VoxelGrid.Unit, Seed, snapshotSequence: 2);
        job.Edits.Add(
            VoxelEdit.Sphere(new VoxelCoord(64, 250, 64), 40, VoxelEditOperation.Clear) with
            {
                Sequence = 1,
            }
        );
        job.Edits.Add(
            VoxelEdit.Box(new VoxelCoord(50, -600, 50), new VoxelCoord(20, 1000, 20), VoxelEditOperation.Set, 9) with
            {
                Sequence = 2,
            }
        );

        job.Run(_generator, _memory, new byte[VoxelConstants.ChunkVoxels], new byte[VoxelConstants.ChunkVoxels]);

        var expected = new byte[VoxelConstants.ChunkVoxels];
        var actual = new byte[VoxelConstants.ChunkVoxels];
        for (var layer = 0; layer < job.Layers.Length; layer++)
        {
            var chunk = key.Chunk(layer, VoxelGrid.Unit);
            _generator.Fill(chunk, Seed, expected);
            foreach (var edit in job.Edits)
            {
                VoxelEditing.ApplyToRegion(
                    in edit,
                    new GridLevel(VoxelGrid.Unit, 0),
                    chunk.MinVoxel,
                    VoxelConstants.ChunkSize,
                    expected
                );
            }

            ChunkTree.Decode(_memory, job.Layers[layer], actual);
            // A span comparison: Assert.Equal compared the 2 MB arrays element by element.
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Layer {layer} differs.");
        }

        // The box reaches from the stone layers up into the air, so edited uniform layers were filled too.
        Assert.True(job.FilledLayers > 3);
    }

    [Fact]
    public void One_and_four_workers_build_the_same_columns()
    {
        // One column for each of the four workers. Each column's content is pinned by the tests below.
        var columns = Enumerable.Range(0, 4).Select(i => new ColumnKey(0, i % 2 - 1, i / 2 - 1)).ToArray();

        var one = HashesWith(1, columns);
        var four = HashesWith(4, columns);

        Assert.Equal(one, four);
    }

    [Fact]
    public void A_column_always_hashes_to_the_same_value()
    {
        var job = RunJob(new ColumnKey(0, -3, 5));

        // Pinned on the first run, and again when the world grew from 50 to 80 layers and from 80 to 96 (the hash covers
        // every layer). A change means generation, encoding, or decoding no longer gives the same voxels.
        Assert.Equal(0x1F28AA6E5B0A5D25UL, VoxelTestHelpers.ContentHash(_memory, job.Layers));
    }

    [Fact]
    public void Running_out_of_memory_keeps_nothing()
    {
        using var memory = new VoxelMemory(2 << 20);
        var job = new ColumnJob();
        job.Reset(new ColumnKey(0, 0, 0), VoxelGrid.Unit, Seed, 0);

        job.Run(_generator, memory, new byte[VoxelConstants.ChunkVoxels], new byte[VoxelConstants.ChunkVoxels]);

        Assert.Equal(ColumnJobResult.OutOfMemory, job.Result);
        Assert.Equal(0, memory.UsedBytes);
    }

    [Fact]
    public void A_job_cancelled_before_it_runs_does_nothing()
    {
        var job = new ColumnJob();
        job.Reset(new ColumnKey(0, 0, 0), VoxelGrid.Unit, Seed, 0);
        job.Cancel();

        job.Run(_generator, _memory, new byte[VoxelConstants.ChunkVoxels], new byte[VoxelConstants.ChunkVoxels]);

        Assert.Equal(ColumnJobResult.Cancelled, job.Result);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void A_generator_exception_faults_the_workers()
    {
        using var workers = new VoxelWorkers(new ThrowingGenerator(), _memory, 1);
        workers.Start();
        var job = new ColumnJob();
        job.Reset(new ColumnKey(0, 0, 0), VoxelGrid.Unit, Seed, 0);

        workers.Enqueue(job);
        var deadline = TestTime.Deadline(10);
        while (!workers.IsFaulted && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1);
        }

        // Named by the column it failed on, the generator's own exception inside.
        var ex = Assert.Throws<InvalidOperationException>(workers.ThrowIfFaulted);
        Assert.Contains(new ColumnKey(0, 0, 0).ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Equal("generator failure", ex.InnerException?.Message);
    }

    /// <summary>Starting the workers twice is refused (it made a second set of threads).</summary>
    [Fact]
    public void Starting_twice_is_refused()
    {
        using var workers = new VoxelWorkers(new SlopeGenerator(), _memory, 1);
        workers.Start();

        Assert.Throws<InvalidOperationException>(workers.Start);
    }

    [Fact]
    public void Workers_stop_and_can_be_stopped_again()
    {
        var workers = new VoxelWorkers(_generator, _memory, 2);
        workers.Start();

        Assert.True(workers.Stop(TestTime.Limit(5)));
        Assert.True(workers.Stop(TestTime.Limit(5)));
        workers.Dispose();
    }

    private ColumnJob RunJob(ColumnKey key)
    {
        var job = new ColumnJob();
        job.Reset(key, VoxelGrid.Unit, Seed, 0);
        job.Run(_generator, _memory, new byte[VoxelConstants.ChunkVoxels], new byte[VoxelConstants.ChunkVoxels]);
        return job;
    }

    private ulong[] HashesWith(int threads, ColumnKey[] columns)
    {
        using var memory = new VoxelMemory(64 << 20);
        using var workers = new VoxelWorkers(_generator, memory, threads);
        workers.Start();
        foreach (var column in columns)
        {
            var job = new ColumnJob();
            job.Reset(column, VoxelGrid.Unit, Seed, 0);
            workers.Enqueue(job);
        }

        var jobs = VoxelTestHelpers.WaitForJobs(workers, columns.Length);
        Assert.Equal(0, workers.InFlight);
        return columns.Select(c => VoxelTestHelpers.ContentHash(memory, jobs.Single(j => j.Key == c).Layers)).ToArray();
    }
}
