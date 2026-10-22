using System.Numerics;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>The two worlds the block tests read: a slope, and boxes of mixed materials. Loaded once for the class.</summary>
public sealed class VoxelBlocksWorlds : IDisposable
{
    public const int Levels = 3;

    public VoxelBlocksWorlds()
    {
        Slope = LoadSlope();
        Boxes = LoadBoxes();
    }

    public VoxelWorld Slope { get; }

    public VoxelWorld Boxes { get; }

    public static VoxelWorld LoadSlope() =>
        TestWorlds.Load(new SlopeGenerator(), new Vector3(0, 2, 0), radiusMetres: 4, levels: Levels);

    /// <summary>Mixed bricks (one byte per voxel), leaves, a pit, and whole cells of a second solid material.</summary>
    public static VoxelWorld LoadBoxes() =>
        TestWorlds.Load(
            new BoxesGenerator(
                200,
                new VoxelBox(-90, 150, -70, 150, 230, 90, BoxesGenerator.Pattern),
                new VoxelBox(-200, 190, 100, -140, 260, 140, BoxesGenerator.Leaves),
                new VoxelBox(120, 120, 110, 210, 260, 250, VoxelConstants.Air),
                new VoxelBox(-64, 64, -64, 63, 127, 63, BoxesGenerator.Stone),
                new VoxelBox(180, 190, -200, 200, 200, -190, BoxesGenerator.Mushroom)
            ),
            new Vector3(0, 2, 0),
            radiusMetres: 4,
            levels: Levels
        );

    public void Dispose()
    {
        Slope.Dispose();
        Boxes.Dispose();
    }
}

/// <summary>
/// Reading 32^3 blocks out of the chunk trees: with a 2-voxel apron for the mesher, and their own voxels alone for the
/// sky field. Every test but one only reads the shared worlds; that one edits a pair of its own.
/// </summary>
public sealed class VoxelBlocksTests : IClassFixture<VoxelBlocksWorlds>
{
    private const int Edge = VoxelWorld.BlockSampleEdge;
    private const int Levels = VoxelBlocksWorlds.Levels;

    /// <summary>Block coordinates on either side of the chunk boundaries (multiples of 4) and of the origin.</summary>
    private static readonly int[] Columns = [-8, -5, -4, -1, 0, 3, 4, 7];

    private readonly ITestOutputHelper _output;
    private readonly VoxelWorld _slope;
    private readonly VoxelWorld _boxes;
    private readonly byte[] _samples = new byte[VoxelWorld.BlockSamples];
    private readonly byte[] _expected = new byte[VoxelWorld.BlockSamples];
    private readonly byte[] _densities = new byte[VoxelWorld.BlockSamples];
    private readonly byte[] _own = new byte[VoxelWorld.OwnBlockVoxels];

    public VoxelBlocksTests(VoxelBlocksWorlds worlds, ITestOutputHelper output)
    {
        _output = output;
        _slope = worlds.Slope;
        _boxes = worlds.Boxes;
    }

    [Fact]
    public void Samples_equal_the_voxels_in_the_resident_area_at_every_level()
    {
        foreach (var world in new[] { _slope, _boxes })
        {
            var whole = 0;
            var checkedBlocks = 0;
            for (var level = 0; level < Levels; level++)
            {
                // Blocks on either side of every chunk boundary (multiples of 4) and column boundary, across the
                // surface heights of both worlds.
                foreach (var bz in Columns)
                {
                    foreach (var bx in Columns)
                    {
                        foreach (var by in SurfaceBlocks(level))
                        {
                            if (!world.TryReadBlock(level, bx, by, bz, _samples))
                            {
                                continue;
                            }

                            var unavailable = Reference(world, level, bx, by, bz, _expected);
                            if (!_expected.AsSpan().SequenceEqual(_samples))
                            {
                                Assert.Fail($"level {level} block ({bx}, {by}, {bz}) differs at sample {Differs()}");
                            }

                            checkedBlocks++;
                            whole += unavailable == 0 ? 1 : 0;
                        }
                    }
                }
            }

            Assert.True(checkedBlocks > 300, $"Only {checkedBlocks} blocks were read.");
            Assert.True(whole > 100, $"Only {whole} blocks lay wholly in the resident area.");
        }
    }

