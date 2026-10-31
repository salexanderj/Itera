using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Navigation;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Objects;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// Navigation over the voxel objects a world places (<see cref="NavigationService"/> with the objects' world, the mover's
/// reading of them, and their damage, over <see cref="PlacingGenerator"/>'s flat stone): the objects as generated are
/// stamped onto the cells the source fills (a trunk blocks its cell, a low stone raises the floor, a slab overhead lowers
/// the headroom, leaves change nothing), and a read from the placed objects agrees; a trunk cut through stays blocked until
/// its top's removal is logged, then opens, and a reset blocks it again; a cell is read only once the objects round it are
/// known, and a removal logged before then waits as an edit of its own; a moved window stamps the objects in the strips
/// it brings in; and reading edits among objects allocates nothing.
/// </summary>
public sealed class NavigationObjectsTests
{
    private const byte Bark = 30;
    private const byte Leaves = 31;
    private const byte Rock = 40;
    private const int Low = 40;
    private static readonly TimeSpan Timeout = TestTime.Limit(30);

    /// <summary>The 64 by 64 cells round the middle the tests' grid covers: navigation's whole area, so it never moves.</summary>
    private static readonly NavArea Area = new(-1600, -1600, 64);

    [Fact]
    public void The_objects_as_generated_block_raise_and_hang_over_the_cells_they_stand_in()
    {
        using var scene = new Scene();
        var grid = scene.Navigation.Grid;

        Assert.True(grid.IsBlocked(Cell(grid, 125, 125)));
        Assert.False(grid.IsBlocked(Cell(grid, 175, 125)));
        var stone = Cell(grid, 525, 125);
        Assert.False(grid.IsBlocked(stone));
        Assert.Equal(30, grid.Floor[stone]);
        Assert.True(grid.IsBlocked(Cell(grid, 525, 525)));
        var bush = Cell(grid, -275, 125);
        Assert.Equal(0, grid.Floor[bush]);
        Assert.Equal(byte.MaxValue, grid.Clear[bush]);
        var canopy = Cell(grid, -275, 525);
        Assert.Equal(0, grid.Floor[canopy]);
        Assert.Equal(150, grid.ClearCentimetres(canopy));

        // Every cell read again from the placed objects (as a live edit's cells are) keeps its floor and whether it is
        // blocked, and its headroom where no object raised its floor (a stamp keeps the headroom's top, a read looks up
        // from the new floor).
        var scanner = new NavGridScanner(scene.World, new LeavesPass(), Low, scene.Source) { Objects = scene.Columns };
        var read = new NavGrid(grid.OriginX, grid.OriginZ, grid.Width, grid.Depth);
        read.CopyRows(grid, 0, grid.Depth - 1);
        for (var cell = 0; cell < read.Count; cell++)
        {
            Assert.True(scanner.IsResident(read, cell));
            scanner.Rescan(read, cell);
            Assert.Equal(grid.Floor[cell], read.Floor[cell]);
            Assert.Equal(grid.IsBlocked(cell), read.IsBlocked(cell));
            if (grid.Floor[cell] == 0)
            {
                Assert.Equal(grid.Clear[cell], read.Clear[cell]);
            }
        }
    }

    /// <summary>
    /// With the source's floor 3 cm under the voxels' ground, the objects' voxels start off the floor, as a read judges:
    /// the stone still raises the floor and the trunk still blocks (not 2 cm of headroom under it).
    /// </summary>
    [Fact]
    public void An_object_whose_voxels_start_off_the_floor_stands_on_it()
    {
        using var scene = new Scene(sourceFloor: -3);
        var grid = scene.Navigation.Grid;

        var stone = Cell(grid, 525, 125);
        Assert.False(grid.IsBlocked(stone));
        Assert.Equal(30, grid.Floor[stone]);
        Assert.True(grid.IsBlocked(Cell(grid, 125, 125)));
        Assert.Equal(-3, grid.Floor[Cell(grid, 325, 125)]);
    }

