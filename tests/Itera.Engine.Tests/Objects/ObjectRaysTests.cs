using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// Rays through the voxel objects (<see cref="ObjectRays"/>): random rays meet the nearest solid voxel an exact test of
/// every solid voxel's box finds, with the face entered; a ray crosses its stretches to an object 40 m out; the shared
/// ray (<see cref="CollisionSources"/>) meets an object before the ground; a tool's ray from inside a bush goes past its
/// leaves (<see cref="ToolAim"/>); the third-person camera is pulled in by a trunk; the movement probes find a trunk ahead
/// with its tags; and rays allocate nothing.
/// </summary>
public sealed class ObjectRaysTests : IDisposable
{
    private const byte Bark = 30;
    private const byte Leaves = 31;
    private const byte Rock = 40;
    private const uint Climbable = 2;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);
    private readonly VoxelObjectKinds _kinds = new();
    private readonly Dictionary<VoxelObjectKindId, VoxelObjectShape> _shapes = [];
    private readonly Dictionary<VoxelObjectKindId, List<VoxelCoord>> _solid = [];
    private readonly List<ObjectPlacement> _placements = [];
    private readonly VoxelWorld _world;
    private readonly ObjectWorld _objects;
    private readonly ObjectRays _rays;
    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);
    private readonly MeshColliders _colliders;

    public ObjectRaysTests()
    {
        // A trunk 50 cm across and 3 m tall, a rock 1.2 by 1 m and 50 cm high, a bush of leaves 1.2 m across, and an L of
        // two blocks; the trunk again 40 m out, turned; and a slab 1 m square whose top, its box's, is at 0.5 m.
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
        var blocks = Register(
            "blocks",
            2,
            new VoxelCoord(40, 40, 40),
            new VoxelCoord(40, 0, 40),
            s =>
            {
                s.FillBox(0, 0, 0, 80, 30, 20, Rock);
                s.FillBox(0, 0, 0, 20, 80, 80, Bark);
            }
        );
        _placements.Add(new ObjectPlacement(new ObjectId(1), trunk, new VoxelCoord(150, 0, 0), 0));
        _placements.Add(new ObjectPlacement(new ObjectId(2), rock, new VoxelCoord(-300, 0, 300), 700));
        _placements.Add(new ObjectPlacement(new ObjectId(3), bush, new VoxelCoord(0, 0, -300), 0));
        _placements.Add(new ObjectPlacement(new ObjectId(4), blocks, new VoxelCoord(300, 10, -300), 3000));
        _placements.Add(new ObjectPlacement(new ObjectId(5), trunk, new VoxelCoord(4000, 0, 0), 1234));
        var slab = Register(
            "slab",
            5,
            new VoxelCoord(20, 10, 20),
            new VoxelCoord(50, 0, 50),
            s => s.FillBox(0, 0, 0, 100, 50, 100, Rock)
        );
        _placements.Add(new ObjectPlacement(new ObjectId(6), slab, new VoxelCoord(-300, 0, -300), 0));
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

        _rays = new ObjectRays(_objects, _kinds, _world);
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    /// <summary>
    /// Rays from random points round the objects, some inside them, meet the solid voxel an exact test of every solid
    /// voxel's box meets first: the same object and voxel, at the same distance, through the same face.
    /// </summary>
    [Fact]
    public void Rays_meet_the_nearest_solid_voxel_an_exact_test_finds()
    {
        var random = new Random(5);
        var checkedHits = 0;
        for (var i = 0; i < 400; i++)
        {
            var origin = new Vector3(
                random.NextSingle() * 10 - 5,
                random.NextSingle() * 3.5f - 0.5f,
                random.NextSingle() * 10 - 5
            );
            // Half the rays towards an object's anchor, so most meet one.
            var target = _placements[random.Next(4)].Anchor;
            var direction =
                i % 2 == 0
                    ? new Vector3(target.X / 100f, random.NextSingle() * 1.5f, target.Z / 100f) - origin
                    : new Vector3(
                        random.NextSingle() * 2 - 1,
                        random.NextSingle() * 2 - 1,
                        random.NextSingle() * 2 - 1
                    );
            if (direction.LengthSquared() < 1e-4f)
            {
                continue;
            }

            var found = _rays.Raycast(origin, direction, 15, out var hit);
            var expected = Reference(origin, direction, 15);
            Assert.Equal(expected.Found, found);
            if (!found)
            {
                continue;
            }

            Assert.Equal(expected.Distance, hit.DistanceMetres, 1e-4f);
            if (expected.Ties)
            {
                continue;
            }

            Assert.Equal(expected.Id, hit.Id);
            Assert.Equal(expected.Voxel, hit.Voxel);
            Assert.True(
                Vector3.Distance(expected.Normal, hit.Normal) < 1e-3f,
                $"Normal {hit.Normal}, expected {expected.Normal}."
            );
            Assert.Equal(_shapes[hit.Kind].MaterialAt(hit.Voxel.X, hit.Voxel.Y, hit.Voxel.Z), hit.Material);
            Assert.Equal(expected.Exit, hit.ExitMetres, 1e-4f);
            AssertInVoxelTurnedAsDrawn(origin, direction, in hit);
            checkedHits++;
        }

        Assert.True(checkedHits > 100, $"Only {checkedHits} rays met an object.");
    }

    /// <summary>
    /// A ray starting on an object's side meets it there, through that side, going in, and nothing going away.
    /// </summary>
    [Fact]
    public void A_ray_starting_on_an_objects_side_meets_it_only_going_in()
    {
        Assert.True(_rays.Raycast(new Vector3(-3, 0.5f, -3), -Vector3.UnitY, 2, out var down));
        Assert.Equal((new ObjectId(6), 0f, Vector3.UnitY), (down.Id, down.DistanceMetres, down.Normal));
        Assert.Equal(9, down.Voxel.Y);

        Assert.False(_rays.Raycast(new Vector3(-3, 0.5f, -3), Vector3.UnitY, 2, out _));
        // Along its top, touching it only.
        Assert.False(_rays.Raycast(new Vector3(-4, 0.5f, -3), Vector3.UnitX, 2, out _));
    }

    /// <summary>A ray along +x meets the trunk 40 m out, through two stretches and more, where its side is.</summary>
    [Fact]
    public void A_ray_crosses_its_stretches_to_an_object_40_m_out()
    {
        Assert.True(_rays.Raycast(new Vector3(5, 1, 0), Vector3.UnitX, 60, out var hit));

        Assert.Equal(new ObjectId(5), hit.Id);
        // The side of a trunk 25 cm round its axis at 40 m, within a voxel.
        Assert.InRange(hit.DistanceMetres, 40 - 0.25f - 5 - 0.05f, 40 - 0.25f - 5 + 0.05f);
        Assert.False(_rays.Raycast(new Vector3(5, 1, 0), Vector3.UnitX, 30, out _));
    }

    /// <summary>The shared ray meets the trunk before the ground behind it, and the ground where nothing stands.</summary>
    [Fact]
    public void The_shared_ray_meets_an_object_before_the_ground()
    {
        var sources = new CollisionSources(_colliders, new BarkTags(), _world, _rays);

        Assert.True(sources.Raycast(new Vector3(0, 1, 0), new Vector3(1, -0.2f, 0), 20, out var hit));
        Assert.Equal(SurfaceSource.VoxelObject, hit.Source);
        Assert.Equal((Bark, Climbable, new ObjectId(1)), (hit.Material, hit.Tags, hit.ObjectId));
        Assert.Equal(-Vector3.UnitX, hit.Normal);
        Assert.InRange(hit.Point.X, 1.2f, 1.3f);

        Assert.True(sources.Raycast(new Vector3(5, 1, 5), -Vector3.UnitY, 20, out var ground));
        Assert.Equal(SurfaceSource.Voxel, ground.Source);
        Assert.Equal(1, ground.Distance, 1e-4f);
    }

    /// <summary>
    /// From inside a bush of passable leaves a tool's ray goes past them to the ground; from outside it meets the leaves.
    /// </summary>
    [Fact]
    public void A_tools_ray_from_inside_a_bush_goes_past_its_leaves()
    {
        var sources = new CollisionSources(_colliders, new BarkTags(), _world, _rays);
        var passability = new LeavesPass();

        Assert.True(ToolAim.Raycast(sources, passability, new Vector3(0, 0.5f, -3), -Vector3.UnitY, 5, out var down));
        Assert.Equal(SurfaceSource.Voxel, down.Source);
        Assert.Equal(0.5f, down.Distance, 1e-3f);
        Assert.Equal(0, down.Point.Y, 1e-3f);

        Assert.True(
            ToolAim.Raycast(sources, passability, new Vector3(0, 0.5f, -1.5f), -Vector3.UnitZ, 5, out var bush)
        );
        Assert.Equal((SurfaceSource.VoxelObject, Leaves), (bush.Source, bush.Material));
        Assert.InRange(bush.Distance, 0.85f, 0.95f);
    }

    /// <summary>
    /// A mesh floor inside the leaf voxel round the eye is what a tool's ray meets: the skip past the leaves stops short of
    /// it.
    /// </summary>
    [Fact]
    public void A_tools_ray_meets_a_surface_inside_the_leaves_round_the_eye()
    {
        // The eye at 50 cm in a leaf voxel from 48 to 52 cm; the floor at 49 cm.
        new Itera.Engine.Tests.Physics.TestTriangles()
            .Ground(-1, -4, 1, -2, 0.49f)
            .AddTo(_colliders);
        var sources = new CollisionSources(_colliders, new BarkTags(), _world, _rays);

        Assert.True(
            ToolAim.Raycast(sources, new LeavesPass(), new Vector3(0, 0.5f, -3), -Vector3.UnitY, 5, out var hit)
        );

        Assert.Equal(SurfaceSource.Mesh, hit.Source);
        Assert.Equal(0.01f, hit.Distance, 1e-4f);
    }

    /// <summary>A trunk between the shoulder and where the camera would be pulls the third-person camera in front of it.</summary>
    [Fact]
    public void The_third_person_camera_is_pulled_in_by_a_trunk()
    {
        // Looking along -z, the shoulder at x = 1.5 m, z = -1.5 m: the camera 2.5 m behind it, past the trunk at z = 0.
        var eye = new Vector3(1.05f, 1.6f, -1.5f);

        var open = ThirdPersonCamera.Position(_world, eye, 0, 0, 2.5f);
        var pulled = ThirdPersonCamera.Position(_world, eye, 0, 0, 2.5f, _rays);

        Assert.Equal(1, open.Z, 1e-3f);
        Assert.InRange(
            pulled.Z,
            -0.25f - ThirdPersonCamera.WallGapMetres - 0.06f,
            -0.25f - ThirdPersonCamera.WallGapMetres
        );
    }

    /// <summary>The climbing probes find the trunk ahead, its bark's tags, and the face its rays entered.</summary>
    [Fact]
    public void The_movement_probes_find_a_trunk_ahead_with_its_tags()
    {
        var passability = new LeavesPass();
        var mover = new VoxelCharacterMover(_world, passability)
        {
            Objects = new ObjectColumns(_objects, _kinds, passability, 10),
        };
        var probes = new MovementProbes(new CollisionSources(_colliders, new BarkTags(), _world, _rays), mover);
        var feet = new Vector3(0.6f, 0, 0);

        Assert.True(probes.TryWallAhead(feet, in s_shape, Vector2.UnitX, 0.5f, out var wall));
        Assert.Equal((Climbable, 3), (wall.Tags, wall.Rays));
        Assert.Equal(-Vector2.UnitX, wall.Normal);
        // The trunk's voxels end 25 cm from its axis at x = 1.5 m, within a voxel; the body's side at 0.86 m.
        Assert.InRange(wall.DistanceMetres, 1.25f - 0.86f - 0.03f, 1.25f - 0.86f + 0.03f);
    }

    [Fact]
    public void Rays_allocate_nothing()
    {
        var sources = new CollisionSources(_colliders, new BarkTags(), _world, _rays);
        var passability = new LeavesPass();
        void Cast()
        {
            for (var i = 0; i < 64; i++)
            {
                var turn = i * MathF.PI / 32;
                var direction = new Vector3(MathF.Cos(turn), -0.1f, MathF.Sin(turn));
                _rays.Raycast(new Vector3(0, 1, 0), direction, 30, out _);
                sources.Raycast(new Vector3(0, 1, 0), direction, 30, out _);
                ToolAim.Raycast(sources, passability, new Vector3(0, 0.5f, -3), direction, 5, out _);
            }
        }

        Cast();
        var start = SystemAllocation.StartMeasurement();
        Cast();

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
        var solid = new List<VoxelCoord>();
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    if (shape.MaterialAt(x, y, z) != VoxelConstants.Air)
                    {
                        solid.Add(new VoxelCoord(x, y, z));
                    }
                }
            }
        }

        _solid[id] = solid;
        return id;
    }

    /// <summary>
    /// The point 0.1 mm past where the ray entered the hit voxel, turned into the object by the inverse of the turn its
    /// models are drawn with (<see cref="ObjectTurns.Rotation"/>), lies in that voxel (to 0.2 mm: the drawing's turn is
    /// of floats, the rays' of the fixed-point table).
    /// </summary>
    private void AssertInVoxelTurnedAsDrawn(Vector3 origin, Vector3 direction, in ObjectHit hit)
    {
        var id = hit.Id;
        var placement = _placements.Single(p => p.Id == id);
        var kind = _kinds[placement.Kind];
        var inside = origin + Vector3.Normalize(direction) * (hit.DistanceMetres + 1e-4f);
        var offset = inside - new Vector3(placement.Anchor.X, placement.Anchor.Y, placement.Anchor.Z) / 100f;
        var own = Vector3.Transform(offset, Quaternion.Inverse(ObjectTurns.Rotation(placement.Heading))) * 100;
        var unit = kind.UnitCentimetres;
        var low =
            new Vector3(hit.Voxel.X, hit.Voxel.Y, hit.Voxel.Z) * unit
            - new Vector3(kind.AnchorCentimetres.X, kind.AnchorCentimetres.Y, kind.AnchorCentimetres.Z);
        var outside = Vector3.Max(Vector3.Max(low - own, own - (low + new Vector3(unit))), Vector3.Zero);
        Assert.True(outside.Length() < 0.02f, $"{own} lies {outside.Length()} cm outside voxel {hit.Voxel}.");
    }

    /// <summary>
    /// The first solid voxel of any object along the ray within <paramref name="most"/> metres, by testing every solid
    /// voxel's box in its object's voxels; ties when another voxel is met within a micrometre of it;
    /// where the ray leaves the voxel.
    /// </summary>
    private (
        bool Found,
        float Distance,
        ObjectId Id,
        VoxelCoord Voxel,
        Vector3 Normal,
        bool Ties,
        float Exit
    ) Reference(Vector3 origin, Vector3 direction, float most)
    {
        var d = Vector3.Normalize(direction);
        var best = (double)most;
        (bool Found, float Distance, ObjectId Id, VoxelCoord Voxel, Vector3 Normal, bool Ties, float Exit) result =
            default;
        var second = double.PositiveInfinity;
        foreach (var placement in _placements)
        {
            var kind = _kinds[placement.Kind];
            double cos = ObjectTurns.Cos(placement.Heading) / 65536.0,
                sin = ObjectTurns.Sin(placement.Heading) / 65536.0;
            double rx = origin.X * 100.0 - placement.Anchor.X,
                ry = origin.Y * 100.0 - placement.Anchor.Y,
                rz = origin.Z * 100.0 - placement.Anchor.Z;
            var unit = (double)kind.UnitCentimetres;
            double[] start =
            [
                (rx * cos + rz * sin + kind.AnchorCentimetres.X) / unit,
                (ry + kind.AnchorCentimetres.Y) / unit,
                (rz * cos - rx * sin + kind.AnchorCentimetres.Z) / unit,
            ];
            double[] along =
            [
                (d.X * cos + d.Z * sin) * 100 / unit,
                d.Y * 100 / unit,
                (d.Z * cos - d.X * sin) * 100 / unit,
            ];
            foreach (var voxel in _solid[placement.Kind])
            {
                int[] low = [voxel.X, voxel.Y, voxel.Z];
                var enter = 0.0;
                var leave = double.PositiveInfinity;
                var axis = -1;
                var miss = false;
                for (var a = 0; a < 3 && !miss; a++)
                {
                    if (Math.Abs(along[a]) < 1e-12)
                    {
                        miss = start[a] < low[a] || start[a] >= low[a] + 1;
                        continue;
                    }

                    var t0 = (low[a] - start[a]) / along[a];
                    var t1 = (low[a] + 1 - start[a]) / along[a];
                    if (t0 > t1)
                    {
                        (t0, t1) = (t1, t0);
                    }

                    if (t0 >= enter)
                    {
                        enter = t0;
                        axis = a;
                    }

                    leave = Math.Min(leave, t1);
                }

                if (miss || enter >= leave || enter > most)
                {
                    continue;
                }

                if (enter < best)
                {
                    second = best;
                    best = enter;
                    var own = new Vector3(
                        axis == 0 ? -Math.Sign(along[0]) : 0,
                        axis == 1 ? -Math.Sign(along[1]) : 0,
                        axis == 2 ? -Math.Sign(along[2]) : 0
                    );
                    var normal = new Vector3(
                        (float)(own.X * cos - own.Z * sin),
                        own.Y,
                        (float)(own.X * sin + own.Z * cos)
                    );
                    result = (true, (float)enter, placement.Id, voxel, normal, false, (float)leave);
                }
                else
                {
                    second = Math.Min(second, enter);
                }
            }
        }

        return result with
        {
            Ties = result.Found && second - best < 1e-6,
        };
    }

    private sealed class LeavesPass : IVoxelPassability
    {
        public bool IsPassable(byte material) => material == Leaves;
    }

    private sealed class BarkTags : IVoxelSurfaceTags
    {
        public uint TagsOf(byte material) => material == Bark ? Climbable : 0;
    }
}
