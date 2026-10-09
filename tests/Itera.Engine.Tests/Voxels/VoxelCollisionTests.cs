using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// The collision queries (<c>VoxelWorld.Collision.cs</c>) against a voxel-by-voxel walk with
/// <see cref="VoxelWorld.TryGetMaterial(VoxelCoord, out byte)"/>, over boxes that hold uniform cells, bricks of one
/// material, bricks where foliage meets stone, and bricks of more than 16 materials.
/// </summary>
public sealed class VoxelCollisionTests : IDisposable
{
    private const int Floor = 200;

    private static readonly MaterialSet s_leavesPass = MaterialSet.Of(BoxesGenerator.Leaves);

    private static readonly BoxesGenerator s_generator = new(
        Floor,
        // A stone wall: whole cells and bricks of one material.
        new VoxelBox(100, 200, -50, 140, 299, 50, BoxesGenerator.Stone),
        // A leafy block, which characters pass through.
        new VoxelBox(-100, 200, -30, -40, 260, 30, BoxesGenerator.Leaves),
        // Leaves against stone inside the same bricks (x -16 to -9).
        new VoxelBox(-30, 200, 60, -13, 240, 80, BoxesGenerator.Leaves),
        new VoxelBox(-12, 200, 60, 0, 240, 80, BoxesGenerator.Stone),
        // Bricks of more than 16 materials, some of them leaves.
        new VoxelBox(40, 204, 40, 71, 235, 71, BoxesGenerator.Pattern),
        // A floating slab, an overhang to stand under.
        new VoxelBox(-60, 380, -100, -20, 399, -60, BoxesGenerator.Stone),
        // A pit dug into the floor, with a mushroom in it.
        new VoxelBox(60, 150, -120, 90, 199, -90, VoxelConstants.Air),
        new VoxelBox(70, 150, -110, 74, 185, -106, BoxesGenerator.Mushroom)
    );

    private readonly VoxelWorld _world;