    [Fact]
    public void Densities_read_with_the_samples_equal_the_voxels_densities()
    {
        var densities = new byte[VoxelWorld.BlockSamples];

        // The edits below change the worlds, so this test reads its own pair.
        using var slope = VoxelBlocksWorlds.LoadSlope();
        using var boxes = VoxelBlocksWorlds.LoadBoxes();
        foreach (var world in new[] { slope, boxes })
        {
            // Spheres filled and cleared across block and chunk edges leave voxels partly solid, on 1 cm voxels from
            // level 1 up.
            world.Apply(VoxelEdit.Sphere(new VoxelCoord(30, 280, -20), 70, VoxelEditOperation.Fill, 9));
            world.Apply(VoxelEdit.Sphere(new VoxelCoord(-130, 200, 128), 55, VoxelEditOperation.Clear));
            var partial = 0;
            var checkedBlocks = 0;
            for (var level = 0; level < Levels; level++)
            {
                foreach (var bz in Columns)
                {
                    foreach (var bx in Columns)
                    {
                        foreach (var by in SurfaceBlocks(level))
                        {
                            if (!world.TryReadBlock(level, bx, by, bz, _samples, densities))
                            {
                                continue;
                            }

                            Reference(world, level, bx, by, bz, _expected);
                            Assert.True(_expected.AsSpan().SequenceEqual(_samples));
                            Reference(world, level, bx, by, bz, _expected, densities: true);
                            if (!_expected.AsSpan().SequenceEqual(densities))
                            {
                                Assert.Fail($"level {level} block ({bx}, {by}, {bz}) differs");
                            }

                            checkedBlocks++;
                            partial += densities.AsSpan().IndexOfAnyExcept((byte)0, (byte)255) >= 0 ? 1 : 0;
                        }
                    }
                }
            }

            Assert.True(checkedBlocks > 300, $"Only {checkedBlocks} blocks were read.");
            Assert.True(partial > 10, $"Only {partial} blocks held a voxel partly solid.");
        }
    }

    [Fact]
    public void Blocks_with_a_mix_of_materials_read_exactly()
    {
        // Level 0 blocks inside the pattern box, which stores one byte per voxel, and across its edges.
        var mixed = 0;
        for (var bz = -3; bz <= 2; bz++)
        {
            for (var bx = -4; bx <= 4; bx++)
            {
                for (var by = 4; by <= 7; by++)
                {
                    Assert.True(_boxes.TryReadBlock(0, bx, by, bz, _samples));
                    Reference(_boxes, 0, bx, by, bz, _expected);
                    if (!_expected.AsSpan().SequenceEqual(_samples))
                    {
                        Assert.Fail($"block ({bx}, {by}, {bz}) differs");
                    }

                    mixed += _samples.AsSpan().IndexOfAnyExcept(_samples[0]) >= 0 ? 1 : 0;
                }
            }
        }

        Assert.True(mixed > 40);
    }

    /// <summary>
    /// A block's own voxels, read without an apron, equal the middle of its samples; a block whose cell is empty or one
    /// material is named by its cell, with nothing written.
    /// </summary>
    [Fact]
    public void Own_blocks_equal_the_middle_of_their_samples_and_uniform_ones_are_named_by_their_cell()
    {
        const int edge = VoxelWorld.BlockEdge;
        var middle = new byte[VoxelWorld.OwnBlockVoxels];
        var own = new byte[VoxelWorld.OwnBlockVoxels];
        foreach (var world in new[] { _slope, _boxes })
        {
            int uniform = 0,
                stored = 0;
            for (var level = 0; level < Levels; level++)
            {
                foreach (var bz in Columns)
                {
                    foreach (var bx in Columns)
                    {
                        foreach (var by in SurfaceBlocks(level).Append(-2))
                        {
                            var read = world.TryReadBlock(level, bx, by, bz, _samples);
                            Array.Fill(_own, (byte)0xEE);
                            Assert.Equal(read, world.TryReadOwnBlock(level, bx, by, bz, _own, out var cell));
                            if (!read)
                            {
                                continue;
                            }

                            if (cell.IsUniformOrEmpty)
                            {
                                if (_own.AsSpan().IndexOfAnyExcept((byte)0xEE) >= 0)
                                {
                                    Assert.Fail($"level {level} block ({bx}, {by}, {bz}) is uniform but wrote voxels.");
                                }

                                own.AsSpan().Fill(cell.Material);
                                uniform++;
                            }
                            else
                            {
                                _own.AsSpan().CopyTo(own);
                                stored++;
                            }

                            CopyMiddle(_samples, middle);
                            var first = middle.AsSpan().CommonPrefixLength(own);
                            if (first < middle.Length)
                            {
                                int x = first % edge,
                                    y = first / edge % edge,
                                    z = first / (edge * edge);
                                Assert.Fail(
                                    $"level {level} block ({bx}, {by}, {bz}) voxel ({x}, {y}, {z}): {own[first]}, expected {middle[first]}"
                                );
                            }
                        }
                    }
                }
            }

            _output.WriteLine($"{uniform} uniform blocks, {stored} read");
            Assert.True(uniform > 50, $"Only {uniform} uniform blocks.");
            Assert.True(stored > 50, $"Only {stored} blocks were read.");
        }
    }

