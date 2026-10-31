using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// A generator for tests: ground that rises by 1 cm for every 16 cm in x and in z, dirt 50 cm deep over stone, and the
/// seed shifting the height. Integer maths only.
/// </summary>
internal sealed class SlopeGenerator : IChunkGenerator
{
    public const byte Dirt = 2;
    public const byte Stone = 3;
    private const int DirtDepth = 50;

    public string Id => "test.slope";

    public int Version => 1;

    /// <summary>First level-0 y above the ground at (x, z); every voxel below it is solid.</summary>
    public static int Height(int x, int z, ulong seed) => 200 + (int)(seed % 64) + ((x + z) >> 4);

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        var level = column.Level;
        var firstX = VoxelConstants.SampleAt(column.X << VoxelConstants.ChunkShift, level);
        var firstZ = VoxelConstants.SampleAt(column.Z << VoxelConstants.ChunkShift, level);
        var lastX = VoxelConstants.SampleAt(
            (column.X << VoxelConstants.ChunkShift) + VoxelConstants.ChunkSize - 1,
            level
        );
        var lastZ = VoxelConstants.SampleAt(
            (column.Z << VoxelConstants.ChunkShift) + VoxelConstants.ChunkSize - 1,
            level
        );
        var lowest = Height(firstX, firstZ, seed);
        var highest = Height(lastX, lastZ, seed);
        for (var layer = 0; layer < layers.Length; layer++)
        {
            var chunkY = column.Chunk(layer, VoxelGrid.Unit).Y << VoxelConstants.ChunkShift;
            var bottom = VoxelConstants.SampleAt(chunkY, level);
            var top = VoxelConstants.SampleAt(chunkY + VoxelConstants.ChunkSize - 1, level);
            layers[layer] =
                bottom >= highest ? ChunkFill.Air
                : top < lowest - DirtDepth ? ChunkFill.Uniform(Stone)
                : ChunkFill.Mixed;
        }
    }

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels)
    {
        var level = chunk.Level;
        var origin = new VoxelCoord(
            chunk.X << VoxelConstants.ChunkShift,
            chunk.Y << VoxelConstants.ChunkShift,
            chunk.Z << VoxelConstants.ChunkShift
        );
        for (var z = 0; z < VoxelConstants.ChunkSize; z++)
        {
            var sampleZ = VoxelConstants.SampleAt(origin.Z + z, level);
            for (var y = 0; y < VoxelConstants.ChunkSize; y++)
            {
                var sampleY = VoxelConstants.SampleAt(origin.Y + y, level);
                var row = voxels.Slice(
                    (z * VoxelConstants.ChunkSize + y) * VoxelConstants.ChunkSize,
                    VoxelConstants.ChunkSize
                );
                for (var x = 0; x < VoxelConstants.ChunkSize; x++)
                {
                    var height = Height(VoxelConstants.SampleAt(origin.X + x, level), sampleZ, seed);
                    row[x] =
                        sampleY < height - DirtDepth ? Stone
                        : sampleY < height ? Dirt
                        : VoxelConstants.Air;
                }
            }
        }
    }
}

/// <summary>A generator whose every call throws, for fault handling tests.</summary>
internal sealed class ThrowingGenerator : IChunkGenerator
{
    public string Id => "test.throwing";

    public int Version => 1;

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) =>
        throw new InvalidOperationException("generator failure");

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) =>
        throw new InvalidOperationException("generator failure");
}

/// <summary>Helpers shared by the voxel tests.</summary>
internal static class VoxelTestHelpers
{
    /// <summary>FNV-1a hash of every voxel in the given layers, decoded.</summary>
    public static ulong ContentHash(VoxelMemory memory, ReadOnlySpan<ChildState> layers)
    {
        var hash = 14695981039346656037UL;
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        foreach (var layer in layers)
        {
            ChunkTree.Decode(memory, layer, voxels);
            foreach (var value in voxels)
            {
                hash = (hash ^ value) * 1099511628211UL;
            }
        }

        return hash;
    }

    /// <summary>Waits for <paramref name="count"/> finished jobs, failing after 30 s.</summary>
    public static List<ColumnJob> WaitForJobs(VoxelWorkers workers, int count)
    {
        var done = new List<ColumnJob>();
        var deadline = TestTime.Deadline(30);
        while (done.Count < count)
        {
            workers.ThrowIfFaulted();
            if (workers.TryTakeFinished(out var job))
            {
                done.Add(job);
            }
            else if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {done.Count} of {count} jobs finished.");
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        return done;
    }
}
