using System.Numerics;
using Itera.Engine.Navigation;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// Objects of 10 cm voxels placed and removed during play over 1 m ground, read in the mover's 10 cm cells: a wall 10 cm
/// thick blocks every navigation cell whose square it passes through and none beside them, a block 30 cm high raises
/// the floor of the cells it stands in, and both open again when removed.
/// </summary>
public sealed class NavigationPlacedObjectsTests : IDisposable
{
    private const byte Stone = 40;
    private const int Low = 40;
    private static readonly TimeSpan Timeout = TestTime.Limit(30);

    private readonly VoxelWorld _world;
    private readonly VoxelModels _models;
    private readonly ObjectWorld _objects;
    private readonly ObjectDamage _damage;
    private readonly NavigationService _navigation;
    private readonly VoxelObjectKindId _wall;
    private readonly VoxelObjectKindId _block;

    public NavigationPlacedObjectsTests()
    {
        var kinds = new VoxelObjectKinds();
        // 2 m long, 2 m tall, 10 cm thick, its anchor its bottom's middle.
        _wall = Register(
            kinds,
            "wall",
            new VoxelCoord(20, 20, 1),
            new VoxelCoord(100, 0, 5),
            s => s.FillBox(0, 0, 0, 200, 200, 10, Stone)
        );
        // 1 m square, 30 cm high.
        _block = Register(
            kinds,
            "block",
            new VoxelCoord(10, 3, 10),
            new VoxelCoord(50, 0, 50),
            s => s.FillBox(0, 0, 0, 100, 30, 100, Stone)
        );
        var generator = new FlatTestGenerator(new VoxelGrid(100));
        _world = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 0,
                Levels = 1,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 2,
                Seed = 17,
            },
            0,
            generator,
            NullLogger<VoxelWorld>.Instance,
            kinds: kinds
        );
        _world.Initialize();
        _world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(_world);
        _models = new VoxelModels(_world);
        var options = Options.Create(new EngineOptions());
        _objects = new ObjectWorld(_world, kinds, generator, options, NullLogger<ObjectWorld>.Instance);
        var kindModels = new VoxelObjects(
            kinds,
            _world,
            _models,
            new NothingPassable(),
            NullLogger<VoxelObjects>.Instance
        );
        _damage = new ObjectDamage(
            _world,
            _objects,
            kinds,
            kindModels,
            _models,
            options,
            NullLogger<ObjectDamage>.Instance
        );
        var passability = new NothingPassable();
        _navigation = new NavigationService(
            new FlatSource(),
            () => true,
            NullLogger<NavigationService>.Instance,
            _world,
            passability,
            new NavArea(-1600, -1600, 64),
            1024,
            placed: _objects,
            // The mover's 10 cm cells over the 1 m voxels.
            objectColumns: new ObjectColumns(_objects, kinds, passability, 10, _damage),
            damage: _damage
        )
        {
            JobIntervalSeconds = 0,
        };
        _navigation.Initialize();
        kindModels.Initialize();
        _objects.Initialize();
        _damage.Initialize();
        for (var i = 0; i < 100 && _objects.WaitingAreas > 0; i++)
        {
            _objects.Update();
        }

        Assert.True(_navigation.WaitUntilPublished(Timeout));
        _navigation.AfterFixedStep();
        Assert.True(_navigation.Ready);
    }

    public void Dispose()
    {
        _navigation.Dispose();
        _damage.Dispose();
        _models.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_thin_wall_blocks_the_cells_it_passes_through_and_opens_them_when_removed()
    {
        var grid = _navigation.Grid;
        // Along x from 0.3 to 2.3 m, from 4.15 to 4.25 m on z: in the row of cells from 4 to 4.5 m.
        Assert.True(_objects.TryPlace(_wall, new VoxelCoord(130, 0, 420), 0, VoxelEdit.NoMaker, out var wall));
        _navigation.AfterFixedStep();
        Assert.True(_navigation.Settle(Timeout));

        for (var x = -25; x <= 325; x += 50)
        {
            var inside = x is > 0 and < 250;
            Assert.Equal(inside, grid.IsBlocked(Cell(grid, x, 425)));
            Assert.False(grid.IsBlocked(Cell(grid, x, 375)));
            Assert.False(grid.IsBlocked(Cell(grid, x, 475)));
        }

        Assert.True(_objects.TryRemove(in wall, VoxelEdit.NoMaker));
        _navigation.AfterFixedStep();
        Assert.True(_navigation.Settle(Timeout));
        for (var x = 25; x <= 225; x += 50)
        {
            Assert.False(grid.IsBlocked(Cell(grid, x, 425)));
        }
    }

    [Fact]
    public void A_low_block_raises_the_floor_of_the_cells_it_stands_in()
    {
        var grid = _navigation.Grid;
        Assert.True(_objects.TryPlace(_block, new VoxelCoord(-500, 0, -500), 0, VoxelEdit.NoMaker, out var block));
        _navigation.AfterFixedStep();
        Assert.True(_navigation.Settle(Timeout));

        Assert.Equal(30, grid.Floor[Cell(grid, -525, -525)]);
        Assert.Equal(30, grid.Floor[Cell(grid, -475, -475)]);
        Assert.False(grid.IsBlocked(Cell(grid, -475, -475)));
        Assert.Equal(0, grid.Floor[Cell(grid, -425, -475)]);

        Assert.True(_objects.TryRemove(in block, VoxelEdit.NoMaker));
        _navigation.AfterFixedStep();
        Assert.True(_navigation.Settle(Timeout));
        Assert.Equal(0, grid.Floor[Cell(grid, -525, -525)]);
    }

    private static int Cell(NavGrid grid, int x, int z)
    {
        Assert.True(grid.TryCell(x, z, out var cell));
        return cell;
    }

    private static VoxelObjectKindId Register(
        VoxelObjectKinds kinds,
        string name,
        VoxelCoord size,
        VoxelCoord anchor,
        Action<VoxelObjectShape> fill
    )
    {
        var shape = new VoxelObjectShape(10, size, anchor);
        fill(shape);
        return kinds.Register(name, shape);
    }

    /// <summary>Open ground at 0 over the whole window, with one field to a corner.</summary>
    private sealed class FlatSource : INavGridSource
    {
        private static readonly NavSize s_size = new("small", 200, 30, 45, 64);
        private static readonly NavCostTable s_costs = new([10], 400, 3000);

        public int LowCentimetres => Low;

        public IReadOnlyList<NavFieldSpec> CreateFields() =>
            [new NavFieldSpec("corner", s_size, s_costs, [new VoxelCoord(-1475, 0, -1475)])];

        public void Fill(NavGrid grid, int firstColumn, int firstRow, int lastColumn, int lastRow)
        {
            for (var row = firstRow; row <= lastRow; row++)
            {
                var start = grid.Index(firstColumn, row);
                grid.Floor.AsSpan(start, lastColumn - firstColumn + 1).Clear();
                grid.Clear.AsSpan(start, lastColumn - firstColumn + 1).Fill(byte.MaxValue);
            }
        }
    }
}