    [Fact]
    public void Samples_outside_the_resident_area_take_the_material_of_the_clamped_voxel()
    {
        var densities = new byte[VoxelWorld.BlockSamples];
        foreach (var world in new[] { _slope, _boxes })
        {
            for (var level = 0; level < Levels; level++)
            {
                var edges = 0;

                // Walk out along each horizontal axis until the block's column is not resident: the last block that
                // reads has the unresident column beside it.
                foreach (var (stepX, stepZ) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    foreach (var by in SurfaceBlocks(level))
                    {
                        int bx = 0,
                            bz = 0;
                        while (world.TryReadBlock(level, bx + stepX, by, bz + stepZ, _samples))
                        {
                            bx += stepX;
                            bz += stepZ;
                        }

                        Assert.True(world.TryReadBlock(level, bx, by, bz, _samples, densities));
                        var unavailable = Reference(world, level, bx, by, bz, _expected);
                        if (!_expected.AsSpan().SequenceEqual(_samples))
                        {
                            Assert.Fail($"edge block differs at {Differs()}");
                        }

                        Assert.True(unavailable > 0, "The block's neighbours are all resident.");
                        Reference(world, level, bx, by, bz, _expected, densities: true);
                        Assert.True(_expected.AsSpan().SequenceEqual(densities), "edge block's densities differ");
                        edges++;
                    }
                }

                Assert.True(edges >= 4);
            }
        }
    }

    [Fact]
    public void Samples_outside_the_map_take_the_material_of_the_clamped_voxel()
    {
        // A world around the map's west edge: level 0 chunk -391 is the first inside it.
        using var world = TestWorlds.Load(new SlopeGenerator(), new Vector3(-499, 2, 0), radiusMetres: 3);
        var firstBlockX = VoxelGrid.Unit.MinChunkXZ * 4;
        var checkedBlocks = 0;
        for (var bz = -3; bz <= 3; bz++)
        {
            foreach (var by in SurfaceBlocks(0))
            {
                foreach (var bx in new[] { firstBlockX, firstBlockX + 1, firstBlockX + 4 })
                {
                    Assert.True(world.TryReadBlock(0, bx, by, bz, _samples));
                    var unavailable = Reference(world, 0, bx, by, bz, _expected);
                    if (!_expected.AsSpan().SequenceEqual(_samples))
                    {
                        Assert.Fail($"block ({bx}, {by}, {bz}) differs");
                    }

                    Assert.Equal(bx == firstBlockX ? 2 * Edge * Edge : 0, unavailable);
                    checkedBlocks++;
                }

                Assert.False(world.TryReadBlock(0, firstBlockX - 1, by, bz, _samples));
            }
        }

        Assert.True(checkedBlocks > 50);
    }

