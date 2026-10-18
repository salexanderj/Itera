using System.Numerics;
using Arch.Core;
using Itera.Engine.Maths;
using Itera.Engine.Spatial;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Spatial;

/// <summary>
/// <see cref="SpatialGrid"/>: points, points on the ground, and lines against every entry worked out by hand, over random
/// entries narrow and wide and queries small and larger than the grid, in cells of two widths; tags; entries moved,
/// removed, and set again; entities no longer alive passed over; the nearest kept in order with ties by entity; a move of
/// the origin between the setting and the asking changing nothing, for a grid refilled each step and one kept across many
/// moves; growth past its first room; and no allocation.
/// </summary>
public sealed class SpatialGridTests : IDisposable
{
    private static readonly float[] s_distances = [1, 2, 4, 4];

    private readonly World _world = World.Create();
    private readonly WorldOrigin _origin = new();

    public void Dispose() => World.Destroy(_world);

    [Theory]
    [InlineData(8f, 1)]
    [InlineData(2f, 2)]
    public void Within_finds_exactly_the_entries_whose_cylinders_reach_the_point(float cell, int seed)
    {
        var grid = new SpatialGrid(_origin, _world, cell);
        var entries = Scatter(grid, new Random(seed), 600);
        var random = new Random(seed + 10);
        for (var query = 0; query < 400; query++)
        {
            var centre = RandomPoint(random);
            var radius = query % 10 == 0 ? 150 * random.NextSingle() : 12 * random.NextSingle();
            var collector = new Collector([]);
            grid.Within(centre, radius, default, ref collector);
            var expected = entries
                .Where(e => SpatialGrid.DistanceTo(e, centre) <= radius)
                .Select(e => e.Entity)
                .OrderBy(e => e.Id);
            Assert.Equal(expected, collector.Found.Select(f => f.Entry.Entity).OrderBy(e => e.Id));
            foreach (var (entry, distance) in collector.Found)
            {
                Assert.Equal(SpatialGrid.DistanceTo(entry, centre), distance, 1e-4f);
                Assert.Equal(entries.First(e => e.Entity == entry.Entity), entry);
            }
        }
    }

    [Theory]
    [InlineData(8f, 3)]
    [InlineData(2f, 4)]
    public void Along_finds_the_entries_whose_cylinders_come_within_the_radius_of_the_line(float cell, int seed)
    {
        var grid = new SpatialGrid(_origin, _world, cell);
        var entries = Scatter(grid, new Random(seed), 600);
        var random = new Random(seed + 10);
        for (var query = 0; query < 200; query++)
        {
            var from = RandomPoint(random);
            var to =
                from
                + new Vector3(
                    random.NextSingle() - 0.5f,
                    (random.NextSingle() - 0.5f) * 0.3f,
                    random.NextSingle() - 0.5f
                ) * 40;
            var radius = 3 * random.NextSingle();
            var collector = new Collector([]);
            grid.Along(from, to, radius, default, ref collector);
            var found = collector.Found.ToDictionary(f => f.Entry.Entity, f => f.Distance);
            foreach (var entry in entries)
            {
                // Across alone is never more than the distance: one farther across than the radius is not found.
                if (Across(entry, from, to) > radius + 0.01f)
                {
                    Assert.False(found.ContainsKey(entry.Entity));
                    continue;
                }

                // The least distance along the line, by 4,000 points on it: no more than 1 cm past the true least.
                var least = float.PositiveInfinity;
                for (var i = 0; i <= 4000; i++)
                {
                    least = MathF.Min(least, SpatialGrid.DistanceTo(entry, Vector3.Lerp(from, to, i / 4000f)));
                }

                if (MathF.Abs(least - radius) < 0.01f)
                {
                    continue;
                }

                Assert.Equal(least < radius, found.ContainsKey(entry.Entity));
                if (found.TryGetValue(entry.Entity, out var distance))
                {
                    Assert.InRange(distance, least - 0.01f, least + 1e-4f);
                }
            }
        }
    }

