using System.Numerics;
using Itera.Engine.Navigation;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Worlds of voxels coarser than 10 cm (20, 50 cm, and 1 m), as a game deforming its terrain in large strokes would use:
/// a body stands on the ground inside its voxel, walks a 20 degree slope on its surface, cannot walk one past the limit, and
/// stops at a wall; a ball rests on the ground and settles back as a sphere fill; a crater lowers the ground and a rim
/// raises it; navigation reads the ground's floor; a ray meets the ground and a tool's edit takes the voxel it met; an
/// edit larger than a chunk spreads over steps; and at the default options level 0 streams 4.625 of its columns out.
/// </summary>
public sealed class CoarseVoxelTests
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void A_body_stands_on_level_ground_inside_its_voxel(int baseCentimetres)
    {
        using var world = Load(baseCentimetres);
        var feet = new Vector3(2.5f, 3, 2.5f);

        Fall(new VoxelCharacterMover(world, new NothingPassable()), ref feet);

        Assert.Equal(1.5f, feet.Y, 0.02f);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void A_20_degree_slope_is_walked_up_on_its_surface(int baseCentimetres)
    {
        var rise = Math.Tan(20 * Math.PI / 180);
        using var world = Load(baseCentimetres, rise);
        var mover = new VoxelCharacterMover(world, new NothingPassable());
        var feet = new Vector3(-1, 2, 2.5f);
        Fall(mover, ref feet);

        var velocity = Vector3.Zero;
        for (var i = 0; i < 70; i++)
        {
            velocity = new Vector3(4.5f, velocity.Y - Gravity * Step, 0);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        // An upright cylinder stands on the highest ground under it: between the slope's height under its centre and
        // under its uphill edge.
        Assert.True(feet.X > 3, $"The body stopped at {feet}.");
        Assert.InRange(
            feet.Y,
            (float)(1.5 + rise * feet.X) - 0.02f,
            (float)(1.5 + rise * (feet.X + s_shape.RadiusMetres)) + 0.03f
        );
    }

    /// <summary>
    /// A slope past the 45 degree limit makes, on these voxels, risers under the body's 41 cm step at 20 cm on treads
    /// narrower than its 26 cm radius (it would climb them as stairs but for the steepness rule's look round the body,
    /// at least 0.8 of a cell) and walls over it at 50 cm and 1 m.
    /// </summary>
    [Theory]
    [InlineData(20, 60)]
    [InlineData(20, 50)]
    [InlineData(50, 60)]
    [InlineData(100, 60)]
    public void A_slope_past_the_limit_cannot_be_walked_up(int baseCentimetres, double degrees)
    {
        using var world = Load(baseCentimetres, Math.Tan(degrees * Math.PI / 180));
        var mover = new VoxelCharacterMover(world, new NothingPassable());
        var feet = new Vector3(-1, 2, 2.5f);
        Fall(mover, ref feet);

        var velocity = Vector3.Zero;
        for (var i = 0; i < 120; i++)
        {
            velocity = new Vector3(4.5f, velocity.Y - Gravity * Step, 0);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        // A coarse voxel blurs where the slope starts, so the body may step onto its foot: no more than a step and a voxel
        // up, and no farther along than a voxel.
        var voxel = baseCentimetres / 100f;
        Assert.True(feet.Y < 1.5f + s_shape.StepMetres + voxel, $"The body climbed to {feet}.");
        Assert.True(feet.X < voxel + 0.3f, $"The body went up to {feet}.");
    }

    /// <summary>
    /// On cells wider than the body's radius, a slope past the limit whose columns each rise less than its step is the
    /// same voxels as stairs a game might build (30 cm treads, 36 cm risers for 50 degrees on 30 cm voxels): the body
    /// climbs it, a tread at a time.
    /// </summary>
    [Fact]
    public void On_cells_wider_than_the_body_a_slope_rising_less_than_a_step_a_cell_reads_as_stairs()
    {
        using var world = Load(30, Math.Tan(50 * Math.PI / 180));
        var mover = new VoxelCharacterMover(world, new NothingPassable());
        var feet = new Vector3(-1, 2, 2.5f);
        Fall(mover, ref feet);

        var velocity = Vector3.Zero;
        for (var i = 0; i < 120; i++)
        {
            velocity = new Vector3(4.5f, velocity.Y - Gravity * Step, 0);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.True(feet.Y > 4, $"The body stayed at {feet}.");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void A_wall_stops_the_body_at_its_radius(int baseCentimetres)
    {
        using var world = Load(baseCentimetres, wallCentimetres: 600);
        var mover = new VoxelCharacterMover(world, new NothingPassable());
        var feet = new Vector3(4, 2, 2.5f);
        Fall(mover, ref feet);

        var velocity = Vector3.Zero;
        var wall = false;
        for (var i = 0; i < 90; i++)
        {
            velocity = new Vector3(4.5f, velocity.Y - Gravity * Step, 0);
            wall |= mover.Move(ref feet, ref velocity, in s_shape, Step).HitWall;
        }

        Assert.True(wall);
        Assert.Equal(6 - s_shape.RadiusMetres, feet.X, 0.01f);
        Assert.Equal(1.5f, feet.Y, 0.02f);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void A_ball_rests_on_the_ground_and_settles_back_as_a_sphere_fill(int baseCentimetres)
    {
        using var world = Load(baseCentimetres);
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        physics.Initialize();
        var passability = new NothingPassable();
        using var colliders = new VoxelColliders(physics, world, passability, NullLogger<VoxelColliders>.Instance);
        colliders.Initialize();
        var bodies = new LooseBodies(
            physics,
            world,
            colliders,
            new VoxelModels(world),
            passability,
            Options.Create(new EngineOptions()),
            NullLogger<LooseBodies>.Instance
        );
        Assert.True(
            bodies.TryCreateBall(12, CoarseTerrain.Dirt, new Vector3(2.5f, 2.2f, 2.5f), Vector3.Zero, out var ball)
        );

        var steps = 0;
        var resting = 0;
        while (resting < 60)
        {
            Assert.True(++steps < 1200, $"The ball did not rest; it is at {bodies.PivotOf(ball)}.");
            colliders.Update(Step);
            physics.Step(Step);
            resting = bodies.IsAtRest(ball) ? resting + 1 : 0;
        }

        // The ground's surface is at 1.5 m inside its voxel, and the ball's pivot 12 cm over its lowest centimetre.
        Assert.Equal(1.62f, bodies.PivotOf(ball).Y, 0.03f);
        var sequence = bodies.Settle(ball);
        ref readonly var edit = ref world.GetEdit(sequence);
        Assert.Equal((VoxelEditShape.Sphere, VoxelEditOperation.Fill), (edit.Shape, edit.Operation));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void A_crater_lowers_the_ground_and_a_rim_raises_it(int baseCentimetres)
    {
        using var world = Load(baseCentimetres);
        var mover = new VoxelCharacterMover(world, new NothingPassable());
        // A blast 2 m round on the ground leaves a bowl 50 cm under it; its voxels' densities hold the share each keeps,
        // so a coarse voxel's surface is the bowl's mean height over its square, and the body stands on the highest
        // ground under it: no more than a fifth of a voxel over the bowl's bottom.
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(250, 150, 250), 200, VoxelEditOperation.Clear));
        var feet = new Vector3(2.5f, 2, 2.5f);
        Fall(mover, ref feet);
        Assert.InRange(feet.Y, -0.52f, -0.5f + 0.02f + 0.2f * baseCentimetres / 100);

        // Earth thrown up beside it: a 60 cm sphere filled into the air on the ground.
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(650, 150, 250), 60, VoxelEditOperation.Fill, CoarseTerrain.Dirt));
        var rim = new Vector3(6.5f, 3, 2.5f);
        Fall(mover, ref rim);
        Assert.True(rim.Y > 1.55f, $"The rim stands at {rim.Y} m.");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void Navigation_reads_the_grounds_floor_from_its_coarse_voxels(int baseCentimetres)
    {
        using var world = Load(baseCentimetres);
        var scanner = new NavGridScanner(world, new NothingPassable(), CoarseSource.Low, new CoarseSource());
        var grid = new NavGrid(0, 0, 8, 8);
        grid.Floor.AsSpan().Fill(150);
        grid.Clear.AsSpan().Fill(byte.MaxValue);

        for (var cell = 0; cell < grid.Count; cell++)
        {
            Assert.True(scanner.IsResident(grid, cell));
            scanner.Rescan(grid, cell);
            Assert.InRange(grid.Floor[cell], 148, 152);
            Assert.False(grid.IsBlocked(cell));
        }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void A_ray_meets_the_ground_and_a_tools_edit_takes_the_voxel_it_met(int baseCentimetres)
    {
        using var world = Load(baseCentimetres);

        Assert.True(world.Raycast(new Vector3(2.5f, 5, 2.5f), -Vector3.UnitY, 10, out var hit));
        // The ray meets the top of the highest voxel the ground makes solid (one whose sample point lies under it), within
        // a voxel of the surface.
        Assert.InRange(
            hit.DistanceMetres,
            3.5f - baseCentimetres / 100f - 0.001f,
            3.5f + baseCentimetres / 100f + 0.001f
        );
        Assert.Equal(CoarseTerrain.Dirt, hit.Material);

        world.Apply(VoxelEdit.Sphere(hit.BoxMiddle, baseCentimetres, VoxelEditOperation.Clear));

        Assert.True(world.TryGetMaterial(hit.BoxMiddle, out var material));
        Assert.Equal(VoxelConstants.Air, material);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void An_edit_larger_than_a_chunk_spreads_over_the_steps(int baseCentimetres)
    {
        using var world = Load(baseCentimetres);
        // 140 voxels across: more than a chunk's 128.
        var centre = new VoxelCoord(250, 150, 250);
        world.Apply(VoxelEdit.Sphere(centre, 70 * baseCentimetres, VoxelEditOperation.Clear));

        Assert.True(world.PendingEditColumns > 0);
        world.ApplyPendingEdits(int.MaxValue);

        Assert.Equal(0, world.PendingEditColumns);
        Assert.True(world.TryGetMaterial(new VoxelCoord(250, 100, 250), out var material));
        Assert.Equal(VoxelConstants.Air, material);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public void At_the_default_options_level_0_streams_4_625_of_its_columns_out(int baseCentimetres)
    {
        // Wide enough for column 5 at 1 m (the default bounds hold 4 columns each side there).
        var grid = VoxelGrid.Sized(baseCentimetres, 1_000, -10, 20);
        var settings = new VoxelSettings
        {
            Levels = 2,
            MemoryBudgetMegabytes = 256,
            WorkerThreads = 2,
        };
        using var world = new VoxelWorld(settings, 0, new FlatTestGenerator(grid), NullLogger<VoxelWorld>.Instance);
        world.Initialize();
        world.SetCenters([new Vector3(1, 1, 1)]);
        TestWorlds.WaitUntilLoaded(world);

        Assert.Equal(4.625 * grid.ColumnCentimetres(0) / 100, world.RadiusMetres(0), 6);
        // From inside column 0, column 4's nearest side is 4 columns away and column 5's 5: past the radius.
        Assert.True(world.IsResident(new ColumnKey(0, 4, 0)));
        Assert.False(world.IsResident(new ColumnKey(0, 5, 0)));
    }

    private static VoxelWorld Load(int baseCentimetres, double rise = 0, int wallCentimetres = int.MaxValue) =>
        TestWorlds.Load(
            new CoarseTerrain(VoxelGrid.Sized(baseCentimetres, 300, -10, 20), rise, wallCentimetres),
            new Vector3(2.5f, 2, 2.5f),
            radiusMetres: 8
        );

    /// <summary>Lets the body fall onto what is under it.</summary>
    private static void Fall(VoxelCharacterMover mover, ref Vector3 feet)
    {
        var velocity = Vector3.Zero;
        for (var i = 0; i < 120; i++)
        {
            velocity = new Vector3(0, velocity.Y - Gravity * Step, 0);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }
    }

    /// <summary>
    /// Dirt up to 1.5 m, rising <paramref name="rise"/> per centimetre along x from x = 0, and a wall of dirt 4 m high
    /// from <paramref name="wallCentimetres"/> on (a face on the voxels' boundaries at 20, 50 cm, and 1 m). Each voxel
    /// holds what its sample point gives, with a density of the share of it below the ground there.
    /// </summary>
    private sealed class CoarseTerrain(VoxelGrid grid, double rise, int wallCentimetres) : IChunkGenerator
    {
        public const byte Dirt = 2;
        private const double Ground = 150;
        private const double WallTop = 400;

        public VoxelGrid Grid { get; } = grid;

        public string Id => "test.coarse";

        public int Version => 1;

        public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
        {
            for (var layer = 0; layer < layers.Length; layer++)
            {
                layers[layer] = column.Chunk(layer, Grid).Y < 0 ? ChunkFill.Uniform(Dirt) : ChunkFill.Mixed;
            }
        }

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) => Fill(chunk, seed, voxels, default);

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels, Span<byte> densities)
        {
            const int size = VoxelConstants.ChunkSize;
            var level = chunk.Level;
            var edge = Grid.Edge(level);
            var origin = chunk.LevelOrigin;
            for (var z = 0; z < size; z++)
            {
                for (var x = 0; x < size; x++)
                {
                    var sampleX = Grid.SampleAt(origin.X + x, level);
                    var height = sampleX >= wallCentimetres ? WallTop : Ground + rise * Math.Max(sampleX, 0);
                    for (var y = 0; y < size; y++)
                    {
                        var index = x + size * (y + size * z);
                        var low = (double)(origin.Y + y) * edge;
                        var material = Grid.SampleAt(origin.Y + y, level) < height ? Dirt : VoxelConstants.Air;
                        voxels[index] = material;
                        if (!densities.IsEmpty)
                        {
                            var share = Math.Clamp((height - low) / edge, 0, 1);
                            densities[index] = VoxelDensity.Fit(material, (byte)Math.Floor(share * 255 + 0.5));
                        }
                    }
                }
            }
        }
    }

    /// <summary>Navigation's source over <see cref="CoarseTerrain"/>'s level ground.</summary>
    private sealed class CoarseSource : INavGridSource
    {
        public const int Low = 25;

        public int LowCentimetres => Low;

        public IReadOnlyList<NavFieldSpec> CreateFields() => throw new NotSupportedException();

        public void Fill(NavGrid grid, int firstColumn, int firstRow, int lastColumn, int lastRow) =>
            throw new NotSupportedException();

        public int GroundAt(int x, int z) => 150;

        public bool IsGround(byte material) => material == CoarseTerrain.Dirt;
    }
}
