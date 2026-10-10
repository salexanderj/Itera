using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed class VoxelCoordinateTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(127, 0)]
    [InlineData(128, 1)]
    [InlineData(-1, -1)]
    [InlineData(-128, -1)]
    [InlineData(-129, -2)]
    public void Chunk_coordinates_round_down_for_negative_voxels(int voxel, int chunk)
    {
        var key = ChunkKey.Containing(new VoxelCoord(voxel, voxel, voxel), 0);

        Assert.Equal(new ChunkKey(0, chunk, chunk, chunk), key);
    }

    [Fact]
    public void A_chunk_contains_its_own_min_voxel_at_every_level()
    {
        for (var level = 0; level <= VoxelConstants.MaxLevel; level++)
        {
            var key = new ChunkKey(level, -3, 2, -1);

            Assert.Equal(key, ChunkKey.Containing(key.MinVoxel, level));
            var last = new VoxelCoord(
                key.MinVoxel.X + key.EdgeInLevel0Voxels - 1,
                key.MinVoxel.Y + key.EdgeInLevel0Voxels - 1,
                key.MinVoxel.Z + key.EdgeInLevel0Voxels - 1
            );
            Assert.Equal(key, ChunkKey.Containing(last, level));
        }
    }

    [Fact]
    public void Level_two_chunks_cover_four_level_zero_chunks_per_edge()
    {
        var key = ChunkKey.Containing(new VoxelCoord(-1, 0, 511), 2);

        Assert.Equal(new ChunkKey(2, -1, 0, 0), key);
        Assert.Equal(512, key.EdgeInLevel0Voxels);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Column_layers_span_the_world_height(int baseCentimetres)
    {
        var grid = new VoxelGrid(baseCentimetres);
        var column = new ColumnKey(0, 5, -7);

        Assert.Equal(grid.MaxLayers, column.LayerCount(grid));
        Assert.Equal(grid.MinChunkY, column.Chunk(0, grid).Y);
        Assert.Equal(grid.MaxChunkY, column.Chunk(column.LayerCount(grid) - 1, grid).Y);
        Assert.Equal(3, column.Chunk(3, grid).Layer(grid));
        Assert.Equal(grid.MinVoxelY, column.Chunk(0, grid).MinVoxel.Y);
    }

    /// <summary>
    /// Whole level-0 chunks round the map: at 1 cm ±500.48 m, a floor at -40.96 m, and a top at 81.92 m; at 10 cm ±512 m,
    /// -51.2 m, and 102.4 m.
    /// </summary>
    [Theory]
    [InlineData(1, -391, 390, -32, 63)]
    [InlineData(10, -40, 39, -4, 7)]
    public void The_bounds_are_whole_chunks_round_the_map(int baseCentimetres, int minXZ, int maxXZ, int minY, int maxY)
    {
        var grid = new VoxelGrid(baseCentimetres);

        Assert.Equal((minXZ, maxXZ, minY, maxY), (grid.MinChunkXZ, grid.MaxChunkXZ, grid.MinChunkY, grid.MaxChunkY));
        Assert.True(grid.ToCentimetres(grid.MinVoxelXZ) <= -500 * VoxelConstants.CentimetresPerMetre);
        Assert.True(grid.ToCentimetres(grid.EndVoxelXZ) >= 500 * VoxelConstants.CentimetresPerMetre);
        Assert.True(grid.ToCentimetres(grid.MinVoxelY) <= VoxelGrid.DefaultFloorCentimetres);
        Assert.True(grid.ToCentimetres(grid.EndVoxelY) >= VoxelGrid.DefaultTopCentimetres);
        Assert.True(new ColumnKey(0, grid.MaxChunkXZ, grid.MinChunkXZ).IsInBounds(grid));
        Assert.False(new ColumnKey(0, grid.MaxChunkXZ + 1, 0).IsInBounds(grid));
        Assert.False(new ChunkKey(0, 0, grid.MaxChunkY + 1, 0).IsInBounds(grid));
        Assert.False(new ColumnKey(VoxelConstants.MaxLevel + 1, 0, 0).IsInBounds(grid));
    }

    [Theory]
    [InlineData(1, new[] { 96, 48, 24, 12, 6 })]
    [InlineData(10, new[] { 12, 6, 3, 2, 2 })]
    public void Coarser_columns_have_fewer_layers(int baseCentimetres, int[] layers)
    {
        var grid = new VoxelGrid(baseCentimetres);

        for (var level = 0; level < layers.Length; level++)
        {
            Assert.Equal(layers[level], grid.LayerCount(level));
        }
    }

    /// <summary>
    /// The levels whose chunks divide the floor's and the top's chunk counts share them: at 1 cm levels 0 to 5 the floor
    /// and 0 to 6 the top; at 10 cm 0 to 2 the floor and 0 to 3 the top. A coarser level reaches deeper.
    /// </summary>
    [Theory]
    [InlineData(1, 5, 6)]
    [InlineData(10, 2, 3)]
    public void Levels_share_the_floor_and_the_top_while_their_chunks_divide_them(
        int baseCentimetres,
        int floorLevels,
        int topLevels
    )
    {
        var grid = new VoxelGrid(baseCentimetres);
        for (var level = 0; level <= VoxelConstants.MaxLevel; level++)
        {
            var column = new ColumnKey(level, 0, 0);
            var bottom = column.Chunk(0, grid).MinVoxel.Y;
            var top = column.Chunk(column.LayerCount(grid) - 1, grid);
            var end = top.MinVoxel.Y + top.EdgeInLevel0Voxels;
            Assert.Equal(level <= floorLevels, bottom == grid.MinVoxelY);
            Assert.True(bottom <= grid.MinVoxelY);
            Assert.Equal(level <= topLevels, end == grid.EndVoxelY);
            Assert.True(end >= grid.EndVoxelY);
        }
    }

    [Fact]
    public void Metres_convert_to_the_containing_voxel()
    {
        Assert.Equal(new VoxelCoord(150, -1, -250), VoxelCoord.FromMetres(new Vector3(1.505f, -0.004f, -2.5f)));
        Assert.Equal(new Vector3(1.5f, 0f, -2.5f), new VoxelCoord(150, 0, -250).ToMetres());
    }

    [Theory]
    [InlineData(5, 0, 5)]
    [InlineData(5, 1, 11)]
    [InlineData(-1, 2, -2)]
    [InlineData(3, 3, 28)]
    public void Level_voxels_are_sampled_at_their_centre(int coordinate, int level, int expected)
    {
        Assert.Equal(expected, VoxelConstants.SampleAt(coordinate, level));
    }
}
