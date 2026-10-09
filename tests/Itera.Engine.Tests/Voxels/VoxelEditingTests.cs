using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

[RunsLong]
public sealed class VoxelEditingTests : IDisposable
{
    private const byte Stone = 3;
    private readonly VoxelMemory _memory = new(256 << 20);

    public void Dispose() => _memory.Dispose();

    /// <summary>The rule, voxel by voxel: a voxel whose sample point the edit covers takes the edit's material.</summary>
    private static bool ApplyVoxelByVoxel(in VoxelEdit edit, int level, VoxelCoord origin, int size, byte[] voxels)
    {
        var changed = false;
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (
                        !edit.Contains(
                            VoxelConstants.SampleAt(origin.X + x, level),
                            VoxelConstants.SampleAt(origin.Y + y, level),
                            VoxelConstants.SampleAt(origin.Z + z, level)
                        )
                    )
                    {
                        continue;
                    }

                    var index = x + size * (y + size * z);
                    var next = edit.Apply(voxels[index]);
                    changed |= next != voxels[index];
                    voxels[index] = next;
                }
            }
        }

        return changed;
    }

    [Fact]
    public void Edits_on_a_stored_chunk_match_edits_on_dense_voxels()
    {
        var key = new ChunkKey(0, 2, 1, -3);
        var origin = key.MinVoxel;
        var dense = ChunkTreeTests.TestChunk();
        var chunk = ChunkTree.Encode(_memory, dense, false, out _);
        VoxelEdit[] edits =
        [
            VoxelEdit.Sphere(Offset(origin, 60, 50, 60), 20, VoxelEditOperation.Clear),
            VoxelEdit.Sphere(Offset(origin, -5, 70, 10), 30, VoxelEditOperation.Clear),
            VoxelEdit.Box(Offset(origin, 100, 40, 100), new VoxelCoord(60, 30, 5), VoxelEditOperation.Set, 9),
            VoxelEdit.Sphere(Offset(origin, 30, 45, 90), 25, VoxelEditOperation.Paint, 11),
            // A fill and a clear of some materials over whole bricks of one material and over mixed ones.
            VoxelEdit.Box(Offset(origin, 8, 56, 8), new VoxelCoord(64, 48, 67), VoxelEditOperation.Fill, 12),
            VoxelEdit.MaterialsBox(Offset(origin, 0, 30, 0), new VoxelCoord(128, 90, 61), [9, 12, Stone]),
            VoxelEdit.Box(Offset(origin, 0, 0, 0), new VoxelCoord(128, 20, 128), VoxelEditOperation.Clear),
        ];
        var changed = new int[VoxelConstants.BricksPerChunk];

        foreach (var edit in edits)
        {
            var before = (byte[])dense.Clone();
            VoxelEditing.ApplyToRegion(
                in edit,
                new GridLevel(VoxelGrid.Unit, 0),
                origin,
                VoxelConstants.ChunkSize,
                dense
            );
            chunk = VoxelEditing.ApplyToChunk(_memory, VoxelGrid.Unit, chunk, key, in edit, changed, out var count);

            Assert.Equal(ChangedBricks(before, dense), changed.Take(count).Order());
        }

        var decoded = new byte[VoxelConstants.ChunkVoxels];
        ChunkTree.Decode(_memory, chunk, decoded);
        Assert.Equal(dense, decoded);
    }

    [Fact]
    public void Row_spans_change_exactly_the_voxels_the_point_test_covers()
    {
        // Random spheres and boxes, every operation but lift (the same fill as clear), at levels 0 to 4, against a
        // voxel-by-voxel copy of the rule: each voxel's sample point tested with VoxelEdit.Contains.
        var random = new Random(7);
        VoxelEditOperation[] operations =
        [
            VoxelEditOperation.Set,
            VoxelEditOperation.Clear,
            VoxelEditOperation.Fill,
            VoxelEditOperation.Paint,
        ];
        foreach (var size in (int[])[VoxelConstants.BrickSize, 32])
        {
            var expected = new byte[size * size * size];
            var actual = new byte[expected.Length];
            for (var trial = 0; trial < 3000; trial++)
            {
                var level = random.Next(0, 5);
                var origin = new VoxelCoord(random.Next(-40, 40), random.Next(-40, 40), random.Next(-40, 40));
                var reach = size << level;
                var at = new VoxelCoord(
                    (origin.X << level) + random.Next(-reach / 2, reach + reach / 2),
                    (origin.Y << level) + random.Next(-reach / 2, reach + reach / 2),
                    (origin.Z << level) + random.Next(-reach / 2, reach + reach / 2)
                );
                var operation = operations[random.Next(operations.Length)];
                var material = (byte)random.Next(1, 20);
                var edit =
                    random.Next(2) == 0
                        ? VoxelEdit.Sphere(at, random.Next(0, reach), operation, material)
                        : VoxelEdit.Box(
                            at,
                            new VoxelCoord(random.Next(1, reach), random.Next(1, reach), random.Next(1, reach)),
                            operation,
                            material
                        );
                for (var i = 0; i < expected.Length; i++)
                {
                    expected[i] = random.Next(3) == 0 ? VoxelConstants.Air : (byte)random.Next(1, 20);
                }

                expected.CopyTo(actual, 0);
                var expectedChanged = ApplyVoxelByVoxel(in edit, level, origin, size, expected);

                var actualChanged = VoxelEditing.ApplyToRegion(
                    in edit,
                    new GridLevel(VoxelGrid.Unit, level),
                    origin,
                    size,
                    actual
                );

                Assert.Equal(expected, actual);
                Assert.Equal(expectedChanged, actualChanged);
            }
        }
    }

    [Fact]
    public void Capsule_row_spans_change_exactly_the_voxels_the_point_test_covers()
    {
        // Random capsules, with ends that coincide, lie along an axis, or run either way, and radius 0, every operation
        // but lift, at levels 0 to 4, against the rule voxel by voxel.
        var random = new Random(13);
        VoxelEditOperation[] operations =
        [
            VoxelEditOperation.Set,
            VoxelEditOperation.Clear,
            VoxelEditOperation.Fill,
            VoxelEditOperation.Paint,
        ];
        foreach (var size in (int[])[VoxelConstants.BrickSize, 32])
        {
            var expected = new byte[size * size * size];
            var actual = new byte[expected.Length];
            for (var trial = 0; trial < 3000; trial++)
            {
                var level = random.Next(0, 5);
                var origin = new VoxelCoord(random.Next(-40, 40), random.Next(-40, 40), random.Next(-40, 40));
                var reach = size << level;
                var start = new VoxelCoord(
                    (origin.X << level) + random.Next(-reach / 2, reach + reach / 2),
                    (origin.Y << level) + random.Next(-reach / 2, reach + reach / 2),
                    (origin.Z << level) + random.Next(-reach / 2, reach + reach / 2)
                );
                var offset = (trial % 4) switch
                {
                    0 => default,
                    1 => new VoxelCoord(random.Next(-reach, reach), 0, 0),
                    2 => new VoxelCoord(0, random.Next(-reach, reach), random.Next(-reach, reach)),
                    _ => new VoxelCoord(
                        random.Next(-reach, reach),
                        random.Next(-reach, reach),
                        random.Next(-reach, reach)
                    ),
                };
                var end = new VoxelCoord(start.X + offset.X, start.Y + offset.Y, start.Z + offset.Z);
                var radius = trial % 7 == 0 ? 0 : random.Next(0, reach / 2 + 1);
                var edit = VoxelEdit.Capsule(
                    start,
                    end,
                    radius,
                    operations[random.Next(operations.Length)],
                    (byte)random.Next(1, 20)
                );
                Assert.Null(edit.Validate());
                for (var i = 0; i < expected.Length; i++)
                {
                    expected[i] = random.Next(3) == 0 ? VoxelConstants.Air : (byte)random.Next(1, 20);
                }

                expected.CopyTo(actual, 0);
                var expectedChanged = ApplyVoxelByVoxel(in edit, level, origin, size, expected);

                var actualChanged = VoxelEditing.ApplyToRegion(
                    in edit,
                    new GridLevel(VoxelGrid.Unit, level),
                    origin,
                    size,
                    actual
                );

                Assert.Equal(expected, actual);
                Assert.Equal(expectedChanged, actualChanged);
            }
        }
    }

    [Fact]
    public void A_capsule_on_a_stored_chunk_matches_the_same_capsule_on_dense_voxels()
    {
        var key = new ChunkKey(0, 1, 0, 2);
        var origin = key.MinVoxel;
        var dense = ChunkTreeTests.TestChunk();
        var chunk = ChunkTree.Encode(_memory, dense, false, out _);
        VoxelEdit[] edits =
        [
            VoxelEdit.Capsule(Offset(origin, 10, 60, 10), Offset(origin, 110, 70, 100), 12, VoxelEditOperation.Clear),
            VoxelEdit.Capsule(Offset(origin, 64, 10, 64), Offset(origin, 64, 120, 64), 30, VoxelEditOperation.Set, 9),
            VoxelEdit.Capsule(Offset(origin, -20, 40, 90), Offset(origin, 50, 40, 90), 8, VoxelEditOperation.Paint, 11),
        ];
        var changed = new int[VoxelConstants.BricksPerChunk];
        var counts = new int[256];

        foreach (var edit in edits)
        {
            // What a clear would remove, counted before the edit, is the covered solid voxels.
            Array.Clear(counts);
            VoxelEditing.CountInChunk(_memory, VoxelGrid.Unit, chunk, key, in edit, counts);
            var solid = new int[256];
            for (var z = 0; z < VoxelConstants.ChunkSize; z++)
            {
                for (var y = 0; y < VoxelConstants.ChunkSize; y++)
                {
                    for (var x = 0; x < VoxelConstants.ChunkSize; x++)
                    {
                        var voxel = dense[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)];
                        if (voxel != VoxelConstants.Air && edit.Contains(origin.X + x, origin.Y + y, origin.Z + z))
                        {
                            solid[voxel]++;
                        }
                    }
                }
            }

            Assert.Equal(solid, counts);

            var before = (byte[])dense.Clone();
            VoxelEditing.ApplyToRegion(
                in edit,
                new GridLevel(VoxelGrid.Unit, 0),
                origin,
                VoxelConstants.ChunkSize,
                dense
            );
            chunk = VoxelEditing.ApplyToChunk(_memory, VoxelGrid.Unit, chunk, key, in edit, changed, out var count);

            Assert.Equal(ChangedBricks(before, dense), changed.Take(count).Order());
        }

        var decoded = new byte[VoxelConstants.ChunkVoxels];
        ChunkTree.Decode(_memory, chunk, decoded);
        Assert.Equal(dense, decoded);
    }

    [Fact]
    public void A_capsule_on_dense_voxels_allocates_nothing()
    {
        var voxels = new byte[32 * 32 * 32];
        Array.Fill(voxels, Stone);
        var edit = VoxelEdit.Capsule(new VoxelCoord(2, 3, 4), new VoxelCoord(28, 20, 25), 6, VoxelEditOperation.Clear);
        VoxelEditing.ApplyToRegion(in edit, new GridLevel(VoxelGrid.Unit, 0), default, 32, voxels);
        Array.Fill(voxels, Stone);

        var before = SystemAllocation.StartMeasurement();
        VoxelEditing.ApplyToRegion(in edit, new GridLevel(VoxelGrid.Unit, 0), default, 32, voxels);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Coarse_voxels_are_tested_at_their_sample_point()
    {
        var key = new ChunkKey(2, 0, 0, 0);
        var dense = new byte[VoxelConstants.ChunkVoxels];
        dense.AsSpan().Fill(Stone);
        var edit = VoxelEdit.Sphere(new VoxelCoord(203, 97, 150), 41, VoxelEditOperation.Clear);

        VoxelEditing.ApplyToRegion(
            in edit,
            new GridLevel(VoxelGrid.Unit, 2),
            new VoxelCoord(0, 0, 0),
            VoxelConstants.ChunkSize,
            dense
        );

        var cleared = 0;
        for (var z = 0; z < 128; z++)
        {
            for (var y = 0; y < 128; y++)
            {
                for (var x = 0; x < 128; x++)
                {
                    var inside = edit.Contains(
                        VoxelConstants.SampleAt(x, 2),
                        VoxelConstants.SampleAt(y, 2),
                        VoxelConstants.SampleAt(z, 2)
                    );
                    Assert.Equal(inside ? VoxelConstants.Air : Stone, dense[x + 128 * y + 16384 * z]);
                    cleared += inside ? 1 : 0;
                }
            }
        }

        Assert.InRange(cleared, 4000, 5000);
        Assert.Equal(2, key.Level);
    }

    [Fact]
    public void Clearing_a_whole_uniform_chunk_leaves_it_empty_without_memory()
    {
        var key = new ChunkKey(0, -1, 0, 4);
        var edit = VoxelEdit.Box(
            Offset(key.MinVoxel, -10, -10, -10),
            new VoxelCoord(200, 200, 200),
            VoxelEditOperation.Clear
        );
        var changed = new int[VoxelConstants.BricksPerChunk];

        var chunk = VoxelEditing.ApplyToChunk(
            _memory,
            VoxelGrid.Unit,
            ChildState.Uniform(Stone),
            key,
            in edit,
            changed,
            out var count
        );

        Assert.Equal(ChildState.Empty, chunk);
        Assert.Equal(VoxelConstants.BricksPerChunk, count);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void Edits_that_cannot_change_a_chunk_do_nothing()
    {
        var key = new ChunkKey(0, 0, 0, 0);
        var changed = new int[VoxelConstants.BricksPerChunk];
        var dig = VoxelEdit.Sphere(new VoxelCoord(64, 64, 64), 30, VoxelEditOperation.Clear);
        var far = VoxelEdit.Sphere(new VoxelCoord(1000, 64, 64), 30, VoxelEditOperation.Set, Stone);

        Assert.Equal(
            ChildState.Empty,
            VoxelEditing.ApplyToChunk(_memory, VoxelGrid.Unit, ChildState.Empty, key, in dig, changed, out var a)
        );
        Assert.Equal(
            ChildState.Uniform(Stone),
            VoxelEditing.ApplyToChunk(
                _memory,
                VoxelGrid.Unit,
                ChildState.Uniform(Stone),
                key,
                in far,
                changed,
                out var b
            )
        );
        Assert.Equal(0, a + b);
    }

    private static VoxelCoord Offset(VoxelCoord origin, int x, int y, int z) =>
        new(origin.X + x, origin.Y + y, origin.Z + z);

    private static List<int> ChangedBricks(byte[] before, byte[] after)
    {
        var cells = new List<int>();
        for (var bz = 0; bz < 16; bz++)
        {
            for (var by = 0; by < 16; by++)
            {
                for (var bx = 0; bx < 16; bx++)
                {
                    var differs = false;
                    for (var z = 0; z < 8 && !differs; z++)
                    {
                        for (var y = 0; y < 8 && !differs; y++)
                        {
                            var start = bx * 8 + 128 * (by * 8 + y) + 16384 * (bz * 8 + z);
                            differs = !before.AsSpan(start, 8).SequenceEqual(after.AsSpan(start, 8));
                        }
                    }

                    if (differs)
                    {
                        cells.Add(bx | (by << 4) | (bz << 8));
                    }
                }
            }
        }

        cells.Sort();
        return cells;
    }
}
