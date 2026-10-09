using System.Numerics;
using Itera.Engine.Navigation;
using Itera.Engine.Tests.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// <see cref="NavGridScanner"/> over a flat floor with boxes: cells read from the voxels stand on the floor or on a low
/// step, are blocked by a block too high to step onto, keep the headroom under an overhang, and pass through leaves; a
/// dig lowers a cell, stone placed on it blocks it, and a tunnel dug through a block opens it. Stamping an edit from its
/// shape (for edits whose voxels are not loaded) gives what reading the voxels gives for digs and stone on open ground,
/// and leaves a tunnel through a block closed until the voxels are read. Judged from the ground, cubes stacked one at a
/// time block as one block does; stamping reads the edit's materials; a deep dig finds its bottom; and an edit covers a
/// cell only when it reaches its centre column.
/// </summary>
public sealed class NavGridScannerTests : IDisposable
{
    private const int Floor = 200;
    private const int Low = 25;

    private readonly VoxelWorld _world = TestWorlds.Load(
        new BoxesGenerator(
            Floor,
            // A block 60 cm high under cells (10, 7) and (10, 8).
            new VoxelBox(100, Floor, -50, 149, Floor + 59, 49, BoxesGenerator.Stone),
            // A step 20 cm high under cells (5, 7) and (5, 8).
            new VoxelBox(-150, Floor, -50, -101, Floor + 19, 49, BoxesGenerator.Stone),
            // A slab from 1.5 m to 2 m over cells (7, 11) and (8, 11).
            new VoxelBox(-50, Floor + 150, 150, 49, Floor + 199, 199, BoxesGenerator.Stone),
            // Leaves 3 m high over cell (8, 4).
            new VoxelBox(0, Floor, -200, 49, Floor + 299, -151, BoxesGenerator.Leaves)
        ),
        new Vector3(0, 2, 0),
        radiusMetres: 5
    );

    private readonly NavGridScanner _scanner;