    [Fact]
    public void A_line_through_a_cylinder_meets_it_at_no_distance_and_one_over_it_at_its_height_above()
    {
        var grid = new SpatialGrid(_origin, _world, 4);
        var tall = _world.Create();
        grid.Set(tall, new Vector3(10, 0, 0), 0.5f, 2, 1);
        var collector = new Collector([]);
        grid.Along(new Vector3(0, 1, 0), new Vector3(20, 1, 0), 0.1f, default, ref collector);
        Assert.Equal(0, Assert.Single(collector.Found).Distance, 1e-5f);

        collector = new Collector([]);
        grid.Along(new Vector3(0, 3, 0), new Vector3(20, 3, 0), 1.5f, default, ref collector);
        Assert.Equal(1, Assert.Single(collector.Found).Distance, 1e-4f);
    }

    [Fact]
    public void A_wide_entry_is_found_from_past_its_cell_and_a_narrow_one_only_near_it()
    {
        var grid = new SpatialGrid(_origin, _world, 4);
        var temple = _world.Create();
        var block = _world.Create();
        grid.Set(temple, new Vector3(100, 0, 100), 25, 30, 1);
        grid.Set(block, new Vector3(140, 0, 100), 0.5f, 1, 1);
        Assert.True(grid.Any(new Vector3(128, 5, 100), 3.5f, default));
        Assert.True(grid.Any(new Vector3(100, 0, 74), 1.5f, default));
        Assert.False(grid.Any(new Vector3(100, 0, 70), 4.5f, default));
        Assert.True(grid.Any(new Vector3(142, 0, 100), 2, default));
        Assert.False(grid.Any(new Vector3(150, 0, 100), 9, default));
    }

    [Fact]
    public void A_narrow_entry_is_found_from_the_next_cell_its_side_reaches_into()
    {
        var grid = new SpatialGrid(_origin, _world, 4);
        var west = _world.Create();
        var south = _world.Create();
        grid.Set(west, new Vector3(3.9f, 0, 2), 1.9f, 2, 1);
        grid.Set(south, new Vector3(-2, 0, -0.1f), 1.9f, 2, 1);
        Assert.Equal(new[] { west }, Found(grid, new Vector3(5.85f, 1, 2), 0.1f, default));
        Assert.Equal(new[] { south }, Found(grid, new Vector3(-2, 1, 1.85f), 0.1f, default));
        Assert.Empty(Found(grid, new Vector3(5.95f, 1, 2), 0.1f, default));
    }

    [Fact]
    public void Tags_find_an_entry_with_all_bits_asked_none_refused_and_one_of_any()
    {
        var grid = new SpatialGrid(_origin, _world, 8);
        var a = _world.Create();
        var b = _world.Create();
        var c = _world.Create();
        grid.Set(a, Vector3.Zero, 0.3f, 1.8f, 0b001);
        grid.Set(b, Vector3.UnitX, 0.3f, 1.8f, 0b011);
        grid.Set(c, Vector3.UnitZ, 0.3f, 1.8f, 0b100);
        Assert.Equal(new[] { a, b, c }, Found(grid, Vector3.Zero, 5, default));
        Assert.Equal(new[] { a, b }, Found(grid, Vector3.Zero, 5, new SpatialMatch(0b001)));
        Assert.Equal(new[] { b }, Found(grid, Vector3.Zero, 5, new SpatialMatch(0b011)));
        Assert.Equal(new[] { a }, Found(grid, Vector3.Zero, 5, new SpatialMatch(0b001, None: 0b010)));
        Assert.Equal(new[] { c }, Found(grid, Vector3.Zero, 5, new SpatialMatch(0, None: 0b011)));
        Assert.Equal(new[] { b, c }, Found(grid, Vector3.Zero, 5, new SpatialMatch(0, Any: 0b110)));
        Assert.Equal(new[] { b }, Found(grid, Vector3.Zero, 5, new SpatialMatch(0b001, Any: 0b110)));
        Assert.Empty(Found(grid, Vector3.Zero, 5, new SpatialMatch(0b1000)));
    }

