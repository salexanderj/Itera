using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// <see cref="VoxelWorld.BlockingMask"/>, which compares a word of packed palette indices at a time: over random bricks
/// of 1 to 20 materials (indices of 1, 2, 4, and 8 bits), some passable, it gives what reading each voxel gives.
/// </summary>
public sealed class BlockingMaskTests : IDisposable
{
    private readonly VoxelMemory _memory = new(64 << 20);

    public void Dispose() => _memory.Dispose();

    [Fact]
    public void Every_brick_s_mask_matches_a_read_of_each_voxel()
    {
        var random = new Random(11);
        var voxels = new byte[VoxelConstants.BrickVoxels];
        var chosen = new byte[20];
        Span<byte> passing = stackalloc byte[20];
        Span<ulong> mask = stackalloc ulong[VoxelConstants.BrickMaskWords];
        var widths = new int[9];
        for (var brick = 0; brick < 4000; brick++)
        {
            var materials = random.Next(1, 21);
            for (var i = 0; i < materials; i++)
            {
                chosen[i] = (byte)random.Next(1, 256);
            }

            var air = random.NextDouble() * 0.6;
            for (var i = 0; i < voxels.Length; i++)
            {
                voxels[i] = random.NextDouble() < air ? VoxelConstants.Air : chosen[random.Next(materials)];
            }

            var passingCount = 0;
            for (var i = 0; i < materials; i++)
            {
                if (random.Next(2) == 0)
                {
                    passing[passingCount++] = chosen[i];
                }
            }

            var passable = MaterialSet.Of(passing[..passingCount]);
            var state = BrickCodec.Encode(_memory, voxels, false, out _);
            if (state.Kind != ChildKind.Handle)
            {
                continue;
            }

            widths[BrickRecordBits(state.Handle)]++;
            mask.Clear();
            var any = VoxelWorld.BlockingMask(_memory, state.Handle, in passable, mask);

            var expectedAny = false;
            for (var i = 0; i < voxels.Length; i++)
            {
                var blocks = voxels[i] != VoxelConstants.Air && !passable.Contains(voxels[i]);
                expectedAny |= blocks;
                if (any && blocks != ((mask[i >> 6] >> (i & 63) & 1) != 0))
                {
                    Assert.Fail($"Brick {brick} ({materials} materials): voxel {i} of {voxels[i]} is wrong.");
                }
            }

            Assert.Equal(expectedAny, any);
            BrickCodec.Free(_memory, state.Handle);
        }

        // Every width was tried.
        Assert.All([1, 2, 4, 8], bits => Assert.True(widths[bits] > 100, $"Few bricks of {bits}-bit indices."));
    }

    private unsafe int BrickRecordBits(int brick) => _memory.Brick(brick)->Bits;
}
