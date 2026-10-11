using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed class ChunkTreeTests : IDisposable
{
    private const byte Stone = 3;
    private const byte Dirt = 2;
    private readonly VoxelMemory _memory = new(256 << 20);

    public void Dispose() => _memory.Dispose();

    [Theory]
    [InlineData((byte)0)]
    [InlineData(Stone)]
    public void Uniform_chunks_take_no_memory(byte material)
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        voxels.AsSpan().Fill(material);

        var chunk = ChunkTree.Encode(_memory, voxels, false, out var outOfMemory);

        Assert.False(outOfMemory);
        Assert.Equal(ChildState.Uniform(material), chunk);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void Chunks_round_trip()
    {
        var voxels = TestChunk();

        var chunk = ChunkTree.Encode(_memory, voxels, false, out _);

        var decoded = new byte[VoxelConstants.ChunkVoxels];
        ChunkTree.Decode(_memory, chunk, decoded);
        Assert.Equal(voxels, decoded);
        var random = new Random(4);
        for (var i = 0; i < 10_000; i++)
        {
            int x = random.Next(128),
                y = random.Next(128),
                z = random.Next(128);
            Assert.Equal(voxels[x + 128 * y + 16384 * z], ChunkTree.ReadVoxel(_memory, chunk, x, y, z));
        }
    }

    [Fact]
    public void Densities_round_trip_through_a_chunk()
    {
        var voxels = TestChunk();
        var densities = new byte[VoxelConstants.ChunkVoxels];
        var random = new Random(5);
        for (var i = 0; i < densities.Length; i++)
        {
            // Partial densities in a slab across the chunk, the defaults elsewhere.
            densities[i] = (i >> 14) is >= 40 and < 48
                ? VoxelDensity.Fit(voxels[i], (byte)random.Next(256))
                : VoxelDensity.Default(voxels[i]);
        }

        var chunk = ChunkTree.Encode(_memory, voxels, densities, false, out _);

        var decoded = new byte[VoxelConstants.ChunkVoxels];
        ChunkTree.Decode(_memory, chunk, decoded);
        Assert.Equal(voxels, decoded);
        ChunkTree.DecodeDensities(_memory, chunk, decoded);
        Assert.Equal(densities, decoded);
        for (var i = 0; i < 10_000; i++)
        {
            int x = random.Next(128),
                y = random.Next(128),
                z = random.Next(128);
            Assert.Equal(densities[x + 128 * y + 16384 * z], ChunkTree.ReadDensity(_memory, chunk, x, y, z));
        }

        ChunkTree.Free(_memory, ChunkTree.Copy(_memory, chunk, ChunkTree.RootDepth), ChunkTree.RootDepth);
        ChunkTree.Free(_memory, chunk, ChunkTree.RootDepth);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void Air_with_densities_is_not_an_empty_chunk()
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        densities[5 + 128 * 6 + 16384 * 7] = 60;

        var chunk = ChunkTree.Encode(_memory, voxels, densities, false, out _);

        Assert.Equal(ChildKind.Handle, chunk.Kind);
        Assert.Equal(VoxelConstants.Air, ChunkTree.ReadVoxel(_memory, chunk, 5, 6, 7));
        Assert.Equal(60, ChunkTree.ReadDensity(_memory, chunk, 5, 6, 7));
        Assert.Equal(VoxelDensity.Empty, ChunkTree.ReadDensity(_memory, chunk, 5, 6, 8));
        ChunkTree.Probe(_memory, chunk, 5, 6, 7, out var material);
        Assert.Equal(VoxelConstants.Air, material);
        ChunkTree.Free(_memory, chunk, ChunkTree.RootDepth);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void Flat_ground_stores_one_layer_of_bricks()
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        voxels.AsSpan(0, 50 * 128).Fill(Stone);
        for (var z = 1; z < 128; z++)
        {
            voxels.AsSpan(0, 50 * 128).CopyTo(voxels.AsSpan(z * 16384));
        }

        var chunk = ChunkTree.Encode(_memory, voxels, false, out _);

        int nodes = 0,
            bricks = 0;
        ChunkTree.Count(_memory, chunk, ref nodes, ref bricks);
        Assert.Equal(256, bricks);
        Assert.Equal(1 + 16, nodes);
        Assert.Equal(ChildState.Uniform(Stone), ChunkTree.GetBrick(_memory, chunk, 3, 5, 9));
        Assert.Equal(ChildState.Empty, ChunkTree.GetBrick(_memory, chunk, 3, 7, 9));
        Assert.Equal(ChildKind.Handle, ChunkTree.GetBrick(_memory, chunk, 3, 6, 9).Kind);
    }

    [Fact]
    public void Setting_a_brick_into_an_empty_chunk_and_back_leaves_no_memory()
    {
        var brick = BrickCodec.Encode(_memory, BrickCodecTests.RandomBrick(3, seed: 1), false, out _);

        var chunk = ChunkTree.SetBrick(_memory, ChildState.Empty, 3, 4, 5, brick);

        Assert.Equal(ChildKind.Handle, chunk.Kind);
        Assert.Equal(brick, ChunkTree.GetBrick(_memory, chunk, 3, 4, 5));
        Assert.Equal(ChildState.Empty, ChunkTree.GetBrick(_memory, chunk, 3, 4, 6));

        chunk = ChunkTree.SetBrick(_memory, chunk, 3, 4, 5, ChildState.Empty);

        Assert.Equal(ChildState.Empty, chunk);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void A_chunk_edited_back_to_one_material_collapses()
    {
        var chunk = ChildState.Uniform(Stone);

        chunk = ChunkTree.SetBrick(_memory, chunk, 15, 0, 7, ChildState.Uniform(Dirt));

        Assert.Equal(ChildKind.Handle, chunk.Kind);
        Assert.Equal(Dirt, ChunkTree.ReadVoxel(_memory, chunk, 127, 0, 60));
        Assert.Equal(Stone, ChunkTree.ReadVoxel(_memory, chunk, 119, 0, 60));

        chunk = ChunkTree.SetBrick(_memory, chunk, 15, 0, 7, ChildState.Uniform(Stone));

        Assert.Equal(ChildState.Uniform(Stone), chunk);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void Child_lists_grow_and_shrink_through_every_class()
    {
        var chunk = ChildState.Empty;
        var expected = new ChildState[64];
        var order = Enumerable.Range(0, 64).ToArray();
        new Random(5).Shuffle(order);

        foreach (var b in order)
        {
            var state = b % 3 == 0 ? ChildState.Uniform((byte)(b + 1)) : StoredBrick(b);
            expected[b] = state;
            chunk = ChunkTree.SetBrick(_memory, chunk, b & 3, (b >> 2) & 3, b >> 4, state);
            AssertCell(chunk, expected);
        }

        new Random(6).Shuffle(order);
        foreach (var b in order)
        {
            expected[b] = ChildState.Empty;
            chunk = ChunkTree.SetBrick(_memory, chunk, b & 3, (b >> 2) & 3, b >> 4, ChildState.Empty);
            AssertCell(chunk, expected);
        }

        Assert.Equal(ChildState.Empty, chunk);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void Running_out_of_memory_while_encoding_keeps_nothing()
    {
        using var memory = new VoxelMemory(3 << 20);
        var voxels = TestChunk();

        var chunk = ChunkTree.Encode(memory, voxels, false, out var outOfMemory);

        Assert.True(outOfMemory);
        Assert.Equal(ChildState.Empty, chunk);
        Assert.Equal(0, memory.UsedBytes);
    }

    [Fact]
    public void Freeing_a_chunk_returns_all_its_memory()
    {
        var chunk = ChunkTree.Encode(_memory, TestChunk(), false, out _);
        Assert.True(_memory.UsedBytes > 0);

        ChunkTree.Free(_memory, chunk, ChunkTree.RootDepth);

        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void A_column_reads_voxels_through_its_layers()
    {
        var column = new Column();
        var key = new ColumnKey(0, -2, 3);
        column.Reset(key, VoxelGrid.Unit);
        var layer = 10 - VoxelGrid.Unit.MinChunkY;
        column.Widen(layer - 1) = ChildState.Uniform(Stone);
        column.Widen(layer) = ChunkTree.Encode(_memory, TestChunk(), false, out _);

        var min = key.Chunk(layer, VoxelGrid.Unit).MinVoxel;
        Assert.Equal(Stone, column.ReadVoxel(_memory, new VoxelCoord(min.X, min.Y - 1, min.Z)));
        Assert.Equal(
            ChunkTree.ReadVoxel(_memory, column.LayerAt(layer), 5, 6, 7),
            column.ReadVoxel(_memory, new VoxelCoord(min.X + 5, min.Y + 6, min.Z + 7))
        );
        Assert.Equal(
            VoxelConstants.Air,
            column.ReadVoxel(_memory, new VoxelCoord(min.X, VoxelGrid.Unit.EndVoxelY, min.Z))
        );

        int mixed = 0,
            nodes = 0,
            bricks = 0;
        column.Count(_memory, ref mixed, ref nodes, ref bricks);
        Assert.Equal(1, mixed);
        foreach (var state in column.Stored)
        {
            ChunkTree.Free(_memory, state, ChunkTree.RootDepth);
        }

        Assert.Equal(0, _memory.UsedBytes);
    }

    /// <summary>Stone below a sloped ground, dirt on top, a ball of mixed materials, and air above.</summary>
    internal static byte[] TestChunk()
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        var random = new Random(7);
        for (var z = 0; z < 128; z++)
        {
            for (var y = 0; y < 128; y++)
            {
                for (var x = 0; x < 128; x++)
                {
                    var ground = 40 + x / 4 + z / 8;
                    byte material =
                        y < ground - 6 ? Stone
                        : y < ground ? Dirt
                        : VoxelConstants.Air;
                    var dx = x - 90;
                    var dy = y - 90;
                    var dz = z - 40;
                    if (dx * dx + dy * dy + dz * dz < 400)
                    {
                        material = (byte)random.Next(4, 30);
                    }

                    voxels[x + 128 * y + 16384 * z] = material;
                }
            }
        }

        return voxels;
    }

    private ChildState StoredBrick(int seed) =>
        BrickCodec.Encode(_memory, BrickCodecTests.RandomBrick(2, seed), false, out _);

    private void AssertCell(ChildState chunk, ChildState[] expected)
    {
        for (var b = 0; b < 64; b++)
        {
            Assert.Equal(expected[b], ChunkTree.GetBrick(_memory, chunk, b & 3, (b >> 2) & 3, b >> 4));
        }
    }

    /// <summary>
    /// A column stores only its window of layers: below it the state below, above it the state above; storing a layer
    /// outside widens the window, the layers between taking the state of their side.
    /// </summary>
    [Fact]
    public void A_column_stores_its_window_and_widens_either_way()
    {
        var column = new Column();
        column.Reset(new ColumnKey(0, 0, 0), VoxelGrid.Unit);
        var mixed = ChunkTree.Encode(_memory, TestChunk(), false, out _);
        column.Install(40, [ChildState.Uniform(Stone), mixed], ChildState.Uniform(Stone), ChildState.Empty);

        Assert.Equal((40, 42, VoxelGrid.Unit.MaxLayers), (column.First, column.End, column.LayerCount));
        Assert.Equal(ChildState.Uniform(Stone), column.LayerAt(0));
        Assert.Equal(mixed, column.LayerAt(41));
        Assert.Equal(ChildState.Empty, column.LayerAt(90));

        column.Widen(45) = ChildState.Uniform(7);
        column.Widen(37) = ChildState.Empty;

        Assert.Equal((37, 46), (column.First, column.End));
        Assert.Equal(
            [
                ChildState.Empty,
                ChildState.Uniform(Stone),
                ChildState.Uniform(Stone),
                ChildState.Uniform(Stone),
                mixed,
                ChildState.Empty,
                ChildState.Empty,
                ChildState.Empty,
                ChildState.Uniform(7),
            ],
            column.Stored.ToArray()
        );
        Assert.Equal(ChildState.Uniform(Stone), column.LayerAt(36));
        Assert.Equal(ChildState.Empty, column.LayerAt(46));
        ChunkTree.Free(_memory, mixed, ChunkTree.RootDepth);
        column.ClearStored();
        Assert.Equal(0, _memory.UsedBytes);
    }
}