    [Theory]
    [InlineData(8f, 7)]
    [InlineData(2f, 8)]
    public void Across_finds_exactly_the_entries_within_the_radius_on_the_ground_at_any_height(float cell, int seed)
    {
        var grid = new SpatialGrid(_origin, _world, cell);
        var entries = Scatter(grid, new Random(seed), 600);
        var random = new Random(seed + 10);
        for (var query = 0; query < 300; query++)
        {
            // The centre far up or down: across alone counts.
            var centre = RandomPoint(random) + new Vector3(0, 400 * random.NextSingle() - 200, 0);
            var radius = query % 10 == 0 ? 150 * random.NextSingle() : 12 * random.NextSingle();
            var collector = new Collector([]);
            grid.Across(centre, radius, default, ref collector);
            var expected = entries
                .Where(e => Across(e, centre, centre) <= radius)
                .Select(e => e.Entity)
                .OrderBy(e => e.Id);
            Assert.Equal(expected, collector.Found.Select(f => f.Entry.Entity).OrderBy(e => e.Id));
            foreach (var (entry, distance) in collector.Found)
            {
                Assert.Equal(Across(entry, centre, centre), distance, 1e-4f);
            }
        }
    }

    [Fact]
    public void An_entry_set_again_moves_and_one_removed_is_gone()
    {
        var grid = new SpatialGrid(_origin, _world, 4);
        var walker = _world.Create();
        var other = _world.Create();
        grid.Set(walker, new Vector3(0, 0, 0), 0.3f, 1.8f, 1);
        grid.Set(other, new Vector3(1, 0, 0), 0.3f, 1.8f, 1);
        grid.Set(walker, new Vector3(50, 0, -30), 0.3f, 1.8f, 2);
        Assert.Equal(2, grid.Count);
        Assert.Equal(new[] { other }, Found(grid, Vector3.Zero, 3, default));
        Assert.Equal(new[] { walker }, Found(grid, new Vector3(50, 0, -30), 3, default));
        Assert.True(grid.TryGet(walker, out var entry));
        Assert.Equal(new SpatialEntry(walker, new Vector3(50, 0, -30), 0.3f, 1.8f, 2), entry);

        grid.Set(walker, new Vector3(50, 0, -30), 20, 1.8f, 2);
        Assert.Equal(new[] { walker }, Found(grid, new Vector3(50, 0, -8), 3, default));
        Assert.True(grid.Remove(walker));
        Assert.False(grid.Remove(walker));
        Assert.False(grid.TryGet(walker, out _));
        Assert.Empty(Found(grid, new Vector3(50, 0, -30), 30, default));
        Assert.Equal(1, grid.Count);

        grid.Clear();
        Assert.Equal(0, grid.Count);
        Assert.Empty(Found(grid, Vector3.Zero, 100, default));
        grid.Set(other, new Vector3(2, 0, 0), 0.3f, 1.8f, 1);
        Assert.Equal(new[] { other }, Found(grid, Vector3.Zero, 3, default));
    }

    [Fact]
    public void An_entity_no_longer_alive_is_passed_over_even_when_its_id_is_used_again()
    {
        var grid = new SpatialGrid(_origin, _world, 8);
        var gone = _world.Create();
        grid.Set(gone, Vector3.Zero, 0.3f, 1.8f, 1);
        _world.Destroy(gone);
        var again = _world.Create();
        Assert.Equal(gone.Id, again.Id);
        Assert.Empty(Found(grid, Vector3.Zero, 5, default));
        Assert.False(grid.Any(Vector3.Zero, 5, default));
        Assert.Equal(1, grid.Count);
    }

