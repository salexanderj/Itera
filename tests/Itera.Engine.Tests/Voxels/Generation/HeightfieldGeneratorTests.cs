using System.Numerics;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary>
/// <see cref="HeightfieldGenerator"/> through <see cref="HillsGenerator"/>: descriptions that agree with the voxels
/// filled, fills that equal a voxel-by-voxel reference of the same rules, features written over the ground, the same
/// voxels on every run and thread, no allocation after a thread's first column, and a world that streams it. Most
/// checks run on a grid of 1 cm and of 10 cm; threading and allocation, which do not depend on the base, on 10 cm only.
/// </summary>
[RunsLong]
public sealed class HeightfieldGeneratorTests
{
    private const int Size = VoxelConstants.ChunkSize;
    private static readonly ulong[] s_seeds = [1, 2];

    private static byte[] NewVoxels() => new byte[VoxelConstants.ChunkVoxels];

    /// <summary>A few columns of a level: the map's middle, one across it, and one further out, kept inside the world.</summary>
    private static ColumnKey[] ColumnsAt(VoxelGrid grid, int level)
    {
        var low = grid.MinChunkXZAt(level);
        var high = grid.MaxChunkXZAt(level);
        return
        [
            new(level, 0, 0),
            new(level, Math.Max(low, -1), Math.Min(high, 1)),
            new(level, Math.Min(high, 2), Math.Max(low, -3)),
        ];
    }

    /// <summary>
    /// A column's range (<see cref="IChunkGenerator.DescribeRange"/>), with the deep material below it, air above it,
    /// and its layers described from its first (<see cref="IChunkGenerator.DescribeLayers"/>), stands for every layer
    /// as <see cref="IChunkGenerator.DescribeColumn"/> describes it: over 9 x 9 columns round the middle at levels 0 to 2
    /// and a few at every level, on 1 and 10 cm voxels, the boulders and the caves under the ground among them. The
    /// range holds a small share of the column's layers.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void A_columns_range_and_its_layers_stand_for_every_layer(int baseCentimetres)
    {
        var grid = new VoxelGrid(baseCentimetres);
        var generator = new HillsGenerator(grid);
        int stored = 0,
            total = 0;
        foreach (var seed in s_seeds)
        {
            for (var level = 0; level <= VoxelConstants.MaxLevel; level++)
            {
                var columns = new List<ColumnKey>(ColumnsAt(grid, level));
                for (var z = -4; z <= 4 && level <= 2; z++)
                {
                    for (var x = -4; x <= 4; x++)
                    {
                        columns.Add(new ColumnKey(level, x, z));
                    }
                }

                foreach (var column in columns)
                {
                    var fills = Describe(generator, column, seed);
                    generator.DescribeRange(column, seed, out var first, out var last, out var below, out var above);
                    var window = new ChunkFill[Math.Max(last - first + 1, 0)];
                    if (window.Length > 0)
                    {
                        generator.DescribeLayers(column, seed, first, window);
                    }

                    for (var layer = 0; layer < fills.Length; layer++)
                    {
                        var stands =
                            layer < first ? below
                            : layer > last ? above
                            : window[layer - first];
                        if (fills[layer] != stands)
                        {
                            Assert.Fail(
                                $"seed {seed}, {column}, layer {layer} of {first} to {last}: {fills[layer]}, the range's {stands}"
                            );
                        }
                    }

                    stored += window.Length;
                    total += fills.Length;
                }
            }
        }

        Assert.True(stored * 3 < total, $"{stored} of {total} layers stored.");
    }

    private static ChunkFill[] Describe(HillsGenerator generator, ColumnKey column, ulong seed)
    {
        var fills = new ChunkFill[column.LayerCount(generator.Grid)];
        generator.DescribeColumn(column, seed, fills);
        return fills;
    }

    private static ulong Hash(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            hash = (hash ^ value) * 0x100000001B3;
        }

