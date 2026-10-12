using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Objects;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The voxel objects as statics near bodies (<see cref="VoxelColliders"/>, made before the objects' world is initialised,
/// as a match makes them): a ball rests on a rock's top, a ball rolled at a trunk stops at it, an object is turned by its
/// heading (a plank anchored at one end), passable leaves give no boxes, an object of two physics materials has a static
/// of each, a body waits frozen until the objects round it are known, an object's statics go with the cells that reach it
/// and stay while the objects change, a body cut off a character stops on a trunk, a kind's boxes cover its blocking
/// voxels once, and steps among objects allocate nothing.
/// </summary>
public sealed class ObjectCollidersTests : IDisposable
{
    private const float Step = 1f / 60;
    private const float Radius = 0.1f;
    private const byte Bark = 30;
    private const byte Leaves = 31;
    private const byte Rock = 40;
    private readonly VoxelObjectKinds _kinds = new();
    private readonly PhysicsMaterials _materials = new();
    private readonly List<ObjectPlacement> _placements = [];
    private readonly VoxelWorld _world;
    private readonly ObjectWorld _objects;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;

    public ObjectCollidersTests()
    {
        // A trunk 50 cm across, a rock whose top is at 50 cm, a bush of leaves, a plank 2 m long and 30 cm wide 80 to
        // 100 cm up, from its anchor along its own +x, turned a quarter (along the world's +z), and a stump of bark with a
        // cap of rock, its top at 60 cm; each but the trunk and the rock with no other within a cell of it.
        var trunk = Register(
            "trunk",
            5,
            new VoxelCoord(12, 64, 12),
            new VoxelCoord(30, 0, 30),
            s => s.FillCapsule(30, -10, 30, 30, 300, 30, 25, Bark)
        );
        var rock = Register(
            "rock",
            5,
            new VoxelCoord(26, 12, 22),
            new VoxelCoord(65, 0, 55),
            s => s.FillBox(5, -10, 5, 125, 50, 105, Rock)
        );
        var bush = Register(
            "bush",
            4,
            new VoxelCoord(32, 30, 32),
            new VoxelCoord(64, 0, 64),
            s => s.FillEllipsoid(64, 50, 64, 60, 50, 60, Leaves)
        );
        var plank = Register(
            "plank",
            5,
            new VoxelCoord(40, 20, 6),
            new VoxelCoord(0, 0, 15),
            s => s.FillBox(0, 80, 0, 200, 100, 30, Rock)
        );
        var stump = Register(
            "stump",
            5,
            new VoxelCoord(10, 12, 10),
            new VoxelCoord(25, 0, 25),
            s =>
            {
                s.FillBox(0, -10, 0, 50, 50, 50, Bark);
                s.FillBox(0, 50, 0, 50, 60, 50, Rock);
            }
        );
        _placements.Add(new ObjectPlacement(new ObjectId(1), trunk, new VoxelCoord(150, 0, 0), 0));
        _placements.Add(new ObjectPlacement(new ObjectId(2), rock, new VoxelCoord(-300, 0, 300), 700));
        _placements.Add(new ObjectPlacement(new ObjectId(3), bush, new VoxelCoord(-800, 0, -800), 0));
        _placements.Add(new ObjectPlacement(new ObjectId(4), plank, new VoxelCoord(800, 0, -800), 1024));
        _placements.Add(new ObjectPlacement(new ObjectId(5), stump, new VoxelCoord(800, 0, 800), 0));
        var rough = _materials.Register("rough", new PhysicsMaterial(2, 0));
        _materials.MapVoxel(Rock, rough);
        var generator = new PlacingGenerator(_placements);
        _world = TestWorlds.Load(generator, Vector3.Zero, radiusMetres: 0);
        _objects = new ObjectWorld(
            _world,
            _kinds,
            generator,
            Options.Create(new EngineOptions()),
            NullLogger<ObjectWorld>.Instance
        );
        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, null, _materials);
        _physics.Initialize();
        // As a match makes them: the colliders first, the objects' world initialised at its later step.
        _colliders = new VoxelColliders(
            _physics,
            _world,
            new LeavesPass(),
            NullLogger<VoxelColliders>.Instance,
            _objects,
            _kinds
        );
        _colliders.Initialize();
        _objects.Initialize();
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_ball_dropped_on_a_rock_rests_on_its_top()
    {
        Fill();
        var ball = Drop(new Vector3(-3, 1.5f, 3));

        Run(120);

        // The rock's top voxels end at 50 cm.
        Assert.Equal(0.5f + Radius, Position(ball).Y, 0.005f);
        Assert.True(_colliders.ObjectCount >= 1);
    }

    [Fact]
    public void A_ball_rolled_at_a_trunk_stops_at_it()
    {
        Fill();
        var ball = Drop(new Vector3(0, Radius, 0), new Vector3(3, 0, 0));

        var farthest = 0f;
        for (var i = 0; i < 120; i++)
        {
            Run(1);
            farthest = MathF.Max(farthest, Position(ball).X);
        }

        // The trunk's voxels end 25 cm short of its axis at 1.5 m.
        Assert.InRange(farthest, 1.25f - Radius - 0.03f, 1.25f - Radius + 0.01f);
        Assert.True(Position(ball).X < farthest, "It did not come back off the trunk.");
    }

    /// <summary>
    /// The plank turned a quarter lies from its anchor along the world's +z: a ball on it rests on it; one where it would
    /// lie unturned, or turned the other way, falls past.
    /// </summary>
    [Fact]
    public void An_object_is_turned_by_its_heading()
    {
        Fill();
        var on = Drop(new Vector3(8, 2, -6.5f));
        var unturned = Drop(new Vector3(9.5f, 2, -8));
        var mirrored = Drop(new Vector3(8, 2, -9.5f));

        Run(150);

        Assert.Equal(1 + Radius, Position(on).Y, 0.005f);
        Assert.Equal(Radius, Position(unturned).Y, 0.005f);
        Assert.Equal(Radius, Position(mirrored).Y, 0.005f);
    }

    [Fact]
    public void Passable_leaves_give_no_boxes()
    {
        Fill();
        var ball = Drop(new Vector3(-8, 1.5f, -8));

        Run(120);

        Assert.Equal(Radius, Position(ball).Y, 0.005f);
        Assert.Equal((1, 0), (_colliders.ObjectCount, _colliders.ObjectStaticCount));
    }

    /// <summary>The stump's bark and its cap of rough rock are a static each; a ball rests on the cap.</summary>
    [Fact]
    public void An_object_of_two_physics_materials_has_a_static_of_each()
    {
        Fill();
        var ball = Drop(new Vector3(8, 1.5f, 8));

        Run(120);

        Assert.Equal(0.6f + Radius, Position(ball).Y, 0.005f);
        Assert.Equal((1, 2), (_colliders.ObjectCount, _colliders.ObjectStaticCount));
    }

    /// <summary>
    /// When the objects change (areas filled far away, as the world streams round a second centre), every cell finds its
    /// objects again: the rock under a resting ball keeps its static, and the ball stays where it rests.
    /// </summary>
    [Fact]
    public void An_objects_statics_stay_while_the_objects_change()
    {
        Fill();
        var ball = Drop(new Vector3(-3, 1.5f, 3));
        Run(120);
        var rested = Position(ball);
        var statics = _colliders.ObjectStaticCount;
        var version = _objects.Version;

        _world.SetCenters([Vector3.Zero, new Vector3(150, 0, 0)]);
        TestWorlds.WaitUntilLoaded(_world);
        for (var i = 0; i < 100 && _objects.Version == version; i++)
        {
            _world.Update(0);
            _objects.Update();
            Run(1);
        }

        Assert.NotEqual(version, _objects.Version);
        Run(30);
        Assert.Equal(statics, _colliders.ObjectStaticCount);
        Assert.True(Vector3.Distance(rested, Position(ball)) < 0.002f, $"It moved from {rested} to {Position(ball)}.");
    }

    /// <summary>Over areas not filled yet a body waits frozen, then falls onto the rock once they are.</summary>
    [Fact]
    public void A_body_waits_frozen_until_the_objects_round_it_are_known()
    {
        var ball = Drop(new Vector3(-3, 1.5f, 3));

        Run(30);
        Assert.True(_colliders.IsFrozen(ball));
        Assert.Equal(1.5f, Position(ball).Y, 1e-4f);

        Fill();
        Run(120);
        Assert.False(_colliders.IsFrozen(ball));
        Assert.Equal(0.5f + Radius, Position(ball).Y, 0.005f);
    }

    /// <summary>An object's statics stay while a cell near a body reaches it and go with the last such cell.</summary>
    [Fact]
    public void An_objects_statics_go_with_the_cells_that_reach_it()
    {
        Fill();
        var statics = _colliders.StaticCount;
        var ball = Drop(new Vector3(-3, 1.5f, 3));
        Run(60);
        Assert.Equal(1, _colliders.ObjectCount);
        Assert.True(_colliders.StaticCount > statics);

        _physics.RemoveBody(ball);
        Run(VoxelColliders.IdleSteps + 2);

        Assert.Equal(0, _colliders.ObjectCount);
        Assert.Equal(0, _colliders.StaticCount);
    }

    /// <summary>A body cut off a character touches only the voxels: the objects' statics are among them.</summary>
    [Fact]
    public void A_cut_body_stops_on_a_trunk()
    {
        Fill();
        var cut = Drop(new Vector3(0, 1, 0), new Vector3(3, 0, 0));
        _physics.Kinds.Set(cut, BodyKind.Cut, 0, 0);

        var farthest = 0f;
        for (var i = 0; i < 90; i++)
        {
            Run(1);
            farthest = MathF.Max(farthest, Position(cut).X);
        }

        Assert.True(farthest < 1.25f - Radius + 0.01f, $"It reached {farthest}.");
    }

    /// <summary>
    /// The boxes of a kind two parts wide (112 voxels and 18, the last not a whole number of bricks) cover each of its
    /// voxels that blocks once and nothing else, leaves left out.
    /// </summary>
    [Fact]
    public void A_kinds_boxes_cover_its_blocking_voxels_once()
    {
        var shape = new VoxelObjectShape(2, new VoxelCoord(130, 24, 20), new VoxelCoord(130, 0, 20));
        shape.FillBox(0, 0, 0, 260, 30, 40, Rock);
        shape.FillEllipsoid(200, 30, 20, 50, 16, 14, Leaves);
        shape.FillCapsule(40, 0, 20, 40, 48, 20, 9, Bark);
        var kind = _kinds[_kinds.Register("wide", shape)];
        Assert.Equal(2, kind.PartCount);
        var boxes = new VoxelBoxes();

        boxes.Build(kind, null, MaterialSet.Passable(new LeavesPass()));

        var covered = new int[130 * 24 * 20];
        foreach (var box in boxes.Boxes)
        {
            for (var z = box.Z0; z <= box.Z1; z++)
            {
                for (var y = box.Y0; y <= box.Y1; y++)
                {
                    for (var x = box.X0; x <= box.X1; x++)
                    {
                        covered[x + 130 * (y + 24 * z)]++;
                    }
                }
            }
        }

        for (var z = 0; z < 20; z++)
        {
            for (var y = 0; y < 24; y++)
            {
                for (var x = 0; x < 130; x++)
                {
                    var material = shape.MaterialAt(x, y, z);
                    var expected = material is not VoxelConstants.Air and not Leaves ? 1 : 0;
                    Assert.True(
                        covered[x + 130 * (y + 24 * z)] == expected,
                        $"Voxel ({x}, {y}, {z}) of {material} is covered {covered[x + 130 * (y + 24 * z)]} times."
                    );
                }
            }
        }
    }

    [Fact]
    public void Steps_among_objects_allocate_nothing()
    {
        Fill();
        Drop(new Vector3(-3, 1.5f, 3));
        Drop(new Vector3(0, Radius, 0), new Vector3(3, 0, 0));
        Run(120);

        var start = Itera.TestSupport.SystemAllocation.StartMeasurement();
        Run(60);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    private VoxelObjectKindId Register(
        string name,
        int unit,
        VoxelCoord size,
        VoxelCoord anchor,
        Action<VoxelObjectShape> fill
    )
    {
        var shape = new VoxelObjectShape(unit, size, anchor);
        fill(shape);
        return _kinds.Register(name, shape);
    }

    /// <summary>Fills the objects' areas round the loaded world.</summary>
    private void Fill()
    {
        for (var i = 0; i < 100 && _objects.WaitingAreas > 0; i++)
        {
            _objects.Update();
        }
    }

    private BodyHandle Drop(Vector3 at, Vector3 velocity = default)
    {
        var sphere = new Sphere(Radius);
        return _physics.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(at),
                new BodyVelocity(velocity),
                sphere.ComputeInertia(1),
                new CollidableDescription(_physics.Simulation.Shapes.Add(sphere), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
    }

    private void Run(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            _colliders.Update(Step);
            _physics.Step(Step);
        }
    }

    private Vector3 Position(BodyHandle body) => _physics.Simulation.Bodies[body].Pose.Position;

    private sealed class LeavesPass : IVoxelPassability
    {
        public bool IsPassable(byte material) => material == Leaves;
    }
}
