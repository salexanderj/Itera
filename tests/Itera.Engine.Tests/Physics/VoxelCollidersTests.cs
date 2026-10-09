using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Bepu bodies against statics that <see cref="VoxelColliders"/> builds from the voxels: resting, sliding, falling into a
/// dig, thin walls, foliage, unloading, freezing, and a lost change list.
/// </summary>
public sealed class VoxelCollidersTests : IDisposable
{
    private const int Floor = 200;
    private const float Step = 1f / 60;

    private readonly VoxelWorld _world;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;

    public VoxelCollidersTests()
    {
        var boxes = new List<VoxelBox>
        {
            // A stone wall 1 voxel thick at x = 100, 2 m high.
            new(100, 200, -100, 100, 399, 100, BoxesGenerator.Stone),
            // A block of leaves 50 cm high (x -150 to -100).
            new(-150, 200, -50, -100, 249, 50, BoxesGenerator.Leaves),
        };
        // A 30-degree slope rising along -z from z = -120, to 46 cm (z -200 to -121), and a 60-degree one rising along
        // +z from z = 120 (z 121 to 200) to 1.38 m, both from x -60 to 60.
        for (var i = 1; i <= 80; i++)
        {
            boxes.Add(new VoxelBox(-60, 200, -120 - i, 60, Floor + (int)(i * 0.57735027) - 1, -120 - i, 3));
        }

        for (var i = 1; i <= 80; i++)
        {
            boxes.Add(new VoxelBox(-60, 200, 120 + i, 60, Floor + (int)(i * 1.7320508) - 1, 120 + i, 3));
        }

        _world = TestWorlds.Load(new BoxesGenerator(Floor, [.. boxes]), new Vector3(0, 2, 0), radiusMetres: 4);
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
    public void A_sphere_dropped_on_the_floor_comes_to_rest_on_it()
    {
        var ball = AddSphere(new Vector3(0, 2.5f, 0), 0.1f);

        Run(120);

        var position = Pose(ball).Position;
        Assert.Equal(2.1f, position.Y, 0.01f);
        Assert.Equal(0, position.X, 0.01f);
        Assert.True(_colliders.StaticCount > 0);
    }

    [Fact]
    public void A_box_rests_on_a_30_degree_slope_and_slides_down_a_60_degree_one()
    {
        // Each box starts just above the middle of its slope, turned to lie along it.
        var gentle = AddBox(
            new Vector3(0, 2.29f, -1.6f),
            new Vector3(0.2f, 0.1f, 0.2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 30 * MathF.PI / 180)
        );
        var steep = AddBox(
            new Vector3(0, 2.73f, 1.55f),
            new Vector3(0.2f, 0.1f, 0.2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -60 * MathF.PI / 180)
        );

        Run(30);
        var gentleStart = Pose(gentle).Position;
        var steepStart = Pose(steep).Position;
        Run(90);

        Assert.True(
            Vector3.Distance(Pose(gentle).Position, gentleStart) < 0.02f,
            $"The box on 30 degrees moved from {gentleStart} to {Pose(gentle).Position}."
        );
        Assert.True(
            Pose(steep).Position.Z < steepStart.Z - 0.2f,
            $"The box on 60 degrees stayed near {steepStart}: {Pose(steep).Position}."
        );
    }

    [Fact]
    public void Digging_under_a_resting_body_wakes_it_and_it_falls()
    {
        var ball = AddSphere(new Vector3(0.5f, 2.3f, 0.5f), 0.1f);
        Run(300);
        var rest = Pose(ball).Position;

        // A pit 40 cm deep under the ball: the change list rebuilds the cell, which wakes the ball.
        _world.Apply(VoxelEdit.Box(new VoxelCoord(30, 160, 30), new VoxelCoord(40, 40, 40), VoxelEditOperation.Clear));
        Run(60);

        Assert.Equal(2.1f, rest.Y, 0.01f);
        Assert.Equal(1.7f, Pose(ball).Position.Y, 0.02f);
    }

    [Fact]
    public void Changed_cells_past_the_limit_wait_for_later_steps_while_new_cells_are_built_at_once()
    {
        // Nine balls resting over a 70 cm square of floor.
        var balls = new List<BodyHandle>();
        for (var i = 0; i < 9; i++)
        {
            balls.Add(AddSphere(new Vector3(0.1f + i % 3 * 0.25f, 2.3f, 0.1f + i / 3 * 0.25f), 0.1f));
        }

        Run(300);
        // One changed cell a step.
        _colliders.RebuildLimit = 1;

        // A pit 40 cm deep under all of them changes every cell they rest on; a new ball just above the floor away
        // from them needs new cells at once.
        _world.Apply(VoxelEdit.Box(new VoxelCoord(0, 160, 0), new VoxelCoord(80, 40, 80), VoxelEditOperation.Clear));
        var dropped = AddSphere(new Vector3(-0.6f, 2.13f, -0.9f), 0.1f);
        Run(1);
        var waiting = _colliders.DirtyCount;
        Run(120);

        Assert.True(waiting > 1, $"{waiting} changed cell(s) waited after the first step.");
        Assert.Equal(0, _colliders.DirtyCount);
        Assert.All(balls, ball => Assert.Equal(1.7f, Pose(ball).Position.Y, 0.03f));
        // The new ball's cells were built in its first step, so it rests on the floor rather than falling through.
        Assert.Equal(2.1f, Pose(dropped).Position.Y, 0.01f);
    }

    [Fact]
    public void Every_static_holds_its_own_cells_boxes_after_cells_are_rebuilt()
    {
        // Bepu's Statics.SetShape (2.5.0-beta.29) moves a static's bounds but keeps its old shape, which the rebuild then
        // freed: the static read another cell's boxes or freed memory (a crash in the sweep of a thrown ball, 2026-09-25).
        AddSphere(new Vector3(0.5f, 2.3f, 0.5f), 0.1f);
        Run(120);

        // The pit spans several cells, so one update rebuilds several.
        _world.Apply(VoxelEdit.Box(new VoxelCoord(30, 160, 30), new VoxelCoord(40, 40, 40), VoxelEditOperation.Clear));
        _colliders.Update(Step);
        AssertStaticsMatchTheirCells();
        _world.Apply(VoxelEdit.Sphere(new VoxelCoord(40, 200, 60), 20, VoxelEditOperation.Clear));
        Run(60);
        AssertStaticsMatchTheirCells();
    }

    [Fact]
    public void A_fast_ball_does_not_pass_through_a_wall_1_voxel_thick()
    {
        // 40 m/s moves 67 cm a step; the wall at x = 1 m is 1 cm thick.
        var ball = AddSphere(new Vector3(0.3f, 2.5f, 0), 0.05f, new Vector3(40, 0, 0), continuous: true);

        Run(20);

        Assert.True(Pose(ball).Position.X < 1, $"The ball went through the wall to {Pose(ball).Position}.");
    }

    [Fact]
    public void A_ball_falls_through_leaves_to_the_floor()
    {
        var ball = AddSphere(new Vector3(-1.25f, 2.8f, 0), 0.1f);

        Run(120);

        Assert.Equal(2.1f, Pose(ball).Position.Y, 0.01f);
    }

    [Fact]
    public void Statics_are_dropped_a_second_after_no_body_needs_them()
    {
        var ball = AddSphere(new Vector3(0, 2.5f, 0), 0.1f);
        Run(10);
        var built = _colliders.StaticCount;

        _colliders.Forget(ball);
        _physics.Simulation.Bodies.Remove(ball);
        Run(VoxelColliders.IdleSteps);
        var kept = _colliders.StaticCount;
        Run(1);

        Assert.True(built > 0);
        Assert.Equal(built, kept);
        Assert.Equal(0, _colliders.StaticCount);
        Assert.Equal(0, _colliders.BoxCount);
    }

    [Fact]
    public void A_body_freezes_where_columns_are_not_resident_and_moves_on_when_they_load()
    {
        // 20 m away, far outside the 4 m that is loaded.
        var ball = AddSphere(new Vector3(20, 2.5f, 0), 0.1f, new Vector3(1, 0, 0));
        Run(30);
        var frozen = _colliders.IsFrozen(ball);
        var held = Pose(ball).Position;

        _world.SetCenters([new Vector3(20, 2, 0)]);
        TestWorlds.WaitUntilLoaded(_world);
        Run(120);

        Assert.True(frozen);
        Assert.Equal(20, held.X, 0.02f);
        Assert.Equal(2.5f, held.Y, 0.02f);
        Assert.False(_colliders.IsFrozen(ball));
        // It kept its speed while frozen and came to rest on the floor there.
        Assert.True(Pose(ball).Position.X > 20.3f, $"The ball is at {Pose(ball).Position}.");
        Assert.Equal(2.1f, Pose(ball).Position.Y, 0.01f);
    }

    [Fact]
    public void Columns_that_unload_take_their_statics_with_them()
    {
        var ball = AddSphere(new Vector3(0, 2.5f, 0), 0.1f);
        Run(60);
        var built = _colliders.StaticCount;

        _world.SetCenters([new Vector3(40, 2, 0)]);
        TestWorlds.WaitUntilLoaded(_world);
        for (var i = 0; i < 10; i++)
        {
            _world.Update(0);
        }

        Run(1);

        Assert.True(built > 0);
        Assert.Equal(0, _colliders.StaticCount);
        Assert.True(_colliders.IsFrozen(ball));
    }

    [Fact]
    public void After_falling_behind_the_change_list_every_cell_is_rebuilt()
    {
        var ball = AddSphere(new Vector3(0.5f, 2.3f, 0.5f), 0.1f);
        Run(120);
        var builtBefore = _colliders.CellsBuilt;

        // More brick changes than the change list keeps, far from the ball, then a pit under it: small edits, each
        // recorded brick by brick (a large one is recorded once per chunk and would not overrun it).
        var start = _world.ChangeCount;
        for (var i = 0; _world.ChangeCount - start <= 70_000; i++)
        {
            Assert.True(i < 20_000, "The edits did not fill the change list.");
            var centre = new VoxelCoord(-250, 150, -200);
            _world.Apply(VoxelEdit.Sphere(centre, 12, VoxelEditOperation.Clear));
            _world.Apply(VoxelEdit.Sphere(centre, 12, VoxelEditOperation.Set, BoxesGenerator.Stone));
        }

        _world.Apply(VoxelEdit.Box(new VoxelCoord(30, 160, 30), new VoxelCoord(40, 40, 40), VoxelEditOperation.Clear));
        Run(60);

        Assert.True(_colliders.CellsBuilt > builtBefore);
        Assert.Equal(1.7f, Pose(ball).Position.Y, 0.02f);
    }

    [Fact]
    public void Updating_around_a_resting_body_allocates_nothing()
    {
        AddSphere(new Vector3(0, 2.5f, 0), 0.1f);
        Run(120);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            _colliders.Update(Step);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
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

    /// <summary>Each static is a live compound with as many boxes as the voxels of the cell at its corner give now.</summary>
    private void AssertStaticsMatchTheirCells()
    {
        var simulation = _physics.Simulation;
        var boxes = new VoxelBoxes();
        var passable = MaterialSet.Passable(new MoverWorldFixture.LeavesPass());
        Assert.True(simulation.Statics.Count > 1, "Too few statics to test.");
        for (var i = 0; i < simulation.Statics.Count; i++)
        {
            var reference = simulation.Statics[simulation.Statics.IndexToHandle[i]];
            Assert.Equal(BigCompound.TypeId, reference.Shape.Type);
            var compound = simulation.Shapes.GetShape<BigCompound>(reference.Shape.Index);
            Assert.True(compound.Tree.Nodes.Allocated, $"Static {i} holds a freed compound.");
            var corner = reference.Pose.Position * VoxelConstants.CentimetresPerMetre;
            int x0 = (int)MathF.Round(corner.X),
                y0 = (int)MathF.Round(corner.Y),
                z0 = (int)MathF.Round(corner.Z),
                size = VoxelColliders.CellSize;
            boxes.Build(_world, x0, y0, z0, x0 + size - 1, y0 + size - 1, z0 + size - 1, in passable);
            Assert.True(
                boxes.Count == compound.ChildCount,
                $"The static at ({x0}, {y0}, {z0}) has {compound.ChildCount} boxes; its cell gives {boxes.Count}."
            );
        }
    }

    private BodyHandle AddSphere(Vector3 position, float radius, Vector3 velocity = default, bool continuous = false)
    {
        var sphere = new Sphere(radius);
        var simulation = _physics.Simulation;
        var shape = simulation.Shapes.Add(sphere);
        var continuity = continuous ? ContinuousDetection.Continuous(1e-3f, 1e-3f) : ContinuousDetection.Discrete;
        return simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(position),
                new BodyVelocity(velocity),
                sphere.ComputeInertia(1),
                new CollidableDescription(shape, 0.1f, continuity),
                new BodyActivityDescription(0.01f)
            )
        );
    }

    private BodyHandle AddBox(Vector3 position, Vector3 size, Quaternion orientation)
    {
        var box = new Box(size.X, size.Y, size.Z);
        var simulation = _physics.Simulation;
        var shape = simulation.Shapes.Add(box);
        return simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(position, orientation),
                box.ComputeInertia(1),
                new CollidableDescription(shape, 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
    }
}