        return hash;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void A_layer_described_as_air_or_one_material_is_wholly_that(int baseCentimetres)
    {
        var grid = new VoxelGrid(baseCentimetres);
        var generator = new HillsGenerator(grid);
        var voxels = NewVoxels();
        var densities = NewVoxels();
        int air = 0,
            uniform = 0,
            mixed = 0;
        foreach (var seed in s_seeds)
        {
            for (var level = 0; level <= VoxelConstants.MaxLevel; level++)
            {
                foreach (var column in ColumnsAt(grid, level))
                {
                    var fills = Describe(generator, column, seed);
                    for (var layer = 0; layer < fills.Length; layer++)
                    {
                        mixed += fills[layer].Kind == ChunkFillKind.Mixed ? 1 : 0;
                        if (fills[layer].Kind == ChunkFillKind.Mixed || !NearMixed(fills, layer))
                        {
                            continue;
                        }

                        generator.Fill(column.Chunk(layer, grid), seed, voxels, densities);
                        var message = $"seed {seed}, {column}, layer {layer}: {fills[layer]}";
                        if (fills[layer].Kind == ChunkFillKind.Air)
                        {
                            air++;
                            Assert.True(voxels.AsSpan().IndexOfAnyExcept(VoxelConstants.Air) < 0, message);
                            Assert.True(densities.AsSpan().IndexOfAnyExcept(VoxelDensity.Empty) < 0, message);
                        }
                        else
                        {
                            uniform++;
                            Assert.True(voxels.AsSpan().IndexOfAnyExcept(fills[layer].Material) < 0, message);
                            Assert.True(densities.AsSpan().IndexOfAnyExcept(VoxelDensity.Full) < 0, message);
                        }
                    }
                }
            }
        }

        // The check must have met all three kinds of layer.
        Assert.True(air > 20 && uniform > 20 && mixed > 20, $"air {air}, uniform {uniform}, mixed {mixed}");
    }

