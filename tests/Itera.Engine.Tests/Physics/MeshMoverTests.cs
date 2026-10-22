using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The test features of <see cref="MoverWorldFixture"/> built again as triangles (the floor's top round the pit, the
/// pit's walls and bottom, and a closed box for each box but the leaves) over a voxel world of air at 1 cm, so the mover
/// reads them in the world's cells, and the world's columns still decide where it waits.
/// </summary>
public sealed class MeshMoverWorldFixture : IMoverFeatures, IDisposable
{
    /// <summary>How far the floor's triangles reach from the middle, in metres.</summary>
    private const float Edge = 4;

    /// <summary>The side of a tile of <see cref="_tiles"/>, in cells.</summary>
    private const int TileShift = 4;

    private const int TileCells = 1 << TileShift;

    private readonly VoxelBox[] _boxes = MoverWorldFixture.Boxes();

    /// <summary>
    /// For each tile of columns, the generator of the boxes that meet it, in their original order (later boxes override
    /// earlier ones). A cell is looked up in its tile's few boxes, not in all of them. Made on first use.
    /// </summary>
    private readonly Dictionary<(int X, int Z), BoxesGenerator> _tiles = [];

    // The tile of the last cell asked for: a body's cells lie together.
    private (int X, int Z) _lastTile = (int.MinValue, 0);
    private BoxesGenerator? _last;

    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);

    public MeshMoverWorldFixture()
    {
        _physics.Initialize();
        Colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
        var triangles = new TestTriangles();
        VoxelBox? pit = null;
        foreach (var box in MoverWorldFixture.Boxes())
        {
            if (box.Material == VoxelConstants.Air)
            {
                pit = box;
            }
            else if (box.Material != BoxesGenerator.Leaves)
            {
                triangles.CellBox(box.X0, box.Y0, box.Z0, box.X1, box.Y1, box.Z1);
            }
        }

        // The floor's top round the pit, and the pit's walls (their fronts into it) and bottom.
        var hole = pit!.Value;
        float x0 = hole.X0 / 100f,
            x1 = (hole.X1 + 1) / 100f,
            z0 = hole.Z0 / 100f,
            z1 = (hole.Z1 + 1) / 100f,
            bottom = hole.Y0 / 100f,
            top = MoverWorldFixture.Floor / 100f;
        triangles
            .Ground(-Edge, -Edge, x0, Edge, top)
            .Ground(x1, -Edge, Edge, Edge, top)
            .Ground(x0, -Edge, x1, z0, top)
            .Ground(x0, z1, x1, Edge, top)
            .Ground(x0, z0, x1, z1, bottom)
            .Quad(Vector3.UnitX, new(x0, bottom, z0), new(x0, top, z0), new(x0, top, z1), new(x0, bottom, z1))
            .Quad(-Vector3.UnitX, new(x1, bottom, z0), new(x1, top, z0), new(x1, top, z1), new(x1, bottom, z1))
            .Quad(Vector3.UnitZ, new(x0, bottom, z0), new(x1, bottom, z0), new(x1, top, z0), new(x0, top, z0))
            .Quad(-Vector3.UnitZ, new(x0, bottom, z1), new(x1, bottom, z1), new(x1, top, z1), new(x0, top, z1));
        triangles.AddTo(Colliders);

        World = TestWorlds.Load(new BoxesGenerator(-1_000_000), new Vector3(0, 2, 0), radiusMetres: 5);
        Mover = new VoxelCharacterMover(World, new MoverWorldFixture.LeavesPass(), meshes: Colliders);
    }

    public MeshColliders Colliders { get; }

    public VoxelWorld World { get; }

    public VoxelCharacterMover Mover { get; }

    public bool FirstReadsAllocate => true;

    public bool Blocks(int x, int y, int z)
    {
        var tile = (x >> TileShift, z >> TileShift);
        if (tile != _lastTile || _last is null)
        {
            _last = Tile(tile.Item1, tile.Item2);
            _lastTile = tile;
        }

        var material = _last.Material(x, y, z);
        return material != VoxelConstants.Air && material != BoxesGenerator.Leaves;
    }

    private BoxesGenerator Tile(int tileX, int tileZ)
    {
        if (_tiles.TryGetValue((tileX, tileZ), out var tile))
        {
            return tile;
        }

        int x0 = tileX * TileCells,
            z0 = tileZ * TileCells;
        var meeting = _boxes
            .Where(box => box.X0 < x0 + TileCells && box.X1 >= x0 && box.Z0 < z0 + TileCells && box.Z1 >= z0)
            .ToArray();
        return _tiles[(tileX, tileZ)] = new BoxesGenerator(MoverWorldFixture.Floor, meeting);
    }

    public void Dispose()
    {
        World.Dispose();
        Colliders.Dispose();
        _physics.Dispose();
    }
}