    [Fact]
    public void The_nearest_come_nearest_first_with_ties_in_the_order_of_their_entities_and_the_filter_obeyed()
    {
        var grid = new SpatialGrid(_origin, _world, 4);
        var made = Enumerable.Range(0, 8).Select(_ => _world.Create()).ToArray();
        // Set in the reverse order of the entities: two pairs at the same distance.
        grid.Set(made[7], new Vector3(6, 0, 0), 0, 2, 1);
        grid.Set(made[6], new Vector3(0, 0, -4), 0, 2, 1);
        grid.Set(made[5], new Vector3(4, 0, 0), 0, 2, 1);
        grid.Set(made[4], new Vector3(0, 0, 2), 0, 2, 2);
        grid.Set(made[3], new Vector3(2, 0, 0), 0, 2, 1);
        grid.Set(made[2], new Vector3(-1, 0, 0), 0, 2, 1);
        grid.Set(made[1], new Vector3(30, 0, 0), 0, 2, 1);
        Span<SpatialHit> nearest = stackalloc SpatialHit[4];
        var count = grid.Nearest(Vector3.Zero, 10, new SpatialMatch(1), new EveryEntry(), nearest);
        Assert.Equal(4, count);
        Assert.Equal(new[] { made[2], made[3], made[5], made[6] }, nearest.ToArray().Select(h => h.Entry.Entity));
        Assert.Equal(s_distances, nearest.ToArray().Select(h => h.Distance));

        count = grid.Nearest(Vector3.Zero, 10, default, new Not(made[2]), nearest);
        Assert.Equal(
            new[] { made[3], made[4], made[5], made[6] },
            nearest[..count].ToArray().Select(h => h.Entry.Entity)
        );
        Assert.True(grid.Nearest(new Vector3(29, 0, 0), 5, default, new EveryEntry(), out var hit));
        Assert.Equal(made[1], hit.Entry.Entity);
        Assert.False(grid.Nearest(new Vector3(100, 0, 0), 5, default, new EveryEntry(), out _));
    }

    [Fact]
    public void A_move_of_the_origin_between_the_setting_and_the_asking_changes_nothing()
    {
        var kept = new SpatialGrid(_origin, _world, 8);
        var refilled = new SpatialGrid(_origin, _world, 8);
        var entries = Scatter(kept, new Random(5), 300);
        foreach (var entry in entries)
        {
            refilled.Set(entry.Entity, entry.Feet, entry.Radius, entry.Height, entry.Tags);
        }

        var random = new Random(6);
        var queries = Enumerable
            .Range(0, 100)
            .Select(_ => (Centre: RandomPoint(random), Radius: 10 * random.NextSingle()))
            .ToArray();
        var before = queries.Select(q => Found(kept, q.Centre, q.Radius, default)).ToArray();

        // The origin moves 512 m east and 3 m up: every local position is now that much less.
        _origin.MoveTo(new VoxelCoord(51_200, 300, 0));
        var shift = new Vector3(512, 3, 0);
        refilled.Clear();
        foreach (var entry in entries)
        {
            refilled.Set(entry.Entity, entry.Feet - shift, entry.Radius, entry.Height, entry.Tags);
        }

        for (var q = 0; q < queries.Length; q++)
        {
            Assert.Equal(before[q], Found(kept, queries[q].Centre - shift, queries[q].Radius, default));
            Assert.Equal(before[q], Found(refilled, queries[q].Centre - shift, queries[q].Radius, default));
        }

        Assert.True(kept.TryGet(entries[0].Entity, out var moved));
        Assert.Equal(entries[0].Feet - shift, moved.Feet);
    }

