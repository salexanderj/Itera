using System.Numerics;
using System.Runtime.Intrinsics;

namespace Itera.Engine.Voxels;

/// <summary>
/// Converts between a brick's 512 materials (index <c>x + 8y + 64z</c>), with their densities
/// (<see cref="VoxelDensity"/>), and its stored form: a 64-byte occupancy mask plus a <see cref="BrickRecord"/>, an index
/// block when the brick holds more than one solid material, and a density block when some voxel's density is not its
/// material's default. A brick that is all air or all one material with every density its default is never stored;
/// encoding returns a uniform or empty state instead. A brick of one material whose densities differ is stored, with an
/// empty mask when it is all air. Palettes list materials in ascending order, so the same voxels always give the same
/// bytes. Densities are fitted to their materials when stored (<see cref="VoxelDensity.Fit"/>).
/// </summary>
internal static unsafe class BrickCodec
{
    /// <summary>
    /// Stores <paramref name="voxels"/> (512 materials), every voxel with its material's default density, and returns the
    /// state that represents them. When memory runs out, nothing is kept, <paramref name="outOfMemory"/> is true, and the
    /// result is empty.
    /// </summary>
    public static ChildState Encode(
        VoxelMemory memory,
        ReadOnlySpan<byte> voxels,
        bool mayExceedBudget,
        out bool outOfMemory
    ) => Encode(memory, voxels, default, mayExceedBudget, out outOfMemory);

    /// <summary>
    /// Stores <paramref name="voxels"/> (512 materials) with <paramref name="densities"/> (512, or empty for every
    /// voxel's default) and returns the state that represents them. When memory runs out, nothing is kept,
    /// <paramref name="outOfMemory"/> is true, and the result is empty.
    /// </summary>
    public static ChildState Encode(
        VoxelMemory memory,
        ReadOnlySpan<byte> voxels,
        ReadOnlySpan<byte> densities,
        bool mayExceedBudget,
        out bool outOfMemory
    )
    {
        outOfMemory = false;
        var first = voxels[0];
        var hasDensity = !densities.IsEmpty && !AllDefault(voxels, densities);
        if (!hasDensity && voxels[..VoxelConstants.BrickVoxels].IndexOfAnyExcept(first) < 0)
        {
            return ChildState.Uniform(first);
        }

        Span<ulong> mask = stackalloc ulong[VoxelConstants.BrickMaskWords];
        Span<ulong> present = stackalloc ulong[4];
        mask.Clear();
        present.Clear();
        for (var i = 0; i < VoxelConstants.BrickVoxels; i++)
        {
            var material = voxels[i];
            if (material != VoxelConstants.Air)
            {
                mask[i >> 6] |= 1UL << (i & 63);
                present[material >> 6] |= 1UL << (material & 63);
            }
        }

        var count =
            BitOperations.PopCount(present[0])
            + BitOperations.PopCount(present[1])
            + BitOperations.PopCount(present[2])
            + BitOperations.PopCount(present[3]);
        var bits = IndexBits(count);

        if (!memory.TryAllocate(VoxelPool.Bricks, mayExceedBudget, out var brick))
        {
            outOfMemory = true;
            return ChildState.Empty;
        }

        var block = -1;
        if (bits > 0 && !memory.TryAllocate(IndexPool(bits), mayExceedBudget, out block))
        {
            memory.Free(VoxelPool.Bricks, brick);
            outOfMemory = true;
            return ChildState.Empty;
        }

        var densityBlock = -1;
        if (hasDensity && !memory.TryAllocate(VoxelPool.Density, mayExceedBudget, out densityBlock))
        {
            if (bits > 0)
            {
                memory.Free(IndexPool(bits), block);
            }

            memory.Free(VoxelPool.Bricks, brick);
            outOfMemory = true;
            return ChildState.Empty;
        }

        mask.CopyTo(new Span<ulong>(memory.BrickMask(brick), VoxelConstants.BrickMaskWords));
        var record = memory.Brick(brick);
        record->Bits = (byte)bits;
        record->Reserved = 0;
        record->Block = block;
        record->Density = densityBlock;

        if (bits == 0)
        {
            record->MaterialOrCount = (byte)FirstMaterial(present);
        }
        else if (bits == 8)
        {
            record->MaterialOrCount = 0;
            voxels[..VoxelConstants.BrickVoxels]
                .CopyTo(new Span<byte>(memory.Pointer(VoxelPool.Index8, block), VoxelConstants.BrickVoxels));
        }
        else
        {
            record->MaterialOrCount = (byte)count;
            WritePalette(memory.Pointer(IndexPool(bits), block), voxels, present, bits);
        }

        if (hasDensity)
        {
            var stored = memory.Densities(densityBlock);
            for (var i = 0; i < VoxelConstants.BrickVoxels; i++)
            {
                stored[i] = VoxelDensity.Fit(voxels[i], densities[i]);
            }
        }

        return ChildState.Of(brick);
    }