    public NavGridScannerTests()
    {
        _scanner = new NavGridScanner(_world, new MoverWorldFixture.LeavesPass(), Low);
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Cells_read_from_the_voxels_follow_the_rule()
    {
        var grid = Scanned();
        Assert.Equal(Floor, grid.Floor[grid.Index(2, 2)]);
        Assert.Equal(NavGrid.MostClear, grid.ClearCentimetres(grid.Index(2, 2)));
        Assert.True(grid.IsBlocked(grid.Index(10, 7)));
        Assert.True(grid.IsBlocked(grid.Index(10, 8)));
        Assert.Equal(Floor + 20, grid.Floor[grid.Index(5, 8)]);
        Assert.False(grid.IsBlocked(grid.Index(5, 8)));
        Assert.Equal(150, grid.ClearCentimetres(grid.Index(7, 11)));
        Assert.Equal(NavGrid.MostClear, grid.ClearCentimetres(grid.Index(8, 4)));
    }

    [Fact]
    public void Edits_change_the_cells_they_reach()
    {
        var grid = Scanned();
        var dig = grid.Index(2, 2);
        _world.Apply(VoxelEdit.Sphere(Centre(grid, dig, Floor), 30, VoxelEditOperation.Clear));
        var place = grid.Index(12, 2);
        _world.Apply(
            VoxelEdit.Box(
                Centre(grid, place, Floor) - new VoxelCoord(20, 0, 20),
                new VoxelCoord(40, 40, 40),
                VoxelEditOperation.Set,
                BoxesGenerator.Stone
            )
        );
        var tunnel = grid.Index(10, 8);
        _world.Apply(VoxelEdit.Box(new VoxelCoord(90, Floor, 0), new VoxelCoord(70, 60, 40), VoxelEditOperation.Clear));

        Assert.True(_scanner.Rescan(grid, dig));
        Assert.Equal(Floor - 30, grid.Floor[dig]);
        Assert.True(_scanner.Rescan(grid, place));
        Assert.True(grid.IsBlocked(place));
        Assert.True(_scanner.Rescan(grid, tunnel));
        Assert.False(grid.IsBlocked(tunnel));
        Assert.Equal(Floor, grid.Floor[tunnel]);
        Assert.False(_scanner.Rescan(grid, tunnel));
    }

    [Theory]
    [InlineData(VoxelEditShape.Sphere, VoxelEditOperation.Clear, 30)]
    [InlineData(VoxelEditShape.Sphere, VoxelEditOperation.Clear, 6)]
    [InlineData(VoxelEditShape.Sphere, VoxelEditOperation.Set, 25)]
    [InlineData(VoxelEditShape.Sphere, VoxelEditOperation.Set, 12)]
    [InlineData(VoxelEditShape.Box, VoxelEditOperation.Clear, 40)]
    [InlineData(VoxelEditShape.Box, VoxelEditOperation.Set, 40)]
    [InlineData(VoxelEditShape.Box, VoxelEditOperation.Set, 10)]
    [InlineData(VoxelEditShape.Capsule, VoxelEditOperation.Clear, 30)]
    [InlineData(VoxelEditShape.Capsule, VoxelEditOperation.Set, 20)]
    public void Stamping_matches_the_voxels_on_open_ground(VoxelEditShape shape, VoxelEditOperation operation, int size)
    {
        var grid = Scanned();
        var cell = grid.Index(3, 12);
        var centre = Centre(grid, cell, Floor);
        var edit =
            shape == VoxelEditShape.Sphere ? VoxelEdit.Sphere(centre, size, operation, BoxesGenerator.Stone)
            : shape == VoxelEditShape.Capsule
                ? VoxelEdit.Capsule(centre, centre - new VoxelCoord(size, size, 0), 6, operation, BoxesGenerator.Stone)
            : VoxelEdit.Box(
                centre - new VoxelCoord(size / 2, 0, size / 2),
                new VoxelCoord(size, size, size),
                operation,
                BoxesGenerator.Stone
            );
        var stamped = Copy(grid);
        _scanner.Stamp(stamped, in edit);
        _world.Apply(in edit);
        foreach (var near in new[] { cell, cell + 1, cell - 1, cell + grid.Width, cell - grid.Width })
        {
            _scanner.Rescan(grid, near);
            Assert.Equal(grid.IsBlocked(near), stamped.IsBlocked(near));
            Assert.InRange(stamped.Floor[near] - grid.Floor[near], -1, 1);
        }
    }

    /// <summary>
    /// A clear of only materials walkers pass through (leaves) changes nothing a walker meets; one of stone does. So the
    /// navigation service neither reads nor stamps the first (stamped from its shape, a blast's 5 m strip of foliage
    /// would count as a 5 m pit).
    /// </summary>
    [Fact]
    public void A_clear_of_only_passable_materials_changes_nothing_a_walker_meets()
    {
        var box = new VoxelCoord(-200, Floor - 250, -450);
        var size = new VoxelCoord(500, 500, 500);
        Assert.True(_scanner.ClearsOnlyPassable(VoxelEdit.MaterialsBox(box, size, [BoxesGenerator.Leaves])));
        Assert.False(
            _scanner.ClearsOnlyPassable(
                VoxelEdit.MaterialsBox(box, size, [BoxesGenerator.Leaves, BoxesGenerator.Stone])
            )
        );
        Assert.False(_scanner.ClearsOnlyPassable(VoxelEdit.Box(box, size, VoxelEditOperation.Clear)));
    }

    [Fact]
    public void Stamping_keeps_a_tunnel_through_a_block_closed_until_its_voxels_are_read()
    {
        var grid = Scanned();
        var tunnel = grid.Index(10, 8);
        var edit = VoxelEdit.Box(new VoxelCoord(90, Floor, 0), new VoxelCoord(70, 60, 40), VoxelEditOperation.Clear);
        _scanner.Stamp(grid, in edit);
        Assert.True(grid.IsBlocked(tunnel));
        _world.Apply(in edit);
        _scanner.Rescan(grid, tunnel);
        Assert.False(grid.IsBlocked(tunnel));
    }

    /// <summary>
    /// Judged from the ground three 1 dm
    /// cubes stacked one read apart block a cell as one 30 cm block does, read or stamped; two still raise its floor; and
    /// cubes filling a 1 m pit back up to the ground leave the cell open at the ground.
    /// </summary>
    [Fact]
    public void Cubes_stacked_one_at_a_time_block_as_one_block_does()
    {
        var scanner = new NavGridScanner(_world, new MoverWorldFixture.LeavesPass(), Low, new FlatSource());
        var grid = Scanned();
        var stamped = Copy(grid);
        var cell = grid.Index(3, 12);
        var centre = Centre(grid, cell, Floor);
        for (var cube = 0; cube < 3; cube++)
        {
            var edit = VoxelEdit.Box(
                centre - new VoxelCoord(5, -10 * cube, 5),
                new VoxelCoord(10, 10, 10),
                VoxelEditOperation.Fill,
                BoxesGenerator.Stone
            );
            _world.Apply(in edit);
            scanner.Rescan(grid, cell);
            scanner.Stamp(stamped, in edit);
            if (cube == 1)
            {
                Assert.Equal((Floor + 20, false), (grid.Floor[cell], grid.IsBlocked(cell)));
                Assert.Equal((Floor + 20, false), (stamped.Floor[cell], stamped.IsBlocked(cell)));
            }
        }

        Assert.True(grid.IsBlocked(cell));
        Assert.True(stamped.IsBlocked(cell));

        // A pit 1 m deep, then filled back up to the ground 10 cm at a time.
        var pit = grid.Index(12, 12);
        var bottom = Centre(grid, pit, Floor - 100);
        _world.Apply(
            VoxelEdit.Box(bottom - new VoxelCoord(30, 0, 30), new VoxelCoord(60, 100, 60), VoxelEditOperation.Clear)
        );
        scanner.Rescan(grid, pit);
        Assert.Equal(Floor - 100, grid.Floor[pit]);
        for (var layer = 0; layer < 10; layer++)
        {
            _world.Apply(
                VoxelEdit.Box(
                    bottom - new VoxelCoord(30, -10 * layer, 30),
                    new VoxelCoord(60, 10, 60),
                    VoxelEditOperation.Fill,
                    BoxesGenerator.Stone
                )
            );
            scanner.Rescan(grid, pit);
        }

        Assert.Equal((Floor, false), (grid.Floor[pit], grid.IsBlocked(pit)));
    }

    /// <summary>
    /// Stamping reads what an edit sets: a fill of leaves changes nothing, and
    /// leaves set dig; a clear of some materials digs only when the set holds ground; a lift digs; a piece put back counts
    /// as filled.
    /// </summary>
    [Fact]
    public void Stamping_reads_what_an_edit_sets()
    {
        var scanner = new NavGridScanner(_world, new MoverWorldFixture.LeavesPass(), Low, new FlatSource());
        var grid = Scanned();
        int Stamped(in VoxelEdit edit)
        {
            var copy = Copy(grid);
            var changed = 0;
            scanner.Stamp(copy, in edit, _ => changed++);
            return changed;
        }

        var cell = grid.Index(3, 12);
        var centre = Centre(grid, cell, Floor);
        var box = centre - new VoxelCoord(20, 20, 20);
        var size = new VoxelCoord(40, 40, 40);
        Assert.Equal(0, Stamped(VoxelEdit.Box(box, size, VoxelEditOperation.Fill, BoxesGenerator.Leaves)));
        Assert.True(Stamped(VoxelEdit.Box(box, size, VoxelEditOperation.Set, BoxesGenerator.Leaves)) > 0);
        Assert.Equal(0, Stamped(VoxelEdit.MaterialsBox(box, size, [BoxesGenerator.Stone])));
        Assert.True(Stamped(VoxelEdit.MaterialsBox(box, size, [BoxesGenerator.Stone, FlatSource.Soil])) > 0);
        Assert.True(Stamped(VoxelEdit.Sphere(centre, 20, VoxelEditOperation.Lift)) > 0);
        Assert.True(Stamped(VoxelEdit.PieceAt(1, 20, centre + new VoxelCoord(0, 15, 0), 0)) > 0);

        // Without the game's ground, a clear of any material digs, as before.
        var plain = new NavGridScanner(_world, new MoverWorldFixture.LeavesPass(), Low);
        Assert.True(Stamps(plain, Copy(grid), VoxelEdit.MaterialsBox(box, size, [BoxesGenerator.Stone])));
    }

    /// <summary>A dig deeper than the read's search finds its floor at its bottom.</summary>
    [Fact]
    public void A_dig_deeper_than_the_search_finds_its_bottom()
    {
        var grid = Scanned();
        var cell = grid.Index(12, 3);
        var centre = Centre(grid, cell, Floor);
        _world.Apply(
            VoxelEdit.Box(centre - new VoxelCoord(30, 500, 30), new VoxelCoord(60, 500, 60), VoxelEditOperation.Clear)
        );

        Assert.True(_scanner.Rescan(grid, cell));
        Assert.Equal(Floor - 500, grid.Floor[cell]);
    }

    /// <summary>An edit covers a cell's centre column only when its shape reaches one.</summary>
    [Fact]
    public void An_edit_covers_a_cell_centre_only_when_its_shape_reaches_one()
    {
        var grid = new NavGrid(-400, -400, 16, 16);
        // Centres lie at 25 cm past every 50 cm: (-375, -375) and so on.
        Assert.True(
            NavGridScanner.CoversAnyCentre(
                grid,
                VoxelEdit.Sphere(new VoxelCoord(25, 0, 25), 3, VoxelEditOperation.Clear)
            )
        );
        Assert.False(
            NavGridScanner.CoversAnyCentre(
                grid,
                VoxelEdit.Sphere(new VoxelCoord(0, 0, 0), 20, VoxelEditOperation.Clear)
            )
        );
        Assert.False(
            NavGridScanner.CoversAnyCentre(
                grid,
                VoxelEdit.Capsule(new VoxelCoord(-300, 0, 0), new VoxelCoord(300, 0, 0), 3, VoxelEditOperation.Clear)
            )
        );
        Assert.True(
            NavGridScanner.CoversAnyCentre(
                grid,
                VoxelEdit.Box(new VoxelCoord(20, 0, 20), new VoxelCoord(6, 6, 6), VoxelEditOperation.Clear)
            )
        );
        Assert.False(
            NavGridScanner.CoversAnyCentre(
                grid,
                VoxelEdit.Box(new VoxelCoord(26, 0, 26), new VoxelCoord(48, 6, 48), VoxelEditOperation.Clear)
            )
        );
    }

    private static bool Stamps(NavGridScanner scanner, NavGrid grid, in VoxelEdit edit)
    {
        var changed = false;
        scanner.Stamp(grid, in edit, _ => changed = true);
        return changed;
    }

    /// <summary>A 16 by 16 grid of 50 cm cells from (-4, -4) m, every cell read from the voxels.</summary>
    private NavGrid Scanned()
    {
        var grid = new NavGrid(-400, -400, 16, 16);
        grid.Floor.AsSpan().Fill(Floor);
        grid.Clear.AsSpan().Fill(byte.MaxValue);
        for (var cell = 0; cell < grid.Count; cell++)
        {
            Assert.True(_scanner.IsResident(grid, cell));
            _scanner.Rescan(grid, cell);
        }

        return grid;
    }

    private static NavGrid Copy(NavGrid grid)
    {
        var copy = new NavGrid(grid.OriginX, grid.OriginZ, grid.Width, grid.Depth);
        copy.CopyRows(grid, 0, grid.Depth - 1);
        return copy;
    }

    private static VoxelCoord Centre(NavGrid grid, int cell, int y) =>
        new(grid.CentreX(grid.Column(cell)), y, grid.CentreZ(grid.Row(cell)));

    /// <summary>The flat floor's ground everywhere; its own soil material is ground.</summary>
    private sealed class FlatSource : INavGridSource
    {
        public const byte Soil = 7;

        public int LowCentimetres => Low;

        public NavGrid CreateGrid() => throw new NotSupportedException();

        public IReadOnlyList<NavFieldSpec> CreateFields(NavGrid grid) => throw new NotSupportedException();

        public void Fill(NavGrid grid) => throw new NotSupportedException();

        public int GroundAt(int x, int z) => Floor;

        public bool IsGround(byte material) => material == Soil;
    }
}