    [Fact]
    public void Samples_below_and_above_the_world_take_the_material_of_the_clamped_voxel()
    {
        foreach (var world in new[] { _slope, _boxes })
        {
            for (var level = 0; level < Levels; level++)
            {
                var lowest = (VoxelGrid.Unit.MinChunkY >> level) * 4;
                var highest = ((VoxelGrid.Unit.MaxChunkY >> level) + 1) * 4 - 1;
                foreach (var by in new[] { lowest, lowest + 1, highest - 1, highest })
                {
                    foreach (var (bx, bz) in new[] { (0, 0), (-1, 3), (3, -4), (-4, -1) })
                    {
                        Assert.True(world.TryReadBlock(level, bx, by, bz, _samples));
                        var unavailable = Reference(world, level, bx, by, bz, _expected);
                        if (!_expected.AsSpan().SequenceEqual(_samples))
                        {
                            Assert.Fail($"differs at {Differs()}");
                        }

                        Assert.Equal(by == lowest || by == highest ? 2 * Edge * Edge : 0, unavailable);
                    }
                }

                // One block below or above them is outside the world.
                Assert.False(world.TryReadBlock(level, 0, lowest - 1, 0, _samples));
                Assert.False(world.TryReadBlock(level, 0, highest + 1, 0, _samples));
            }
        }
    }

    [Fact]
    public void A_block_that_is_not_resident_is_refused_and_nothing_is_written()
    {
        Array.Fill(_samples, (byte)0xEE);

        // Inside the map, not loaded.
        Assert.False(_slope.TryReadBlock(0, 1000, 6, 0, _samples));
        Assert.False(_slope.TryReadBlock(1, 0, 6, -500, _samples));

        // Outside the map, outside the world, and a level the world does not keep.
        Assert.False(_slope.TryReadBlock(0, VoxelGrid.Unit.MaxChunkXZ * 4 + 4, 6, 0, _samples));
        Assert.False(_slope.TryReadBlock(0, 0, 6, int.MinValue, _samples));
        Assert.False(_slope.TryReadBlock(0, int.MaxValue, int.MaxValue, int.MaxValue, _samples));
        Assert.False(_slope.TryReadBlock(Levels, 0, 6, 0, _samples));
        Assert.False(_slope.TryReadBlock(-1, 0, 6, 0, _samples));
        Assert.All(_samples, sample => Assert.Equal(0xEE, sample));

        Array.Fill(_own, (byte)0xEE);
        Assert.False(_slope.TryReadOwnBlock(0, 1000, 6, 0, _own, out _));
        Assert.False(_slope.TryReadOwnBlock(0, VoxelGrid.Unit.MaxChunkXZ * 4 + 4, 6, 0, _own, out _));
        Assert.False(_slope.TryReadOwnBlock(0, 0, 6, int.MinValue, _own, out _));
        Assert.False(_slope.TryReadOwnBlock(0, int.MaxValue, int.MaxValue, int.MaxValue, _own, out _));
        Assert.False(_slope.TryReadOwnBlock(Levels, 0, 6, 0, _own, out _));
        Assert.False(_slope.TryReadOwnBlock(-1, 0, 6, 0, _own, out _));
        Assert.All(_own, voxel => Assert.Equal(0xEE, voxel));
    }