    [Fact]
    public void A_trunk_cut_through_opens_its_cell_once_its_top_is_removed()
    {
        using var scene = new Scene();
        var navigation = scene.Navigation;
        var grid = navigation.Grid;
        var cell = Cell(grid, 125, 125);
        Assert.True(grid.IsBlocked(cell));

        // Through the trunk from 10 to 30 cm: its top still stands until the labeller finds it cut off.
        scene.World.Apply(
            VoxelEdit.Box(new VoxelCoord(90, 10, 90), new VoxelCoord(70, 20, 70), VoxelEditOperation.Clear)
        );
        Assert.True(navigation.Settle(Timeout));
        Assert.True(grid.IsBlocked(cell));

        scene.Damage.Update();
        scene.Damage.Labeller.LabelPending();
        scene.Damage.Update();
        Assert.Equal(VoxelEditOperation.RemovePart, scene.World.GetEdit(2).Operation);
        Assert.True(navigation.Settle(Timeout));

        // The stump stands 10 cm high, open over it.
        Assert.False(grid.IsBlocked(cell));
        Assert.Equal(10, grid.Floor[cell]);
        Assert.Equal(byte.MaxValue, grid.Clear[cell]);

        // A reset fills the window again with the trunk as generated, and heals it.
        scene.World.Reset();
        Assert.True(navigation.Settle(Timeout));
        Assert.True(grid.IsBlocked(cell));
    }

    /// <summary>
    /// A cut and its top's removal logged while the trunk's area is not filled each wait as edits (the removal counts as a
    /// change of its own, its stamp changing nothing); once the area fills, the cell is read open over the stump.
    /// </summary>
    [Fact]
    public void A_removal_logged_before_its_objects_are_known_is_read_once_they_are()
    {
        using var scene = new Scene(fill: false);
        var navigation = scene.Navigation;
        var grid = navigation.Grid;
        var cell = Cell(grid, 125, 125);

        scene.World.Apply(
            VoxelEdit.Box(new VoxelCoord(90, 10, 90), new VoxelCoord(70, 20, 70), VoxelEditOperation.Clear)
        );
        scene.World.Apply(
            VoxelEdit.RemovePart(1, new VoxelCoord(6, 10, 6), new VoxelCoord(100, 30, 100), new VoxelCoord(50, 290, 50))
        );
        navigation.AfterFixedStep();
        Assert.Equal(2, navigation.UncheckedEdits);
        Assert.True(grid.IsBlocked(cell));

        scene.FillAreas();
        Assert.True(navigation.Settle(Timeout));
        Assert.Equal(0, navigation.UncheckedEdits);
        Assert.False(grid.IsBlocked(cell));
        Assert.Equal(10, grid.Floor[cell]);
    }

    /// <summary>A window that moves on stamps the objects in the strip it brings in: a trunk 45 m out blocks its cell.</summary>
    [Fact]
    public void A_moved_window_stamps_the_objects_in_the_strips_it_brings_in()
    {
        using var scene = new Scene(area: new NavArea(-6400, -6400, 256), windowCells: 128);
        var navigation = scene.Navigation;
        var grid = navigation.Grid;
        Assert.False(grid.TryCell(4525, 125, out _));

        navigation.Follow(new WorldPoint(40, 0, 0));
        Assert.True(navigation.Settle(Timeout));

        Assert.Equal(1, navigation.Moves);
        Assert.True(grid.IsBlocked(Cell(grid, 4525, 125)));
        Assert.True(grid.IsBlocked(Cell(grid, 125, 125)));
    }

