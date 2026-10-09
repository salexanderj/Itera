using Itera.Engine.Maths;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary><see cref="JitteredGrid"/>: one point in a cell, found the same for any box that asks.</summary>
public sealed class JitteredGridTests
{
    private const ulong Seed = 12345;

    private static readonly JitteredGrid s_grid = new(Cell: 300, Reach: 70, Salt: 0x646562726973);

    [Fact]
    public void Every_point_found_lies_in_a_cell_the_box_reaches_and_can_touch_it()
    {
        var random = new Random(4);
        var found = new JitteredPoint[4096];
        var total = 0;
        for (var i = 0; i < 300; i++)
        {
            var minX = random.Next(-5000, 5000);
            var minZ = random.Next(-5000, 5000);
            var maxX = minX + random.Next(0, 2500);
            var maxZ = minZ + random.Next(0, 2500);
            var count = s_grid.Find(Seed, 0.4, minX, maxX, minZ, maxZ, found);
            DeterministicMath.CellRange(s_grid.Cell, s_grid.Reach, minX, maxX, out var firstX, out var lastX);
            DeterministicMath.CellRange(s_grid.Cell, s_grid.Reach, minZ, maxZ, out var firstZ, out var lastZ);
            for (var p = 0; p < count; p++)
            {
                var point = found[p];
                Assert.InRange(point.CellX, firstX, lastX);
                Assert.InRange(point.CellZ, firstZ, lastZ);
                Assert.True(point.X + s_grid.Reach >= minX && point.X - s_grid.Reach <= maxX);
                Assert.True(point.Z + s_grid.Reach >= minZ && point.Z - s_grid.Reach <= maxZ);
            }

            total += count;
        }

        Assert.True(total > 1000, $"{total} points found");
    }

    [Fact]
    public void Find_misses_no_point_that_can_touch_the_box()
    {
        var random = new Random(5);
        var found = new JitteredPoint[4096];
        for (var i = 0; i < 100; i++)
        {
            var minX = random.Next(-3000, 3000);
            var minZ = random.Next(-3000, 3000);
            var maxX = minX + random.Next(0, 1500);
            var maxZ = minZ + random.Next(0, 1500);
            var count = s_grid.Find(Seed, 0.5, minX, maxX, minZ, maxZ, found);

            // Every cell of a wide range asked one by one, in the same order as Find.
            var expected = new List<JitteredPoint>();
            for (var cellZ = -30; cellZ <= 30; cellZ++)
            {
                for (var cellX = -30; cellX <= 30; cellX++)
                {
                    if (
                        s_grid.TryGet(cellX, cellZ, Seed, 0.5, out var point)
                        && point.X + s_grid.Reach >= minX
                        && point.X - s_grid.Reach <= maxX
                        && point.Z + s_grid.Reach >= minZ
                        && point.Z - s_grid.Reach <= maxZ
                    )
                    {
                        expected.Add(point);
                    }
                }
            }

            Assert.Equal(expected, found.AsSpan(0, count).ToArray());
        }
    }

    [Fact]
    public void The_same_seed_gives_the_same_points_and_another_seed_other_ones()
    {
        var first = new JitteredPoint[512];
        var second = new JitteredPoint[512];
        var other = new JitteredPoint[512];
        var a = s_grid.Find(Seed, 0.5, -1000, 1000, -1000, 1000, first);
        var b = s_grid.Find(Seed, 0.5, -1000, 1000, -1000, 1000, second);
        var c = s_grid.Find(Seed + 1, 0.5, -1000, 1000, -1000, 1000, other);
        Assert.Equal(a, b);
        Assert.Equal(first.AsSpan(0, a).ToArray(), second.AsSpan(0, b).ToArray());
        Assert.NotEqual(first.AsSpan(0, a).ToArray(), other.AsSpan(0, c).ToArray());

        var salted = (s_grid with { Salt = s_grid.Salt + 1 }).Find(Seed, 0.5, -1000, 1000, -1000, 1000, other);
        Assert.NotEqual(first.AsSpan(0, a).ToArray(), other.AsSpan(0, salted).ToArray());
    }

    [Fact]
    public void Points_are_found_whatever_the_order_or_size_of_the_boxes_that_ask()
    {
        var random = new Random(6);
        var all = new JitteredPoint[8192];
        var count = s_grid.Find(Seed, 0.5, -3000, 3000, -3000, 3000, all);

        // Tiles of 700 cm over the same area, asked in a shuffled order.
        var tiles = new List<(int X, int Z)>();
        for (var z = -3000; z < 3000; z += 700)
        {
            for (var x = -3000; x < 3000; x += 700)
            {
                tiles.Add((x, z));
            }
        }

        for (var i = tiles.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }

        var perTile = new JitteredPoint[512];
        var seen = new HashSet<JitteredPoint>();
        foreach (var (tileX, tileZ) in tiles)
        {
            var found = s_grid.Find(Seed, 0.5, tileX, tileX + 699, tileZ, tileZ + 699, perTile);
            for (var p = 0; p < found; p++)
            {
                seen.Add(perTile[p]);

                // A point a tile finds is the very point the big box finds, when the big box can touch it too.
                var point = perTile[p];
                var reach = s_grid.Reach;
                if (
                    point.X + reach >= -3000
                    && point.X - reach <= 3000
                    && point.Z + reach >= -3000
                    && point.Z - reach <= 3000
                )
                {
                    Assert.Contains(point, all.AsSpan(0, count).ToArray());
                }
            }
        }

        // Every point of the big box that lies inside the big box was found by the tile that holds it.
        foreach (var point in all.AsSpan(0, count))
        {
            if (point.X >= -3000 && point.X < 3000 && point.Z >= -3000 && point.Z < 3000)
            {
                Assert.Contains(point, seen);
            }
        }
    }

