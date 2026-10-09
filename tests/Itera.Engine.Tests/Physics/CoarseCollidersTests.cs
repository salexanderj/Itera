using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A giant's bodies against the coarse statics <see cref="VoxelColliders"/> builds from the world's level-2 voxels
/// (<see cref="VoxelColliders.CoarseLevel"/>): resting on them, touching only them while other bodies touch only the
/// level-0 ones, falling into a dig, freezing only where level 2 is not resident, a cut body falling on the coarse
/// voxels where its own are not resident, the level <see cref="LooseBodies"/> gives a body of 4 cm parts (and level 0 in
/// a world without level 2), and no allocation.
/// </summary>
public sealed class CoarseCollidersTests : IDisposable
{
    // The floor's top at 2 m is a whole level-2 voxel's top too (50 voxels of 4 cm).
    private const int Floor = 200;
    private const float Step = 1f / 60;

    private readonly VoxelWorld _world;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;

    public CoarseCollidersTests()
    {
        // Level 0 within 4 m, level 2 within 16 m.
        _world = TestWorlds.Load(new BoxesGenerator(Floor), new Vector3(0, 2, 0), radiusMetres: 4, levels: 3);
        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        _colliders = new VoxelColliders(
            _physics,
            _world,
            new MoverWorldFixture.LeavesPass(),
            NullLogger<VoxelColliders>.Instance
        );
        _colliders.Initialize();
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_giants_body_rests_on_statics_of_the_level_2_voxels()
    {
        var box = AddBox(new Vector3(0.5f, 2.5f, 0.5f), VoxelColliders.CoarseLevel);

        Run(120);

        Assert.Equal(2.2f, Pose(box).Position.Y, 0.01f);
        Assert.True(_physics.Simulation.Statics.Count > 0);
        var boxes = new VoxelBoxes();
        var passable = MaterialSet.Passable(new MoverWorldFixture.LeavesPass());
        for (var i = 0; i < _physics.Simulation.Statics.Count; i++)
        {
            var handle = _physics.Simulation.Statics.IndexToHandle[i];
            Assert.Equal(VoxelColliders.CoarseLevel, _physics.Kinds.StaticLevel(handle.Value));
            // Each holds the boxes of its cell of level-2 voxels (1.28 m on an edge).
            var reference = _physics.Simulation.Statics[handle];
            var compound = _physics.Simulation.Shapes.GetShape<BigCompound>(reference.Shape.Index);
            var corner = reference.Pose.Position * (VoxelConstants.CentimetresPerMetre >> VoxelColliders.CoarseLevel);
            int x0 = (int)MathF.Round(corner.X),
                y0 = (int)MathF.Round(corner.Y),
                z0 = (int)MathF.Round(corner.Z),
                last = VoxelColliders.CellSize - 1;
            boxes.Build(_world, VoxelColliders.CoarseLevel, x0, y0, z0, x0 + last, y0 + last, z0 + last, in passable);
            Assert.Equal(boxes.Count, compound.ChildCount);
        }
    }

    [Fact]
    public void Each_body_touches_only_the_statics_of_its_own_level()
    {
        var fine = AddBox(new Vector3(-0.5f, 2.3f, 0.5f), 0);
        var coarse = AddBox(new Vector3(1.5f, 2.3f, 0.5f), VoxelColliders.CoarseLevel);
        Run(60);

        var callbacks = new NarrowPhaseCallbacks(_physics.Kinds);
        var margin = 0f;
        var levels = new HashSet<int>();
        for (var i = 0; i < _physics.Simulation.Statics.Count; i++)
        {
            var handle = _physics.Simulation.Statics.IndexToHandle[i];
            var level = _physics.Kinds.StaticLevel(handle.Value);
            levels.Add(level);
            var ground = new CollidableReference(handle);
            Assert.Equal(level == 0, callbacks.AllowContactGeneration(0, ground, Dynamic(fine), ref margin));
            Assert.Equal(level == 0, callbacks.AllowContactGeneration(0, Dynamic(fine), ground, ref margin));
            Assert.Equal(level != 0, callbacks.AllowContactGeneration(0, ground, Dynamic(coarse), ref margin));
            Assert.Equal(level != 0, callbacks.AllowContactGeneration(0, Dynamic(coarse), ground, ref margin));
        }

        Assert.Equal([0, VoxelColliders.CoarseLevel], levels.Order());
        // Both rest on the floor, each on its own statics.
        Assert.Equal(2.2f, Pose(fine).Position.Y, 0.01f);
        Assert.Equal(2.2f, Pose(coarse).Position.Y, 0.01f);
        Assert.True(callbacks.AllowContactGeneration(0, Dynamic(fine), Dynamic(coarse), ref margin));
    }

    [Fact]
    public void Digging_under_a_resting_giants_body_wakes_it_and_it_falls()
    {
        var box = AddBox(new Vector3(0.5f, 2.3f, 0.5f), VoxelColliders.CoarseLevel);
        Run(300);
        var rest = Pose(box).Position;

        // A pit 50 cm deep and 60 cm wide under the box: level 2's change rebuilds its cell, which wakes the box.
        _world.Apply(VoxelEdit.Box(new VoxelCoord(20, 150, 20), new VoxelCoord(60, 50, 60), VoxelEditOperation.Clear));
        Run(90);

        Assert.Equal(2.2f, rest.Y, 0.01f);
        // Level 2's pit reaches down to 1.48 m: its voxel from 1.48 to 1.52 m samples 1.50 m, inside the pit.
        Assert.Equal(1.68f, Pose(box).Position.Y, 0.02f);
    }

    [Fact]
    public void A_giants_body_moves_where_level_2_is_resident_and_freezes_past_it()
    {
        // At 10 m level 0 is not resident (4 m) but level 2 is (16 m); at 25 m neither is.
        var fine = AddBox(new Vector3(10, 2.5f, 0), 0);
        var coarse = AddBox(new Vector3(10, 2.5f, 1), VoxelColliders.CoarseLevel);
        var far = AddBox(new Vector3(25, 2.5f, 0), VoxelColliders.CoarseLevel);

        Run(120);

        Assert.True(_colliders.IsFrozen(fine));
        Assert.False(_colliders.IsFrozen(coarse));
        Assert.True(_colliders.IsFrozen(far));
        Assert.Equal(2.2f, Pose(coarse).Position.Y, 0.01f);
        Assert.Equal(2.5f, Pose(far).Position.Y, 0.01f);
    }

    /// <summary>
    /// A body cut off a character falls on the coarser voxels where its own are not resident (it
    /// froze in the air): at 6 m on level 1's, at 12 m on level 2's, and
    /// frozen only at 25 m, where no level is; a thrown ball still freezes, since it goes back into the grid; and once
    /// level 0 loads round it, it collides with level 0 again.
    /// </summary>
    [Fact]
    public void A_cut_body_falls_on_the_coarse_voxels_where_its_own_are_not_resident()
    {
        var one = AddBox(new Vector3(6, 2.5f, 0), 0, BodyKind.Cut);
        var two = AddBox(new Vector3(12, 2.5f, 0), 0, BodyKind.Cut);
        var far = AddBox(new Vector3(25, 2.5f, 0), 0, BodyKind.Cut);
        var ball = AddBox(new Vector3(6, 2.5f, 2), 0);

        Run(120);

        Assert.False(_colliders.IsFrozen(one));
        Assert.Equal(1, _physics.Kinds.Level(one.Value));
        Assert.Equal(2.2f, Pose(one).Position.Y, 0.02f);
        Assert.False(_colliders.IsFrozen(two));
        Assert.Equal(VoxelColliders.CoarseLevel, _physics.Kinds.Level(two.Value));
        Assert.Equal(2.2f, Pose(two).Position.Y, 0.02f);
        Assert.True(_colliders.IsFrozen(far));
        Assert.True(_colliders.IsFrozen(ball));

        // Level 0 loads round it: back to level 0, still resting on the floor.
        _world.SetCenters([new Vector3(6, 2, 0)]);
        TestWorlds.WaitUntilLoaded(_world);
        Run(30);
        Assert.Equal(0, _physics.Kinds.Level(one.Value));
        Assert.Equal(0, _physics.Kinds.BaseLevel(one.Value));
        Assert.Equal(2.2f, Pose(one).Position.Y, 0.02f);
    }

    [Fact]
    public void A_body_of_4_cm_parts_collides_with_level_2_and_only_where_the_world_has_it()
    {
        using var single = TestWorlds.Load(new BoxesGenerator(Floor), new Vector3(0, 2, 0), radiusMetres: 2);
        var coarse = CollisionLevels(_world);
        var fine = CollisionLevels(single);

        // Parts of 4 cm voxels give level 2, and a mix with a 1 cm part or a world of level 0 alone gives level 0.
        Assert.Equal((VoxelColliders.CoarseLevel, 0), coarse);
        Assert.Equal((0, 0), fine);
    }

    [Fact]
    public void Updating_around_resting_bodies_of_both_levels_allocates_nothing()
    {
        AddBox(new Vector3(-0.5f, 2.3f, 0.5f), 0);
        AddBox(new Vector3(1.5f, 2.3f, 0.5f), VoxelColliders.CoarseLevel);
        Run(120);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            _colliders.Update(Step);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// The collision levels <see cref="LooseBodies"/> gives, in <paramref name="world"/>, a body of two 4 cm parts and a
    /// body of a 4 cm part and a 1 cm one.
    /// </summary>
    private static (int AllCoarse, int Mixed) CollisionLevels(VoxelWorld world)
    {
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        physics.Initialize();
        using var colliders = new VoxelColliders(
            physics,
            world,
            new MoverWorldFixture.LeavesPass(),
            NullLogger<VoxelColliders>.Instance
        );
        var models = new VoxelModels(world);
        var bodies = new LooseBodies(
            physics,
            world,
            colliders,
            models,
            new MoverWorldFixture.LeavesPass(),
            Options.Create(new EngineOptions()),
            NullLogger<LooseBodies>.Instance
        );
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < 10; z++)
        {
            for (var y = 0; y < 10; y++)
            {
                for (var x = 0; x < 10; x++)
                {
                    voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = BoxesGenerator.Stone;
                }
            }
        }

        var tree = models.Encode(voxels);
        var size = new VoxelCoord(10, 10, 10);
        LoosePart Part(float x, int level) =>
            new(-1, tree, size, new Vector3(x, 3, 0), Quaternion.Identity, false, level);

        Assert.True(bodies.TryCreate([Part(0, 2), Part(0.5f, 2)], 1000, Vector3.Zero, 0, out var allCoarse));
        Assert.True(bodies.TryCreate([Part(1, 2), Part(1.5f, 0)], 1000, Vector3.Zero, 0, out var mixed));
        return (bodies.CollisionLevel(allCoarse), bodies.CollisionLevel(mixed));
    }

    private void Run(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            _colliders.Update(Step);
            _physics.Step(Step);
        }
    }

    private RigidPose Pose(BodyHandle body) => _physics.Simulation.Bodies[body].Pose;

    private static CollidableReference Dynamic(BodyHandle body) => new(CollidableMobility.Dynamic, body);

    /// <summary>A 40 cm box of 1 kg of <paramref name="kind"/>, colliding with the voxels of <paramref name="level"/>.</summary>
    private BodyHandle AddBox(Vector3 position, int level, BodyKind kind = BodyKind.Loose)
    {
        var box = new Box(0.4f, 0.4f, 0.4f);
        var simulation = _physics.Simulation;
        var shape = simulation.Shapes.Add(box);
        var body = simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(position),
                box.ComputeInertia(1),
                new CollidableDescription(shape, 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
        _physics.Kinds.Set(body, kind, level);
        return body;
    }
}
