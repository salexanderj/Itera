using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The sky field's flood on small grids (open air, a dead-end tunnel, a sealed cave, the solid cells round them) and its
/// read of a test world's voxels.
/// </summary>
public sealed class SkyFieldTests
{
    private const int Size = 12;

    [Fact]
    public void Open_air_sees_all_the_sky()
    {
        var light = Flood(new byte[Size * Size * Size]);

        Assert.All(light, value => Assert.Equal(255, value));
    }

    /// <summary>
    /// A solid block from y 0 to 7 with a tunnel along x at y 3 from its open face at x 0: the open air above sees all
    /// the sky, and each cell into the tunnel keeps 218/256 of the one before.
    /// </summary>
    [Fact]
    public void A_tunnel_darkens_a_share_each_cell_in()
    {
        var solid = new byte[Size * Size * Size];
        for (var z = 0; z < Size; z++)
        {
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var tunnel = y == 3 && z == 6 && x < 9;
                    var face = x == 0;
                    solid[Index(x, y, z)] = (byte)(tunnel || face ? 0 : 1);
                }
            }
        }

        var light = Flood(solid);

        Assert.Equal(255, light[Index(5, 9, 6)]);
        // The face's air at x 0 is open to the sky (nothing above it), so the tunnel starts bright.
        Assert.Equal(255, light[Index(0, 3, 6)]);
        var expected = 255;
        for (var x = 1; x < 9; x++)
        {
            expected = expected * SkyField.Keep >> 8;
            Assert.Equal(expected, light[Index(x, 3, 6)]);
        }

        // The wall at the tunnel's end takes the tunnel's last cell's light; deep stone takes none.
        Assert.Equal(light[Index(8, 3, 6)], light[Index(9, 3, 6)]);
        Assert.Equal(0, light[Index(9, 3, 3)]);
    }

    [Fact]
    public void A_sealed_cave_is_dark_and_its_walls_too()
    {
        var solid = new byte[Size * Size * Size];
        Array.Fill(solid, (byte)1);
        for (var z = 4; z < 7; z++)
        {
            for (var y = 4; y < 7; y++)
            {
                for (var x = 4; x < 7; x++)
                {
                    solid[Index(x, y, z)] = 0;
                }
            }
        }

        var light = Flood(solid);

        Assert.All(light, value => Assert.Equal(0, value));
    }

    /// <summary>
    /// The slope world, read at its coarsest loaded level: the field's cells below the ground are solid and above it
    /// open, so the air over the ground sees the sky and the stone deep below sees none.
    /// </summary>
    [Fact]
    public void A_read_of_the_world_sees_its_ground()
    {
        using var world = TestWorlds.Load(new SlopeGenerator(), new Vector3(0, 2.5f, 0), radiusMetres: 4, levels: 2);
        var field = new SkyField();

        field.Read(world, new WorldPoint(0, 2.5, 0), coarsestLevel: 1);
        field.Flood();

        var cell = SkyField.CellMetresFor(VoxelGrid.Unit);
        var x = (int)((0 - field.Corner.X) / cell);
        var z = (int)((0 - field.Corner.Z) / cell);
        var ground = (int)((SlopeGenerator.Height(0, 0, 17) / 100f - field.Corner.Y) / cell);
        Assert.Equal(255, field.Light[Cell(x, ground + 2, z)]);
        Assert.Equal(0, field.Light[Cell(x, ground - 3, z)]);
        // The box's middle is the camera's cell, snapped to 8 cells.
        Assert.InRange(field.Centre.X, -8 * cell, 8 * cell);
    }

    /// <summary>
    /// A read spread over many calls, one block each (a time limit already passed), gives the field a read in one call
    /// gives, and counts the same blocks of one material or air and the same blocks read.
    /// </summary>
    [Fact]
    public void A_read_spread_over_calls_gives_the_same_field()
    {
        var centre = new Vector3(0, 2.5f, 0);
        using var world = TestWorlds.Load(new SlopeGenerator(), centre, radiusMetres: 4, levels: 2);
        var at = new WorldPoint(0, 2.5, 0);
        var whole = new SkyField();
        whole.Read(world, at, coarsestLevel: 1);
        whole.Flood();

        var spread = new SkyField();
        spread.BeginRead(world, at, coarsestLevel: 1);
        var calls = 1;
        while (!spread.ContinueRead(world, untilTicks: 0))
        {
            calls++;
        }

        spread.Flood();
        Assert.True(calls > 10, $"The read took {calls} call(s).");
        Assert.True(whole.UniformBlocks > 0 && whole.StoredBlocks > 0);
        Assert.Equal(whole.UniformBlocks, spread.UniformBlocks);
        Assert.Equal(whole.StoredBlocks, spread.StoredBlocks);
        Assert.Equal(whole.Corner, spread.Corner);
        Assert.True(whole.Light.AsSpan().SequenceEqual(spread.Light));
    }

    /// <summary>
    /// The slope world with seven levels (cells are level-6 voxels) and with six (every other level-5 voxel), read round
    /// a point 60 m from the loaded centre: the box's edges cut blocks and its far side lies outside the resident
    /// columns. Every cell holds the material the generator gives its voxel where the voxel's column is resident, and
    /// air elsewhere.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(6)]
    public void Each_cell_holds_its_voxel_at_the_game_strides(int levels)
    {
        var loaded = new Vector3(0, 2.5f, 0);
        using var world = TestWorlds.Load(new SlopeGenerator(), loaded, radiusMetres: 1, levels: levels);
        var field = new SkyField();
        var level = levels - 1;
        var stride = 1 << (SkyField.LevelFor(VoxelGrid.Unit) - level);

        field.Read(world, new WorldPoint(60, 2.5, 0), coarsestLevel: level);

        var resident = new List<ColumnKey>();
        world.CollectResidentColumns(resident);
        var columns = resident.Where(key => key.Level == level).ToHashSet();
        var corner = new Vector3(
            (float)(field.Corner.X / field.CellMetres),
            (float)(field.Corner.Y / field.CellMetres),
            (float)(field.Corner.Z / field.CellMetres)
        );
        var (x0, y0, z0) = (
            (int)MathF.Round(corner.X) * stride,
            (int)MathF.Round(corner.Y) * stride,
            (int)MathF.Round(corner.Z) * stride
        );
        var solid = field.Solid;
        int inside = 0,
            outside = 0,
            ground = 0;
        for (var z = 0; z < SkyField.SizeXZ; z++)
        {
            var vz = z0 + z * stride;
            for (var x = 0; x < SkyField.SizeXZ; x++)
            {
                var vx = x0 + x * stride;
                var column = new ColumnKey(level, vx >> VoxelConstants.ChunkShift, vz >> VoxelConstants.ChunkShift);
                var isResident = columns.Contains(column);
                var height = SlopeGenerator.Height(
                    VoxelConstants.SampleAt(vx, level),
                    VoxelConstants.SampleAt(vz, level),
                    seed: 17
                );
                for (var y = 0; y < SkyField.SizeY; y++)
                {
                    var sampleY = VoxelConstants.SampleAt(y0 + y * stride, level);
                    var expected =
                        !isResident ? VoxelConstants.Air
                        : sampleY < height - 50 ? SlopeGenerator.Stone
                        : sampleY < height ? SlopeGenerator.Dirt
                        : VoxelConstants.Air;
                    var actual = solid[Cell(x, y, z)];
                    if (actual != expected)
                    {
                        Assert.Fail($"Cell ({x}, {y}, {z}): {actual}, expected {expected}.");
                    }

                    ground += expected == SlopeGenerator.Dirt ? 1 : 0;
                }

                inside += isResident ? 1 : 0;
                outside += isResident ? 0 : 1;
            }
        }

        // Both kinds of block were read, and the box held resident and absent columns and the ground's dirt.
        Assert.True(field.UniformBlocks > 0 && field.StoredBlocks > 0);
        Assert.True(inside > 0 && outside > 0, $"{inside} resident and {outside} absent cell columns.");
        Assert.True(ground > 0);
        // The box's corner is not on a block's corner, so its edges cut blocks.
        Assert.True(x0 % VoxelWorld.BlockEdge != 0 || y0 % VoxelWorld.BlockEdge != 0);
    }

    [Fact]
    public void A_read_allocates_nothing_once_its_lists_have_grown()
    {
        var centre = new Vector3(0, 2.5f, 0);
        using var world = TestWorlds.Load(new SlopeGenerator(), centre, radiusMetres: 4, levels: 2);
        var at = new WorldPoint(0, 2.5, 0);
        var field = new SkyField();
        field.Read(world, at, coarsestLevel: 1);

        var before = SystemAllocation.StartMeasurement();
        field.BeginRead(world, at, coarsestLevel: 1);
        while (!field.ContinueRead(world, untilTicks: 0)) { }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static int Cell(int x, int y, int z) => x + SkyField.SizeXZ * (y + SkyField.SizeY * z);

    private static int Index(int x, int y, int z) => x + Size * (y + Size * z);

    private static byte[] Flood(byte[] solid)
    {
        var light = new byte[solid.Length];
        var buckets = new List<int>[256];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = [];
        }

        SkyField.Flood(solid, light, Size, Size, Size, buckets);
        Assert.All(buckets, bucket => Assert.Empty(bucket));
        return light;
    }
}