/// <summary>
/// The mover through the test features of triangles over a world of air (<see cref="MeshMoverWorldFixture"/>): every
/// test of <see cref="MoverFeatureTests"/>, and a collider added during play that the body is lifted onto.
/// </summary>
public sealed class MeshMoverFeatureTests(MeshMoverWorldFixture fixture, ITestOutputHelper output)
    : MoverFeatureTests(fixture, output),
        IClassFixture<MeshMoverWorldFixture>
{
    [Fact]
    public void A_body_inside_a_collider_added_during_play_is_lifted_onto_it()
    {
        var feet = Metres(-240, 200, 240);
        var box = new TestTriangles().CellBox(-270, 200, 210, -210, 249, 270).AddTo(fixture.Colliders);
        try
        {
            Assert.True(Mover.Overlaps(feet, in Shape));
            var velocity = Vector3.Zero;

            var result = Mover.Move(ref feet, ref velocity, in Shape, Step);

            Assert.Equal(CharacterMoveStatus.Lifted, result.Status);
            Assert.Equal(2.50f, feet.Y, 4);
            Assert.False(Mover.Overlaps(feet, in Shape));
            Assert.True(result.Grounded);
        }
        finally
        {
            fixture.Colliders.Remove(box);
        }

        Assert.False(Mover.Overlaps(Metres(-240, 200, 240), in Shape));
    }
}

/// <summary>
/// The ground of <see cref="DensityTerrainGenerator"/> built again as triangles with no voxel world, read in 10 cm cells
/// (<c>Engine:Physics:CollisionCentimetres</c>' default): the floor at 1.53 m, the slopes as planes from x = 2 m to 5 m
/// with flats beyond and walls along their sides, and the stairs, the step, the wall, and the ceiling as closed boxes.
/// </summary>
public sealed class MeshDensityGroundFixture : IDensityGround, IDisposable
{
    private const float Floor = (float)(DensityTerrainGenerator.FloorCentimetres / 100);
    private const float Near = -1;
    private const float Far = 8;

    private static readonly double[] s_rises = [0.57735026918962576, 0.83909963117728, 1.7320508075688772];

    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);

    public MeshDensityGroundFixture()
        : this(new WorldOrigin(), WorldPoint.Zero) { }

    /// <summary>The same ground with its triangles placed at <paramref name="at"/>, read from <paramref name="origin"/>.</summary>
    internal MeshDensityGroundFixture(WorldOrigin origin, WorldPoint at)
    {
        _physics.Initialize();
        Colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance, origin);
        var triangles = new TestTriangles();
        float width = DensityTerrainGenerator.StripWidth / 100f,
            start = DensityTerrainGenerator.Start / 100f;
        var z = Near;
        for (var strip = 0; strip < DensityTerrainGenerator.Strips.Length; strip++)
        {
            float z0 = DensityTerrainGenerator.Strips[strip] / 100f,
                z1 = z0 + width;
            // The floor before the strip, then the strip's own ground.
            triangles.Ground(Near, z, Far, z0, Floor);
            var rise =
                strip < 3 ? s_rises[strip]
                : strip == 6 ? s_rises[0]
                : 0;
            if (rise == 0)
            {
                triangles.Ground(Near, z0, Far, z1, Floor);
            }
            else
            {
                var high = (float)(Floor + rise * 3);
                triangles
                    .Ground(Near, z0, start, z1, Floor)
                    .Quad(
                        Vector3.UnitY,
                        new(start, Floor, z0),
                        new(start, Floor, z1),
                        new(5, high, z1),
                        new(5, high, z0)
                    )
                    .Ground(5, z0, Far, z1, high);
                foreach (var (side, front) in new[] { (z0, -Vector3.UnitZ), (z1, Vector3.UnitZ) })
                {
                    triangles
                        .Triangle(front, new(start, Floor, side), new(5, Floor, side), new(5, high, side))
                        .Quad(
                            front,
                            new(5, Floor, side),
                            new(Far, Floor, side),
                            new(Far, high, side),
                            new(5, high, side)
                        );
                }
            }

            z = z1;
        }

        triangles.Ground(Near, z, Far, 28, Floor);

        // The stairs, from the 10 cm line under the floor; the step; the wall; the ceiling.
        float stairs = DensityTerrainGenerator.Strips[3] / 100f,
            step = DensityTerrainGenerator.Strips[4] / 100f,
            wall = DensityTerrainGenerator.Strips[5] / 100f,
            ceiling = DensityTerrainGenerator.Strips[6] / 100f;
        for (var k = 0; k < 4; k++)
        {
            var end = k < 3 ? start + 0.3f * (k + 1) : Far;
            triangles.Box(new(start + 0.3f * k, 1.5f, stairs), new(end, 1.7f + 0.2f * k, stairs + width));
        }

        triangles
            .Box(new(3, 1.5f, step), new(4, 2, step + width))
            .Box(new(3, 1.5f, wall), new(3.3f, 4.5f, wall + width))
            .Box(new(3, 3.8f, ceiling), new(6, 4.2f, ceiling + width));
        triangles.AddTo(Colliders, in at);
        Mover = new VoxelCharacterMover(new NothingPassable(), Colliders, cellCentimetres: 10);
    }

    public MeshColliders Colliders { get; }

    public VoxelCharacterMover Mover { get; }

    public bool FirstReadsAllocate => true;

    public bool Solid(int x, int y, int z) => DensityTerrainGenerator.Solid(x, y, z);

    public void Dispose()
    {
        Colliders.Dispose();
        _physics.Dispose();
    }
}