    [Fact]
    public void Refilled_each_step_and_kept_across_many_moves_of_the_origin_it_answers_where_things_stand()
    {
        var refilled = new SpatialGrid(_origin, _world, 8);
        var kept = new SpatialGrid(_origin, _world, 8);
        var random = new Random(12);
        // Where things stand, absolute, in whole centimetres; the kept grid set once, local to the first origin.
        var standing = Enumerable
            .Range(0, 200)
            .Select(_ =>
                (
                    Entity: _world.Create(),
                    At: new VoxelCoord(
                        random.Next(-50_000, 50_000),
                        random.Next(-500, 500),
                        random.Next(-50_000, 50_000)
                    )
                )
            )
            .ToArray();
        foreach (var (entity, at) in standing)
        {
            kept.Set(entity, _origin.ToLocal(at), 0.4f, 1.8f, 1);
        }

        for (var step = 0; step < 40; step++)
        {
            // The origin moves by whole metres on most steps, as far as 2 km, and stays on some.
            if (step % 4 != 3)
            {
                var at = _origin.Centimetres;
                _origin.MoveTo(
                    new VoxelCoord(
                        at.X + random.Next(-2000, 2001) * 100,
                        at.Y + random.Next(-20, 21) * 100,
                        at.Z + random.Next(-2000, 2001) * 100
                    )
                );
            }

            refilled.Clear();
            foreach (var (entity, at) in standing)
            {
                refilled.Set(entity, _origin.ToLocal(at), 0.4f, 1.8f, 1);
            }

            var query = standing[random.Next(standing.Length)].At;
            var centre = _origin.ToLocal(query);
            var near = Found(refilled, centre, 60, default);
            Assert.Equal(near, Found(kept, centre, 60, default));
            foreach (var (entity, at) in standing)
            {
                // The distance to its cylinder, worked out in doubles from the absolute points; a millimetre either side
                // of the radius left to the floats.
                var distance = Distance(at, query);
                if (Math.Abs(distance - 60) > 1e-3)
                {
                    Assert.Equal(distance < 60, near.Contains(entity));
                }
            }

            foreach (var (entity, at) in standing)
            {
                Assert.True(refilled.TryGet(entity, out var fresh));
                Assert.True(kept.TryGet(entity, out var old));
                Assert.Equal(_origin.ToLocal(at), fresh.Feet);
                Assert.True(
                    Vector3.Distance(_origin.ToLocal(at), old.Feet) < 0.01f,
                    $"{old.Feet} against {_origin.ToLocal(at)}"
                );
            }
        }
    }

    /// <summary>
    /// The distance from the absolute point <paramref name="point"/> to the cylinder 0.4 m round and 1.8 m tall standing
    /// at <paramref name="feet"/>, in metres.
    /// </summary>
    private static double Distance(VoxelCoord feet, VoxelCoord point)
    {
        var across = Math.Max(
            Math.Sqrt(Math.Pow((point.X - feet.X) / 100.0, 2) + Math.Pow((point.Z - feet.Z) / 100.0, 2)) - 0.4,
            0
        );
        var up = Math.Max(Math.Max((feet.Y - point.Y) / 100.0, (point.Y - feet.Y) / 100.0 - 1.8), 0);
        return Math.Sqrt(across * across + up * up);
    }

    [Fact]
    public void It_grows_past_its_first_room_and_finds_what_it_held_before()
    {
        var grid = new SpatialGrid(_origin, _world, 8);
        var entries = Scatter(grid, new Random(9), 5000);
        Assert.Equal(5000, grid.Count);
        var random = new Random(10);
        for (var query = 0; query < 50; query++)
        {
            var centre = RandomPoint(random);
            var expected = entries
                .Where(e => SpatialGrid.DistanceTo(e, centre) <= 6)
                .Select(e => e.Entity)
                .OrderBy(e => e.Id);
            Assert.Equal(expected, Found(grid, centre, 6, default));
        }
    }