    public VoxelCollisionTests()
    {
        _world = TestWorlds.Load(s_generator, Vector3.Zero);
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Blocked_cells_match_a_voxel_by_voxel_walk() => CheckBlockedCells(_world, 60, 140, 400);

    [Fact]
    public void Highest_top_and_lowest_bottom_match_a_voxel_by_voxel_walk() => CheckFootprints(_world, 150, 140, 400);

    [Theory]
    [InlineData("trunk")]
    [InlineData("slope")]
    public void Queries_match_on_a_round_trunk_and_a_slope(string name)
    {
        IChunkGenerator generator = name == "trunk" ? new TrunkGenerator() : new SlopeGenerator();
        using var world = TestWorlds.Load(generator, new Vector3(0.64f, 2, 0.64f));

        CheckBlockedCells(world, 20, 150, 300);
        CheckFootprints(world, 60, 150, 300);
    }

    /// <summary>
    /// The walk at levels 1 and 2 (the coarse colliders' and a wide body's) hands over exactly the voxels of that
    /// level that block, as a voxel-by-voxel walk of the level finds them (only level 0 was checked).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_walk_at_a_coarse_level_matches_a_voxel_by_voxel_walk(int level)
    {
        using var world = TestWorlds.Load(s_generator, Vector3.Zero, radiusMetres: 3, levels: 3);
        var random = new Random(23 + level);
        for (var i = 0; i < 40; i++)
        {
            // A window of the level's voxels round the base's features, within its resident columns.
            var size = new VoxelCoord(random.Next(1, 24), random.Next(1, 24), random.Next(1, 24));
            var x0 = random.Next(-160 >> level, (160 >> level) - size.X);
            var y0 = random.Next(140 >> level, (400 >> level) - size.Y);
            var z0 = random.Next(-140 >> level, (140 >> level) - size.Z);
            var passable = i % 3 == 0 ? MaterialSet.None : s_leavesPass;
            var visitor = new MarkVisitor(x0, y0, z0, size);

            world.WalkBlocking(
                level,
                x0,
                y0,
                z0,
                x0 + size.X - 1,
                y0 + size.Y - 1,
                z0 + size.Z - 1,
                in passable,
                ref visitor
            );

            for (var z = 0; z < size.Z; z++)
            {
                for (var y = 0; y < size.Y; y++)
                {
                    for (var x = 0; x < size.X; x++)
                    {
                        var voxel = new VoxelCoord((x0 + x) << level, (y0 + y) << level, (z0 + z) << level);
                        Assert.True(world.TryGetMaterial(voxel, level, out var material));
                        var expected = material != VoxelConstants.Air && !passable.Contains(material);
                        Assert.True(
                            expected == visitor.Blocked(x, y, z),
                            $"Level {level}, window {i}: voxel ({x0 + x}, {y0 + y}, {z0 + z}) holds {material}."
                        );
                    }
                }
            }
        }
    }

    /// <summary>Marks the window's voxels the walk hands over.</summary>
    private struct MarkVisitor(int x0, int y0, int z0, VoxelCoord size) : IBlockingVisitor
    {
        private readonly bool[] _blocked = new bool[size.X * size.Y * size.Z];

        public readonly bool Finished => false;

        public readonly bool Blocked(int x, int y, int z) => _blocked[x + size.X * (y + size.Y * z)];

        public readonly void Box(int bx0, int by0, int bz0, int bx1, int by1, int bz1)
        {
            for (var z = bz0; z <= bz1; z++)
            {
                for (var y = by0; y <= by1; y++)
                {
                    for (var x = bx0; x <= bx1; x++)
                    {
                        Mark(x, y, z);
                    }
                }
            }
        }

        public readonly void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask)
        {
            for (var w = 0; w < VoxelConstants.BrickMaskWords; w++)
            {
                for (var bit = 0; bit < 64; bit++)
                {
                    if (((mask[w] >> bit) & 1) != 0)
                    {
                        Mark(x + (bit & 7), y + (bit >> 3), z + w);
                    }
                }
            }
        }

        private readonly void Mark(int x, int y, int z)
        {
            var (lx, ly, lz) = (x - x0, y - y0, z - z0);
            Assert.True(
                (uint)lx < (uint)size.X && (uint)ly < (uint)size.Y && (uint)lz < (uint)size.Z,
                $"The walk handed over ({x}, {y}, {z}), outside the window."
            );
            _blocked[lx + size.X * (ly + size.Y * lz)] = true;
        }
    }

    private static void CheckBlockedCells(VoxelWorld world, int count, int yLow, int yHigh)
    {
        var random = new Random(5);
        Span<UInt128> rows = stackalloc UInt128[48];
        for (var i = 0; i < count; i++)
        {
            var width = random.Next(1, 129);
            var depth = random.Next(1, 49);
            var x = random.Next(-160, 160 - width);
            var z = random.Next(-140, 140 - depth);
            var y0 = random.Next(yLow, yHigh);
            var y1 = y0 + random.Next(0, 40);
            var passable = i % 3 == 0 ? MaterialSet.None : s_leavesPass;

            world.BlockedCells(x, z, width, y0, y1, in passable, rows[..depth]);

            for (var row = 0; row < depth; row++)
            {
                for (var cell = 0; cell < width; cell++)
                {
                    var expected = BlocksAnywhere(world, x + cell, z + row, y0, y1, in passable);
                    var actual = ((rows[row] >> cell) & UInt128.One) != UInt128.Zero;
                    Assert.True(
                        expected == actual,
                        $"Query {i}: cell ({x + cell}, {z + row}) over y {y0} to {y1}: expected {expected}."
                    );
                }

                // Nothing is set past the window's width.
                if (width < 128)
                {
                    Assert.Equal(UInt128.Zero, rows[row] >> width);
                }
            }
        }
    }