    /// <summary>
    /// True when every voxel's density, fitted to its material, is that material's default: exactly when it is the
    /// default already (only 0 fits to an air voxel's 0, and only 255 to a solid one's 255), so no fitting is needed.
    /// </summary>
    private static bool AllDefault(ReadOnlySpan<byte> voxels, ReadOnlySpan<byte> densities)
    {
        var i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i < VoxelConstants.BrickVoxels; i += Vector256<byte>.Count)
            {
                var air = Vector256.Equals(
                    Vector256.Create(voxels.Slice(i, Vector256<byte>.Count)),
                    Vector256<byte>.Zero
                );
                if (Vector256.Create(densities.Slice(i, Vector256<byte>.Count)) != ~air)
                {
                    return false;
                }
            }

            return true;
        }

        for (; i < VoxelConstants.BrickVoxels; i++)
        {
            if (densities[i] != VoxelDensity.Default(voxels[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes a stored brick's 512 materials into <paramref name="voxels"/>.</summary>
    public static void Decode(VoxelMemory memory, int brick, Span<byte> voxels)
    {
        var record = memory.Brick(brick);
        var bits = record->Bits;
        if (bits == 8)
        {
            new ReadOnlySpan<byte>(memory.Pointer(VoxelPool.Index8, record->Block), VoxelConstants.BrickVoxels).CopyTo(
                voxels
            );
            return;
        }

        voxels[..VoxelConstants.BrickVoxels].Clear();
        var mask = memory.BrickMask(brick);
        if (bits == 0)
        {
            var material = record->MaterialOrCount;
            for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
            {
                for (var word = mask[w]; word != 0; word &= word - 1)
                {
                    voxels[(w << 6) + BitOperations.TrailingZeroCount(word)] = material;
                }
            }

            return;
        }

        var block = memory.Pointer(IndexPool(bits), record->Block);
        var indices = block + VoxelMemory.PaletteBytes;
        var valueMask = (1 << bits) - 1;
        for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
        {
            for (var word = mask[w]; word != 0; word &= word - 1)
            {
                var i = (w << 6) + BitOperations.TrailingZeroCount(word);
                var position = i * bits;
                voxels[i] = block[(indices[position >> 3] >> (position & 7)) & valueMask];
            }
        }
    }

    /// <summary>The material of one voxel of a stored brick.</summary>
    public static byte Read(VoxelMemory memory, int brick, int index)
    {
        if (((memory.BrickMask(brick)[index >> 6] >> (index & 63)) & 1) == 0)
        {
            return VoxelConstants.Air;
        }

        var record = memory.Brick(brick);
        var bits = record->Bits;
        if (bits == 0)
        {
            return record->MaterialOrCount;
        }

        if (bits == 8)
        {
            return memory.Pointer(VoxelPool.Index8, record->Block)[index];
        }

        var block = memory.Pointer(IndexPool(bits), record->Block);
        var position = index * bits;
        var paletteIndex = (block[VoxelMemory.PaletteBytes + (position >> 3)] >> (position & 7)) & ((1 << bits) - 1);
        return block[paletteIndex];
    }

    /// <summary>Writes a stored brick's 512 densities into <paramref name="densities"/>.</summary>
    public static void DecodeDensities(VoxelMemory memory, int brick, Span<byte> densities)
    {
        var block = memory.Brick(brick)->Density;
        if (block >= 0)
        {
            new ReadOnlySpan<byte>(memory.Densities(block), VoxelConstants.BrickVoxels).CopyTo(densities);
            return;
        }

        // Every voxel has its default: full where the mask has it, empty elsewhere.
        var mask = memory.BrickMask(brick);
        for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
        {
            var word = mask[w];
            var row = densities.Slice(w << 6, 64);
            if (word == 0)
            {
                row.Clear();
            }
            else if (word == ulong.MaxValue)
            {
                row.Fill(VoxelDensity.Full);
            }
            else
            {
                for (var bit = 0; bit < 64; bit++)
                {
                    row[bit] = ((word >> bit) & 1) != 0 ? VoxelDensity.Full : VoxelDensity.Empty;
                }
            }
        }
    }

    /// <summary>The density of one voxel of a stored brick.</summary>
    public static byte ReadDensity(VoxelMemory memory, int brick, int index)
    {
        var block = memory.Brick(brick)->Density;
        if (block >= 0)
        {
            return memory.Densities(block)[index];
        }

        return ((memory.BrickMask(brick)[index >> 6] >> (index & 63)) & 1) != 0
            ? VoxelDensity.Full
            : VoxelDensity.Empty;
    }

    /// <summary>True when a stored brick keeps densities of its own, not only its materials' defaults.</summary>
    public static bool HasDensities(VoxelMemory memory, int brick) => memory.Brick(brick)->Density >= 0;

    /// <summary>Writes the 512 materials a state represents: air, one material, or a stored brick.</summary>
    public static void Fill(VoxelMemory memory, ChildState state, Span<byte> voxels)
    {
        if (state.Kind == ChildKind.Handle)
        {
            Decode(memory, state.Handle, voxels);
        }
        else
        {
            voxels[..VoxelConstants.BrickVoxels].Fill(state.Material);
        }
    }

    /// <summary>Writes the 512 densities a state represents: an empty or uniform brick's default, or a stored brick's.</summary>
    public static void FillDensities(VoxelMemory memory, ChildState state, Span<byte> densities)
    {
        if (state.Kind == ChildKind.Handle)
        {
            DecodeDensities(memory, state.Handle, densities);
        }
        else
        {
            densities[..VoxelConstants.BrickVoxels].Fill(VoxelDensity.Default(state.Material));
        }
    }

    /// <summary>
    /// A copy of a stored brick, byte for byte: its mask, its record, its index block, and its density block. Any thread
    /// that owns the brick; it may pass the budget.
    /// </summary>
    public static ChildState Copy(VoxelMemory memory, int brick)
    {
        var copy = memory.Allocate(VoxelPool.Bricks);
        new ReadOnlySpan<ulong>(memory.BrickMask(brick), VoxelConstants.BrickMaskWords).CopyTo(
            new Span<ulong>(memory.BrickMask(copy), VoxelConstants.BrickMaskWords)
        );
        var source = *memory.Brick(brick);
        *memory.Brick(copy) = source;
        if (source.Bits > 0)
        {
            var pool = IndexPool(source.Bits);
            var block = memory.Allocate(pool);
            var bytes = memory.BlockSize(pool);
            new ReadOnlySpan<byte>(memory.Pointer(pool, source.Block), bytes).CopyTo(
                new Span<byte>(memory.Pointer(pool, block), bytes)
            );
            memory.Brick(copy)->Block = block;
        }

        if (source.Density >= 0)
        {
            var density = memory.Allocate(VoxelPool.Density);
            new ReadOnlySpan<byte>(memory.Densities(source.Density), VoxelConstants.BrickVoxels).CopyTo(
                new Span<byte>(memory.Densities(density), VoxelConstants.BrickVoxels)
            );
            memory.Brick(copy)->Density = density;
        }

        return ChildState.Of(copy);
    }

    /// <summary>Returns a stored brick's memory.</summary>
    public static void Free(VoxelMemory memory, int brick)
    {
        var record = memory.Brick(brick);
        if (record->Bits > 0)
        {
            memory.Free(IndexPool(record->Bits), record->Block);
        }

        if (record->Density >= 0)
        {
            memory.Free(VoxelPool.Density, record->Density);
        }

        memory.Free(VoxelPool.Bricks, brick);
    }

    /// <summary>Index width for a brick with <paramref name="materials"/> distinct solid materials.</summary>
    public static int IndexBits(int materials) =>
        materials switch
        {
            <= 1 => 0,
            2 => 1,
            <= 4 => 2,
            <= 16 => 4,
            _ => 8,
        };

    /// <summary>The pool that holds index blocks of <paramref name="bits"/> bits per voxel.</summary>
    public static VoxelPool IndexPool(int bits) =>
        bits switch
        {
            1 => VoxelPool.Index1,
            2 => VoxelPool.Index2,
            4 => VoxelPool.Index4,
            8 => VoxelPool.Index8,
            _ => throw new ArgumentOutOfRangeException(nameof(bits), bits, "Index width must be 1, 2, 4, or 8."),
        };

    private static int FirstMaterial(ReadOnlySpan<ulong> present)
    {
        for (var w = 0; w < present.Length; w++)
        {
            if (present[w] != 0)
            {
                return (w << 6) + BitOperations.TrailingZeroCount(present[w]);
            }
        }

        return VoxelConstants.Air;
    }

    private static void WritePalette(byte* block, ReadOnlySpan<byte> voxels, ReadOnlySpan<ulong> present, int bits)
    {
        Span<byte> lookup = stackalloc byte[256];
        var count = 0;
        for (var w = 0; w < present.Length; w++)
        {
            for (var word = present[w]; word != 0; word &= word - 1)
            {
                var material = (w << 6) + BitOperations.TrailingZeroCount(word);
                block[count] = (byte)material;
                lookup[material] = (byte)count;
                count++;
            }
        }

        new Span<byte>(block + count, VoxelMemory.PaletteBytes - count).Clear();
        var indices = block + VoxelMemory.PaletteBytes;
        new Span<byte>(indices, VoxelConstants.BrickVoxels * bits / 8).Clear();
        for (var i = 0; i < VoxelConstants.BrickVoxels; i++)
        {
            var material = voxels[i];
            if (material != VoxelConstants.Air)
            {
                var position = i * bits;
                indices[position >> 3] |= (byte)(lookup[material] << (position & 7));
            }
        }
    }
}
