using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed unsafe class BrickCodecTests : IDisposable
{
    private readonly VoxelMemory _memory = new(64 << 20);

    public void Dispose() => _memory.Dispose();

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)7)]
    public void Uniform_bricks_are_not_stored(byte material)
    {
        var voxels = new byte[512];
        voxels.AsSpan().Fill(material);

        var state = BrickCodec.Encode(_memory, voxels, false, out var outOfMemory);

        Assert.False(outOfMemory);
        Assert.Equal(ChildState.Uniform(material), state);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(16, 4)]
    [InlineData(17, 8)]
    [InlineData(255, 8)]
    public void Bricks_round_trip_at_every_index_width(int materials, int expectedBits)
    {
        var voxels = RandomBrick(materials, seed: materials);

        var state = BrickCodec.Encode(_memory, voxels, false, out _);

        Assert.Equal(ChildKind.Handle, state.Kind);
        Assert.Equal(expectedBits, _memory.Brick(state.Handle)->Bits);
        var decoded = new byte[512];
        BrickCodec.Decode(_memory, state.Handle, decoded);
        Assert.Equal(voxels, decoded);
        for (var i = 0; i < 512; i++)
        {
            Assert.Equal(voxels[i], BrickCodec.Read(_memory, state.Handle, i));
        }
    }

    [Fact]
    public void A_brick_of_air_and_one_material_takes_76_bytes()
    {
        var voxels = new byte[512];
        voxels.AsSpan(0, 64).Fill(3);

        BrickCodec.Encode(_memory, voxels, false, out _);

        Assert.Equal(76, _memory.UsedBytes);
    }

    [Fact]
    public void A_full_brick_of_two_materials_still_has_a_full_mask()
    {
        var voxels = new byte[512];
        voxels.AsSpan().Fill(1);
        voxels[300] = 2;

        var state = BrickCodec.Encode(_memory, voxels, false, out _);

        for (var w = 0; w < 8; w++)
        {
            Assert.Equal(ulong.MaxValue, _memory.BrickMask(state.Handle)[w]);
        }

        Assert.Equal(2, BrickCodec.Read(_memory, state.Handle, 300));
        Assert.Equal(1, BrickCodec.Read(_memory, state.Handle, 301));
    }

    [Fact]
    public void Re_encoding_narrows_the_index_width()
    {
        var voxels = RandomBrick(40, seed: 1);
        var wide = BrickCodec.Encode(_memory, voxels, false, out _);
        BrickCodec.Decode(_memory, wide.Handle, voxels);
        BrickCodec.Free(_memory, wide.Handle);

        for (var i = 0; i < voxels.Length; i++)
        {
            voxels[i] = voxels[i] == 0 ? (byte)0 : (byte)(voxels[i] % 2 + 1);
        }

        var narrow = BrickCodec.Encode(_memory, voxels, false, out _);

        Assert.Equal(1, _memory.Brick(narrow.Handle)->Bits);
        var decoded = new byte[512];
        BrickCodec.Decode(_memory, narrow.Handle, decoded);
        Assert.Equal(voxels, decoded);
    }

    [Fact]
    public void The_same_voxels_give_the_same_bytes()
    {
        var voxels = RandomBrick(6, seed: 9);

        var a = BrickCodec.Encode(_memory, voxels, false, out _);
        var b = BrickCodec.Encode(_memory, voxels, false, out _);

        Assert.True(
            new Span<byte>(_memory.BrickMask(a.Handle), 64).SequenceEqual(
                new Span<byte>(_memory.BrickMask(b.Handle), 64)
            )
        );
        var blockA = new Span<byte>(_memory.Pointer(VoxelPool.Index4, _memory.Brick(a.Handle)->Block), 272);
        var blockB = new Span<byte>(_memory.Pointer(VoxelPool.Index4, _memory.Brick(b.Handle)->Block), 272);
        Assert.True(blockA.SequenceEqual(blockB));
    }

    [Fact]
    public void Freeing_returns_the_brick_and_its_block()
    {
        var state = BrickCodec.Encode(_memory, RandomBrick(3, seed: 2), false, out _);

        BrickCodec.Free(_memory, state.Handle);

        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void Running_out_of_memory_keeps_nothing()
    {
        // Room for the brick slab and two index slabs, so the brick succeeds and its index block fails.
        using var memory = new VoxelMemory(4 << 20);
        var voxels = RandomBrick(200, seed: 3);
        var stored = 0;
        while (BrickCodec.Encode(memory, voxels, false, out _).Kind == ChildKind.Handle)
        {
            stored++;
        }

        Assert.Equal(4096, stored);
        var used = memory.UsedBytes;
        var failed = BrickCodec.Encode(memory, voxels, false, out var failedOutOfMemory);

        Assert.True(failedOutOfMemory);
        Assert.Equal(ChildState.Empty, failed);
        Assert.Equal(used, memory.UsedBytes);
    }

    [Fact]
    public void Fill_writes_uniform_states_without_memory()
    {
        var voxels = new byte[512];

        BrickCodec.Fill(_memory, ChildState.Uniform(5), voxels);

        Assert.All(voxels, v => Assert.Equal(5, v));
    }

    [Fact]
    public void Default_densities_store_no_density_block()
    {
        var voxels = RandomBrick(3, seed: 5);
        var densities = new byte[512];
        for (var i = 0; i < 512; i++)
        {
            densities[i] = VoxelDensity.Default(voxels[i]);
        }

        var state = BrickCodec.Encode(_memory, voxels, densities, false, out _);

        Assert.False(BrickCodec.HasDensities(_memory, state.Handle));
        var uniform = new byte[512];
        uniform.AsSpan().Fill(4);
        densities.AsSpan().Fill(VoxelDensity.Full);
        Assert.Equal(ChildState.Uniform(4), BrickCodec.Encode(_memory, uniform, densities, false, out _));
        densities.AsSpan().Clear();
        Assert.Equal(ChildState.Empty, BrickCodec.Encode(_memory, new byte[512], densities, false, out _));
    }

    [Fact]
    public void Densities_round_trip_with_their_materials()
    {
        var voxels = RandomBrick(5, seed: 6);
        var densities = RandomDensities(voxels, seed: 6);

        var state = BrickCodec.Encode(_memory, voxels, densities, false, out _);

        Assert.True(BrickCodec.HasDensities(_memory, state.Handle));
        var decoded = new byte[512];
        BrickCodec.Decode(_memory, state.Handle, decoded);
        Assert.Equal(voxels, decoded);
        var decodedDensities = new byte[512];
        BrickCodec.DecodeDensities(_memory, state.Handle, decodedDensities);
        Assert.Equal(densities, decodedDensities);
        for (var i = 0; i < 512; i++)
        {
            Assert.Equal(densities[i], BrickCodec.ReadDensity(_memory, state.Handle, i));
        }
    }

    [Fact]
    public void A_brick_of_one_material_with_its_own_densities_is_stored_whole()
    {
        var voxels = new byte[512];
        voxels.AsSpan().Fill(6);
        var densities = new byte[512];
        densities.AsSpan().Fill(VoxelDensity.Full);
        densities[100] = 200;

        var state = BrickCodec.Encode(_memory, voxels, densities, false, out _);

        Assert.Equal(ChildKind.Handle, state.Kind);
        for (var w = 0; w < 8; w++)
        {
            Assert.Equal(ulong.MaxValue, _memory.BrickMask(state.Handle)[w]);
        }

        Assert.Equal(6, BrickCodec.Read(_memory, state.Handle, 100));
        Assert.Equal(200, BrickCodec.ReadDensity(_memory, state.Handle, 100));
        Assert.Equal(VoxelDensity.Full, BrickCodec.ReadDensity(_memory, state.Handle, 101));
    }

    [Fact]
    public void A_brick_of_air_with_densities_is_stored_with_an_empty_mask()
    {
        var voxels = new byte[512];
        var densities = new byte[512];
        densities[7] = 90;

        var state = BrickCodec.Encode(_memory, voxels, densities, false, out _);

        Assert.Equal(ChildKind.Handle, state.Kind);
        for (var w = 0; w < 8; w++)
        {
            Assert.Equal(0UL, _memory.BrickMask(state.Handle)[w]);
        }

        var decoded = new byte[512];
        decoded.AsSpan().Fill(9);
        BrickCodec.Decode(_memory, state.Handle, decoded);
        Assert.All(decoded, v => Assert.Equal(VoxelConstants.Air, v));
        Assert.Equal(90, BrickCodec.ReadDensity(_memory, state.Handle, 7));
        Assert.Equal(0, BrickCodec.ReadDensity(_memory, state.Handle, 8));
        Span<ulong> mask = stackalloc ulong[VoxelConstants.BrickMaskWords];
        Assert.False(VoxelWorld.BlockingMask(_memory, state.Handle, default, mask));
    }

    [Fact]
    public void Densities_are_fitted_to_their_materials()
    {
        var voxels = new byte[512];
        voxels.AsSpan(0, 256).Fill(2);
        var densities = new byte[512];
        densities[0] = 3;
        densities[1] = 255;
        densities[300] = 200;
        densities[301] = 20;

        var state = BrickCodec.Encode(_memory, voxels, densities, false, out _);

        Assert.Equal(VoxelDensity.Solid, BrickCodec.ReadDensity(_memory, state.Handle, 0));
        Assert.Equal(255, BrickCodec.ReadDensity(_memory, state.Handle, 1));
        Assert.Equal(VoxelDensity.Solid, BrickCodec.ReadDensity(_memory, state.Handle, 2));
        Assert.Equal(VoxelDensity.Solid - 1, BrickCodec.ReadDensity(_memory, state.Handle, 300));
        Assert.Equal(20, BrickCodec.ReadDensity(_memory, state.Handle, 301));
    }

    [Fact]
    public void Copying_and_freeing_take_the_density_block_with_the_brick()
    {
        var voxels = RandomBrick(3, seed: 7);
        var densities = RandomDensities(voxels, seed: 7);
        var state = BrickCodec.Encode(_memory, voxels, densities, false, out _);

        var copy = BrickCodec.Copy(_memory, state.Handle);
        BrickCodec.Free(_memory, state.Handle);

        var decoded = new byte[512];
        BrickCodec.DecodeDensities(_memory, copy.Handle, decoded);
        Assert.Equal(densities, decoded);
        Assert.Equal(1, _memory.LiveBlocks(VoxelPool.Density));
        BrickCodec.Free(_memory, copy.Handle);
        Assert.Equal(0, _memory.UsedBytes);
    }

    [Fact]
    public void Running_out_of_memory_for_densities_keeps_nothing()
    {
        // Room for the brick slab and an index slab, but not the density slab after them.
        using var memory = new VoxelMemory(5 << 19);
        var voxels = RandomBrick(2, seed: 8);
        var densities = RandomDensities(voxels, seed: 8);

        var state = BrickCodec.Encode(memory, voxels, densities, false, out var outOfMemory);

        Assert.True(outOfMemory);
        Assert.Equal(ChildState.Empty, state);
        Assert.Equal(0, memory.UsedBytes);
    }

    [Fact]
    public void FillDensities_writes_the_defaults_of_uniform_states()
    {
        var densities = new byte[512];

        BrickCodec.FillDensities(_memory, ChildState.Uniform(5), densities);
        Assert.All(densities, d => Assert.Equal(VoxelDensity.Full, d));
        BrickCodec.FillDensities(_memory, ChildState.Empty, densities);
        Assert.All(densities, d => Assert.Equal(VoxelDensity.Empty, d));
    }

    /// <summary>A random density for each voxel within its material's half (<see cref="VoxelDensity.Fit"/>).</summary>
    internal static byte[] RandomDensities(ReadOnlySpan<byte> voxels, int seed)
    {
        var random = new Random(seed);
        var densities = new byte[voxels.Length];
        for (var i = 0; i < voxels.Length; i++)
        {
            densities[i] = VoxelDensity.Fit(voxels[i], (byte)random.Next(256));
        }

        return densities;
    }

    /// <summary>Half air, half a random choice of <paramref name="materials"/> materials, each used at least once.</summary>
    internal static byte[] RandomBrick(int materials, int seed)
    {
        var random = new Random(seed);
        var voxels = new byte[512];
        for (var i = 0; i < voxels.Length; i++)
        {
            voxels[i] =
                i < materials ? (byte)(i + 1)
                : random.Next(2) == 0 ? (byte)0
                : (byte)random.Next(1, materials + 1);
        }

        random.Shuffle(voxels);
        return voxels;
    }
}