    [Fact]
    public void The_capacity_holds_every_point_a_box_can_find()
    {
        var random = new Random(7);
        for (var i = 0; i < 300; i++)
        {
            var minX = random.Next(-9000, 9000);
            var minZ = random.Next(-9000, 9000);
            var maxX = minX + random.Next(0, 4000);
            var maxZ = minZ + random.Next(0, 4000);

            // A chance of 1 puts a point in every cell; the span would throw if it were too short.
            var capacity = s_grid.Capacity(minX, maxX, minZ, maxZ);
            var found = new JitteredPoint[capacity];
            var count = s_grid.Find(Seed, 1.0, minX, maxX, minZ, maxZ, found);
            Assert.InRange(count, 1, capacity);
        }
    }

    [Fact]
    public void The_chance_sets_how_many_cells_hold_a_point_and_nested_chances_nest()
    {
        var all = new List<JitteredPoint>();
        var some = new List<JitteredPoint>();
        for (var cellZ = -50; cellZ < 50; cellZ++)
        {
            for (var cellX = -50; cellX < 50; cellX++)
            {
                Assert.False(s_grid.TryGet(cellX, cellZ, Seed, 0, out _));
                Assert.True(s_grid.TryGet(cellX, cellZ, Seed, 1, out var every));
                all.Add(every);
                if (s_grid.TryGet(cellX, cellZ, Seed, 0.3, out var point))
                {
                    some.Add(point);

                    // The same point as at chance 1, and its draw is under the chance asked for.
                    Assert.Equal(every, point);
                    Assert.True(point.Draw < 0.3);
                }
                else
                {
                    Assert.True(every.Draw >= 0.3);
                }
            }
        }

        Assert.Equal(10_000, all.Count);
        Assert.InRange(some.Count, 2700, 3300);
    }

    [Fact]
    public void A_point_lies_in_its_cell_and_inside_the_margin()
    {
        var margin = new JitteredGrid(Cell: 600, Reach: 900, Salt: 0x74726565, Margin: 100);
        for (var cellZ = -20; cellZ < 20; cellZ++)
        {
            for (var cellX = -20; cellX < 20; cellX++)
            {
                Assert.True(margin.TryGet(cellX, cellZ, Seed, 1, out var point));
                Assert.InRange(point.X, cellX * 600 + 100, cellX * 600 + 499);
                Assert.InRange(point.Z, cellZ * 600 + 100, cellZ * 600 + 499);
                Assert.True(s_grid.TryGet(cellX, cellZ, Seed, 1, out var free));
                Assert.InRange(free.X, cellX * 300, cellX * 300 + 299);
                Assert.InRange(free.Z, cellZ * 300, cellZ * 300 + 299);
            }
        }
    }

    [Fact]
    public void A_layer_is_the_grid_with_its_salt_raised()
    {
        var layered = new JitteredGrid(Cell: 1600, Reach: 200, Salt: 0x706F636B6574);
        for (var layer = 0; layer < 4; layer++)
        {
            var raised = layered with { Salt = layered.Salt + (ulong)layer };
            for (var cell = -10; cell < 10; cell++)
            {
                Assert.Equal(
                    raised.TryGet(cell, cell + 3, Seed, 0.5, out var expected),
                    layered.TryGet(cell, cell + 3, Seed, 0.5, out var point, layer)
                );
                Assert.Equal(expected, point);
            }
        }
    }

    [Fact]
    public void A_cell_draws_its_chance_then_x_then_z_from_its_hash()
    {
        // The order the game's scattered things use: the hash of the cell, a unit draw for the chance, then x and z.
        for (var cellZ = -10; cellZ < 10; cellZ++)
        {
            for (var cellX = -10; cellX < 10; cellX++)
            {
                var state = DeterministicMath.Hash(cellX, cellZ, Seed, s_grid.Salt);
                var draw = DeterministicMath.Unit(DeterministicMath.Next(ref state));
                var hasPoint = s_grid.TryGet(cellX, cellZ, Seed, 0.2, out var point);
                Assert.Equal(draw < 0.2, hasPoint);
                if (hasPoint)
                {
                    var x =
                        cellX * s_grid.Cell
                        + DeterministicMath.Range(DeterministicMath.Next(ref state), 0, s_grid.Cell - 1);
                    var z =
                        cellZ * s_grid.Cell
                        + DeterministicMath.Range(DeterministicMath.Next(ref state), 0, s_grid.Cell - 1);
                    Assert.Equal(new JitteredPoint(cellX, cellZ, x, z, draw, state), point);
                }
            }
        }
    }
}
