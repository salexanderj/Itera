using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// <see cref="MeshColumns"/> in 10 cm cells: a triangle blocks the cells it passes through; one on a cell's boundary
/// blocks the cell behind its front, so a closed box blocks the same cells as a voxel box; a slope's surface is its plane
/// at the column's centre; a triangle that only touches a column's corner leaves it alone; a thin wall in the middle of a
/// cell blocks it; the tags are the highest triangle's; a change to the colliders is read at the next query; no more than
/// <see cref="MeshColumns.MostChunks"/> chunks are kept; and with none, every answer is the voxel world's none.
/// </summary>
public sealed class MeshColumnsTests : IDisposable
{
    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);
    private readonly MeshColliders _colliders;
    private readonly MeshColumns _columns;

    public MeshColumnsTests()
    {
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
        _columns = new MeshColumns(_colliders, cellsPerMetre: 10);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
    }

    [Fact]
    public void A_closed_box_blocks_the_outer_cells_of_a_voxel_box()
    {
        // Cells 3 to 6 across, 10 to 14 up, and -2 to 1 deep: every face lies on a cell's boundary.
        new TestTriangles()
            .CellBox(3, 10, -2, 6, 14, 1, cellCentimetres: 10)
            .AddTo(_colliders);

        for (var x = 1; x <= 8; x++)
        {
            for (var z = -4; z <= 3; z++)
            {
                var inside = x is >= 3 and <= 6 && z is >= -2 and <= 1;
                var side = inside && (x is 3 or 6 || z is -2 or 1);
                Assert.Equal(inside ? 15 : int.MinValue, Top(x, z, 0, 30));
                Assert.Equal(inside ? 10 : int.MaxValue, Bottom(x, z, 0, 30));
                // Between the top and the bottom only the side faces' columns block: a mesh has no inside.
                Assert.Equal(side ? 14 : int.MinValue, Top(x, z, 11, 13));
            }
        }
    }

    [Theory]
    [InlineData(1, 19)]
    [InlineData(-1, 20)]
    public void A_face_on_a_boundary_blocks_the_cell_behind_it(int up, int cell)
    {
        // A floor (front up) at 2 m blocks the cell under it; a ceiling (front down) the cell over it.
        new TestTriangles()
            .Quad(new Vector3(0, up, 0), new(0, 2, 0), new(0, 2, 1), new(1, 2, 1), new(1, 2, 0))
            .AddTo(_colliders);

        Assert.Equal(cell + 1, Top(5, 5, 0, 40));
        Assert.Equal(cell, Bottom(5, 5, 0, 40));
    }

    [Theory]
    [InlineData(1, 9)]
    [InlineData(-1, 10)]
    public void A_wall_on_a_boundary_blocks_the_column_behind_it(int front, int column)
    {
        new TestTriangles()
            .Quad(new Vector3(front, 0, 0), new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1))
            .AddTo(_colliders);

        Assert.Equal(10, Top(column, 5, 0, 40));
        Assert.Equal(int.MinValue, Top(column == 9 ? 10 : 9, 5, 0, 40));
        Assert.Equal(0, Bottom(column, 5, 0, 40));
        // A wall has no height at the column's centre: its surface is its highest point.
        Assert.Equal(10, _columns.SurfaceOver(column, 10, 5), 1e-4);
    }

    [Fact]
    public void A_thin_wall_inside_a_cell_blocks_it_and_no_other()
    {
        new TestTriangles()
            .Quad(Vector3.UnitX, new(1.05f, 0.33f, 0), new(1.05f, 1.27f, 0), new(1.05f, 1.27f, 1), new(1.05f, 0.33f, 1))
            .AddTo(_colliders);
        Span<UInt128> rows = stackalloc UInt128[10];

        _columns.BlockedCells(5, 0, 10, 0, 40, rows);

        foreach (var row in rows)
        {
            // Only column 10 (bit 5 of a window from x = 5).
            Assert.Equal(UInt128.One << 5, row);
        }

        // From the cell the wall's foot is in (3) to the one its top is in (12).
        Assert.Equal(13, Top(10, 5, 0, 40));
        Assert.Equal(3, Bottom(10, 5, 0, 40));
    }

    [Fact]
    public void A_slopes_surface_is_its_plane_at_the_columns_centre()
    {
        // Rising 0.5 m a metre along x from (0, 1, 0).
        new TestTriangles()
            .Quad(Vector3.UnitY, new(0, 1, 0), new(0, 1, 2), new(2, 2, 2), new(2, 2, 0))
            .AddTo(_colliders);

        for (var x = 0; x < 20; x++)
        {
            var top = Top(x, 5, 0, 40);
            var plane = 10 + 0.5 * (x + 0.5);
            // The column's top is the cell its highest point lies in; the surface the plane over its centre.
            Assert.Equal((int)Math.Ceiling(10 + 0.5 * (x + 1)), top);
            Assert.Equal(plane, _columns.SurfaceOver(x, top, 5), 1e-4);
        }

        Assert.True(double.IsNaN(_columns.SurfaceOver(5, 30, 5)));
    }

    [Fact]
    public void A_triangle_that_only_touches_a_columns_corner_leaves_it_alone()
    {
        // The triangle x < z of the square from (0, 0) to (1, 1) m: its long side passes through the corners of the
        // columns on the diagonal, and of the ones beside them.
        new TestTriangles()
            .Triangle(Vector3.UnitY, new(0, 1, 0), new(0, 1, 1), new(1, 1, 1))
            .AddTo(_colliders);

        for (var i = 0; i < 10; i++)
        {
            Assert.NotEqual(int.MinValue, Top(i, i, 0, 20));
            if (i < 9)
            {
                Assert.Equal(int.MinValue, Top(i + 1, i, 0, 20));
                Assert.NotEqual(int.MinValue, Top(i, i + 1, 0, 20));
            }
        }
    }

    [Fact]
    public void The_tags_are_the_highest_triangles_in_the_band()
    {
        new TestTriangles().Ground(0, 0, 1, 1, 1).AddTo(_colliders, tags: 2);
        new TestTriangles().Ground(0, 0, 1, 1, 2).AddTo(_colliders, tags: 4);

        Assert.Equal(4u, _columns.TagsAt(5, 5, 0, 40));
        Assert.Equal(2u, _columns.TagsAt(5, 5, 0, 15));
        Assert.Equal(0u, _columns.TagsAt(5, 5, 25, 40));
    }

    [Fact]
    public void A_change_to_the_colliders_is_read_at_the_next_query()
    {
        var first = new TestTriangles().Ground(0, 0, 1, 1, 1).AddTo(_colliders);
        Assert.Equal(10, Top(5, 5, 0, 40));

        new TestTriangles().Ground(0, 0, 1, 1, 2).AddTo(_colliders);
        Assert.Equal(20, Top(5, 5, 0, 40));

        _colliders.Remove(first);
        Assert.Equal(20, Bottom(5, 5, 0, 40) + 1);
    }

    [Fact]
    public void Past_the_most_chunks_old_ones_are_dropped_and_made_again_when_read()
    {
        // Ground 1 m up over 64 m by 64 m: 20 by 20 chunks of 3.2 m.
        new TestTriangles()
            .Ground(0, 0, 64, 64, 1)
            .AddTo(_colliders);

        for (var i = 0; i < 400; i++)
        {
            Assert.Equal(10, Top(i % 20 * 32 + 5, i / 20 * 32 + 5, 0, 40));
        }

        Assert.Equal(MeshColumns.MostChunks, _columns.ChunkCount);
        // The first chunks were dropped for later ones, and are made again when read.
        Assert.Equal(10, Top(5, 5, 0, 40));
        Assert.Equal(10, Bottom(37, 5, 0, 40) + 1);
        Assert.Equal(MeshColumns.MostChunks, _columns.ChunkCount);
    }

    [Fact]
    public void Past_the_most_chunks_the_one_read_longest_ago_goes()
    {
        new TestTriangles().Ground(0, 0, 64, 64, 1).AddTo(_colliders);
        for (var i = 0; i < MeshColumns.MostChunks; i++)
        {
            Top(i % 20 * 32 + 5, i / 20 * 32 + 5, 0, 40);
        }

        // Chunk (0, 0) read again, so chunk (1, 0) is now the one read longest ago.
        Top(5, 5, 0, 40);
        Top(MeshColumns.MostChunks % 20 * 32 + 5, MeshColumns.MostChunks / 20 * 32 + 5, 0, 40);

        Assert.True(_columns.Keeps(5, 5));
        Assert.False(_columns.Keeps(37, 5));
        Assert.True(_columns.Keeps(69, 5));
    }

    [Fact]
    public void With_no_collider_every_answer_is_none()
    {
        Span<UInt128> rows = stackalloc UInt128[4];
        Span<int> tops = stackalloc int[16];
        var footprint = new CircleFootprint(5, 5, 3);

        _columns.BlockedCells(0, 0, 4, -100, 100, rows);
        _columns.ColumnTops(0, 0, 4, 4, -100, 100, tops);

        Assert.True(_columns.Empty);
        Assert.Equal(int.MinValue, _columns.HighestTop(in footprint, -100, 100));
        Assert.Equal(int.MaxValue, _columns.LowestBottom(in footprint, -100, 100));
        Assert.All(rows.ToArray(), row => Assert.Equal(UInt128.Zero, row));
        Assert.All(tops.ToArray(), top => Assert.Equal(int.MinValue, top));
        Assert.True(double.IsNaN(_columns.SurfaceOver(5, 0, 5)));
    }

    [Fact]
    public void A_query_after_the_first_allocates_nothing()
    {
        new TestTriangles().Ground(-5, -5, 5, 5, 1).Box(new(1, 0, 1), new(2, 3, 2)).AddTo(_colliders);
        var footprint = new CircleFootprint(10, 10, 6);
        Span<UInt128> rows = stackalloc UInt128[24];
        _columns.HighestTop(in footprint, 0, 40);
        _columns.BlockedCells(0, 0, 24, 0, 40, rows);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            _columns.HighestTop(in footprint, 0, 40);
            _columns.LowestBottom(in footprint, 0, 40);
            _columns.BlockedCells(0, 0, 24, 0, 40, rows);
            _columns.SurfaceOver(10, 11, 10);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private int Top(int x, int z, int y0, int y1)
    {
        var footprint = new CircleFootprint(x + 0.5, z + 0.5, 0.1);
        return _columns.HighestTop(in footprint, y0, y1);
    }

    private int Bottom(int x, int z, int y0, int y1)
    {
        var footprint = new CircleFootprint(x + 0.5, z + 0.5, 0.1);
        return _columns.LowestBottom(in footprint, y0, y1);
    }
}
