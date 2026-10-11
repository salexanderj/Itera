using System.Numerics;
using Itera.Engine.Tests.Voxels.Generation;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// Columns that store a window of their layers, at its edges: a column whose generator stores no layer (flat ground on a
/// chunk's face) keeps the air between its ground and an edit far over it, and the stone between its ground and a dig far
/// under it, whether the edit came while it was loaded or before; a heightfield whose ground lies over the column's top
/// with a cave in it describes every layer between the cave and the top; and a column's layers that can hold a surface
/// count what the column past its +x side holds, since its blocks own the faces between them.
/// </summary>
public sealed class SparseColumnTests
{
    private static readonly VoxelGrid s_grid = new(10);

    /// <summary>The layer of the chunk of 10 cm voxels holding height <paramref name="centimetres"/>.</summary>
    private static int LayerOf(int centimetres) =>
        (s_grid.ToVoxel(centimetres) >> VoxelConstants.ChunkShift) - s_grid.MinChunkY;

    [Fact]
    public void An_edit_over_or_under_a_column_with_no_stored_layer_keeps_the_fills_between()
    {
        using var world = TestWorlds.Load(new BoundaryGenerator(s_grid), Vector3.Zero, radiusMetres: 13);
        var key = new ColumnKey(0, 0, 0);
        Assert.True(world.TryGetStoredLayers(key, out var first, out var end));
        Assert.Equal((LayerOf(0), LayerOf(0)), (first, end));

        world.Apply(
            VoxelEdit.Sphere(new VoxelCoord(64, 4_000, 64), 50, VoxelEditOperation.Set, BoundaryGenerator.Stone)
        );
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(640, -4_000, 640), 50, VoxelEditOperation.Clear));

        AssertFills(world, 64, 640);
    }

    [Fact]
    public void An_edit_logged_before_such_a_column_loads_keeps_the_fills_between()
    {
        using var world = TestWorlds.Load(new BoundaryGenerator(s_grid), Vector3.Zero, radiusMetres: 13);
        // 100 m out, where no column is loaded: the edits wait in the log for the columns' jobs.
        world.Apply(
            VoxelEdit.Sphere(new VoxelCoord(10_064, 4_000, 64), 50, VoxelEditOperation.Set, BoundaryGenerator.Stone)
        );
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(10_640, -4_000, 640), 50, VoxelEditOperation.Clear));

        world.SetCenters([new Vector3(103, 0, 3)]);
        TestWorlds.WaitUntilLoaded(world);

        AssertFills(world, 10_064, 10_640);
    }

    /// <summary>
    /// Ground at 200 m, over the column's top (102.4 m), and a cave at the middle's height: the column is the deep
    /// material but for the cave, and its range stands for every layer, those between the cave and the top included.
    /// </summary>
    [Fact]
    public void A_cave_under_ground_over_the_columns_top_leaves_every_layer_described()
    {
        IChunkGenerator generator = new BuriedCaveGenerator(s_grid);
        for (var level = 0; level <= 2; level++)
        {
            var column = new ColumnKey(level, 0, 0);
            var fills = new ChunkFill[column.LayerCount(s_grid)];
            generator.DescribeColumn(column, 1, fills);
            generator.DescribeRange(column, 1, out var first, out var last, out var below, out var above);
            var window = new ChunkFill[Math.Max(last - first + 1, 0)];
            if (window.Length > 0)
            {
                generator.DescribeLayers(column, 1, first, window);
            }

            Assert.Contains(ChunkFillKind.Mixed, fills.Select(fill => fill.Kind));
            for (var layer = 0; layer < fills.Length; layer++)
            {
                var stands =
                    layer < first ? below
                    : layer > last ? above
                    : window[layer - first];
                Assert.True(
                    fills[layer] == stands,
                    $"level {level}, layer {layer} of {first} to {last}: {fills[layer]}, {stands}"
                );
            }
        }
    }

    /// <summary>
    /// A stone box set flush against the first voxels of the column past the middle one's +x side, 40 m up, reaches only
    /// that column; the middle column's layers that can hold a surface hold its layer, since the middle column's blocks
    /// own the faces between its voxels and the box's.
    /// </summary>
    [Fact]
    public void A_box_flush_with_the_next_column_gives_this_one_its_layer()
    {
        using var world = TestWorlds.Load(new BoundaryGenerator(s_grid), Vector3.Zero, radiusMetres: 13);
        var runs = new List<(int First, int End)>();
        var layer = LayerOf(4_000);

        world.Apply(
            VoxelEdit.Box(
                new VoxelCoord(1_280, 4_000, 0),
                new VoxelCoord(100, 100, 100),
                VoxelEditOperation.Set,
                BoundaryGenerator.Stone
            )
        );

        Assert.True(world.TryGetStoredLayers(new ColumnKey(0, 0, 0), out var first, out var end));
        Assert.True(layer >= end, $"The middle column stores layers {first} to {end}, the box's {layer} among them.");
        Assert.True(world.CollectSurfaceLayers(new ColumnKey(0, 0, 0), runs));
        Assert.Contains(runs, run => layer >= run.First && layer < run.End);
    }

    /// <summary>Air between the ground and the ball over it, stone between the ground and the dig under it.</summary>
    private static void AssertFills(VoxelWorld world, int ballX, int digX)
    {
        Assert.True(world.TryGetMaterial(new VoxelCoord(ballX, 4_000, 64), out var ball));
        Assert.True(world.TryGetMaterial(new VoxelCoord(ballX, 2_000, 64), out var between));
        Assert.True(world.TryGetMaterial(new VoxelCoord(ballX, 1_400, 64), out var justOver));
        Assert.True(world.TryGetMaterial(new VoxelCoord(digX, -4_000, 640), out var dig));
        Assert.True(world.TryGetMaterial(new VoxelCoord(digX, -2_000, 640), out var buried));
        Assert.Equal(
            (
                BoundaryGenerator.Stone,
                VoxelConstants.Air,
                VoxelConstants.Air,
                VoxelConstants.Air,
                BoundaryGenerator.Stone
            ),
            (ball, between, justOver, dig, buried)
        );
    }

    /// <summary>Ground at 200 m (over a default 10 cm grid's top), and one cave 3 m across at height 0 under the middle.</summary>
    private sealed class BuriedCaveGenerator(VoxelGrid grid)
        : HeightfieldGenerator(grid, new GroundLayers(5, [new GroundLayer(100, 2)], HillsGenerator.Stone))
    {
        public override string Id => "test.buried-cave";

        public override int Version => 1;

        protected override double HeightAt(int x, int z, ulong seed) => 20_000;

        protected override byte SurfaceAt(int x, int z, ulong seed, double height) => HillsGenerator.Grass;

        protected override GroundFeatures CreateFeatures() => new Cave();

        private sealed class Cave : GroundFeatures
        {
            private static readonly HillsGenerator.Ball s_cave = new(0, 0, 0, 300, VoxelConstants.Air);
            private readonly GroundFeatureList<HillsGenerator.Ball> _caves = new(1);

            public override void Find(ulong seed, GridLevel level, int minX, int maxX, int minZ, int maxZ)
            {
                _caves.Clear();
                if (s_cave.Touches(minX, maxX, int.MinValue, int.MaxValue, minZ, maxZ))
                {
                    _caves.Add(s_cave);
                }
            }

            public override bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
                _caves.Touches(minX, maxX, minY, maxY, minZ, maxZ);

            public override void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels) =>
                _caves.Fill(first, level, voxels);
        }
    }
}