    [Fact]
    public void A_buffer_of_the_wrong_length_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _slope.TryReadBlock(0, 0, 6, 0, new byte[VoxelWorld.BlockSamples - 1]));
        Assert.Throws<ArgumentException>(() => _slope.TryReadBlock(0, 0, 6, 0, new byte[VoxelWorld.BlockSamples + 1]));
        Assert.Throws<ArgumentException>(() =>
            _slope.TryReadOwnBlock(0, 0, 6, 0, new byte[VoxelWorld.OwnBlockVoxels - 1], out _)
        );
    }

    [Fact]
    public void A_block_with_no_surface_is_never_reported_as_having_one()
    {
        foreach (var world in new[] { _slope, _boxes })
        {
            int blocks = 0,
                with = 0,
                without = 0;
            for (var level = 0; level < Levels; level++)
            {
                foreach (var bz in Columns)
                {
                    foreach (var bx in Columns)
                    {
                        for (var by = -3 >> level; by <= 13 >> level; by++)
                        {
                            var read = world.TryReadBlock(level, bx, by, bz, _samples);
                            var mayHave = world.BlockMayHaveSurface(level, bx, by, bz);
                            if (!read)
                            {
                                Assert.False(mayHave);
                                continue;
                            }

                            blocks++;
                            if (HasOwnedCrossing(_samples))
                            {
                                Assert.True(mayHave, $"level {level} block ({bx}, {by}, {bz}) has a crossing");
                                with++;
                            }
                            else
                            {
                                without += mayHave ? 0 : 1;
                            }
                        }
                    }
                }
            }

            _output.WriteLine($"{blocks} blocks: {with} with a crossing, {without} the test rules out");
            Assert.True(with > 50);
            Assert.True(without > 500);
        }
    }

    [Fact]
    public void Blocks_deep_in_stone_or_in_open_air_have_no_surface()
    {
        // Level 0: the chunk at y 0 (voxels 0 to 127) is stone under the slope's surface, the one at y 3 is open air.
        Assert.False(_slope.BlockMayHaveSurface(0, 0, 0, 0));
        Assert.False(_slope.BlockMayHaveSurface(0, -3, 2, 2));
        Assert.False(_slope.BlockMayHaveSurface(0, 0, 12, 0));
        Assert.False(_slope.BlockMayHaveSurface(1, 0, 20, 0));
        Assert.False(_slope.BlockMayHaveSurface(2, -2, -20, 1));

        // The block just under the surface and one on it have one.
        Assert.True(_slope.BlockMayHaveSurface(0, 0, 6, 0));
        Assert.True(_slope.BlockMayHaveSurface(1, 0, 3, 0));

        // Cells of a second solid material among dirt: no surface, since both are solid. The cell at y 3 has the
        // pattern box's cells above it.
        Assert.False(_boxes.BlockMayHaveSurface(0, 0, 2, 0));
        Assert.False(_boxes.BlockMayHaveSurface(0, -1, 2, -1));
        Assert.False(_boxes.BlockMayHaveSurface(0, 1, 2, 0));
        Assert.True(_boxes.BlockMayHaveSurface(0, 0, 3, 0));
    }

    [Fact]
    public void A_block_next_to_an_unresident_column_counts_it_as_the_same()
    {
        // The neighbour above or beside that cannot be read shows nothing: a block that is uniform in air and
        // whose readable neighbours are air has no surface, whatever lies beyond the loaded area.
        var level = 0;
        var by = 12;
        var bx = 0;
        while (_slope.TryReadBlock(level, bx + 1, by, 0, _samples))
        {
            bx++;
        }

        Assert.False(_slope.BlockMayHaveSurface(level, bx, by, 0));
        Assert.False(_slope.BlockMayHaveSurface(level, 5000, by, 0));
    }

    [Fact]
    public void Reading_blocks_allocates_nothing()
    {
        var blocks = new (int Level, int X, int Y, int Z)[]
        {
            (0, 0, 6, 0),
            (0, 3, 6, 4),
            (0, -4, 5, -4),
            (0, 0, 0, 0),
            (0, 0, 12, 0),
            (1, 0, 3, 0),
            (2, -1, 1, 0),
            (0, 1000, 6, 0),
            (0, 0, -128, 0),
        };
        for (var warm = 0; warm < 20; warm++)
        {
            foreach (var (level, x, y, z) in blocks)
            {
                _slope.TryReadBlock(level, x, y, z, _samples);
                _slope.BlockMayHaveSurface(level, x, y, z);
                _boxes.TryReadBlock(level, x, y, z, _samples, _densities);
                _slope.TryReadOwnBlock(level, x, y, z, _own, out _);
                _boxes.TryReadOwnBlock(level, x, y, z, _own, out _);
            }
        }

        var before = SystemAllocation.StartMeasurement();
        for (var round = 0; round < 100; round++)
        {
            foreach (var (level, x, y, z) in blocks)
            {
                _slope.TryReadBlock(level, x, y, z, _samples);
                _slope.BlockMayHaveSurface(level, x, y, z);
                _boxes.TryReadBlock(level, x, y, z, _samples, _densities);
                _slope.TryReadOwnBlock(level, x, y, z, _own, out _);
                _boxes.TryReadOwnBlock(level, x, y, z, _own, out _);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The block heights that hold the surfaces of the test worlds at a level.</summary>
    private static IEnumerable<int> SurfaceBlocks(int level)
    {
        // Level-0 surfaces lie at y 150 to 260: blocks 4 to 8; level k has them over 2^k fewer blocks.
        for (var by = 4 >> level; by <= (8 >> level) + 1; by++)
        {
            yield return by;
        }

        yield return 12 >> level;
    }

    /// <summary>
    /// Fills <paramref name="expected"/> with the block's samples read one voxel at a time with
    /// <see cref="VoxelWorld.TryGetMaterial(VoxelCoord, int, out byte)"/> and the clamp rule; returns how many samples
    /// were not in a resident column inside the world.
    /// </summary>
    private static int Reference(VoxelWorld world, int level, int bx, int by, int bz, byte[] expected) =>
        Reference(world, level, bx, by, bz, expected, densities: false);

    /// <summary>
    /// The samples of a block read voxel by voxel: materials, or with <paramref name="densities"/> each voxel's density.
    /// Returns how many lay outside the resident area and took their clamped voxel's.
    /// </summary>
    private static int Reference(VoxelWorld world, int level, int bx, int by, int bz, byte[] expected, bool densities)
    {
        bool Read(VoxelCoord point, out byte value)
        {
            if (densities)
            {
                return world.TryGetDensity(point, level, out value);
            }

            return world.TryGetMaterial(point, level, out value);
        }

        var unavailable = 0;
        var lowestY = (VoxelGrid.Unit.MinChunkY >> level) << VoxelConstants.ChunkShift;
        var endY = ((VoxelGrid.Unit.MaxChunkY >> level) + 1) << VoxelConstants.ChunkShift;
        for (var sz = 0; sz < Edge; sz++)
        {
            for (var sy = 0; sy < Edge; sy++)
            {
                for (var sx = 0; sx < Edge; sx++)
                {
                    int vx = bx * 32 + sx - 2,
                        vy = by * 32 + sy - 2,
                        vz = bz * 32 + sz - 2;
                    if (
                        vy < lowestY
                        || vy >= endY
                        || !Read(new VoxelCoord(vx << level, vy << level, vz << level), out var m)
                    )
                    {
                        unavailable++;
                        Assert.True(
                            Read(
                                new VoxelCoord(
                                    Math.Clamp(vx, bx * 32, bx * 32 + 31) << level,
                                    Math.Clamp(vy, by * 32, by * 32 + 31) << level,
                                    Math.Clamp(vz, bz * 32, bz * 32 + 31) << level
                                ),
                                out m
                            ),
                            "The block's own box is not resident."
                        );
                    }

                    expected[sx + Edge * (sy + Edge * sz)] = m;
                }
            }
        }

        return unavailable;
    }

    /// <summary>Copies the block's own voxels, the middle of its samples without the apron, into <paramref name="middle"/>.</summary>
    private static void CopyMiddle(ReadOnlySpan<byte> samples, Span<byte> middle)
    {
        const int edge = VoxelWorld.BlockEdge;
        const int apron = VoxelWorld.BlockApron;
        for (var z = 0; z < edge; z++)
        {
            for (var y = 0; y < edge; y++)
            {
                samples
                    .Slice(apron + Edge * (y + apron + Edge * (z + apron)), edge)
                    .CopyTo(middle.Slice(edge * (y + edge * z)));
            }
        }
    }

    private string Differs()
    {
        for (var i = 0; i < _samples.Length; i++)
        {
            if (_samples[i] != _expected[i])
            {
                return $"({i % Edge}, {i / Edge % Edge}, {i / (Edge * Edge)}): read {_samples[i]}, expected {_expected[i]}";
            }
        }

        return "none";
    }

    /// <summary>True when an edge from a voxel of the block to its +x, +y, or +z neighbour joins air and a solid voxel.</summary>
    private static bool HasOwnedCrossing(ReadOnlySpan<byte> samples)
    {
        for (var z = 2; z < 34; z++)
        {
            for (var y = 2; y < 34; y++)
            {
                for (var x = 2; x < 34; x++)
                {
                    var solid = samples[x + Edge * (y + Edge * z)] != VoxelConstants.Air;
                    if (
                        solid != (samples[x + 1 + Edge * (y + Edge * z)] != VoxelConstants.Air)
                        || solid != (samples[x + Edge * (y + 1 + Edge * z)] != VoxelConstants.Air)
                        || solid != (samples[x + Edge * (y + Edge * (z + 1))] != VoxelConstants.Air)
                    )
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
