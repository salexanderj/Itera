using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A body at rest (<see cref="VoxelCharacterMover.StandsUnchanged"/>) over 1 m voxels in the mover's 10 cm cells, among
/// mesh colliders and voxel objects with their damage: a collider or an object placed or removed away from it, and an
/// area of objects filled or dropped away from it, keep its rest, and the same near it end it; without the objects'
/// damage any change of the objects ends it; changes past those kept end it; and every rest kept through random
/// changes answers as the full move does.
/// </summary>
public sealed class MoverRestTests : IDisposable
{
    private const byte Stone = 40;
    private const float Step = 1f / 60;
    private const float Gravity = 9.81f;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.4f);

    private readonly VoxelWorld _world;
    private readonly VoxelModels _models;
    private readonly ObjectWorld _objects;
    private readonly ObjectDamage _damage;
    private readonly PhysicsWorld _physics;
    private readonly MeshColliders _colliders;
    private readonly VoxelObjectKinds _kinds = new();
    private readonly NothingPassable _passability = new();
    private readonly VoxelObjectKindId _block;
    private readonly VoxelCharacterMover _mover;

    public MoverRestTests()
    {
        // 1 m square, 30 cm high, of 10 cm voxels, its anchor its bottom's middle.
        var shape = new VoxelObjectShape(10, new VoxelCoord(10, 3, 10), new VoxelCoord(50, 0, 50));
        shape.FillBox(0, 0, 0, 100, 30, 100, Stone);
        _block = _kinds.Register("block", shape);
        var generator = new FlatTestGenerator(new VoxelGrid(100));
        _world = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 20,
                Levels = 1,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 2,
                Seed = 17,
            },
            0,
            generator,
            NullLogger<VoxelWorld>.Instance,
            kinds: _kinds
        );
        _world.Initialize();
        _world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(_world);
        _models = new VoxelModels(_world);
        var options = Options.Create(new EngineOptions());
        _objects = new ObjectWorld(_world, _kinds, generator, options, NullLogger<ObjectWorld>.Instance);
        var kindModels = new VoxelObjects(_kinds, _world, _models, _passability, NullLogger<VoxelObjects>.Instance);
        _damage = new ObjectDamage(
            _world,
            _objects,
            _kinds,
            kindModels,
            _models,
            options,
            NullLogger<ObjectDamage>.Instance
        );
        kindModels.Initialize();
        _objects.Initialize();
        _damage.Initialize();
        FillAreas();
        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
        _mover = new VoxelCharacterMover(_world, 10, _passability, meshes: _colliders)
        {
            Objects = new ObjectColumns(_objects, _kinds, _passability, 10, _damage),
        };
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _damage.Dispose();
        _models.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_mesh_collider_added_or_removed_away_from_a_body_keeps_its_rest_and_one_round_it_ends_it()
    {
        var feet = Rested(Vector3.Zero, out var stamp);

        var far = Crate(new Vector3(9, 0, 9));
        Assert.True(_mover.StandsUnchanged(ref stamp, feet, in s_shape));
        _colliders.Remove(far);
        Assert.True(_mover.StandsUnchanged(ref stamp, feet, in s_shape));

        Crate(new Vector3(0.9f, 0, 0));
        Assert.False(_mover.StandsUnchanged(ref stamp, feet, in s_shape));
    }

    [Fact]
    public void An_object_placed_or_removed_away_from_a_body_keeps_its_rest_and_one_round_it_ends_it()
    {
        var feet = Rested(Vector3.Zero, out var stamp);

        Assert.True(_objects.TryPlace(_block, new VoxelCoord(900, 0, -900), 0, VoxelEdit.NoMaker, out var far));
        Assert.True(_mover.StandsUnchanged(ref stamp, feet, in s_shape));
        Assert.True(_objects.TryRemove(in far, VoxelEdit.NoMaker));
        Assert.True(_mover.StandsUnchanged(ref stamp, feet, in s_shape));

        Assert.True(_objects.TryPlace(_block, new VoxelCoord(120, 0, 0), 0, VoxelEdit.NoMaker, out _));
        Assert.False(_mover.StandsUnchanged(ref stamp, feet, in s_shape));
    }

    [Fact]
    public void An_area_of_objects_filled_or_dropped_away_from_a_body_keeps_its_rest()
    {
        // The map's four areas of objects are 512 m wide and meet at its middle: the body stands alone in the middle of one.
        var home = new Vector3(-256, 0, -256);
        _world.SetCenters([home]);
        TestWorlds.WaitUntilLoaded(_world);
        FillAreas();
        Assert.Equal(1, _objects.AreaCount);
        var feet = Rested(home, out var stamp);

        // Columns in the area across the middle, loaded and filled, then unloaded and dropped.
        var version = _objects.Version;
        _world.SetCenters([home, -home]);
        TestWorlds.WaitUntilLoaded(_world);
        FillAreas();
        Assert.Equal(2, _objects.AreaCount);
        Assert.NotEqual(version, _objects.Version);
        Assert.True(_mover.StandsUnchanged(ref stamp, feet, in s_shape));

        version = _objects.Version;
        _world.SetCenters([home]);
        TestWorlds.WaitUntilLoaded(_world);
        FillAreas();
        Assert.Equal(1, _objects.AreaCount);
        Assert.NotEqual(version, _objects.Version);
        Assert.True(_mover.StandsUnchanged(ref stamp, feet, in s_shape));
    }

    /// <summary>
    /// An object placed beside a resting body while its area was dropped, read once the area fills again, ends the rest:
    /// the damage names no change for an area that is not filled.
    /// </summary>
    [Fact]
    public void An_object_placed_round_a_body_while_its_area_was_dropped_ends_the_rest()
    {
        var home = new Vector3(-256, 0, -256);
        _world.SetCenters([home]);
        TestWorlds.WaitUntilLoaded(_world);
        FillAreas();
        var feet = Rested(home, out var stamp);

        _world.SetCenters([-home]);
        TestWorlds.WaitUntilLoaded(_world);
        FillAreas();
        Assert.False(_objects.IsKnown(-25_700, -25_500, -25_700, -25_500));
        var anchor = new VoxelCoord(-25_600 + 120, 0, -25_600);
        Assert.True(_objects.TryPlace(_block, anchor, 0, VoxelEdit.NoMaker, out _));
        _world.SetCenters([home]);
        TestWorlds.WaitUntilLoaded(_world);
        FillAreas();

        Assert.False(_mover.StandsUnchanged(ref stamp, feet, in s_shape));
    }

    /// <summary>A physics material given to a collider under a resting body ends its rest, which kept the old one.</summary>
    [Fact]
    public void A_collider_given_another_material_round_a_body_ends_its_rest()
    {
        var materials = new PhysicsMaterials();
        var ice = materials.Register("ice", new PhysicsMaterial(0.05f, 0));
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, materials: materials);
        physics.Initialize();
        using var colliders = new MeshColliders(physics, NullLogger<MeshColliders>.Instance);
        var mover = new VoxelCharacterMover(_world, 10, _passability, meshes: colliders)
        {
            Objects = new ObjectColumns(_objects, _kinds, _passability, 10, _damage),
        };
        var floor = new TestTriangles().Ground(-1, -1, 1, 1, 0.05f).AddTo(colliders);
        var far = new TestTriangles().Ground(9, 9, 10, 10, 0.05f).AddTo(colliders);
        var feet = Rested(mover, new Vector3(0, 0.2f, 0), out var stamp);

        colliders.SetSettings(far, new PhysicsBodySettings { Material = ice });
        Assert.True(mover.StandsUnchanged(ref stamp, feet, in s_shape));
        colliders.SetSettings(floor, new PhysicsBodySettings { Material = ice });
        Assert.False(mover.StandsUnchanged(ref stamp, feet, in s_shape));
    }

    [Fact]
    public void Without_the_objects_damage_an_object_placed_anywhere_ends_the_rest()
    {
        var mover = new VoxelCharacterMover(_world, 10, _passability, meshes: _colliders)
        {
            Objects = new ObjectColumns(_objects, _kinds, _passability, 10),
        };
        var feet = Rested(mover, Vector3.Zero, out var stamp);

        Assert.True(_objects.TryPlace(_block, new VoxelCoord(900, 0, -900), 0, VoxelEdit.NoMaker, out _));
        Assert.False(mover.StandsUnchanged(ref stamp, feet, in s_shape));
    }

    [Fact]
    public void Collider_changes_past_those_kept_end_the_rest()
    {
        var feet = Rested(Vector3.Zero, out var stamp);
        for (var i = 0; i <= MeshColliders.KeptChanges / 2; i++)
        {
            _colliders.Remove(Crate(new Vector3(9, 0, 9)));
        }

        Assert.False(_mover.StandsUnchanged(ref stamp, feet, in s_shape));
    }

    /// <summary>
    /// Bodies on a grid 1.5 m apart, with crates and blocks placed and removed at random round them, near and far, and
    /// now and then a small crater dug: each time a body's rest is kept, the full move it skips answers the same.
    /// </summary>
    [Fact]
    public void Every_rest_kept_through_random_changes_answers_as_the_full_move_does()
    {
        var random = new Random(23);
        var bodies = new List<Body>();
        for (var x = -3; x <= 3; x++)
        {
            for (var z = -3; z <= 3; z++)
            {
                bodies.Add(new Body { Feet = new Vector3(x * 1.5f, 0.2f, z * 1.5f) });
            }
        }

        var crates = new List<int>();
        var blocks = new List<ObjectPlacement>();
        int kept = 0,
            ended = 0;
        for (var step = 0; step < 400; step++)
        {
            var at = new Vector3(random.NextSingle() * 24 - 12, 0, random.NextSingle() * 24 - 12);
            switch (random.Next(8))
            {
                case 0:
                case 1:
                    crates.Add(Crate(at));
                    break;
                case 2 when crates.Count > 0:
                    var crate = random.Next(crates.Count);
                    _colliders.Remove(crates[crate]);
                    crates.RemoveAt(crate);
                    break;
                case 3:
                case 4:
                    var anchor = new VoxelCoord((int)(at.X * 100), 0, (int)(at.Z * 100));
                    if (
                        _objects.TryPlace(
                            _block,
                            anchor,
                            random.Next(ObjectPlacement.HeadingSteps),
                            VoxelEdit.NoMaker,
                            out var block
                        )
                    )
                    {
                        blocks.Add(block);
                    }

                    break;
                case 5 when blocks.Count > 0:
                    var removed = random.Next(blocks.Count);
                    Assert.True(_objects.TryRemove(blocks[removed], VoxelEdit.NoMaker));
                    blocks.RemoveAt(removed);
                    break;
                case 6 when step % 40 == 0:
                    var centre = new VoxelCoord((int)(at.X * 100), 0, (int)(at.Z * 100));
                    _world.Apply(VoxelEdit.Sphere(centre, 60, VoxelEditOperation.Clear));
                    _world.ApplyPendingEdits(64);
                    break;
            }

            foreach (var body in bodies)
            {
                if (body.Resting && _mover.StandsUnchanged(ref body.Stamp, body.Feet, in s_shape))
                {
                    var feet = body.Feet;
                    var velocity = new Vector3(0, -Gravity * Step, 0);
                    var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
                    if (!Rests(result, feet, body.Feet, velocity) || result.GroundMaterial != body.Material)
                    {
                        Assert.Fail(
                            $"Step {step}: the rest at {body.Feet} was kept, but the full move gave {result} to {feet}."
                        );
                    }

                    kept++;
                    continue;
                }

                ended += body.Resting ? 1 : 0;
                Move(_mover, body);
            }
        }

        Assert.True(kept > 10_000 && ended > 20, $"{kept} rests kept, {ended} ended.");
    }

    /// <summary>A crate of 60 cm on each side standing on the ground at <paramref name="at"/>; its collider.</summary>
    private int Crate(Vector3 at) =>
        new TestTriangles()
            .Box(new Vector3(at.X - 0.3f, -0.1f, at.Z - 0.3f), new Vector3(at.X + 0.3f, 0.5f, at.Z + 0.3f))
            .AddTo(_colliders);

    private Vector3 Rested(Vector3 feet, out GroundStamp stamp) => Rested(_mover, feet, out stamp);

    /// <summary>A body dropped onto the ground at <paramref name="feet"/>, moved until it rests; its feet and the stamp.</summary>
    private static Vector3 Rested(VoxelCharacterMover mover, Vector3 feet, out GroundStamp stamp)
    {
        var body = new Body { Feet = feet };
        for (var i = 0; i < 120 && !body.Resting; i++)
        {
            Move(mover, body);
        }

        Assert.True(body.Resting, $"The body at {body.Feet} did not come to rest.");
        stamp = body.Stamp;
        return body.Feet;
    }

    /// <summary>
    /// A full move with nothing across, gravity's one step down, as <c>PlayerMovementSystem</c> makes it; the body rests
    /// when it stood still on walkable ground, with the stamp taken after it.
    /// </summary>
    private static void Move(VoxelCharacterMover mover, Body body)
    {
        var before = body.Feet;
        var velocity = new Vector3(0, body.Resting ? -Gravity * Step : body.Fall - Gravity * Step, 0);
        var result = mover.Move(ref body.Feet, ref velocity, in s_shape, Step);
        body.Fall = velocity.Y;
        body.Resting = Rests(result, body.Feet, before, velocity);
        if (body.Resting)
        {
            body.Material = result.GroundMaterial;
            body.Stamp = mover.Stamp();
        }
    }

    /// <summary>The rule of <c>PlayerMovementSystem</c>'s rest: the move stood still, grounded on walkable ground.</summary>
    private static bool Rests(in CharacterMoveResult result, Vector3 feet, Vector3 before, Vector3 velocity) =>
        velocity == Vector3.Zero
        && feet == before
        && result.Status == CharacterMoveStatus.Moved
        && result.Grounded
        && !result.Steep
        && !result.HitWall
        && result.SteppedMetres == 0;

    /// <summary>Has the objects read the world's columns, and fills the areas waiting.</summary>
    private void FillAreas()
    {
        for (var i = 0; i == 0 || (i < 100 && _objects.WaitingAreas > 0); i++)
        {
            _objects.Update();
            _damage.Update();
        }
    }

    private sealed class Body
    {
        public Vector3 Feet;
        public float Fall;
        public bool Resting;
        public PhysicsMaterialId Material;
        public GroundStamp Stamp;
    }
}
