using System.Diagnostics;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Voxel materials' physics materials (<see cref="PhysicsMaterials.MapVoxel"/>): a cell of two physics materials has a
/// static for each, of that material, each holding the boxes of its own voxels; with nothing mapped (or every voxel
/// material mapped to one) a cell is one static, as before; a box slides down a slope of ice voxels; clearing a cell's
/// voxels of one material removes that static; a ball takes its voxel material's; the mapping's rules; the time to build
/// cells of one and three materials (logged); and rebuilding cells of two materials allocates nothing.
/// </summary>
public sealed class VoxelPhysicsMaterialsTests : IDisposable
{
    private const int Floor = 200;
    private const float Step = 1f / 60;

    private readonly ITestOutputHelper _output;
    private readonly VoxelWorld _world;
    private readonly List<(PhysicsWorld Physics, VoxelColliders Colliders)> _made = [];

    public VoxelPhysicsMaterialsTests(ITestOutputHelper output)
    {
        _output = output;
        var boxes = new List<VoxelBox>
        {
            // A block of stone 20 cm on a side on the dirt, from x = 1 m.
            new(100, 200, 0, 119, 219, 19, BoxesGenerator.Stone),
            // A block of many materials (100 to 122, leaves, and air), 1.5 m by 0.32 m by 1.5 m.
            new(-300, 200, 50, -151, 231, 199, BoxesGenerator.Pattern),
        };
        // A 30-degree slope of stone rising along -z from z = -120, to 46 cm, from x -60 to 60.
        for (var i = 1; i <= 80; i++)
        {
            boxes.Add(new VoxelBox(-60, 200, -120 - i, 60, Floor + (int)(i * 0.57735027) - 1, -120 - i, 3));
        }

        _world = TestWorlds.Load(new BoxesGenerator(Floor, [.. boxes]), new Vector3(0, 2, 0), radiusMetres: 4);
    }

    public void Dispose()
    {
        foreach (var (physics, colliders) in _made)
        {
            colliders.Dispose();
            physics.Dispose();
        }

        _world.Dispose();
    }

    [Fact]
    public void A_cell_of_stone_and_dirt_has_a_static_of_each_material_with_its_own_voxels()
    {
        var materials = new PhysicsMaterials();
        var ice = materials.Register("ice", new PhysicsMaterial(0.03f, 0));
        materials.MapVoxel(BoxesGenerator.Stone, ice);
        var (physics, colliders) = Make(materials);
        var ball = AddSphere(physics, new Vector3(1.1f, 2.35f, 0.1f), 0.1f);

        Run(physics, colliders, 60);

        Assert.Equal(2, colliders.PartCount);
        // The cell from (96, 192, 0) holds the dirt under the floor and the block on it.
        var corner = new Vector3(0.96f, 1.92f, 0);
        var statics = StaticsAt(physics, corner);
        Assert.Equal(2, statics.Count);
        Assert.Contains(statics, handle => physics.MaterialOf(handle) == ice);
        Assert.Contains(statics, handle => physics.MaterialOf(handle) == PhysicsMaterialId.Default);
        AssertStaticsHoldTheirVoxels(physics, materials);
        // It rests on the block's top.
        Assert.Equal(2.3f, physics.Simulation.Bodies[ball].Pose.Position.Y, 0.01f);
    }

    [Fact]
    public void Statics_are_split_only_where_the_voxels_have_two_physics_materials()
    {
        var none = Make(null);
        var unmapped = Make(new PhysicsMaterials());
        var allOne = new PhysicsMaterials();
        var stone = allOne.Register("stone", new PhysicsMaterial(0.8f, 0));
        for (var voxel = 1; voxel < 256; voxel++)
        {
            allOne.MapVoxel((byte)voxel, stone);
        }

        var one = Make(allOne);
        var split = new PhysicsMaterials();
        split.MapVoxel(BoxesGenerator.Stone, split.Register("ice", new PhysicsMaterial(0.03f, 0)));
        var two = Make(split);
        foreach (var (physics, colliders) in _made)
        {
            AddSphere(physics, new Vector3(1.1f, 2.35f, 0.1f), 0.1f);
            Run(physics, colliders, 1);
        }

        Assert.Equal("1 1 1 2", string.Join(' ', _made.Select(made => made.Colliders.PartCount)));
        Assert.Equal(none.Colliders.StaticCount, unmapped.Colliders.StaticCount);
        Assert.Equal(none.Colliders.StaticCount, one.Colliders.StaticCount);
        Assert.Equal(none.Colliders.BoxCount, unmapped.Colliders.BoxCount);
        Assert.Equal(none.Colliders.BoxCount, one.Colliders.BoxCount);
        Assert.True(two.Colliders.StaticCount > none.Colliders.StaticCount);
        AssertStaticsHoldTheirVoxels(one.Physics, allOne);
    }