/// <summary>Every test of <see cref="DensityGroundTests"/> over triangles with no voxel world, in 10 cm cells.</summary>
public sealed class MeshDensityGroundTests(MeshDensityGroundFixture fixture, ITestOutputHelper output)
    : DensityGroundTests(fixture, output),
        IClassFixture<MeshDensityGroundFixture>;

/// <summary>
/// The mover over mesh colliders alone, in 10 cm cells: a thin wall stops a body from either side; a slope that rises
/// along no axis is walked up as a ramp; a collider added during play blocks at once and a removed one no more; with no
/// collider a body never waits and falls; the map's edge is a wall.
/// </summary>
public sealed class MeshMoverTests : IDisposable
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;
    private const float Walk = 4.5f;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);
    private readonly MeshColliders _colliders;
    private readonly VoxelCharacterMover _mover;

    public MeshMoverTests()
    {
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
        _mover = new VoxelCharacterMover(new NothingPassable(), _colliders, cellCentimetres: 10);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
    }

    [Fact]
    public void A_thin_wall_stops_a_body_from_either_side()
    {
        // One quad at x = 1.05 m, inside the cells from 1.0 to 1.1 m, which it blocks; its front faces -x.
        new TestTriangles()
            .Ground(-3, -3, 4, 3, 0)
            .Quad(-Vector3.UnitX, new(1.05f, 0, -2), new(1.05f, 2, -2), new(1.05f, 2, 2), new(1.05f, 0, 2))
            .AddTo(_colliders);
        var before = new Vector3(0, 0, 0);
        var behind = new Vector3(2, 0, 0);

        var hitFront = WalkTicks(ref before, new Vector2(Walk, 0), 40);
        var hitBack = WalkTicks(ref behind, new Vector2(-Walk, 0), 40);

        Assert.True(hitFront && hitBack);
        Assert.Equal(1 - s_shape.RadiusMetres, before.X, 0.002f);
        Assert.Equal(1.1f + s_shape.RadiusMetres, behind.X, 0.002f);
        Assert.Equal(0, before.Y, 0.002f);
    }

    [Fact]
    public void A_slope_along_no_axis_is_walked_up_as_a_ramp()
    {
        // 25 degrees, rising along (1, 0, 1) / sqrt 2 from the line x + z = 0, to a flat 3 m along it.
        var rise = MathF.Tan(25 * MathF.PI / 180);
        var along = Vector3.Normalize(new Vector3(1, 0, 1));
        var across = Vector3.Normalize(new Vector3(1, 0, -1));
        Vector3 At(float a, float c) => along * a + across * c + new Vector3(0, rise * Math.Clamp(a, 0, 3), 0);
        new TestTriangles()
            .Quad(Vector3.UnitY, At(-3, -4), At(-3, 4), At(0, 4), At(0, -4))
            .Quad(Vector3.UnitY, At(0, -4), At(0, 4), At(3, 4), At(3, -4))
            .Quad(Vector3.UnitY, At(3, -4), At(3, 4), At(6, 4), At(6, -4))
            .AddTo(_colliders);
        var feet = At(-1, 0);
        var velocity = Vector3.Zero;
        var alwaysGrounded = true;
        var everSteep = false;
        var farthest = 0f;
        var largestRise = 0f;

        for (var tick = 0; tick < 70; tick++)
        {
            velocity = new Vector3(along.X * Walk, velocity.Y - Gravity * Step, along.Z * Walk);
            var last = feet.Y;
            var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            alwaysGrounded &= result.Grounded;
            everSteep |= result.Steep;
            largestRise = Math.Max(largestRise, feet.Y - last);
            var a = Vector3.Dot(feet, along);
            farthest = Math.Max(farthest, a);
            // The feet on the plane under the body's centre, or on its rim where the slope meets the flats.
            Assert.InRange(
                feet.Y,
                At(a - s_shape.RadiusMetres, 0).Y - 0.02f,
                At(a + s_shape.RadiusMetres, 0).Y + 0.02f
            );
        }

        Assert.True(alwaysGrounded);
        Assert.False(everSteep);
        Assert.True(farthest > 3.5f, $"Stopped {farthest:F2} m along the slope.");
        Assert.Equal(rise * 3, feet.Y, 0.01f);
        // A tick's walk rises by the slope's share of it, and a centimetre: not a cell at a time.
        Assert.True(largestRise < Walk * Step * rise + 0.01f, $"The feet rose {largestRise * 100:F2} cm in a tick.");
    }

    [Fact]
    public void A_collider_added_during_play_blocks_at_once_and_a_removed_one_no_more()
    {
        new TestTriangles().Ground(-5, -5, 5, 5, 0).AddTo(_colliders);
        var feet = Vector3.Zero;
        WalkTicks(ref feet, Vector2.Zero, 5);
        var wall = new TestTriangles().Box(new(1, 0, -1), new(1.2f, 2, 1)).AddTo(_colliders);

        var hit = WalkTicks(ref feet, new Vector2(Walk, 0), 30);
        Assert.True(hit);
        Assert.Equal(1 - s_shape.RadiusMetres, feet.X, 0.002f);

        _colliders.Remove(wall);
        WalkTicks(ref feet, new Vector2(Walk, 0), 30);

        Assert.True(feet.X > 2, $"Stopped at x = {feet.X:F2} m.");
    }

    [Fact]
    public void With_no_collider_a_body_never_waits_and_falls()
    {
        var feet = new Vector3(100, 5, -300);
        var velocity = Vector3.Zero;

        var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.True(_mover.IsResident(feet, 1));
        Assert.Equal(CharacterMoveStatus.Moved, result.Status);
        Assert.False(result.Grounded);
        Assert.False(_mover.GroundTop(feet, 0.3f, 1, 10, out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    public void The_maps_edge_is_a_wall_at_every_height(int x, int z)
    {
        // An edge 5 m from the middle and ground to 8 m; a body walks out from 3.5 m, falling from 2 m up, and meets the
        // edge in the air.
        var mover = new VoxelCharacterMover(new NothingPassable(), _colliders, cellCentimetres: 10, edgeMetres: 5);
        new TestTriangles().Ground(-8, -8, 8, 8, 0).AddTo(_colliders);
        var way = new Vector3(x, 0, z);
        var feet = way * 3.5f + new Vector3(0, 2, 0);
        var velocity = Vector3.Zero;
        var hitInTheAir = false;

        for (var i = 0; i < 60; i++)
        {
            velocity = new Vector3(way.X * Walk, velocity.Y - Gravity * Step, way.Z * Walk);
            var result = mover.Move(ref feet, ref velocity, in s_shape, Step);
            hitInTheAir |= result.HitWall && !result.Grounded;
        }

        Assert.True(hitInTheAir);
        Assert.Equal(5 - s_shape.RadiusMetres, Vector3.Dot(feet, way), 0.002f);
        Assert.Equal(0, feet.Y, 0.002f);
    }

    [Fact]
    public void Moving_over_colliders_allocates_nothing()
    {
        new TestTriangles().Ground(-5, -5, 5, 5, 0).Box(new(1, 0, -1), new(1.2f, 0.3f, 1)).AddTo(_colliders);
        var feet = Vector3.Zero;
        var velocity = Vector3.Zero;
        for (var i = 0; i < 100; i++)
        {
            velocity = new Vector3(i % 100 < 50 ? Walk : -Walk, velocity.Y - Gravity * Step, 0);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 500; i++)
        {
            velocity = new Vector3(i % 100 < 50 ? Walk : -Walk, velocity.Y - Gravity * Step, 0);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Ticks of a walk at <paramref name="wanted"/> with gravity; true when any met a wall.</summary>
    private bool WalkTicks(ref Vector3 feet, Vector2 wanted, int ticks)
    {
        var velocity = Vector3.Zero;
        var hit = false;
        for (var i = 0; i < ticks; i++)
        {
            velocity = new Vector3(wanted.X, velocity.Y - Gravity * Step, wanted.Y);
            var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.Equal(CharacterMoveStatus.Moved, result.Status);
            hit |= result.HitWall;
        }

        return hit;
    }
}
