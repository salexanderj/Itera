using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// The objects a world places near where it is loaded (<see cref="ObjectWorld"/>): each kept area holds the placer's
/// objects of its square once; areas follow the resident columns below the hand-over level, at most eight filled an
/// update; the query finds the objects whose kind reaches into a box; a placer's bad kind stops the fill; with no placer
/// nothing is kept; and following and querying allocate nothing.
/// </summary>
public sealed class ObjectWorldTests : IDisposable
{
    private const byte Leaves = 31;
    private readonly VoxelObjectKinds _kinds = new();
    private readonly PlacingGenerator _generator;
    private VoxelWorld _world;

    public ObjectWorldTests()
    {
        // Two kinds: a bush 1 m across and a rock 40 cm across, at 5 cm.
        _kinds.Register("bush", Ball(20));
        _kinds.Register("rock", Ball(8));
        _generator = new PlacingGenerator(_kinds.Count);
        _world = TestWorlds.Load(_generator, Vector3.Zero, radiusMetres: 6, levels: 3);
    }

    public void Dispose() => _world.Dispose();

    /// <summary>
    /// Every kept area, once filled, holds exactly the objects the placer gives for its square, and no object is in two
    /// areas.
    /// </summary>
    [Fact]
    public void Each_area_holds_the_placers_objects_of_its_square_once()
    {
        var objects = Follow();

        Assert.True(objects.AreaCount > 0);
        Assert.Equal(0, objects.WaitingAreas);
        Assert.True(objects.Near(-100, 100, -100, 100, []));
        var found = new List<ObjectPlacement>();
        objects.Near(-1_000_000, 1_000_000, -1_000_000, 1_000_000, found);
        Assert.Equal(found.Count, found.Select(o => o.Id).Distinct().Count());
        var expected = new List<ObjectPlacement>();
        var width = objects.AreaCentimetres;
        foreach (var (x, z) in found.Select(o => objects.AreaAt(o.Anchor.X, o.Anchor.Z)).Distinct())
        {
            _generator.Placer.Find(
                _world.Seed,
                (int)(x * width),
                (int)((x + 1) * width),
                (int)(z * width),
                (int)((z + 1) * width),
                expected
            );
        }

        Assert.Equal(expected.Select(o => o.Id.Value).Order(), found.Select(o => o.Id.Value).Order());
        Assert.Equal(5_120, width);
    }

    /// <summary>
    /// An area is kept while a column below the hand-over level is resident in it: moved 300 m away, the world drops the
    /// first areas and keeps new ones, filled at most eight an update.
    /// </summary>
    [Fact]
    public void Areas_follow_the_resident_columns_at_most_eight_filled_an_update()
    {
        var objects = Follow();
        Assert.True(objects.IsFilledAt(0, 0));

        _world.SetCenters([new Vector3(300, 0, 0)]);
        TestWorlds.WaitUntilLoaded(_world);
        var waiting = -1;
        while (objects.WaitingAreas > 0 || waiting < 0)
        {
            var before = objects.WaitingAreas;
            objects.Update();
            if (waiting >= 0)
            {
                Assert.InRange(before - objects.WaitingAreas, 0, ObjectWorld.FillsPerUpdate);
            }

            waiting = objects.WaitingAreas;
        }

        Assert.False(objects.IsFilledAt(0, 0));
        Assert.True(objects.IsFilledAt(30_000, 0));
    }

    /// <summary>
    /// The query finds an object whose anchor lies outside the box but whose kind reaches into it, and leaves out one whose
    /// kind does not.
    /// </summary>
    [Fact]
    public void The_query_finds_the_objects_whose_kind_reaches_into_the_box()
    {
        var objects = Follow();
        var all = new List<ObjectPlacement>();
        objects.Near(-500, 500, -500, 500, all);
        var bush = all.First(o => o.Kind.Value == 0);
        var reach = _kinds[bush.Kind].ReachCentimetres;
        Assert.InRange(reach, 70, 72);

        var found = new List<ObjectPlacement>();
        // A point box just within the bush's reach of its anchor, and one just beyond.
        objects.Near(bush.Anchor.X + reach, bush.Anchor.X + reach, bush.Anchor.Z, bush.Anchor.Z, found);
        Assert.Contains(found, o => o.Id == bush.Id);
        found.Clear();
        objects.Near(bush.Anchor.X + reach + 1, bush.Anchor.X + reach + 1, bush.Anchor.Z, bush.Anchor.Z, found);
        Assert.DoesNotContain(found, o => o.Id == bush.Id);
    }