    [Fact]
    public void A_cell_is_read_only_once_the_objects_round_it_are_known()
    {
        using var scene = new Scene(fill: false);
        var navigation = scene.Navigation;
        var grid = navigation.Grid;
        var cell = Cell(grid, 125, 125);
        Assert.True(grid.IsBlocked(cell));

        // A dig in the ground at the trunk's foot: read with the trunk unknown, the cell would open. It waits with the
        // stamped edits (the service's check; the scanner's own, in a read, would put it back there too).
        scene.World.Apply(VoxelEdit.Sphere(new VoxelCoord(125, -5, 125), 12, VoxelEditOperation.Clear));
        navigation.AfterFixedStep();
        Assert.Equal(1, navigation.UncheckedEdits);

        scene.FillAreas();
        Assert.True(navigation.Settle(Timeout));
        Assert.Equal(0, navigation.UncheckedEdits);
        Assert.True(grid.IsBlocked(cell));
    }

    /// <summary>
    /// Digs under the stone (read as edits from the voxels and the damaged stone) and bites out of the trunk's side along
    /// one height (covering no cell's centre: read through the objects' changes), each round, allocate nothing once two
    /// rounds have made the objects' chunks and grown their lists (a bite at a new height would grow a column's runs).
    /// </summary>
    [Fact]
    public void Fixed_steps_reading_edits_among_objects_allocate_nothing()
    {
        using var scene = new Scene();
        var navigation = scene.Navigation;
        long allocated = 0;
        for (var round = 0; round < 6; round++)
        {
            scene.World.Apply(VoxelEdit.Sphere(new VoxelCoord(500 + 10 * round, 0, 125), 20, VoxelEditOperation.Clear));
            scene.World.Apply(
                VoxelEdit.Sphere(new VoxelCoord(105 + 10 * round, 100, 100), 12, VoxelEditOperation.Clear)
            );
            var before = SystemAllocation.StartMeasurement();
            var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
            for (var step = 0; Stopwatch.GetTimestamp() < deadline; step++)
            {
                navigation.AfterFixedStep();
                if (step > 2 && !navigation.Recomputing && navigation.PendingRescans == 0)
                {
                    break;
                }

                Thread.Sleep(1);
            }

            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.False(navigation.Recomputing);
            if (round > 1)
            {
                allocated += bytes;
            }
        }

        Assert.Equal(0, allocated);
    }

    private static int Cell(NavGrid grid, int x, int z)
    {
        Assert.True(grid.TryCell(x, z, out var cell));
        return cell;
    }