    private static void CheckFootprints(VoxelWorld world, int count, int yLow, int yHigh)
    {
        var random = new Random(9);
        for (var i = 0; i < count; i++)
        {
            var footprint = new CircleFootprint(
                random.Next(-150, 150) + random.NextDouble(),
                random.Next(-150, 150) + random.NextDouble(),
                3 + random.NextDouble() * 40
            );
            var y0 = random.Next(yLow, yHigh);
            var y1 = y0 + random.Next(0, 80);
            var passable = i % 3 == 0 ? MaterialSet.None : s_leavesPass;

            var (top, bottom) = WalkFootprint(world, in footprint, y0, y1, in passable);

            Assert.Equal(top, world.HighestTop(in footprint, y0, y1, in passable));
            Assert.Equal(bottom, world.LowestBottom(in footprint, y0, y1, in passable));
        }
    }

    [Fact]
    public void Leaves_pass_and_stone_in_the_same_brick_still_blocks()
    {
        Span<UInt128> rows = stackalloc UInt128[1];

        // Row z = 70 from x = -20 to x = -5: leaves up to x = -13, stone from x = -12.
        _world.BlockedCells(-20, 70, 16, 210, 220, in s_leavesPass, rows);
        Assert.Equal(RangeBits(8, 15), rows[0]);

        _world.BlockedCells(-20, 70, 16, 210, 220, MaterialSet.None, rows);
        Assert.Equal(RangeBits(0, 15), rows[0]);
    }

    [Fact]
    public void A_footprint_leaves_out_cells_the_circle_only_touches()
    {
        // A circle of radius 2 centred on a cell corner covers the four cells around it and the ring next to them,
        // but not the cells whose nearest point is exactly 2 away.
        var footprint = new CircleFootprint(10, 20, 2);

        Assert.True(footprint.Row(20, out var x0, out var x1));
        Assert.Equal((8, 11), (x0, x1));
        Assert.True(footprint.Row(21, out x0, out x1));
        Assert.Equal((8, 11), (x0, x1));
        Assert.False(footprint.Row(22, out _, out _));
        Assert.False(footprint.Row(17, out _, out _));
    }

    [Fact]
    public void Columns_far_from_the_centre_are_not_resident()
    {
        Assert.True(_world.ColumnsResident(-100, -100, 100, 100));
        Assert.False(_world.ColumnsResident(-100, -100, 2000, 100));
        Assert.True(_world.ColumnsResident(5, 5, 4, 4));
    }

    [Fact]
    public void Queries_allocate_nothing()
    {
        var footprint = new CircleFootprint(45.5, 45.5, 26);
        Span<UInt128> rows = stackalloc UInt128[64];
        Run(rows, in footprint, 50);

        var before = SystemAllocation.StartMeasurement();
        Run(rows, in footprint, 500);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private void Run(Span<UInt128> rows, in CircleFootprint footprint, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _world.BlockedCells(10, 10, 100, 190, 380, in s_leavesPass, rows);
            _world.HighestTop(in footprint, 150, 380, in s_leavesPass);
            _world.LowestBottom(in footprint, 150, 380, in s_leavesPass);
            _world.ColumnsResident(-50, -50, 50, 50);
        }
    }

    private static bool BlocksAnywhere(VoxelWorld world, int x, int z, int y0, int y1, in MaterialSet passable)
    {
        for (var y = y0; y <= y1; y++)
        {
            if (Blocks(world, x, y, z, in passable))
            {
                return true;
            }
        }

        return false;
    }

    private static (int Top, int Bottom) WalkFootprint(
        VoxelWorld world,
        in CircleFootprint footprint,
        int y0,
        int y1,
        in MaterialSet passable
    )
    {
        int top = int.MinValue,
            bottom = int.MaxValue;
        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    if (Blocks(world, x, y, z, in passable))
                    {
                        top = Math.Max(top, y + 1);
                        bottom = Math.Min(bottom, y);
                    }
                }
            }
        }

        return (top, bottom);
    }

    private static bool Blocks(VoxelWorld world, int x, int y, int z, in MaterialSet passable)
    {
        Assert.True(world.TryGetMaterial(new VoxelCoord(x, y, z), out var material));
        return material != VoxelConstants.Air && !passable.Contains(material);
    }

    private static UInt128 RangeBits(int from, int to) =>
        ((UInt128.One << (to + 1)) - 1) & ~((UInt128.One << from) - 1);
}
