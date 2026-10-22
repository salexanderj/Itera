using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Solid models that are not loose bodies (<see cref="SolidModelVoxels"/>, a model of a head) and the character mover
/// among them: their columns (<see cref="SolidColumns"/>), queries that match a voxel-by-voxel walk of the world voxels
/// whose centres land in the model at any turn about y, rays meeting them where a march does, nothing reached below them, and the mover stopped by one,
/// standing on one, and carried up by one rising under its feet, allocating nothing.
/// </summary>
public sealed class SolidModelVoxelsTests : IDisposable
{
    private const int Floor = 200;

    /// <summary>The floor in metres, on the 1 cm world the mover's tests walk.</summary>
    private const double FloorMetres = Floor / 100.0;
    private const float Step = 1f / 60;
    private const int Edge = VoxelConstants.ChunkSize;

    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.4f);

    /// <summary>A wide body's cylinder.</summary>
    private static readonly CharacterShape s_wide = new(6, 1.5f, 1);

    private VoxelWorld? _world;
    private PhysicsWorld? _physics;
    private VoxelColliders? _colliders;

    public void Dispose()
    {
        _colliders?.Dispose();
        _physics?.Dispose();
        _world?.Dispose();
    }

    [Fact]
    public void Columns_hold_each_columns_runs()
    {
        var chunk = new byte[VoxelConstants.ChunkVoxels];
        // Column (2, 3): solid at y 1 to 4 and 7, air between.
        for (var y = 1; y <= 4; y++)
        {
            chunk[2 + Edge * y + Edge * Edge * 3] = 5;
        }

        chunk[2 + Edge * 7 + Edge * Edge * 3] = 9;
        var columns = SolidColumns.Build(chunk, new VoxelCoord(4, 8, 5));

        Assert.Equal(5, columns.SolidVoxels);
        Assert.Equal(7, columns.Highest(2, 3, 0, 7));
        Assert.Equal(4, columns.Highest(2, 3, 0, 6));
        Assert.Equal(3, columns.Highest(2, 3, 0, 3));
        Assert.Equal(-1, columns.Highest(2, 3, 5, 6));
        Assert.Equal(1, columns.Lowest(2, 3, -5, 20));
        Assert.Equal(7, columns.Lowest(2, 3, 5, 20));
        Assert.Equal(-1, columns.Highest(1, 3, 0, 7));
    }

    /// <summary>
    /// On a world of 1 cm voxels and one of 10 cm, where a model's voxel of 1, 2, or 4 cm, or of an object kind's 3 or 5
    /// cm, is a share of a world voxel (0.4 for 4 cm in 10), the solid's cells are the world voxels whose centres land on
    /// its voxels.
    /// </summary>
    [Theory]
    [InlineData(0f, 0, 1, 1)]
    [InlineData(0.3f, 1, 1, 1)]
    [InlineData(1.2f, 2, 1, 1)]
    [InlineData(2.9f, 2, 1, 1)]
    [InlineData(-2f, 1, 1, 1)]
    [InlineData(0f, 2, 10, 1)]
    [InlineData(0.7f, 2, 10, 1)]
    [InlineData(-1.9f, 0, 10, 1)]
    [InlineData(0.4f, 0, 10, 5)]
    [InlineData(-1.1f, 1, 10, 3)]
    public void Queries_match_a_voxel_by_voxel_walk(float yaw, int level, int worldCentimetres, int unit)
    {
        var chunk = Lopsided(out var size);
        var solids = new SolidModelVoxels(worldCentimetres);
        var pivot = new Vector3(size.X * 0.4f, 1.3f, size.Z * 0.55f);
        var id = solids.Add(SolidColumns.Build(chunk, size), level, pivot, unit);
        var at = new Vector3(37.3f, 251.6f, -12.8f) / worldCentimetres;
        solids.SetPoseInCells(id, at.X, at.Y, at.Z, yaw);
        var scale = (unit << level) / (float)worldCentimetres;
        var reach = Math.Max(4, (int)(size.X * scale * 1.5f));
        int windowX = (int)at.X - 64,
            windowZ = (int)at.Z - 64;

        Span<UInt128> rows = stackalloc UInt128[128];
        for (var y0 = (int)at.Y - 30; y0 < at.Y + size.Y * scale + 10; y0 += 7)
        {
            var y1 = y0 + 5;
            rows.Clear();
            solids.BlockedCells(windowX, windowZ, 128, y0, y1, rows);
            for (var row = 0; row < 128; row++)
            {
                var expected = UInt128.Zero;
                for (var column = 0; column < 128; column++)
                {
                    for (var y = y0; y <= y1; y++)
                    {
                        if (Solid(chunk, size, scale, pivot, at, yaw, windowX + column, y, windowZ + row))
                        {
                            expected |= UInt128.One << column;
                            break;
                        }
                    }
                }

                Assert.True(expected == rows[row], $"Row {row} from y {y0} differs at yaw {yaw}.");
            }

            var footprint = new CircleFootprint(at.X + 3.7, at.Z - 5.2, Math.Min(reach, 40));
            var (highest, lowest) = Walk(chunk, size, scale, pivot, at, yaw, in footprint, y0, y1);
            Assert.Equal(highest, solids.HighestTop(in footprint, y0, y1));
            Assert.Equal(lowest, solids.LowestBottom(in footprint, y0, y1));
        }
    }

    /// <summary>
    /// Rays from all round, aimed near the solid's middle, meet it where a fine march along each finds its first voxel,
    /// at any turn about y, on worlds of 1 and 10 cm; those aimed away or stopped short meet nothing.
    /// </summary>
    [Theory]
    [InlineData(0f, 2, 1, 1)]
    [InlineData(0.7f, 2, 10, 1)]
    [InlineData(-1.9f, 0, 10, 1)]
    [InlineData(2.6f, 1, 10, 3)]
    public void Rays_meet_a_solid_where_a_march_does(float yaw, int level, int worldCentimetres, int unit)
    {
        var chunk = Lopsided(out var size);
        var solids = new SolidModelVoxels(worldCentimetres);
        var pivot = new Vector3(size.X * 0.4f, 1.3f, size.Z * 0.55f);
        var id = solids.Add(SolidColumns.Build(chunk, size), level, pivot, unit);
        var at = new Vector3(37.3f, 251.6f, -12.8f) / worldCentimetres;
        solids.SetPoseInCells(id, at.X, at.Y, at.Z, yaw);
        var scale = (unit << level) / (float)worldCentimetres;
        var reach = size.X * scale * 2;
        var middle = at + new Vector3(0, size.Y * scale * 0.4f, 0);
        var perMetre = 100.0 / worldCentimetres;
        var random = new Random(7);
        var met = 0;
        for (var i = 0; i < 60; i++)
        {
            var from = middle + RandomUnit(random) * reach;
            var target = middle + RandomUnit(random) * reach * 0.2f;
            var way = Vector3.Normalize(target - from);
            var expected = March(chunk, size, scale, pivot, at, yaw, from, way, reach * 3);
            var origin = new WorldPoint(from.X / perMetre, from.Y / perMetre, from.Z / perMetre);

            var hit = solids.Raycast(origin, way, (float)(reach * 3 / perMetre), out var solid);

            Assert.Equal(expected.HasValue, hit);
            if (expected is { } cells)
            {
                met++;
                Assert.Equal(id, solid.Id);
                Assert.Equal(cells / perMetre, solid.DistanceMetres, 0.002 * scale / perMetre + 1e-4);
                Assert.True(
                    Vector3.Dot(solid.Normal, way) < 0,
                    $"The face {solid.Normal} does not face the ray {way}."
                );
                Assert.False(solids.Raycast(origin, way, (float)(cells / perMetre) * 0.98f, out _));
                Assert.False(solids.Raycast(origin, -way, (float)(reach * 3 / perMetre), out _));
            }
        }

        Assert.True(met > 20, $"Only {met} rays met the solid.");
    }

    /// <summary>A ray along -x into an unturned box meets its +x face square on.</summary>
    [Fact]
    public void A_ray_meets_a_boxs_face_square_on()
    {
        var solids = new SolidModelVoxels(10);
        var id = solids.Add(Block(10, 10, 10), 0, Vector3.Zero, unitCentimetres: 10);
        solids.SetPose(id, new WorldPoint(2, 0, 0), 0);

        Assert.True(solids.Raycast(new WorldPoint(10, 0.5, 0.5), -Vector3.UnitX, 20, out var hit));

        Assert.Equal(7, hit.DistanceMetres, 4);
        Assert.Equal(Vector3.UnitX, hit.Normal);
        Assert.True(solids.Raycast(new WorldPoint(2.5, 5, 0.5), -Vector3.UnitY, 20, out var top));
        Assert.Equal((4f, Vector3.UnitY), (MathF.Round(top.DistanceMetres, 4), top.Normal));

        // Turned a quarter: its +z face now faces +x, its box from x 2 to 3 and z -1 to 0.
        solids.SetPose(id, new WorldPoint(2, 0, 0), MathF.PI / 2);
        Assert.True(solids.Raycast(new WorldPoint(10, 0.5, -0.5), -Vector3.UnitX, 20, out var turned));
        Assert.Equal(7, turned.DistanceMetres, 3);
        Assert.Equal(1, turned.Normal.X, 4);
        Assert.Equal(0, turned.Normal.Z, 4);
        Assert.True(solids.Raycast(new WorldPoint(2.5, 0.5, -10), Vector3.UnitZ, 20, out var side));
        Assert.Equal(9, side.DistanceMetres, 3);
        Assert.Equal(-1, side.Normal.Z, 4);
    }

    [Fact]
    public void Nothing_below_or_beside_a_solid_reaches_it()
    {
        var chunk = Lopsided(out var size);
        var solids = new SolidModelVoxels();
        var id = solids.Add(SolidColumns.Build(chunk, size), 2, Vector3.Zero);
        solids.SetPose(id, new WorldPoint(0, 4, 0), 0.7f);

        Assert.False(solids.Reaches(-100, 0, -100, 100, 350, 100));
        Assert.False(solids.Reaches(1000, 400, 1000, 1100, 500, 1100));
        Assert.True(solids.Reaches(-10, 380, -10, 10, 420, 10));
        var footprint = new CircleFootprint(0, 0, 30);
        Assert.Equal(int.MinValue, solids.HighestTop(in footprint, 0, 350));
        Assert.Equal(int.MaxValue, solids.LowestBottom(in footprint, 0, 350));
    }

    [Fact]
    public void A_removed_solid_blocks_nothing_and_its_id_is_used_again()
    {
        var chunk = Lopsided(out var size);
        var solids = new SolidModelVoxels();
        var columns = SolidColumns.Build(chunk, size);
        var first = solids.Add(columns, 0, Vector3.Zero);
        solids.SetPose(first, WorldPoint.Zero, 0);
        solids.Remove(first);

        Assert.Equal(0, solids.Count);
        Assert.False(solids.Reaches(-50, -50, -50, 50, 50, 50));
        Assert.Equal(first, solids.Add(columns, 0, Vector3.Zero));
    }

    [Fact]
    public void A_character_walking_into_a_solid_is_stopped_by_it()
    {
        var (mover, bodyVoxels) = MoverWorld();
        // A post 40 cm square and 60 cm tall (of 4 cm voxels), taller than a step, standing on the floor from x = 0.6 m.
        var id = bodyVoxels.Solids.Add(Block(10, 15, 10), 2, Vector3.Zero);
        bodyVoxels.Solids.SetPose(id, new WorldPoint(0.6, FloorMetres, -0.2), 0);
        var feet = new Vector3(0, 2, 0);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 30; i++)
        {
            velocity = new Vector3(3, velocity.Y - 0.3f, 0);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.InRange(feet.X, 0.6f - s_shape.RadiusMetres - 0.01f, 0.6f - s_shape.RadiusMetres);
        Assert.Equal(2, feet.Y, 0.005f);
    }

    [Fact]
    public void A_character_lands_on_a_floating_solid_and_stands_on_it()
    {
        var (mover, bodyVoxels) = MoverWorld();
        // A slab 1.2 m square and 20 cm thick, 1 m above the floor, turned a little.
        var id = bodyVoxels.Solids.Add(Block(30, 5, 30), 2, new Vector3(15, 0, 15));
        bodyVoxels.Solids.SetPose(id, new WorldPoint(0, FloorMetres + 1, 0), 0.2f);
        var feet = new Vector3(0, 4, 0);
        var velocity = Vector3.Zero;

        CharacterMoveResult result = default;
        for (var i = 0; i < 90; i++)
        {
            velocity.Y -= 20 * Step;
            result = mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.True(result.Grounded);
        Assert.Equal(3.2f, feet.Y, 0.005f);
    }

    [Fact]
    public void A_solid_rising_under_the_feet_carries_the_character_up()
    {
        var (mover, bodyVoxels) = MoverWorld();
        var id = bodyVoxels.Solids.Add(Block(30, 5, 30), 2, new Vector3(15, 0, 15));
        bodyVoxels.Solids.SetPose(id, new WorldPoint(0, FloorMetres + 1, 0), 0);
        var feet = new Vector3(0, 3.2f, 0);
        var velocity = Vector3.Zero;
        mover.Move(ref feet, ref velocity, in s_shape, Step);

        // Up by 3 cm in a step, as a bobbing head never moves so fast.
        bodyVoxels.Solids.SetPose(id, new WorldPoint(0, FloorMetres + 1.03, 0), 0);
        velocity = new Vector3(0, -0.3f, 0);
        var result = mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.Equal(CharacterMoveStatus.Lifted, result.Status);
        Assert.Equal(3.23f, feet.Y, 0.005f);
    }

    /// <summary>
    /// A post taller than the body slid into a character standing still (as a gate's leaf shuts on one) never carries it
    /// up its face: the character stays on the floor where it stood, overlapping the post, where it rose a step's height
    /// at each move to the post's top.
    /// </summary>
    [Fact]
    public void A_tall_solid_moved_into_a_character_never_lifts_it_up_its_face()
    {
        var (mover, bodyVoxels) = MoverWorld();
        // A post 40 cm square and 2.4 m tall, of 8 cm voxels.
        var id = bodyVoxels.Solids.Add(Block(5, 30, 5), 2, Vector3.Zero, unitCentimetres: 2);
        var feet = new Vector3(0, 2, 0);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 60; i++)
        {
            // From 0.6 m east of the body's axis to 0.6 m west, 2 cm a step, its middle on the body's line.
            bodyVoxels.Solids.SetPose(id, new WorldPoint(0.6 - 0.02 * i, FloorMetres, -0.2), 0);
            velocity = new Vector3(0, velocity.Y - 0.3f, 0);
            var result = mover.Move(ref feet, ref velocity, in s_shape, Step);

            Assert.NotEqual(CharacterMoveStatus.Lifted, result.Status);
        }

        Assert.Equal(2, feet.Y, 0.01f);
    }

    [Fact]
    public void A_wide_body_beside_a_turning_solid_allocates_nothing()
    {
        var (mover, bodyVoxels) = MoverWorld(radiusMetres: 8);
        // A head-sized block, 3.2 m by 4 m by 3.6 m of 4 cm voxels, its bottom 3 m above the floor.
        var id = bodyVoxels.Solids.Add(Block(80, 100, 90), 2, new Vector3(40, 0, 45));
        var feet = new Vector3(-3.5f, 2, 0);
        var velocity = Vector3.Zero;

        void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                bodyVoxels.Solids.SetPose(id, new WorldPoint(0, FloorMetres + 3, 0), i * 0.01f);
                velocity = new Vector3(i % 200 < 100 ? 1.5f : -1.5f, velocity.Y - 0.3f, 0.4f * MathF.Sin(i * 0.05f));
                mover.Move(ref feet, ref velocity, in s_wide, Step);
            }
        }

        Run(50);
        var before = SystemAllocation.StartMeasurement();
        Run(100);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private (VoxelCharacterMover Mover, LooseBodyVoxels BodyVoxels) MoverWorld(float radiusMetres = 5)
    {
        _world = TestWorlds.Load(new BoxesGenerator(Floor), new Vector3(0, 2, 0), radiusMetres: radiusMetres);
        var passability = new MoverWorldFixture.LeavesPass();
        var physics = _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        physics.Initialize();
        var colliders = _colliders = new VoxelColliders(
            physics,
            _world,
            passability,
            NullLogger<VoxelColliders>.Instance
        );
        colliders.Initialize();
        var bodies = new LooseBodies(
            physics,
            _world,
            colliders,
            new VoxelModels(_world),
            passability,
            Options.Create(new EngineOptions()),
            NullLogger<LooseBodies>.Instance
        );
        var bodyVoxels = new LooseBodyVoxels(bodies, _world, passability);
        return (new VoxelCharacterMover(_world, passability, bodyVoxels), bodyVoxels);
    }

    /// <summary>A solid box of <paramref name="x"/> by <paramref name="y"/> by <paramref name="z"/> voxels.</summary>
    private static SolidColumns Block(int x, int y, int z)
    {
        var chunk = new byte[VoxelConstants.ChunkVoxels];
        for (var k = 0; k < z; k++)
        {
            for (var j = 0; j < y; j++)
            {
                for (var i = 0; i < x; i++)
                {
                    chunk[i + Edge * j + Edge * Edge * k] = 1;
                }
            }
        }

        return SolidColumns.Build(chunk, new VoxelCoord(x, y, z));
    }

    /// <summary>
    /// The distance in world voxels along <paramref name="way"/> from <paramref name="from"/> at which a march of small
    /// steps first lies in one of the solid's voxels, or none within <paramref name="most"/>.
    /// </summary>
    private static float? March(
        byte[] chunk,
        VoxelCoord size,
        float scale,
        Vector3 pivot,
        Vector3 at,
        float yaw,
        Vector3 from,
        Vector3 way,
        float most
    )
    {
        float cos = MathF.Cos(yaw),
            sin = MathF.Sin(yaw);
        var step = scale * 0.002f;
        for (var t = 0f; t <= most; t += step)
        {
            var p = from + way * t;
            float dx = p.X - at.X,
                dz = p.Z - at.Z;
            var lx = (int)MathF.Floor((dx * cos - dz * sin) / scale + pivot.X);
            var lz = (int)MathF.Floor((dx * sin + dz * cos) / scale + pivot.Z);
            var ly = (int)MathF.Floor((p.Y - at.Y) / scale + pivot.Y);
            if (
                (uint)lx < (uint)size.X
                && (uint)ly < (uint)size.Y
                && (uint)lz < (uint)size.Z
                && chunk[lx + Edge * ly + Edge * Edge * lz] != 0
            )
            {
                return t;
            }
        }

        return null;
    }

    private static Vector3 RandomUnit(Random random)
    {
        while (true)
        {
            var v = new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1);
            if (v.LengthSquared() is > 0.01f and <= 1)
            {
                return Vector3.Normalize(v);
            }
        }
    }

    /// <summary>A ball with a box sticking out of one side and a hole through it: runs of air in its columns.</summary>
    private static byte[] Lopsided(out VoxelCoord size)
    {
        size = new VoxelCoord(30, 26, 22);
        var chunk = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    float dx = x - 11,
                        dy = y - 12,
                        dz = z - 11;
                    var ball = dx * dx + dy * dy + dz * dz < 100;
                    var box = x >= 18 && y is >= 4 and < 9 && z is >= 6 and < 16;
                    var hole = dx * dx + dz * dz < 9;
                    if ((ball || box) && !hole)
                    {
                        chunk[x + Edge * y + Edge * Edge * z] = 3;
                    }
                }
            }
        }

        return chunk;
    }

    /// <summary>Whether world voxel (x, y, z)'s centre lands on a solid voxel of the model, by the solids' rule.</summary>
    private static bool Solid(
        byte[] chunk,
        VoxelCoord size,
        float scale,
        Vector3 pivot,
        Vector3 at,
        float yaw,
        int x,
        int y,
        int z
    )
    {
        float cos = MathF.Cos(yaw),
            sin = MathF.Sin(yaw);
        float dx = x + 0.5f - at.X,
            dz = z + 0.5f - at.Z;
        var lx = (int)MathF.Floor((dx * cos - dz * sin) / scale + pivot.X);
        var lz = (int)MathF.Floor((dx * sin + dz * cos) / scale + pivot.Z);
        var ly = (int)MathF.Floor((y + 0.5f - at.Y) / scale + pivot.Y);
        return (uint)lx < (uint)size.X
            && (uint)ly < (uint)size.Y
            && (uint)lz < (uint)size.Z
            && chunk[lx + Edge * ly + Edge * Edge * lz] != 0;
    }

    private static (int Highest, int Lowest) Walk(
        byte[] chunk,
        VoxelCoord size,
        float scale,
        Vector3 pivot,
        Vector3 at,
        float yaw,
        in CircleFootprint footprint,
        int y0,
        int y1
    )
    {
        int highest = int.MinValue,
            lowest = int.MaxValue;
        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    if (Solid(chunk, size, scale, pivot, at, yaw, x, y, z))
                    {
                        highest = Math.Max(highest, y + 1);
                        lowest = Math.Min(lowest, y);
                    }
                }
            }
        }

        return (highest, lowest);
    }
}