    // Layers beside a mixed one are where a wrong air or uniform answer would show; the rest are far from the ground.
    private static bool NearMixed(ChunkFill[] fills, int layer)
    {
        for (var other = Math.Max(0, layer - 2); other <= Math.Min(fills.Length - 1, layer + 2); other++)
        {
            if (fills[other].Kind == ChunkFillKind.Mixed)
            {
                return true;
            }
        }

        return layer == 0 || layer == fills.Length - 1;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Fill_gives_what_a_voxel_by_voxel_reference_gives(int baseCentimetres)
    {
        var grid = new VoxelGrid(baseCentimetres);
        var generator = new HillsGenerator(grid);
        var voxels = NewVoxels();
        var densities = NewVoxels();
        var onlyVoxels = NewVoxels();
        var expectedVoxels = NewVoxels();
        var expectedDensities = NewVoxels();
        var chunks = 0;
        for (var level = 0; level <= VoxelConstants.MaxLevel; level++)
        {
            var seed = s_seeds[level % s_seeds.Length];
            foreach (var column in ColumnsAt(grid, level).Take(2))
            {
                var fills = Describe(generator, column, seed);
                var taken = 0;
                for (var layer = 0; layer < fills.Length && taken < 2; layer++)
                {
                    if (fills[layer].Kind != ChunkFillKind.Mixed)
                    {
                        continue;
                    }

                    taken++;
                    chunks++;
                    var chunk = column.Chunk(layer, grid);
                    generator.Fill(chunk, seed, voxels, densities);
                    generator.Fill(chunk, seed, onlyVoxels);
                    HillsReference.Chunk(grid, seed, chunk, expectedVoxels, expectedDensities, features: true);
                    AssertSame(expectedVoxels, voxels, $"materials of {chunk}, seed {seed}");
                    AssertSame(expectedDensities, densities, $"densities of {chunk}, seed {seed}");
                    AssertSame(expectedVoxels, onlyVoxels, $"materials alone of {chunk}, seed {seed}");
                }
            }
        }

        Assert.True(chunks >= 10, $"{chunks} chunks");
    }

    private static void AssertSame(byte[] expected, byte[] actual, string what)
    {
        var at = expected.AsSpan().CommonPrefixLength(actual);
        if (at < expected.Length)
        {
            var x = at % Size;
            var y = at / Size % Size;
            var z = at / (Size * Size);
            Assert.Fail($"{what}: voxel ({x}, {y}, {z}) is {actual[at]}, expected {expected[at]}.");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Features_are_written_over_the_ground_and_keep_their_default_density(int baseCentimetres)
    {
        var grid = new VoxelGrid(baseCentimetres);
        var generator = new HillsGenerator(grid);
        var ground = new HillsGenerator(grid, features: false);
        var voxels = NewVoxels();
        var densities = NewVoxels();
        var groundVoxels = NewVoxels();
        var groundDensities = NewVoxels();
        var winners = new int[VoxelConstants.ChunkVoxels];
        var expectedVoxels = NewVoxels();
        var expectedDensities = NewVoxels();
        int solidOverAir = 0,
            airOverSolid = 0,
            kept = 0,
            written = 0,
            balls = 0;
        foreach (ulong seed in (ReadOnlySpan<ulong>)[1, 2, 3])
        {
            var near = HillsGenerator.BallsNear(seed, -3000, 3000, -3000, 3000);
            var picked = near.Where(ball => ball.Material != VoxelConstants.Air)
                .Take(3)
                .Concat(near.Where(ball => ball.Material == VoxelConstants.Air).Take(2));
            foreach (var ball in picked)
            {
                balls++;
                var level = ball.Material == VoxelConstants.Air ? 1 : 0;
                var chunk = grid.ChunkContaining(new VoxelCoord(ball.X, ball.Y, ball.Z), level);
                generator.Fill(chunk, seed, voxels, densities);
                ground.Fill(chunk, seed, groundVoxels, groundDensities);

                // The layer holding the ball is never described as air or one material.
                var column = chunk.Column;
                Assert.Equal(ChunkFillKind.Mixed, Describe(generator, column, seed)[chunk.Layer(grid)].Kind);

                var origin = chunk.LevelOrigin;
                var all = HillsGenerator.BallsNear(
                    seed,
                    grid.SampleAt(origin.X, level),
                    grid.SampleAt(origin.X + Size - 1, level),
                    grid.SampleAt(origin.Z, level),
                    grid.SampleAt(origin.Z + Size - 1, level)
                );

                // The last ball to hold a voxel's sample point wins.
                Array.Fill(winners, -1);
                for (var b = 0; b < all.Count; b++)
                {
                    for (var z = 0; z < Size; z++)
                    {
                        var sampleZ = grid.SampleAt(origin.Z + z, level);
                        if (Math.Abs(sampleZ - all[b].Z) > all[b].Radius)
                        {
                            continue;
                        }

                        for (var y = 0; y < Size; y++)
                        {
                            var sampleY = grid.SampleAt(origin.Y + y, level);
                            if (Math.Abs(sampleY - all[b].Y) > all[b].Radius)
                            {
                                continue;
                            }

                            for (var x = 0; x < Size; x++)
                            {
                                if (all[b].Contains(grid.SampleAt(origin.X + x, level), sampleY, sampleZ))
                                {
                                    winners[x + Size * (y + Size * z)] = b;
                                }
                            }
                        }
                    }
                }

                for (var i = 0; i < winners.Length; i++)
                {
                    if (winners[i] < 0)
                    {
                        expectedVoxels[i] = groundVoxels[i];
                        expectedDensities[i] = groundDensities[i];
                        continue;
                    }

                    written++;
                    var material = all[winners[i]].Material;
                    expectedVoxels[i] = material;
                    var wasSolid = groundVoxels[i] != VoxelConstants.Air;
                    var isSolid = material != VoxelConstants.Air;
                    if (wasSolid == isSolid)
                    {
                        // The ground already agreed: its density stays.
                        expectedDensities[i] = groundDensities[i];
                        kept += wasSolid && densities[i] < VoxelDensity.Full ? 1 : 0;
                    }
                    else
                    {
                        expectedDensities[i] = VoxelDensity.Default(material);
                        solidOverAir += isSolid ? 1 : 0;
                        airOverSolid += isSolid ? 0 : 1;
                    }
                }

                AssertSame(expectedVoxels, voxels, $"materials of {chunk}, seed {seed}");
                AssertSame(expectedDensities, densities, $"densities of {chunk}, seed {seed}");
            }
        }

        Assert.True(balls >= 10, $"{balls} balls");
        Assert.True(written > 0 && solidOverAir > 0 && airOverSolid > 0, $"{solidOverAir} {airOverSolid} {written}");
        // A partly filled ground voxel inside a feature keeps its density, where the feature agrees with it.
        Assert.True(kept > 0 || baseCentimetres == 1, $"{kept}");
    }

    [Fact]
    public void A_layer_no_feature_touches_is_described_by_the_ground_alone()
    {
        var grid = new VoxelGrid(10);
        var generator = new HillsGenerator(grid);
        var ground = new HillsGenerator(grid, features: false);
        var different = 0;
        foreach (var column in ColumnsAt(grid, 0))
        {
            var withFeatures = Describe(generator, column, 1);
            var without = Describe(ground, column, 1);
            for (var layer = 0; layer < withFeatures.Length; layer++)
            {
                if (withFeatures[layer] != without[layer])
                {
                    different++;
                    Assert.Equal(ChunkFillKind.Mixed, withFeatures[layer].Kind);
                }
            }
        }

        Assert.True(different > 0);
    }

    [Fact]
    public void The_same_seed_gives_the_same_voxels()
    {
        // A change to these hashes means a subclass's Version must rise: the same seed would give other voxels.
        const ulong expected = 4767645373783207152;
        ulong hash = 0xCBF29CE484222325;
        var voxels = NewVoxels();
        var densities = NewVoxels();
        foreach (var baseCentimetres in (ReadOnlySpan<int>)[1, 10])
        {
            var grid = new VoxelGrid(baseCentimetres);
            var generator = new HillsGenerator(grid);
            foreach (ulong seed in (ReadOnlySpan<ulong>)[1, 7])
            {
                foreach (var level in (ReadOnlySpan<int>)[0, 2, 4])
                {
                    foreach (var column in ColumnsAt(grid, level).Take(2))
                    {
                        var fills = Describe(generator, column, seed);
                        var taken = 0;
                        for (var layer = 0; layer < fills.Length && taken < 2; layer++)
                        {
                            if (fills[layer].Kind == ChunkFillKind.Mixed)
                            {
                                taken++;
                                generator.Fill(column.Chunk(layer, grid), seed, voxels, densities);
                                hash = Hash(Hash(hash, voxels), densities);
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(expected, hash);
    }

    [Fact]
    public void A_thread_that_switches_generators_and_seeds_gets_each_ones_voxels()
    {
        var grid = new VoxelGrid(10);
        var withFeatures = new HillsGenerator(grid);
        var without = new HillsGenerator(grid, features: false);
        var column = new ColumnKey(0, 0, 0);
        var layers = Describe(withFeatures, column, 1);
        var layer = Array.FindIndex(layers, fill => fill.Kind == ChunkFillKind.Mixed);
        var chunk = column.Chunk(layer, grid);
        var expectedVoxels = NewVoxels();
        var expectedDensities = NewVoxels();
        var voxels = NewVoxels();
        var densities = NewVoxels();
        for (var round = 0; round < 3; round++)
        {
            foreach (
                var (generator, seed, features) in new[]
                {
                    (withFeatures, 1UL, true),
                    (without, 1UL, false),
                    (withFeatures, 2UL, true),
                }
            )
            {
                generator.Fill(chunk, seed, voxels, densities);
                HillsReference.Chunk(grid, seed, chunk, expectedVoxels, expectedDensities, features);
                AssertSame(expectedVoxels, voxels, $"round {round}, seed {seed}, features {features}");
                AssertSame(expectedDensities, densities, $"round {round}, seed {seed}, features {features}");
            }
        }
    }

    // One grid: threading does not depend on the grid's base.
    [Fact]
    public void Worker_threads_get_the_voxels_one_thread_gets()
    {
        var grid = new VoxelGrid(10);
        var generator = new HillsGenerator(grid);
        var jobs = new List<(ChunkKey Chunk, ulong Seed)>();
        foreach (var level in (ReadOnlySpan<int>)[0, 1, 3])
        {
            foreach (var column in ColumnsAt(grid, level))
            {
                var fills = Describe(generator, column, 5);
                for (var layer = 0; layer < fills.Length; layer++)
                {
                    if (fills[layer].Kind == ChunkFillKind.Mixed)
                    {
                        jobs.Add((column.Chunk(layer, grid), 5));
                    }
                }
            }
        }

        var alone = new ulong[jobs.Count];
        var voxels = NewVoxels();
        var densities = NewVoxels();
        for (var i = 0; i < jobs.Count; i++)
        {
            generator.Fill(jobs[i].Chunk, jobs[i].Seed, voxels, densities);
            alone[i] = Hash(Hash(0, voxels), densities);
        }

        var together = new ulong[jobs.Count];
        Parallel.For(
            0,
            4,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            worker =>
            {
                var myVoxels = NewVoxels();
                var myDensities = NewVoxels();
                // Each worker takes the jobs in another order, so columns change under every thread.
                for (var n = 0; n < jobs.Count; n++)
                {
                    var i = (n + worker * 7) % jobs.Count;
                    generator.Fill(jobs[i].Chunk, jobs[i].Seed, myVoxels, myDensities);
                    var hash = Hash(Hash(0, myVoxels), myDensities);
                    if (alone[i] != hash)
                    {
                        Assert.Fail($"job {i} gave another hash on worker {worker}: {jobs[i].Chunk}");
                    }

                    together[i] = hash;
                }
            }
        );
        Assert.Equal(alone, together);
    }

    // One grid: the allocation does not depend on the grid's base.
    [Fact]
    public void Describing_and_filling_allocate_nothing_after_the_threads_first_column()
    {
        var grid = new VoxelGrid(10);
        var generator = new HillsGenerator(grid);
        long allocated = -1;
        var thread = new Thread(() =>
        {
            var voxels = NewVoxels();
            var densities = NewVoxels();
            var fills = new ChunkFill[grid.MaxLayers];
            var jobs = new List<(ColumnKey Column, int Layer)>();
            // The largest level measured is the first column, so the feature lists are already big enough.
            for (var level = 5; level >= 0; level--)
            {
                foreach (var column in ColumnsAt(grid, level))
                {
                    var layers = fills.AsSpan(0, column.LayerCount(grid));
                    generator.DescribeColumn(column, 3, layers);
                    for (var layer = 0; layer < layers.Length; layer++)
                    {
                        if (layers[layer].Kind == ChunkFillKind.Mixed)
                        {
                            jobs.Add((column, layer));
                        }
                    }
                }
            }

            foreach (var (column, layer) in jobs.Take(6))
            {
                generator.Fill(column.Chunk(layer, grid), 3, voxels, densities);
            }

            allocated = SystemAllocation.BytesAllocatedWithoutCollection(() =>
            {
                foreach (var (column, layer) in jobs)
                {
                    generator.DescribeColumn(column, 3, fills.AsSpan(0, column.LayerCount(grid)));
                    generator.Fill(column.Chunk(layer, grid), 3, voxels, densities);
                    generator.Fill(column.Chunk(layer, grid), 3, voxels);
                }
            });
        });
        thread.Start();
        thread.Join();

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_world_streams_the_generators_columns()
    {
        var grid = new VoxelGrid(10);
        var generator = new HillsGenerator(grid);
        const ulong seed = 17;
        var heightMetres = (float)HillsGenerator.Height(0, 0, seed) / 100;
        using var world = TestWorlds.Load(generator, new Vector3(0, heightMetres + 1, 0), radiusMetres: 3, seed: seed);
        Assert.Equal(10, world.Grid.BaseCentimetres);

        var references = new Dictionary<ChunkKey, (byte[] Voxels, byte[] Densities)>();
        int compared = 0,
            solid = 0,
            air = 0;
        for (var z = -250; z <= 250; z += 37)
        {
            for (var x = -250; x <= 250; x += 37)
            {
                var ground = (int)HillsGenerator.Height(x, z, seed);
                for (var y = ground - 300; y <= ground + 300; y += 23)
                {
                    var point = new VoxelCoord(x, y, z);
                    if (!world.TryGetMaterial(point, out var material))
                    {
                        continue;
                    }

                    var chunk = grid.ChunkContaining(point, 0);
                    if (!references.TryGetValue(chunk, out var reference))
                    {
                        reference = (NewVoxels(), NewVoxels());
                        HillsReference.Chunk(grid, seed, chunk, reference.Voxels, reference.Densities, features: true);
                        references[chunk] = reference;
                    }

                    var index = grid.IndexInChunk(chunk, point);
                    Assert.True(
                        reference.Voxels[index] == material,
                        $"{point}: world {material}, generator {reference.Voxels[index]}"
                    );
                    Assert.True(world.TryGetDensity(point, 0, out var density));
                    Assert.Equal(reference.Densities[index], density);
                    compared++;
                    solid += material == VoxelConstants.Air ? 0 : 1;
                    air += material == VoxelConstants.Air ? 1 : 0;
                }
            }
        }

        Assert.True(compared > 500 && solid > 100 && air > 100, $"{compared} points: {solid} solid, {air} air");
    }

    /// <summary>
    /// A heightfield's objects reach its world through <see cref="IChunkGenerator.Objects"/> when a subclass overrides
    /// the base's: none by default.
    /// </summary>
    [Fact]
    public void A_subclasses_objects_are_the_generators()
    {
        Assert.Null(ObjectsOf(new HillsGenerator(new VoxelGrid(10))));
        var placer = new NoObjects();
        Assert.Same(placer, ObjectsOf(new PlacingHeightfield(placer)));
    }

    // Read as the world reads it, through the interface.
    private static Itera.Engine.Objects.IObjectPlacer? ObjectsOf(IChunkGenerator generator) => generator.Objects;

    private sealed class NoObjects : Itera.Engine.Objects.IObjectPlacer
    {
        public void Find(
            ulong seed,
            int minX,
            int maxX,
            int minZ,
            int maxZ,
            List<Itera.Engine.Objects.ObjectPlacement> found
        ) { }
    }

    private sealed class PlacingHeightfield(Itera.Engine.Objects.IObjectPlacer placer)
        : HeightfieldGenerator(new VoxelGrid(10), new GroundLayers(5, [], HillsGenerator.Stone))
    {
        public override string Id => "test.placing-heightfield";

        public override int Version => 1;

        public override Itera.Engine.Objects.IObjectPlacer? Objects => placer;

        protected override double HeightAt(int x, int z, ulong seed) => 0;

        protected override byte SurfaceAt(int x, int z, ulong seed, double height) => HillsGenerator.Grass;
    }
}