    [Fact]
    public void Bad_entries_and_queries_are_refused()
    {
        var grid = new SpatialGrid(_origin, _world, 8);
        var entity = _world.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialGrid(_origin, _world, 0));
        Assert.Throws<ArgumentException>(() => grid.Set(Entity.Null, Vector3.Zero, 1, 1, 0));
        Assert.Throws<ArgumentException>(() => grid.Set(entity, new Vector3(float.NaN, 0, 0), 1, 1, 0));
        Assert.Throws<ArgumentException>(() => grid.Set(entity, Vector3.Zero, -1, 1, 0));
        Assert.Throws<ArgumentException>(() => grid.Set(entity, Vector3.Zero, 1, float.PositiveInfinity, 0));
        Assert.Throws<ArgumentException>(() => grid.Any(Vector3.Zero, float.NaN, default));
        Assert.Throws<ArgumentException>(() => grid.Any(new Vector3(0, float.PositiveInfinity, 0), 1, default));
        Assert.Equal(0, grid.Count);
    }

    [Fact]
    public void Setting_clearing_and_asking_allocate_nothing_once_it_has_held_as_many()
    {
        var grid = new SpatialGrid(_origin, _world, 8);
        var entities = Enumerable.Range(0, 500).Select(_ => _world.Create()).ToArray();
        var random = new Random(11);
        var places = entities.Select(_ => RandomPoint(random)).ToArray();
        Fill(grid, entities, places);
        Span<SpatialHit> nearest = stackalloc SpatialHit[4];
        Round(grid, entities, places, 0, nearest);
        var start = Itera.TestSupport.SystemAllocation.StartMeasurement();
        for (var round = 1; round < 20; round++)
        {
            Round(grid, entities, places, round, nearest);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    private static void Round(
        SpatialGrid grid,
        Entity[] entities,
        Vector3[] places,
        int round,
        Span<SpatialHit> nearest
    )
    {
        Fill(grid, entities, places);
        var counter = new Counter();
        grid.Within(places[round], 10, default, ref counter);
        grid.Along(places[round], places[round + 1], 2, new SpatialMatch(1), ref counter);
        grid.Nearest(places[round], 20, default, new EveryEntry(), nearest);
        grid.Any(places[round], 3, default);
        grid.Remove(entities[round]);
        grid.Set(entities[round], places[round], 30, 2, 1);
    }

    private static void Fill(SpatialGrid grid, Entity[] entities, Vector3[] places)
    {
        grid.Clear();
        for (var i = 0; i < entities.Length; i++)
        {
            grid.Set(entities[i], places[i], 0.3f, 1.8f, 1);
        }
    }

    /// <summary>
    /// Entities set in the grid over 200 m by 200 m: most narrow (characters, blocks), one in twenty wider than any cell
    /// (buildings), on ground rising and falling 10 m.
    /// </summary>
    private List<SpatialEntry> Scatter(SpatialGrid grid, Random random, int count)
    {
        var entries = new List<SpatialEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var entity = _world.Create();
            var wide = i % 20 == 0;
            var entry = new SpatialEntry(
                entity,
                RandomPoint(random),
                wide ? 4 + 20 * random.NextSingle() : 1.2f * random.NextSingle(),
                wide ? 30 * random.NextSingle() : 2.5f * random.NextSingle(),
                (ulong)(1 << (i % 3))
            );
            grid.Set(entry.Entity, entry.Feet, entry.Radius, entry.Height, entry.Tags);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>How far the line's shadow on the ground passes from the entry's circle there.</summary>
    private static float Across(SpatialEntry entry, Vector3 from, Vector3 to)
    {
        var a = new Vector2(from.X, from.Z);
        var way = new Vector2(to.X, to.Z) - a;
        var feet = new Vector2(entry.Feet.X, entry.Feet.Z);
        var t = way.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(feet - a, way) / way.LengthSquared(), 0, 1) : 0;
        return MathF.Max(Vector2.Distance(a + t * way, feet) - entry.Radius, 0);
    }

    private static Vector3 RandomPoint(Random random) =>
        new(200 * random.NextSingle() - 100, 10 * random.NextSingle() - 5, 200 * random.NextSingle() - 100);

    private static Entity[] Found(SpatialGrid grid, Vector3 centre, float radius, SpatialMatch match)
    {
        var collector = new Collector([]);
        grid.Within(centre, radius, match, ref collector);
        return [.. collector.Found.Select(f => f.Entry.Entity).OrderBy(e => e.Id)];
    }

    private readonly struct Collector(List<(SpatialEntry Entry, float Distance)> found) : ISpatialVisitor
    {
        public List<(SpatialEntry Entry, float Distance)> Found { get; } = found;

        public bool Visit(in SpatialEntry entry, float distance)
        {
            Found.Add((entry, distance));
            return true;
        }
    }

    private struct Counter : ISpatialVisitor
    {
        public int Count { get; private set; }

        public bool Visit(in SpatialEntry entry, float distance)
        {
            Count++;
            return true;
        }
    }

    private readonly record struct Not(Entity Entity) : ISpatialFilter
    {
        public bool Includes(in SpatialEntry entry) => entry.Entity != Entity;
    }
}
