using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Objects;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The voxel objects as the character mover's ground (<see cref="ObjectColumns"/>): its cells are those whose centres,
/// turned into an object, land on its solid voxels that are not passable, against a cell-by-cell reference; a character
/// walking into a trunk is stopped by it, lands on a rock and reads its material and tags, steps up onto a low one,
/// passes through a bush of passable leaves, and waits where the objects' areas are not filled; and moves among objects
/// allocate nothing.
/// </summary>
public sealed class ObjectColumnsTests : IDisposable
{
    private const byte Bark = 30;
    private const byte Leaves = 31;
    private const byte Rock = 40;
    private const float Step = 1f / 60;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.4f);
    private readonly VoxelObjectKinds _kinds = new();
    private readonly Dictionary<VoxelObjectKindId, VoxelObjectShape> _shapes = [];
    private readonly List<ObjectPlacement> _placements = [];
    private readonly VoxelWorld _world;
    private readonly ObjectWorld _objects;
    private readonly PhysicsMaterials _materials = new();
    private readonly LeavesPass _passability = new();

    public ObjectColumnsTests()
    {
        // A trunk 50 cm across and 3 m tall, a rock 1.2 m long, 1 m wide and 50 cm high, its top flat from 40 cm, a low
        // stone 1 m across and 30 cm high (under a step), and a bush of leaves 1.2 m across.
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
        var stone = Register(
            "stone",
            5,
            new VoxelCoord(22, 8, 22),
            new VoxelCoord(55, 0, 55),
            s => s.FillBox(5, -10, 5, 105, 30, 105, Rock)
        );
        var bush = Register(
            "bush",
            4,
            new VoxelCoord(32, 30, 32),
            new VoxelCoord(64, 0, 64),
            s => s.FillEllipsoid(64, 50, 64, 60, 50, 60, Leaves)
        );
        _placements.Add(new ObjectPlacement(new ObjectId(1), trunk, new VoxelCoord(150, 0, 0), 0));
        _placements.Add(new ObjectPlacement(new ObjectId(2), rock, new VoxelCoord(-300, 0, 300), 700));
        _placements.Add(new ObjectPlacement(new ObjectId(3), stone, new VoxelCoord(300, 0, 300), 0));
        _placements.Add(new ObjectPlacement(new ObjectId(4), bush, new VoxelCoord(0, 0, -300), 0));
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
        _objects.Initialize();
        for (var i = 0; i < 100 && _objects.WaitingAreas > 0; i++)
        {
            _objects.Update();
        }
    }

    public void Dispose() => _world.Dispose();

    /// <summary>
    /// Every column round each object holds a blocked cell exactly where the cell's centre, turned into the object, lands on
    /// a solid voxel that is not passable; the highest such cell is the column's top.
    /// </summary>
    [Fact]
    public void Its_cells_are_those_whose_centres_land_on_an_objects_solid_voxels()
    {
        var columns = Columns();
        var tops = new int[60 * 60];
        foreach (var placement in _placements)
        {
            int x0 = placement.Anchor.X / 10 - 30,
                z0 = placement.Anchor.Z / 10 - 30;
            columns.ColumnTops(x0, z0, 60, 60, -5, 40, tops);
            for (var z = 0; z < 60; z++)
            {
                for (var x = 0; x < 60; x++)
                {
                    var expected = int.MinValue;
                    for (var y = -5; y <= 40; y++)
                    {
                        if (ReferenceBlocked(x0 + x, y, z0 + z))
                        {
                            expected = y + 1;
                        }
                    }

                    Assert.Equal(expected, tops[x + 60 * z]);
                }
            }
        }
    }

    [Fact]
    public void A_character_walking_into_a_trunk_is_stopped_by_it()
    {
        var mover = Mover();
        var feet = new Vector3(0, 0, 0);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 60; i++)
        {
            velocity = new Vector3(3, velocity.Y - 0.3f, 0);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        // The trunk's side lies 25 cm short of its axis at x = 1.5 m: the cell from 1.2 to 1.3 m holds it (its centre,
        // 25 cm from the axis, lands on a voxel whose centre lies 22.5 cm out), so the body stops its radius short of 1.2.
        Assert.InRange(feet.X, 1.2f - s_shape.RadiusMetres - 0.02f, 1.2f - s_shape.RadiusMetres + 0.001f);
        Assert.Equal(0, feet.Y, 0.005f);
    }

    /// <summary>A character dropped over the rock lands on its top and reads its physics material and tags.</summary>
    [Fact]
    public void A_character_lands_on_a_rock_and_reads_its_material_and_tags()
    {
        var mover = Mover();
        var feet = new Vector3(-3, 3, 3);
        var velocity = Vector3.Zero;

        CharacterMoveResult result = default;
        for (var i = 0; i < 120; i++)
        {
            velocity.Y -= 20 * Step;
            result = mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        // The rock's top voxels end at 50 cm (the box's top through their centres at 47.5 cm), in the cell from 40 cm.
        Assert.True(result.Grounded);
        Assert.Equal(0.5f, feet.Y, 0.005f);
        Assert.Equal("rough", _materials.NameOf(result.GroundMaterial));
        Assert.Equal(1u, result.GroundTags);
    }

    /// <summary>A stone 30 cm high, under the 40 cm step, is walked up onto.</summary>
    [Fact]
    public void A_character_steps_up_onto_a_low_stone()
    {
        var mover = Mover();
        var feet = new Vector3(1.5f, 0, 3);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 40; i++)
        {
            velocity = new Vector3(3, velocity.Y - 0.3f, 0);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
            if (feet.X > 3)
            {
                break;
            }
        }

        Assert.True(feet.X > 2.8f, $"It stopped at {feet.X}.");
        Assert.InRange(feet.Y, 0.25f, 0.35f);
    }

    [Fact]
    public void A_character_passes_through_a_bush_of_passable_leaves()
    {
        var mover = Mover();
        var feet = new Vector3(0, 0, -1.5f);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 50; i++)
        {
            velocity = new Vector3(0, velocity.Y - 0.3f, -3);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.True(feet.Z < -3.5f, $"It stopped at {feet.Z}.");
    }

    /// <summary>Where the areas of objects round it are not filled yet, a character waits, as it does for unloaded voxels.</summary>
    [Fact]
    public void A_character_waits_where_the_objects_areas_are_not_filled()
    {
        var unfilled = new ObjectWorld(
            _world,
            _kinds,
            new PlacingGenerator(_placements),
            Options.Create(new EngineOptions()),
            NullLogger<ObjectWorld>.Instance
        );
        unfilled.Initialize();
        var mover = new VoxelCharacterMover(_world, _passability)
        {
            Objects = new ObjectColumns(unfilled, _kinds, _passability, 10),
        };
        var feet = Vector3.Zero;
        var velocity = new Vector3(1, 0, 0);

        Assert.Equal(CharacterMoveStatus.Waiting, mover.Move(ref feet, ref velocity, in s_shape, Step).Status);
        unfilled.Update();
        Assert.Equal(CharacterMoveStatus.Moved, mover.Move(ref feet, ref velocity, in s_shape, Step).Status);
    }

    /// <summary>
    /// At the map's edge, where the areas past it hold no column and are never kept, a character moves: only the areas
    /// within the map count (the default grid's edge, 512 m out, lies on an area's border).
    /// </summary>
    [Fact]
    public void A_character_at_the_maps_edge_does_not_wait()
    {
        using var world = TestWorlds.Load(new PlacingGenerator(_placements), new Vector3(508, 0, 0), radiusMetres: 0);
        var objects = new ObjectWorld(
            world,
            _kinds,
            new PlacingGenerator(_placements),
            Options.Create(new EngineOptions()),
            NullLogger<ObjectWorld>.Instance
        );
        objects.Initialize();
        for (var i = 0; i < 100 && objects.WaitingAreas > 0; i++)
        {
            objects.Update();
        }

        Assert.Equal(0, 51_200 % objects.AreaCentimetres);
        var mover = new VoxelCharacterMover(world, _passability)
        {
            Objects = new ObjectColumns(objects, _kinds, _passability, 10),
        };
        var feet = new Vector3(511, 0, 0);
        var velocity = new Vector3(2, 0, 0);

        Assert.Equal(CharacterMoveStatus.Moved, mover.Move(ref feet, ref velocity, in s_shape, Step).Status);
    }

    /// <summary>A chunk made before its area was filled is made again once the objects change.</summary>
    [Fact]
    public void Its_chunks_are_made_again_when_the_objects_change()
    {
        var unfilled = new ObjectWorld(
            _world,
            _kinds,
            new PlacingGenerator(_placements),
            Options.Create(new EngineOptions()),
            NullLogger<ObjectWorld>.Instance
        );
        unfilled.Initialize();
        var columns = new ObjectColumns(unfilled, _kinds, _passability, 10);
        var tops = new int[1];
        // The trunk's axis, at x = 1.5 m.
        columns.ColumnTops(15, 0, 1, 1, -5, 40, tops);
        Assert.Equal(int.MinValue, tops[0]);

        var version = unfilled.Version;
        unfilled.Update();
        Assert.NotEqual(version, unfilled.Version);
        columns.ColumnTops(15, 0, 1, 1, -5, 40, tops);
        var expected = int.MinValue;
        for (var y = -5; y <= 40; y++)
        {
            expected = ReferenceBlocked(15, y, 0) ? y + 1 : expected;
        }

        Assert.Equal(expected, tops[0]);
        Assert.True(expected > 30);
    }

    [Fact]
    public void Moves_among_objects_allocate_nothing()
    {
        var mover = Mover();
        var feet = new Vector3(0, 0, 0);
        var velocity = Vector3.Zero;
        void Walk(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                velocity = new Vector3(i % 120 < 60 ? 2 : -2, velocity.Y - 0.3f, i % 240 < 120 ? 1.5f : -1.5f);
                mover.Move(ref feet, ref velocity, in s_shape, Step);
            }
        }

        Walk(480);
        var start = SystemAllocation.StartMeasurement();
        Walk(480);

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
        var id = _kinds.Register(name, shape);
        _shapes[id] = shape;
        return id;
    }

    private ObjectColumns Columns() => new(_objects, _kinds, _passability, 10);

    private VoxelCharacterMover Mover() =>
        new(_world, _passability)
        {
            Objects = Columns(),
            Materials = _materials,
            SurfaceTags = new RockTags(),
        };

    /// <summary>True when cell (x, y, z)'s centre, turned into some object, lands on its solid voxel that is not leaves.</summary>
    private bool ReferenceBlocked(int x, int y, int z)
    {
        foreach (var placement in _placements)
        {
            var kind = _kinds[placement.Kind];
            var shape = _shapes[placement.Kind];
            var (ox, oz) = ObjectTurns.ToObject(
                placement.Heading,
                x * 10 + 5 - placement.Anchor.X,
                z * 10 + 5 - placement.Anchor.Z
            );
            var oy = y * 10 + 5 - placement.Anchor.Y + kind.AnchorCentimetres.Y;
            var material = shape.MaterialAt(
                (int)Math.Floor((ox + kind.AnchorCentimetres.X) / (double)kind.UnitCentimetres),
                (int)Math.Floor(oy / (double)kind.UnitCentimetres),
                (int)Math.Floor((oz + kind.AnchorCentimetres.Z) / (double)kind.UnitCentimetres)
            );
            if (material is not VoxelConstants.Air and not Leaves)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class LeavesPass : IVoxelPassability
    {
        public bool IsPassable(byte material) => material == Leaves;
    }

    private sealed class RockTags : IVoxelSurfaceTags
    {
        public uint TagsOf(byte material) => material == Rock ? 1u : 0;
    }
}