    /// <summary>
    /// Flat stone with a trunk, a low stone, a rock, a bush of leaves, a slab hanging 1.5 m up, and a second trunk 45 m
    /// out, their objects' world,
    /// models, and damage (labelled on the test's thread), and navigation over them, started in a match's order (before
    /// the objects are initialised).
    /// </summary>
    private sealed class Scene : IDisposable
    {
        public Scene(bool fill = true, NavArea? area = null, int windowCells = 1024, int sourceFloor = 0)
        {
            Source = new FlatSource(sourceFloor);
            var kinds = new VoxelObjectKinds();
            var trunk = Register(
                kinds,
                "trunk",
                5,
                new VoxelCoord(12, 64, 12),
                new VoxelCoord(30, 0, 30),
                s => s.FillCapsule(30, -10, 30, 30, 300, 30, 25, Bark)
            );
            var stone = Register(
                kinds,
                "stone",
                5,
                new VoxelCoord(22, 8, 22),
                new VoxelCoord(55, 0, 55),
                s => s.FillBox(5, -10, 5, 105, 30, 105, Rock)
            );
            var rock = Register(
                kinds,
                "rock",
                5,
                new VoxelCoord(26, 12, 22),
                new VoxelCoord(65, 0, 55),
                s => s.FillBox(5, -10, 5, 125, 50, 105, Rock)
            );
            var bush = Register(
                kinds,
                "bush",
                4,
                new VoxelCoord(32, 30, 32),
                new VoxelCoord(64, 0, 64),
                s => s.FillEllipsoid(64, 50, 64, 60, 50, 60, Leaves)
            );
            var slab = Register(
                kinds,
                "slab",
                5,
                new VoxelCoord(24, 36, 24),
                new VoxelCoord(60, 0, 60),
                s => s.FillBox(10, 150, 10, 110, 170, 110, Rock)
            );
            List<ObjectPlacement> placements =
            [
                new ObjectPlacement(new ObjectId(1), trunk, new VoxelCoord(125, 0, 125), 0),
                new ObjectPlacement(new ObjectId(2), stone, new VoxelCoord(525, 0, 125), 0),
                new ObjectPlacement(new ObjectId(3), rock, new VoxelCoord(525, 0, 525), 700),
                new ObjectPlacement(new ObjectId(4), bush, new VoxelCoord(-275, 0, 125), 0),
                new ObjectPlacement(new ObjectId(5), slab, new VoxelCoord(-275, 0, 525), 0),
                new ObjectPlacement(new ObjectId(6), trunk, new VoxelCoord(4525, 0, 125), 0),
            ];
            var generator = new PlacingGenerator(placements);
            World = TestWorlds.Load(generator, Vector3.Zero, radiusMetres: 0);
            Models = new VoxelModels(World);
            var options = Options.Create(new EngineOptions());
            Objects = new ObjectWorld(World, kinds, generator, options, NullLogger<ObjectWorld>.Instance);
            var passability = new LeavesPass();
            var kindModels = new VoxelObjects(kinds, World, Models, passability, NullLogger<VoxelObjects>.Instance);
            Damage = new ObjectDamage(
                World,
                Objects,
                kinds,
                kindModels,
                Models,
                options,
                NullLogger<ObjectDamage>.Instance
            );
            Damage.Labeller.Manual = true;
            Columns = new ObjectColumns(Objects, kinds, passability, 10, Damage);
            Navigation = new NavigationService(
                Source,
                () => true,
                NullLogger<NavigationService>.Instance,
                World,
                passability,
                area ?? Area,
                windowCells,
                placed: Objects,
                objectColumns: Columns,
                damage: Damage
            )
            {
                JobIntervalSeconds = 0,
            };
            Navigation.Initialize();
            kindModels.Initialize();
            Objects.Initialize();
            Damage.Initialize();
            if (fill)
            {
                FillAreas();
            }

            Assert.True(Navigation.WaitUntilPublished(Timeout));
            Navigation.AfterFixedStep();
            Assert.True(Navigation.Ready);
        }

        public FlatSource Source { get; }

        public VoxelWorld World { get; }

        public VoxelModels Models { get; }

        public ObjectWorld Objects { get; }

        public ObjectDamage Damage { get; }

        public ObjectColumns Columns { get; }

        public NavigationService Navigation { get; }

        public void FillAreas()
        {
            for (var i = 0; i < 100 && Objects.WaitingAreas > 0; i++)
            {
                Objects.Update();
            }

            Damage.Update();
        }

        public void Dispose()
        {
            Navigation.Dispose();
            Damage.Dispose();
            Models.Dispose();
            World.Dispose();
        }

        private static VoxelObjectKindId Register(
            VoxelObjectKinds kinds,
            string name,
            int unit,
            VoxelCoord size,
            VoxelCoord anchor,
            Action<VoxelObjectShape> fill
        )
        {
            var shape = new VoxelObjectShape(unit, size, anchor);
            fill(shape);
            return kinds.Register(name, shape);
        }
    }

    /// <summary>Open ground at <paramref name="floor"/> over the whole window, with one field to cell (2, 2).</summary>
    private sealed class FlatSource(int floor) : INavGridSource
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
                grid.Floor.AsSpan(start, lastColumn - firstColumn + 1).Fill(floor);
                grid.Clear.AsSpan(start, lastColumn - firstColumn + 1).Fill(byte.MaxValue);
            }
        }
    }

    private sealed class LeavesPass : IVoxelPassability
    {
        public bool IsPassable(byte material) => material == Leaves;
    }
}
