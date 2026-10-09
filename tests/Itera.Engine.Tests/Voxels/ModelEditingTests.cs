using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// Editing and reading models' trees (<see cref="VoxelModels"/>): a copy is equal and independent, an edit at every
/// level changes what the world's rule changes, freed trees return their memory, and the ray walk clipped to a model's
/// box hits what an exact test of every voxel hits, from outside and inside the box, allocating nothing.
/// </summary>
public sealed class ModelEditingTests : IDisposable
{
    private readonly VoxelWorld _world;
    private readonly VoxelModels _models;

    public ModelEditingTests()
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = 3,
            Levels = 1,
            MemoryBudgetMegabytes = 64,
            WorkerThreads = 1,
            Seed = 3,
        };
        _world = new VoxelWorld(settings, 0, new SlopeGenerator(), NullLogger<VoxelWorld>.Instance);
        _world.Initialize();
        _models = new VoxelModels(_world);
    }

    public void Dispose() => _world.Dispose();

    /// <summary>
    /// A model's voxels are its own size, not the world's: a character's part of 1 cm voxels, a large model's from its
    /// base level 2 (4 cm), each coarser tree twice the one before.
    /// </summary>
    [Fact]
    public void A_models_voxels_are_its_unit_times_two_to_its_level()
    {
        var tree = _models.Encode(Blobs(1, 20, 20, 20));
        var part = _models.Add([tree, tree], new VoxelCoord(20, 20, 20));
        var large = _models.Add([tree], new VoxelCoord(20, 20, 20), baseLevel: 2);

        Assert.Equal(VoxelModels.Centimetres, _models.UnitCentimetres(part));
        Assert.Equal(0.01f, _models.VoxelMetres(part, 0), 6);
        Assert.Equal(0.02f, _models.VoxelMetres(part, 1), 6);
        Assert.Equal(0.04f, _models.VoxelMetres(large, 0), 6);
        Assert.Equal(0.04f, VoxelModels.LevelMetres(2), 6);
        Assert.Equal(VoxelConstants.SampleAt(37, 2), VoxelModels.SampleAt(37, 2));
    }

    [Fact]
    public void A_copy_reads_back_equal_and_edits_to_either_leave_the_other_alone()
    {
        var voxels = Blobs(1, 90, 70, 100);
        var original = _models.Encode(voxels);
        var copy = _models.Copy(original);
        var decoded = new byte[VoxelConstants.ChunkVoxels];

        _models.Decode(copy, decoded);
        Assert.Equal(voxels, decoded);

        Assert.True(_models.Apply(ref copy, 0, VoxelEdit.Sphere(new(45, 35, 50), 30, VoxelEditOperation.Clear)));
        _models.Decode(original, decoded);
        Assert.Equal(voxels, decoded);

        Assert.True(
            _models.Apply(ref original, 0, VoxelEdit.Box(new(0, 0, 0), new(40, 40, 40), VoxelEditOperation.Set, 7))
        );
        var afterCopy = new byte[VoxelConstants.ChunkVoxels];
        _models.Decode(copy, afterCopy);
        Assert.Equal(VoxelConstants.Air, afterCopy[Index(45, 35, 50)]);
        Assert.NotEqual((byte)7, afterCopy[Index(1, 1, 1)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void An_edit_changes_each_level_as_the_worlds_rule_does(int level)
    {
        var size = VoxelConstants.ChunkSize >> level;
        var voxels = Blobs(2 + level, size, size, size);
        var tree = _models.Encode(voxels);
        VoxelEdit[] edits =
        [
            VoxelEdit.Sphere(new(50, 60, 40), 23, VoxelEditOperation.Clear),
            VoxelEdit.Sphere(new(20, 30, 90), 9, VoxelEditOperation.Set, 5),
            VoxelEdit.Box(new(70, 5, 10), new(30, 50, 21), VoxelEditOperation.Paint, 6),
            VoxelEdit.Sphere(new(100, 100, 100), 40, VoxelEditOperation.Fill, 2),
            // Whole bricks set at once, and whole bricks cleared.
            VoxelEdit.Box(new(16, 16, 16), new(40, 24, 33), VoxelEditOperation.Set, 7),
            VoxelEdit.Sphere(new(30, 30, 30), 30, VoxelEditOperation.Clear),
            // Capsules: a groove and a puncture.
            VoxelEdit.Capsule(new(10, 90, 20), new(110, 70, 60), 6, VoxelEditOperation.Clear),
            VoxelEdit.Capsule(new(64, 64, 0), new(64, 64, 127), 3, VoxelEditOperation.Clear),
        ];

        foreach (var edit in edits)
        {
            var expectChange = VoxelEditing.ApplyToRegion(
                in edit,
                new GridLevel(VoxelGrid.Unit, level),
                default,
                VoxelConstants.ChunkSize,
                voxels
            );
            Assert.Equal(expectChange, _models.Apply(ref tree, level, in edit));
        }

        var decoded = new byte[VoxelConstants.ChunkVoxels];
        _models.Decode(tree, decoded);
        Assert.Equal(voxels, decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Counting_inside_an_edit_finds_what_a_voxel_by_voxel_count_finds(int level)
    {
        var size = VoxelConstants.ChunkSize >> level;
        var voxels = Blobs(20 + level, size, size, size);
        var tree = _models.Encode(voxels);

        // Whole bricks of one material, so uniform bricks are counted too, partly and wholly covered.
        var uniform = VoxelEdit.Box(new(16, 16, 16), new(48, 40, 33), VoxelEditOperation.Set, 7);
        VoxelEditing.ApplyToRegion(
            in uniform,
            new GridLevel(VoxelGrid.Unit, level),
            default,
            VoxelConstants.ChunkSize,
            voxels
        );
        _models.Apply(ref tree, level, in uniform);

        VoxelEdit[] edits =
        [
            VoxelEdit.Sphere(new(50, 60, 40), 23, VoxelEditOperation.Clear),
            VoxelEdit.Sphere(new(30, 30, 30), 30, VoxelEditOperation.Clear),
            VoxelEdit.Sphere(new(100, 100, 100), 40, VoxelEditOperation.Clear),
            VoxelEdit.Sphere(new(3, 120, 7), 11, VoxelEditOperation.Clear),
            VoxelEdit.Box(new(70, 5, 10), new(30, 50, 21), VoxelEditOperation.Clear),
            VoxelEdit.Box(new(8, 8, 8), new(64, 64, 64), VoxelEditOperation.Clear),
            VoxelEdit.Capsule(new(10, 90, 20), new(110, 70, 60), 6, VoxelEditOperation.Clear),
            VoxelEdit.Capsule(new(20, 20, 20), new(20, 20, 20), 17, VoxelEditOperation.Clear),
            // Outside the tree.
            VoxelEdit.Sphere(new(-100, 50, 50), 20, VoxelEditOperation.Clear),
        ];

        var counts = new int[256];
        var expected = new int[256];
        foreach (var edit in edits)
        {
            Array.Clear(counts);
            Array.Clear(expected);
            _models.CountInside(tree, level, in edit, counts);
            for (var i = 0; i < voxels.Length; i++)
            {
                int x = i % 128,
                    y = i / 128 % 128,
                    z = i / (128 * 128);
                if (
                    voxels[i] != VoxelConstants.Air
                    && edit.Contains(
                        VoxelConstants.SampleAt(x, level),
                        VoxelConstants.SampleAt(y, level),
                        VoxelConstants.SampleAt(z, level)
                    )
                )
                {
                    expected[voxels[i]]++;
                }
            }

            Assert.Equal(expected, counts);

            // The same chunk stored elsewhere in the world, with the edit moved along with it.
            if (edit.Shape == VoxelEditShape.Sphere)
            {
                var step = VoxelConstants.ChunkSize << level;
                var moved = VoxelEdit.Sphere(
                    new(edit.Origin.X + step, edit.Origin.Y, edit.Origin.Z - step),
                    edit.Radius,
                    VoxelEditOperation.Clear
                );
                Array.Clear(counts);
                VoxelEditing.CountInChunk(
                    _world.ReadMemory,
                    VoxelGrid.Unit,
                    tree.State,
                    new ChunkKey(level, 1, 0, -1),
                    in moved,
                    counts
                );
                Assert.Equal(expected, counts);
            }
        }
    }

    [Fact]
    public void Counting_inside_an_edit_allocates_nothing()
    {
        var tree = _models.Encode(Blobs(21, 100, 100, 100));
        var edit = VoxelEdit.Sphere(new(50, 50, 50), 35, VoxelEditOperation.Clear);
        var counts = new int[256];
        _models.CountInside(tree, 0, in edit, counts);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 20; i++)
        {
            _models.CountInside(tree, 0, in edit, counts);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Encoding_and_decoding_within_a_box_ignore_what_lies_outside_it()
    {
        var extent = new VoxelCoord(37, 20, 61);
        var voxels = Blobs(13, 100, 100, 100);
        var inside = (byte[])voxels.Clone();
        for (var i = 0; i < inside.Length; i++)
        {
            int x = i % 128,
                y = i / 128 % 128,
                z = i / (128 * 128);
            if (x >= extent.X || y >= extent.Y || z >= extent.Z)
            {
                inside[i] = VoxelConstants.Air;
            }
        }

        var decoded = new byte[VoxelConstants.ChunkVoxels];
        _models.Decode(_models.Encode(voxels, extent), decoded);
        Assert.Equal(inside, decoded);

        // Within the box, a decode writes the tree's voxels over what was there.
        var tree = _models.Encode(voxels);
        var partial = new byte[VoxelConstants.ChunkVoxels];
        Array.Fill(partial, (byte)9);
        _models.Decode(tree, partial, extent);
        for (var i = 0; i < partial.Length; i++)
        {
            int x = i % 128,
                y = i / 128 % 128,
                z = i / (128 * 128);
            if (x < extent.X && y < extent.Y && z < extent.Z)
            {
                Assert.Equal(voxels[i], partial[i]);
            }
        }
    }

    [Fact]
    public void Freed_trees_return_their_memory()
    {
        var memory = _world.ReadMemory;
        var before = memory.UsedBytes;
        var tree = _models.Encode(Blobs(5, 100, 100, 100));
        var copy = _models.Copy(tree);
        _models.Apply(ref copy, 0, VoxelEdit.Sphere(new(50, 50, 50), 20, VoxelEditOperation.Set, 3));
        Assert.True(memory.UsedBytes > before);

        _models.Free(tree);
        _models.Free(copy);

        Assert.Equal(before, memory.UsedBytes);
    }

    [Fact]
    public void Random_rays_hit_what_an_exact_test_of_every_voxel_hits()
    {
        var size = new VoxelCoord(90, 70, 110);
        var voxels = Blobs(7, size.X, size.Y, size.Z);
        var tree = _models.Encode(voxels);
        var solids = Solids(voxels);
        var random = new Random(11);
        int hits = 0,
            inside = 0;
        for (var ray = 0; ray < 400; ray++)
        {
            // Half the rays start outside the box, up to a metre and a half away; half start inside it.
            var outside = ray % 2 == 0;
            var origin = outside
                ? (random.NextDouble() * 400 - 150, random.NextDouble() * 400 - 150, random.NextDouble() * 400 - 150)
                : (random.NextDouble() * size.X, random.NextDouble() * size.Y, random.NextDouble() * size.Z);
            // Towards a point in the box, so most rays meet it.
            var target = (random.NextDouble() * size.X, random.NextDouble() * size.Y, random.NextDouble() * size.Z);
            var direction = (target.Item1 - origin.Item1, target.Item2 - origin.Item2, target.Item3 - origin.Item3);
            if (!outside)
            {
                direction = (random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            }

            var found = _models.Raycast(tree, size, origin, direction, 1000, out var hit);
            var expected = Exact(solids, size, origin, direction, 1000, out var voxel, out var distance);

            Assert.Equal(expected, found);
            if (found)
            {
                hits++;
                inside += outside ? 0 : 1;
                var far = Math.Max(
                    Math.Abs(voxel.X - hit.Voxel.X),
                    Math.Max(Math.Abs(voxel.Y - hit.Voxel.Y), Math.Abs(voxel.Z - hit.Voxel.Z))
                );
                // The same voxel, or a neighbour where the ray passes through an edge or a corner.
                Assert.True(far <= 1, $"Ray {ray}: the exact test hit {voxel}, the walk {hit.Voxel}.");
                Assert.Equal(distance, hit.Distance, 1e-6);
                if (far == 0)
                {
                    Assert.Equal(voxels[Index(voxel.X, voxel.Y, voxel.Z)], hit.Material);
                }
            }
        }

        Assert.True(hits > 100, $"{hits} hits.");
        Assert.True(inside > 20, $"{inside} hits from inside the box.");
    }

    [Fact]
    public void A_ray_from_far_away_reaches_the_box_and_one_beside_it_misses()
    {
        var size = new VoxelCoord(40, 40, 40);
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        voxels[Index(20, 20, 20)] = 3;
        var tree = _models.Encode(voxels);

        // 50 m away along x: the walk starts where the ray enters the box.
        Assert.True(_models.Raycast(tree, size, (-5000, 20.5, 20.5), (1, 0, 0), 10_000, out var hit));
        Assert.Equal(new VoxelCoord(20, 20, 20), hit.Voxel);
        Assert.Equal(new VoxelCoord(-1, 0, 0), hit.Normal);
        Assert.Equal(5020, hit.Distance, 1e-9);

        // Beside the box, and short of it.
        Assert.False(_models.Raycast(tree, size, (-5000, 60.5, 20.5), (1, 0, 0), 10_000, out _));
        Assert.False(_models.Raycast(tree, size, (-5000, 20.5, 20.5), (1, 0, 0), 5010, out _));
    }

    [Fact]
    public void Casting_allocates_nothing()
    {
        var size = new VoxelCoord(90, 70, 110);
        var tree = _models.Encode(Blobs(8, size.X, size.Y, size.Z));
        var random = new Random(12);
        var rays = new ((double, double, double), (double, double, double))[200];
        for (var i = 0; i < rays.Length; i++)
        {
            rays[i] = (
                (random.NextDouble() * 200 - 50, random.NextDouble() * 200 - 50, -30),
                (random.NextDouble() - 0.5, random.NextDouble() - 0.5, 1)
            );
        }

        foreach (var (origin, direction) in rays)
        {
            _models.Raycast(tree, size, origin, direction, 500, out _);
        }

        var before = SystemAllocation.StartMeasurement();
        foreach (var (origin, direction) in rays)
        {
            _models.Raycast(tree, size, origin, direction, 500, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static List<VoxelCoord> Solids(byte[] voxels)
    {
        var solids = new List<VoxelCoord>();
        const int edge = VoxelConstants.ChunkSize;
        for (var i = 0; i < voxels.Length; i++)
        {
            if (voxels[i] != VoxelConstants.Air)
            {
                solids.Add(new VoxelCoord(i % edge, i / edge % edge, i / (edge * edge)));
            }
        }

        return solids;
    }

    /// <summary>
    /// The solid voxel inside the box whose cube the ray enters first (or starts in), testing every solid voxel: the ray
    /// clipped to the box and to <paramref name="max"/>.
    /// </summary>
    private static bool Exact(
        List<VoxelCoord> solids,
        VoxelCoord size,
        (double X, double Y, double Z) origin,
        (double X, double Y, double Z) direction,
        double max,
        out VoxelCoord hit,
        out double distance
    )
    {
        var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
        double[] o = [origin.X, origin.Y, origin.Z];
        double[] d = [direction.X / length, direction.Y / length, direction.Z / length];
        hit = default;
        distance = double.PositiveInfinity;
        if (!Slabs(o, d, [0, 0, 0], [size.X, size.Y, size.Z], out var boxEnter, out var boxExit))
        {
            return false;
        }

        var start = Math.Max(boxEnter, 0);
        var end = Math.Min(boxExit, max);
        foreach (var voxel in solids)
        {
            if (voxel.X >= size.X || voxel.Y >= size.Y || voxel.Z >= size.Z)
            {
                continue;
            }

            if (
                Slabs(
                    o,
                    d,
                    [voxel.X, voxel.Y, voxel.Z],
                    [voxel.X + 1, voxel.Y + 1, voxel.Z + 1],
                    out var enter,
                    out var exit
                )
                && exit >= start
                && Math.Max(enter, start) <= end
                && Math.Max(enter, start) < distance
            )
            {
                distance = Math.Max(enter, start);
                hit = voxel;
            }
        }

        return !double.IsPositiveInfinity(distance);
    }

    /// <summary>Where a line enters and leaves a box, as distances along it; false when it misses the box.</summary>
    private static bool Slabs(double[] o, double[] d, double[] min, double[] max, out double enter, out double exit)
    {
        enter = double.NegativeInfinity;
        exit = double.PositiveInfinity;
        for (var a = 0; a < 3; a++)
        {
            if (d[a] == 0)
            {
                if (o[a] < min[a] || o[a] >= max[a])
                {
                    return false;
                }

                continue;
            }

            var t0 = (min[a] - o[a]) / d[a];
            var t1 = (max[a] - o[a]) / d[a];
            enter = Math.Max(enter, Math.Min(t0, t1));
            exit = Math.Min(exit, Math.Max(t0, t1));
        }

        return enter <= exit;
    }

    /// <summary>
    /// A chunk with spheres of three materials and a post one voxel thick inside the box from 0 to (sx, sy, sz); air
    /// elsewhere.
    /// </summary>
    private static byte[] Blobs(int seed, int sx, int sy, int sz)
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        var random = new Random(seed);
        for (var s = 0; s < 6; s++)
        {
            var r = random.Next(3, Math.Max(4, Math.Min(sx, Math.Min(sy, sz)) / 3));
            int cx = random.Next(0, sx),
                cy = random.Next(0, sy),
                cz = random.Next(0, sz);
            var material = (byte)(1 + s % 3);
            for (var z = Math.Max(0, cz - r); z < Math.Min(sz, cz + r + 1); z++)
            {
                for (var y = Math.Max(0, cy - r); y < Math.Min(sy, cy + r + 1); y++)
                {
                    for (var x = Math.Max(0, cx - r); x < Math.Min(sx, cx + r + 1); x++)
                    {
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz) <= r * r)
                        {
                            voxels[Index(x, y, z)] = material;
                        }
                    }
                }
            }
        }

        for (var y = 0; y < sy; y++)
        {
            voxels[Index(sx / 2, y, sz / 3)] = 4;
        }

        return voxels;
    }

    private static int Index(int x, int y, int z) => x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z);
}
