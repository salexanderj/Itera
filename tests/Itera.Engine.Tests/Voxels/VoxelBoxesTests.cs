using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// <see cref="VoxelBoxes"/> against a voxel-by-voxel walk: the boxes of a region never overlap, stay inside it, and
/// cover exactly its blocking voxels.
/// </summary>
public sealed class VoxelBoxesTests : IDisposable
{
    private const int Floor = 200;

    private static readonly MaterialSet s_leavesPass = MaterialSet.Of(BoxesGenerator.Leaves);

    private static readonly BoxesGenerator s_generator = new(
        Floor,
        new VoxelBox(100, 200, -50, 140, 299, 50, BoxesGenerator.Stone),
        new VoxelBox(-100, 200, -30, -40, 260, 30, BoxesGenerator.Leaves),
        new VoxelBox(-30, 200, 60, -13, 240, 80, BoxesGenerator.Leaves),
        new VoxelBox(-12, 200, 60, 0, 240, 80, BoxesGenerator.Stone),
        new VoxelBox(40, 204, 40, 71, 235, 71, BoxesGenerator.Pattern),
        new VoxelBox(60, 150, -120, 90, 199, -90, VoxelConstants.Air),
        new VoxelBox(70, 150, -110, 74, 185, -106, BoxesGenerator.Mushroom)
    );

    private readonly VoxelWorld _world;

    public VoxelBoxesTests()
    {
        _world = TestWorlds.Load(s_generator, Vector3.Zero);
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Boxes_cover_exactly_the_blocking_voxels() => CheckRegions(_world, 80, 150, 300);

    [Theory]
    [InlineData("slope")]
    [InlineData("dome")]
    [InlineData("trunk")]
    public void Boxes_cover_exactly_the_blocking_voxels_of_slopes_domes_and_trunks(string name)
    {
        IChunkGenerator generator = name switch
        {
            "slope" => new SlopeGenerator(),
            "dome" => new DomeGenerator(),
            _ => new TrunkGenerator(),
        };
        using var world = TestWorlds.Load(generator, new Vector3(0.64f, 2, 0.64f));

        // The dome's top is 4 m up; the slope and the trunk stand on ground near 2 m.
        if (name == "dome")
        {
            CheckRegions(world, 30, 330, 450);
        }
        else
        {
            CheckRegions(world, 30, 150, 300);
        }
    }

    [Fact]
    public void Leaves_give_no_boxes_and_a_flat_floor_gives_few()
    {
        var boxes = new VoxelBoxes();

        boxes.Build(_world, -90, 200, -20, -60, 250, 10, in s_leavesPass);
        var leaves = boxes.Count;
        // 32 by 32 voxels of floor, from 16 below its top to 16 above: one box for the part of the cell of dirt below
        // (y 160 to 191) and one for each of the 16 uniform bricks in the cell holding the top (y 192 to 199).
        boxes.Build(_world, -32, 184, -32, -1, 215, -1, in s_leavesPass);

        Assert.Equal(0, leaves);
        Assert.Equal(17, boxes.Count);
    }

    [Fact]
    public void Building_again_allocates_nothing()
    {
        var boxes = new VoxelBoxes();
        boxes.Build(_world, 30, 190, 30, 80, 250, 80, in s_leavesPass);

        var before = SystemAllocation.StartMeasurement();
        boxes.Build(_world, 30, 190, 30, 80, 250, 80, in s_leavesPass);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void CheckRegions(VoxelWorld world, int count, int yLow, int yHigh)
    {
        var random = new Random(3);
        var boxes = new VoxelBoxes();
        for (var i = 0; i < count; i++)
        {
            int sizeX = random.Next(1, 48),
                sizeY = random.Next(1, 48),
                sizeZ = random.Next(1, 48);
            int x0 = random.Next(-150, 150 - sizeX),
                y0 = random.Next(yLow, yHigh - sizeY),
                z0 = random.Next(-140, 140 - sizeZ);
            var passable = i % 3 == 0 ? MaterialSet.None : s_leavesPass;

            boxes.Build(world, x0, y0, z0, x0 + sizeX - 1, y0 + sizeY - 1, z0 + sizeZ - 1, in passable);

            var covered = new bool[sizeX * sizeY * sizeZ];
            foreach (var box in boxes.Boxes)
            {
                Assert.True(
                    box.X0 <= box.X1 && box.Y0 <= box.Y1 && box.Z0 <= box.Z1,
                    $"Region {i}: box {box} is empty."
                );
                Assert.True(
                    box.X0 >= x0
                        && box.X1 < x0 + sizeX
                        && box.Y0 >= y0
                        && box.Y1 < y0 + sizeY
                        && box.Z0 >= z0
                        && box.Z1 < z0 + sizeZ,
                    $"Region {i}: box {box} leaves the region."
                );
                for (var z = box.Z0; z <= box.Z1; z++)
                {
                    for (var y = box.Y0; y <= box.Y1; y++)
                    {
                        for (var x = box.X0; x <= box.X1; x++)
                        {
                            var index = (x - x0) + sizeX * ((y - y0) + sizeY * (z - z0));
                            Assert.False(covered[index], $"Region {i}: voxel ({x}, {y}, {z}) is in two boxes.");
                            covered[index] = true;
                        }
                    }
                }
            }

            for (var z = 0; z < sizeZ; z++)
            {
                for (var y = 0; y < sizeY; y++)
                {
                    for (var x = 0; x < sizeX; x++)
                    {
                        world.TryGetMaterial(new VoxelCoord(x0 + x, y0 + y, z0 + z), out var material);
                        var blocks = material != VoxelConstants.Air && !passable.Contains(material);
                        Assert.True(
                            blocks == covered[x + sizeX * (y + sizeY * z)],
                            $"Region {i}: voxel ({x0 + x}, {y0 + y}, {z0 + z}) of material {material}: blocks {blocks}."
                        );
                    }
                }
            }
        }
    }
}