    [Fact]
    public void A_box_slides_down_a_30_degree_slope_of_ice_voxels()
    {
        var materials = new PhysicsMaterials();
        materials.MapVoxel(BoxesGenerator.Stone, materials.Register("ice", new PhysicsMaterial(0.03f, 0)));
        var (physics, colliders) = Make(materials);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 30 * MathF.PI / 180);
        var box = AddBox(physics, new Vector3(0, 2.29f, -1.6f), new Vector3(0.2f, 0.1f, 0.2f), turn);

        Run(physics, colliders, 120);

        // Friction sqrt(1 x 0.03) = 0.17, under tan 30 = 0.58: 30 cm or more down to the slope's foot, where the dirt
        // (friction 1) holds its front edge; on a slope of plain stone it stays within 2 cm (VoxelCollidersTests).
        var position = physics.Simulation.Bodies[box].Pose.Position;
        Assert.True(position.Z > -1.3f, $"The box stayed on the slope: {position}.");
    }

    [Fact]
    public void Clearing_a_cells_stone_removes_its_stone_static_and_keeps_its_dirt()
    {
        var materials = new PhysicsMaterials();
        var ice = materials.Register("ice", new PhysicsMaterial(0.03f, 0));
        materials.MapVoxel(BoxesGenerator.Stone, ice);
        var (physics, colliders) = Make(materials);
        AddSphere(physics, new Vector3(0.85f, 2.1f, 0.1f), 0.1f);
        Run(physics, colliders, 5);
        var corner = new Vector3(0.96f, 1.92f, 0);
        Assert.Equal(2, StaticsAt(physics, corner).Count);

        _world.Apply(VoxelEdit.Box(new VoxelCoord(100, 200, 0), new VoxelCoord(20, 20, 20), VoxelEditOperation.Clear));
        Run(physics, colliders, 5);

        var left = StaticsAt(physics, corner);
        Assert.Equal(PhysicsMaterialId.Default, physics.MaterialOf(Assert.Single(left)));
        AssertStaticsHoldTheirVoxels(physics, materials);
    }

    [Fact]
    public void A_ball_takes_its_voxel_materials_physics_material()
    {
        var materials = new PhysicsMaterials();
        var rubber = materials.Register("rubber", new PhysicsMaterial(1, 0.8f));
        materials.MapVoxel(FlatTestGenerator.Stone, rubber);
        using var harness = new PhysicsTestHarness(materials: materials);

        var ball = harness.ThrowBall(new Vector3(0, 0.5f, 0));

        Assert.Equal(rubber, harness.Physics.MaterialOf(ball));
        Assert.Equal(rubber, materials.OfVoxel(FlatTestGenerator.Stone));
        Assert.Equal(PhysicsMaterialId.Default, materials.OfVoxel((byte)(FlatTestGenerator.Stone + 1)));
    }

    [Fact]
    public void The_voxels_have_at_most_eight_physics_materials_mapped_before_the_first_match()
    {
        var materials = new PhysicsMaterials();
        var ids = new PhysicsMaterialId[8];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = materials.Register("m" + i, new PhysicsMaterial(1, 0));
        }

        for (var i = 0; i < 7; i++)
        {
            materials.MapVoxel((byte)(10 + i), ids[i]);
        }

        Assert.Equal(8, materials.VoxelMaterialCount());
        Assert.Throws<InvalidOperationException>(() => materials.MapVoxel(17, ids[7]));
        Assert.Equal(PhysicsMaterialId.Default, materials.OfVoxel(17));
        // Moving a voxel material to one the voxels have already is fine.
        materials.MapVoxel(17, ids[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => materials.MapVoxel(0, ids[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => materials.MapVoxel(18, new PhysicsMaterialId(40)));
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, null, materials);
        physics.Initialize();
        Assert.Throws<InvalidOperationException>(() => materials.MapVoxel(18, ids[0]));
    }

    [Fact]
    public void Building_cells_of_one_and_of_three_physics_materials_is_timed()
    {
        var three = new PhysicsMaterials();
        var a = three.Register("a", new PhysicsMaterial(0.5f, 0));
        var b = three.Register("b", new PhysicsMaterial(2, 0));
        for (var voxel = 101; voxel <= 122; voxel++)
        {
            three.MapVoxel((byte)voxel, voxel < 112 ? a : b);
        }

        // Over the block of many materials: 3 x 2 x 3 cells or so, each of dirt and of the block's voxels.
        var times = new double[2];
        var counts = new (int Statics, int Boxes)[2];
        for (var round = 0; round < 6; round++)
        {
            for (var config = 0; config < 2; config++)
            {
                var (physics, colliders) = Make(config == 0 ? null : three);
                AddSphere(physics, new Vector3(-2.25f, 2.6f, 1.25f), 0.5f);
                var started = Stopwatch.GetTimestamp();
                colliders.Update(Step);
                var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                times[config] = round == 0 ? milliseconds : Math.Min(times[config], milliseconds);
                counts[config] = (colliders.StaticCount, colliders.BoxCount);
                if (config == 1)
                {
                    Assert.Equal(3, colliders.PartCount);
                }
            }
        }

        _output.WriteLine(
            $"Cells round a 1 m ball over the block of many materials (Debug, the least of 6): one physics material "
                + $"{times[0]:F2} ms, {counts[0].Statics} statics of {counts[0].Boxes} boxes; three "
                + $"{times[1]:F2} ms, {counts[1].Statics} statics of {counts[1].Boxes} boxes"
        );
        Assert.True(counts[1].Statics > counts[0].Statics);
    }

    [Fact]
    public void Rebuilding_cells_of_two_physics_materials_allocates_nothing()
    {
        var materials = new PhysicsMaterials();
        materials.MapVoxel(BoxesGenerator.Stone, materials.Register("ice", new PhysicsMaterial(0.03f, 0)));
        var (physics, colliders) = Make(materials);
        AddSphere(physics, new Vector3(1.1f, 2.35f, 0.1f), 0.1f);
        Run(physics, colliders, 30);
        var allocated = 0L;
        for (var round = 0; round < 4; round++)
        {
            // A voxel of the block set to dirt and back: its cell is rebuilt, both of its statics.
            var material = round % 2 == 0 ? BoxesGenerator.Dirt : BoxesGenerator.Stone;
            _world.Apply(
                VoxelEdit.Box(new VoxelCoord(105, 210, 5), new VoxelCoord(1, 1, 1), VoxelEditOperation.Set, material)
            );
            var before = SystemAllocation.StartMeasurement();
            colliders.Update(Step);
            physics.Step(Step);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }

    private (PhysicsWorld Physics, VoxelColliders Colliders) Make(PhysicsMaterials? materials)
    {
        var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, null, materials);
        physics.Initialize();
        var colliders = new VoxelColliders(
            physics,
            _world,
            new MoverWorldFixture.LeavesPass(),
            NullLogger<VoxelColliders>.Instance
        );
        colliders.Initialize();
        _made.Add((physics, colliders));
        return (physics, colliders);
    }

    private static void Run(PhysicsWorld physics, VoxelColliders colliders, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            colliders.Update(Step);
            physics.Step(Step);
        }
    }

    private static List<StaticHandle> StaticsAt(PhysicsWorld physics, Vector3 corner)
    {
        var statics = physics.Simulation.Statics;
        var found = new List<StaticHandle>();
        for (var i = 0; i < statics.Count; i++)
        {
            var handle = statics.IndexToHandle[i];
            if (Vector3.Distance(statics[handle].Pose.Position, corner) < 0.001f)
            {
                found.Add(handle);
            }
        }

        return found;
    }

    /// <summary>Each static holds as many boxes as the voxels of its cell of its physics material give.</summary>
    private void AssertStaticsHoldTheirVoxels(PhysicsWorld physics, PhysicsMaterials materials)
    {
        var simulation = physics.Simulation;
        var boxes = new VoxelBoxes();
        var passable = MaterialSet.Passable(new MoverWorldFixture.LeavesPass());
        Assert.True(simulation.Statics.Count > 1, "Too few statics to test.");
        for (var i = 0; i < simulation.Statics.Count; i++)
        {
            var handle = simulation.Statics.IndexToHandle[i];
            var reference = simulation.Statics[handle];
            var compound = simulation.Shapes.GetShape<BigCompound>(reference.Shape.Index);
            var material = physics.MaterialOf(handle);
            var others = new List<byte>();
            for (var voxel = 1; voxel < 256; voxel++)
            {
                if (materials.OfVoxel((byte)voxel) != material)
                {
                    others.Add((byte)voxel);
                }
            }

            var corner = reference.Pose.Position * VoxelConstants.CentimetresPerMetre;
            int x0 = (int)MathF.Round(corner.X),
                y0 = (int)MathF.Round(corner.Y),
                z0 = (int)MathF.Round(corner.Z),
                size = VoxelColliders.CellSize;
            var excluded = passable.Union(MaterialSet.Of([.. others]));
            boxes.Build(_world, x0, y0, z0, x0 + size - 1, y0 + size - 1, z0 + size - 1, in excluded);
            Assert.True(
                boxes.Count == compound.ChildCount,
                $"The static of material {material.Value} at ({x0}, {y0}, {z0}) has {compound.ChildCount} boxes; its voxels give {boxes.Count}."
            );
        }
    }

    private static BodyHandle AddSphere(PhysicsWorld physics, Vector3 position, float radius)
    {
        var sphere = new Sphere(radius);
        var simulation = physics.Simulation;
        return simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(position),
                sphere.ComputeInertia(1),
                new CollidableDescription(simulation.Shapes.Add(sphere), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
    }

    private static BodyHandle AddBox(PhysicsWorld physics, Vector3 position, Vector3 size, Quaternion orientation)
    {
        var box = new Box(size.X, size.Y, size.Z);
        var simulation = physics.Simulation;
        return simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(position, orientation),
                box.ComputeInertia(1),
                new CollidableDescription(simulation.Shapes.Add(box), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
    }
}