    /// <summary>A placer that gives a kind the run did not register stops the fill, naming the object.</summary>
    [Fact]
    public void A_placers_unknown_kind_stops_the_fill()
    {
        _world.Dispose();
        _world = TestWorlds.Load(new PlacingGenerator(kinds: 3), Vector3.Zero, radiusMetres: 6, levels: 3);
        var objects = World(new PlacingGenerator(kinds: 3));

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                objects.Update();
            }
        });
        Assert.Contains("kind 2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A world whose generator places nothing keeps no areas, and its query is whole and empty.</summary>
    [Fact]
    public void Without_a_placer_nothing_is_kept()
    {
        var objects = World(new FlatTestGenerator(new VoxelGrid(10)));
        objects.Update();

        Assert.Equal(0, objects.AreaCount);
        var found = new List<ObjectPlacement>();
        Assert.True(objects.Near(-10_000, 10_000, -10_000, 10_000, found));
        Assert.Empty(found);
    }

    /// <summary>
    /// A reset unloads every column, so every area goes with it; as the columns load again, the areas come back filled.
    /// </summary>
    [Fact]
    public void A_reset_drops_every_area_and_the_reloaded_columns_bring_them_back()
    {
        var objects = Follow();
        Assert.True(objects.IsFilledAt(0, 0));

        _world.Reset();
        objects.Update();
        Assert.Equal(0, objects.AreaCount);
        Assert.Equal(0, objects.Count);

        TestWorlds.WaitUntilLoaded(_world);
        for (var i = 0; i < 100 && (objects.WaitingAreas > 0 || objects.AreaCount == 0); i++)
        {
            objects.Update();
        }

        Assert.True(objects.IsFilledAt(0, 0));
    }

    /// <summary>
    /// With many areas wanted at once, each update fills the eight nearest a streaming centre: the centre's own first.
    /// </summary>
    [Fact]
    public void Areas_nearest_a_streaming_centre_are_filled_first()
    {
        _world.Dispose();
        _world = TestWorlds.Load(_generator, new Vector3(40, 0, -30), radiusMetres: 120, levels: 1);
        var objects = World(_generator);
        Assert.True(objects.WaitingAreas > 2 * ObjectWorld.FillsPerUpdate);

        objects.Update();

        Assert.True(objects.IsFilledAt(4_000, -3_000));
        // The eight filled lie no farther from the centre than any still waiting.
        var width = objects.AreaCentimetres;
        var filled = new List<double>();
        var waiting = new List<double>();
        for (var z = -10; z <= 10; z++)
        {
            for (var x = -10; x <= 10; x++)
            {
                double dx = (x + 0.5) * width - 4_000,
                    dz = (z + 0.5) * width + 3_000;
                var distance = Math.Sqrt(dx * dx + dz * dz);
                if (objects.IsFilledAt(x * width, z * width))
                {
                    filled.Add(distance);
                }
                else if (objects.IsKeptAt(x * width, z * width))
                {
                    waiting.Add(distance);
                }
            }
        }

        Assert.Equal(ObjectWorld.FillsPerUpdate, filled.Count);
        Assert.True(filled.Max() <= waiting.Min(), $"Filled up to {filled.Max()} cm, waiting from {waiting.Min()} cm.");
    }

    /// <summary>
    /// Once its tables have grown, following the world as it moves away and its areas go, come, and fill allocates
    /// nothing.
    /// </summary>
    [Fact]
    public void Following_a_moving_world_allocates_nothing_once_grown()
    {
        var objects = Follow();
        Move(objects, 300);
        Move(objects, 0);
        _world.SetCenters([new Vector3(300, 0, 0)]);
        TestWorlds.WaitUntilLoaded(_world);

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100 && (i == 0 || objects.WaitingAreas > 0); i++)
        {
            objects.Update();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.True(objects.IsFilledAt(30_000, 0));
    }

    [Fact]
    public void Following_and_querying_allocate_nothing()
    {
        var objects = Follow();
        var found = new List<ObjectPlacement>(4096);
        objects.Near(-10_000, 10_000, -10_000, 10_000, found);

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            objects.Update();
            found.Clear();
            objects.Near(-10_000, 10_000, -10_000, 10_000, found);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    private void Move(ObjectWorld objects, float x)
    {
        _world.SetCenters([new Vector3(x, 0, 0)]);
        TestWorlds.WaitUntilLoaded(_world);
        for (var i = 0; i < 100 && (i == 0 || objects.WaitingAreas > 0); i++)
        {
            objects.Update();
        }
    }

    /// <summary>The objects of this test's world, followed until every kept area is filled.</summary>
    private ObjectWorld Follow()
    {
        var objects = World(_generator);
        for (var i = 0; i < 100 && objects.WaitingAreas > 0; i++)
        {
            objects.Update();
        }

        return objects;
    }

    private ObjectWorld World(IChunkGenerator generator)
    {
        var objects = new ObjectWorld(
            _world,
            _kinds,
            generator,
            Options.Create(new EngineOptions()),
            NullLogger<ObjectWorld>.Instance
        );
        objects.Initialize();
        return objects;
    }

    private static VoxelObjectShape Ball(int voxels)
    {
        var half = voxels * 5 / 2.0;
        var shape = new VoxelObjectShape(
            5,
            new VoxelCoord(voxels, voxels, voxels),
            new VoxelCoord((int)half, 0, (int)half)
        );
        shape.FillEllipsoid(half, half, half, half - 5, half - 5, half - 5, Leaves);
        return shape;
    }

    /// <summary>A flat 10 cm world whose placer puts an object in every 4 m cell, of kind and heading by the cell's hash.</summary>
    private sealed class PlacingGenerator(int kinds) : IChunkGenerator
    {
        private readonly FlatTestGenerator _ground = new(new VoxelGrid(10));

        public TestPlacer Placer { get; } = new(kinds);

        public VoxelGrid Grid => _ground.Grid;

        public string Id => "test.placing";

        public int Version => 1;

        public IObjectPlacer Objects => Placer;

        public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) =>
            _ground.DescribeColumn(column, seed, layers);

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) => _ground.Fill(chunk, seed, voxels);
    }

    /// <summary>The test's placer: one thread's, so it keeps one buffer and allocates nothing once it has grown.</summary>
    private sealed class TestPlacer(int kinds) : IObjectPlacer
    {
        private const ulong Salt = 0x7465737473;
        private static readonly JitteredGrid s_grid = new(400, 0, Salt);
        private JitteredPoint[] _points = [];

        public void Find(ulong seed, int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found)
        {
            var capacity = s_grid.Capacity(minX, maxX - 1, minZ, maxZ - 1);
            if (_points.Length < capacity)
            {
                _points = new JitteredPoint[capacity];
            }

            var points = _points;
            var count = s_grid.Find(seed, 1, minX, maxX - 1, minZ, maxZ - 1, points);
            for (var i = 0; i < count; i++)
            {
                ref readonly var point = ref points[i];
                if (point.X < minX || point.X >= maxX || point.Z < minZ || point.Z >= maxZ)
                {
                    continue;
                }

                var kind = (ushort)(point.State % (ulong)kinds);
                var heading = (int)((point.State >> 20) % ObjectPlacement.HeadingSteps);
                found.Add(
                    new ObjectPlacement(
                        ObjectId.Of(in point, Salt),
                        new VoxelObjectKindId(kind),
                        new VoxelCoord(point.X, 0, point.Z),
                        heading
                    )
                );
            }
        }
    }
}
