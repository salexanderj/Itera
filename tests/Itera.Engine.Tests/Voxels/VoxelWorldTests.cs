using System.Numerics;
using Itera.Engine.Matches;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

[RunsLong]
public sealed class VoxelWorldTests : IDisposable
{
    private const ulong Seed = 17;
    private static readonly Vector3[] Origin = [Vector3.Zero];
    private static readonly Vector3[] Away = [new(50, 0, 0)];
    private static readonly ColumnKey OriginColumn = new(0, 0, 0);
    private readonly List<VoxelWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    [Fact]
    public void Columns_around_a_centre_load_and_read_like_the_generator()
    {
        var world = CreateWorld();

        world.SetCenters(Origin);
        Settle(world, OriginColumn);

        // A 3 m radius meets 6 x 6 columns of 1.28 m round the origin, all but the four corners (3.62 m away).
        Assert.Equal(32, world.ResidentColumns);
        for (var x = -250; x < 250; x += 37)
        {
            for (var y = -100; y < 400; y += 23)
            {
                Assert.True(world.TryGetMaterial(new VoxelCoord(x, y, x / 2), out var material));
                Assert.Equal(Expected(x, y, x / 2), material);
            }
        }

        Assert.False(world.TryGetMaterial(new VoxelCoord(5000, 0, 0), out _));
        Assert.True(world.TryGetMaterial(new VoxelCoord(0, VoxelGrid.Unit.EndVoxelY + 10, 0), out var above));
        Assert.Equal(VoxelConstants.Air, above);
    }

    [Fact]
    public void More_than_four_centres_set_twice_before_an_update_load_their_columns()
    {
        // A level's list of where each centre was when its columns were built starts with room for four centres; the
        // second call, before the first has rebuilt the list, must not read past it.
        var world = CreateWorld(levels: 2);
        Vector3[] five = [Vector3.Zero, new(3, 0, 0), new(0, 0, 3), new(-3, 0, 0), new(0, 0, -3)];

        world.SetCenters(five);
        world.SetCenters(five);
        Settle(world, OriginColumn);
        world.SetCenters(five);

        Assert.True(world.ResidentColumns > 16, $"{world.ResidentColumns} columns are resident.");
    }

    [Fact]
    public void Every_level_loads_the_columns_within_its_own_radius()
    {
        var world = CreateWorld(radius: 2, levels: 3);

        world.SetCenters(Origin);
        SettleAll(world);

        for (var level = 0; level < 3; level++)
        {
            var radius = 200L << level;
            var resident = 0;
            for (var z = -12; z <= 12; z++)
            {
                for (var x = -12; x <= 12; x++)
                {
                    var key = new ColumnKey(level, x, z);
                    var inside = NearestSquared(key, 0, 0) <= radius * radius;
                    Assert.Equal(inside, world.IsResident(key));
                    resident += inside ? 1 : 0;
                }
            }

            Assert.Equal(resident, world.ResidentColumnsAt(level));
            Assert.True(resident >= 4, $"Level {level} has {resident} columns.");
        }

        Assert.Equal(
            world.ResidentColumns,
            world.ResidentColumnsAt(0) + world.ResidentColumnsAt(1) + world.ResidentColumnsAt(2)
        );
    }

    [Fact]
    public void A_small_centre_loads_only_its_own_level_0_disc()
    {
        var world = CreateWorld(radius: 2, levels: 3);

        // The full view at the origin, and a remote player's 3 m level-0 disc 20 m away.
        world.SetStreamingCentres([
            new StreamingCentre(Vector3.Zero, 2, 3),
            new StreamingCentre(new Vector3(20, 0, 0), 3, 1),
        ]);
        SettleAll(world);

        for (var level = 0; level < 3; level++)
        {
            for (var z = -8; z <= 8; z++)
            {
                for (var x = -8; x <= 30; x++)
                {
                    var key = new ColumnKey(level, x, z);
                    var nearOrigin = NearestSquared(key, 0, 0) <= (200L << level) * (200L << level);
                    var nearRemote = level == 0 && NearestSquared(key, 2000, 0) <= 300L * 300L;
                    Assert.Equal(nearOrigin || nearRemote, world.IsResident(key));
                }
            }
        }

        // Taking the remote centre away unloads its disc.
        world.SetStreamingCentres([new StreamingCentre(Vector3.Zero, 2, 3)]);
        SettleAll(world);
        Assert.False(world.IsResident(new ColumnKey(0, 15, 0)));
    }

    [Fact]
    public void The_view_loads_each_level_to_its_own_distance()
    {
        // Level 0 drawn to 3 m and each level after it 1.5 times as far: levels 0 and 1 load 2 of their columns farther
        // (5.56 and 9.62 m), and the coarsest to the view's end (8 m times 4).
        var world = CreateWorld(radius: 8, budgetMegabytes: 256, levels: 3, level0Reach: 3, growth: 1.5);
        world.SetCenters(Origin);
        SettleAll(world);

        long[] radii = [556, 962, 3200];
        for (var level = 0; level < 3; level++)
        {
            Assert.Equal(radii[level] / 100.0, world.RadiusMetres(level));
            for (var z = -12; z <= 12; z++)
            {
                for (var x = -12; x <= 12; x++)
                {
                    var key = new ColumnKey(level, x, z);
                    Assert.Equal(NearestSquared(key, 0, 0) <= radii[level] * radii[level], world.IsResident(key));
                }
            }
        }

        // Another centre keeps its own radius, twice as far at each level.
        Assert.Equal(8, world.RadiusMetres(new StreamingCentre(Vector3.Zero, 2, 3), 2));
        Assert.Equal(0, world.RadiusMetres(new StreamingCentre(Vector3.Zero, 2, 1), 1));
        Assert.Equal(32, world.RadiusMetres(new StreamingCentre(Vector3.Zero, 3, 3, View: true), 2));
    }

    [Fact]
    public void The_coarsest_level_loads_before_finer_ones()
    {
        // One worker runs jobs in the order they were queued, so the loads come in priority order.
        var world = CreateWorld(radius: 3, levels: 3, workers: 1);
        var cursor = world.CreateChangeCursor();
        world.SetCenters(Origin);
        SettleAll(world);
        AssertCoarsestFirst(ReadAll(world, cursor));

        // After a jump far away, every column is new again.
        world.SetCenters([new Vector3(300, 0, 0)]);
        SettleAll(world);
        AssertCoarsestFirst(ReadAll(world, cursor));

        static void AssertCoarsestFirst(List<VoxelChange> changes)
        {
            var levels = changes
                .Where(c => c.Kind == VoxelChangeKind.ColumnLoaded)
                .Select(c => c.Column.Level)
                .ToList();
            var coarsest = levels.Count(level => level == 2);
            Assert.True(coarsest > 4, $"{coarsest} coarsest columns loaded.");
            Assert.All(levels.Take(coarsest), level => Assert.Equal(2, level));
        }
    }

    [Fact]
    public void Priorities_put_the_coarsest_level_first_then_what_rays_reach_then_the_ring()
    {
        const long Radius = 1600;
        static long Reach(int level) => VoxelGrid.Unit.ReachCentimetres(Radius, level);

        // A far column of the coarsest level comes before the finer column under the centre.
        Assert.True(
            VoxelWorld.Priority(VoxelGrid.Unit, 20_000, 4, Reach(4), true)
                < VoxelWorld.Priority(VoxelGrid.Unit, 0, 0, Reach(0), false)
        );
        // Within reach, a parent (half as many of its own columns away) comes before its children.
        Assert.True(
            VoxelWorld.Priority(VoxelGrid.Unit, 640, 1, Reach(1), false)
                < VoxelWorld.Priority(VoxelGrid.Unit, 640, 0, Reach(0), false)
        );
        // A column that rays reach comes before any column of the ring beyond reach.
        Assert.True(
            VoxelWorld.Priority(VoxelGrid.Unit, Reach(0), 0, Reach(0), false)
                < VoxelWorld.Priority(VoxelGrid.Unit, Radius, 0, Reach(0), false)
        );
        Assert.True(
            VoxelWorld.Priority(VoxelGrid.Unit, 2 * Radius, 2, Reach(2), false)
                < VoxelWorld.Priority(VoxelGrid.Unit, Radius, 0, Reach(0), false)
        );
        // Beyond reach, the ring nearest in metres first: level 0's before level 1's.
        Assert.True(
            VoxelWorld.Priority(VoxelGrid.Unit, Radius, 0, Reach(0), false)
                < VoxelWorld.Priority(VoxelGrid.Unit, 2 * Radius, 1, Reach(1), false)
        );
    }

    [Fact]
    public void Wanted_columns_are_rebuilt_after_half_a_column_of_movement()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        // Column (2, 2) has its nearest point at (2.56, 2.56) m: 3.62 m from the origin, outside the 3 m radius, and
        // 2.98 m from (0.45, 0.45) m, inside it.
        var entering = new ColumnKey(0, 2, 2);

        // 64 cm (45 cm on x and z) is less than half a 1.28 m column: nothing is recomputed, so nothing new loads.
        world.SetCenters([new Vector3(0.45f, 0, 0.45f)]);
        for (var i = 0; i < 20; i++)
        {
            world.Update(0);
            Thread.Sleep(1);
        }

        Assert.Equal(0, world.PendingColumns);
        Assert.False(world.IsResident(entering));

        // 71 cm is more, although the centre is still in the same column.
        world.SetCenters([new Vector3(0.5f, 0, 0.5f)]);
        Settle(world, entering);
        Assert.True(world.IsResident(entering));
    }

    [Fact]
    public void Coarse_columns_hold_the_generator_sampled_at_their_level()
    {
        var world = CreateWorld(radius: 2, levels: 3);
        world.SetCenters(Origin);
        SettleAll(world);

        for (var level = 1; level < 3; level++)
        {
            for (var x = -150; x < 150; x += 23)
            {
                for (var y = 0; y < 400; y += 17)
                {
                    var z = -x / 3;
                    Assert.True(world.TryGetMaterial(new VoxelCoord(x, y, z), level, out var material));
                    // A level-k voxel holds what the generator gives at its sample point.
                    int sx = VoxelConstants.SampleAt(x >> level, level),
                        sy = VoxelConstants.SampleAt(y >> level, level),
                        sz = VoxelConstants.SampleAt(z >> level, level);
                    Assert.Equal(Expected(sx, sy, sz), material);
                }
            }
        }
    }

    [Fact]
    public void Edits_reach_resident_coarse_columns()
    {
        var world = CreateWorld(radius: 2, levels: 3);
        world.SetCenters(Origin);
        SettleAll(world);
        var ground = SlopeGenerator.Height(0, 0, Seed);
        var centre = new VoxelCoord(0, ground - 20, 0);

        world.Apply(VoxelEdit.Sphere(centre, 60, VoxelEditOperation.Clear));

        for (var level = 0; level < 3; level++)
        {
            Assert.True(world.TryGetMaterial(centre, level, out var material));
            Assert.Equal(VoxelConstants.Air, material);
        }
    }

    [Fact]
    public void Moving_far_away_unloads_every_level()
    {
        var world = CreateWorld(radius: 2, levels: 3);
        world.SetCenters(Origin);
        SettleAll(world);

        world.SetCenters([new Vector3(300, 0, 0)]);
        SettleAll(world);

        for (var level = 0; level < 3; level++)
        {
            Assert.False(world.IsResident(new ColumnKey(level, 0, 0)));
            Assert.True(world.ResidentColumnsAt(level) > 0);
        }
    }

    [Fact]
    public void Moving_the_centre_unloads_far_columns_and_loads_new_ones()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        var cursor = world.CreateChangeCursor();
        var usedAtOrigin = world.UsedBytes;

        world.SetCenters(Away);
        Settle(world, new ColumnKey(0, 39, 0));

        Assert.False(world.IsResident(OriginColumn));
        var changes = ReadAll(world, cursor);
        Assert.Equal(32, changes.Count(c => c.Kind == VoxelChangeKind.ColumnUnloaded));
        Assert.Equal(world.ResidentColumns, changes.Count(c => c.Kind == VoxelChangeKind.ColumnLoaded));
        Assert.InRange(world.UsedBytes, usedAtOrigin / 2, usedAtOrigin * 2);
    }

    [Fact]
    public void Edits_change_resident_columns_and_record_brick_changes()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        var cursor = world.CreateChangeCursor();
        var centre = new VoxelCoord(10, 200, 10);
        Assert.True(world.TryGetMaterial(centre, out var before));
        Assert.Equal(SlopeGenerator.Dirt, before);

        var sequence = world.Apply(VoxelEdit.Sphere(centre, 20, VoxelEditOperation.Clear));

        Assert.Equal(1, sequence);
        world.TryGetMaterial(centre, out var after);
        Assert.Equal(VoxelConstants.Air, after);
        var changes = ReadAll(world, cursor);
        Assert.NotEmpty(changes);
        Assert.All(
            changes,
            c =>
                Assert.Contains(c.Kind, (VoxelChangeKind[])[VoxelChangeKind.BrickChanged, VoxelChangeKind.ChunkChanged])
        );
        Assert.All(changes, c => Assert.InRange(c.Column.X, -1, 0));
    }

    [Fact]
    public void A_large_edit_records_its_chunks_once_each_and_does_not_overrun_the_change_list()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        var cursor = world.CreateChangeCursor();

        // A 4 m sphere of stone: about 65,000 bricks at level 0 alone.
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 200, 0), 200, VoxelEditOperation.Set, SlopeGenerator.Dirt));
        world.ApplyPendingEdits(int.MaxValue);

        var changes = ReadAll(world, cursor);
        Assert.False(cursor.NeedsResync);
        Assert.InRange(changes.Count, 1, 2000);
        var chunks = changes.Where(c => c.Kind == VoxelChangeKind.ChunkChanged).ToList();
        Assert.NotEmpty(chunks);
        Assert.Equal(chunks.Count, chunks.Distinct().Count());
        Assert.All(chunks, c => Assert.Equal(-1, c.Brick));
    }

    [Fact]
    public void An_edit_on_an_unloaded_column_is_present_after_reload()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        world.SetCenters(Away);
        Settle(world, new ColumnKey(0, 39, 0));
        var centre = new VoxelCoord(10, 200, 10);

        world.Apply(VoxelEdit.Sphere(centre, 20, VoxelEditOperation.Clear));
        Assert.False(world.TryGetMaterial(centre, out _));

        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        world.TryGetMaterial(centre, out var dug);
        world.TryGetMaterial(new VoxelCoord(10, 170, 10), out var untouched);
        Assert.Equal(VoxelConstants.Air, dug);
        Assert.Equal(SlopeGenerator.Dirt, untouched);
    }

    [Fact]
    public void An_edit_made_while_its_column_generates_is_present_after_install()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        world.Update(0);
        Assert.True(world.PendingColumns > 0);

        world.Apply(VoxelEdit.Box(new VoxelCoord(0, 0, 0), new VoxelCoord(10, 400, 10), VoxelEditOperation.Set, 9));
        Settle(world, OriginColumn);

        world.TryGetMaterial(new VoxelCoord(5, 350, 5), out var material);
        Assert.Equal(9, material);
    }

    [Fact]
    public void Reaching_the_budget_stops_loading_with_one_warning()
    {
        var logger = new RecordingLogger<VoxelWorld>();
        var world = CreateWorld(radius: 20, budgetMegabytes: 8, logger: logger);

        world.SetCenters(Origin);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(world.IsMemoryFull && world.PendingColumns == 0))
        {
            Assert.True(DateTime.UtcNow < deadline, "Memory never filled.");
            world.Update(0);
            Thread.Sleep(1);
        }

        for (var i = 0; i < 20; i++)
        {
            world.Update(0);
        }

        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.True(world.ResidentColumns > 0);
        Assert.True(world.UsedBytes <= 8 << 20);
    }

    [Fact]
    public void Memory_full_episodes_warn_at_most_once_a_minute()
    {
        var logger = new RecordingLogger<VoxelWorld>();
        var world = CreateWorld(radius: 20, budgetMegabytes: 8, logger: logger);

        FillMemory(world, now: 0);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

        // Unloading everything frees far more than 5 % of the budget, so loading resumes.
        world.SetCenters([]);
        UpdateUntil(world, 0, () => !world.IsMemoryFull && world.ResidentColumns == 0);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("loads again"));

        // A second episode within a minute of the Warning is logged at Debug only.
        world.SetCenters(Origin);
        FillMemory(world, now: 30);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("reached its budget"));

        // One that starts a minute or more after the Warning warns again.
        world.SetCenters([]);
        UpdateUntil(world, 30, () => !world.IsMemoryFull && world.ResidentColumns == 0);
        world.SetCenters(Origin);
        FillMemory(world, now: 61);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public void Walking_at_the_cap_rarely_starts_jobs_that_fail()
    {
        var world = CreateWorld(radius: 20, budgetMegabytes: 8);
        FillMemory(world, now: 0);
        var failedWhenFull = world.FailedJobCount;

        Vector3[] centre = [Vector3.Zero];
        for (var step = 1; step <= 20; step++)
        {
            centre[0] = new Vector3(step * 1.5f, 0, 0);
            world.SetCenters(centre);
            for (var i = 0; i < 30; i++)
            {
                world.Update(0);
                Thread.Sleep(2);
            }
        }

        UpdateUntil(world, 0, () => world.PendingColumns == 0);
        var failed = world.FailedJobCount - failedWhenFull;
        Assert.True(failed <= 10, $"{failed} jobs failed while walking at the cap.");
        Assert.True(world.IsResident(ColumnKey.Containing(VoxelCoord.FromMetres(centre[0]), 0)));
    }

    [Fact]
    public void Workers_free_the_memory_of_unloaded_columns()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        Assert.True(world.UsedBytes > 0);

        world.SetCenters([]);
        UpdateUntil(world, 0, () => world.ResidentColumns == 0 && world.PendingFrees == 0);

        Assert.Equal(0, world.UsedBytes);
        Assert.Equal(0, world.StoredBricks);
        Assert.Equal(0, world.MixedChunkCount);
    }

    [Fact]
    public void A_full_world_that_stands_still_stays_full()
    {
        var world = CreateWorld(radius: 20, budgetMegabytes: 8);

        FillMemory(world, now: 0);
        for (var i = 0; i < 50; i++)
        {
            world.Update(0);
            Thread.Sleep(1);
        }

        Assert.True(world.IsMemoryFull);
        Assert.Equal(0, world.PendingColumns);
    }

    [Fact]
    public void Running_counts_match_a_walk_over_the_columns()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        AssertCountsMatch(world);
        Assert.True(world.MixedChunkCount > 0);

        // Digging into the stone layer below the ground turns a uniform layer into a mixed one.
        var before = world.MixedChunkCount;
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(64, 64, 64), 20, VoxelEditOperation.Clear));
        Assert.Equal(before + 1, world.MixedChunkCount);
        AssertCountsMatch(world);

        // Filling that chunk with stone again makes it uniform.
        world.Apply(
            VoxelEdit.Box(
                new VoxelCoord(0, 0, 0),
                new VoxelCoord(128, 128, 128),
                VoxelEditOperation.Set,
                SlopeGenerator.Stone
            )
        );
        Assert.Equal(before, world.MixedChunkCount);
        AssertCountsMatch(world);

        world.SetCenters(Away);
        Settle(world, new ColumnKey(0, 39, 0), OriginColumn);
        AssertCountsMatch(world);
    }

    [Fact]
    public void Update_allocates_nothing_while_the_centre_moves()
    {
        var world = CreateWorld(budgetMegabytes: 64);
        var away = new ColumnKey(0, 39, 0);
        for (var i = 0; i < 3; i++)
        {
            world.SetCenters(Origin);
            Settle(world, OriginColumn, away);
            world.SetCenters(Away);
            Settle(world, away, OriginColumn);
        }

        // A long window with worker threads running: a collection in it would add bytes never allocated, so it is
        // measured again when one falls in it.
        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                world.SetCenters(Origin);
                Settle(world, OriginColumn, away);
                world.SetCenters(Away);
                Settle(world, away, OriginColumn);
            }
        });

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Edits_on_resident_columns_allocate_nothing()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        var dig = VoxelEdit.Sphere(new VoxelCoord(0, 210, 0), 30, VoxelEditOperation.Clear);
        var fill = VoxelEdit.Sphere(new VoxelCoord(0, 210, 0), 30, VoxelEditOperation.Set, 5);
        world.Apply(in dig);
        world.Apply(in fill);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 5; i++)
        {
            world.Apply(in dig);
            world.Apply(in fill);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_saved_edit_log_rebuilds_the_same_world()
    {
        var original = CreateWorld();
        original.SetCenters(Origin);
        Settle(original, OriginColumn);
        original.Apply(VoxelEdit.Sphere(new VoxelCoord(-40, 220, 30), 50, VoxelEditOperation.Clear));
        original.Apply(
            VoxelEdit.Box(new VoxelCoord(0, 150, 0), new VoxelCoord(90, 80, 40), VoxelEditOperation.Paint, 7)
        );
        using var stream = new MemoryStream();
        original.SaveEdits(stream, 60, []);
        stream.Position = 0;

        var copy = CreateWorld();
        Assert.Equal(2, copy.LoadEdits(stream));
        copy.SetCenters(Origin);
        Settle(copy, OriginColumn);

        for (var x = -250; x < 250; x += 11)
        {
            for (var y = 100; y < 300; y += 7)
            {
                var voxel = new VoxelCoord(x, y, x / 3);
                original.TryGetMaterial(voxel, out var expected);
                copy.TryGetMaterial(voxel, out var actual);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void Loading_edits_needs_an_unedited_world_with_the_same_seed()
    {
        var original = CreateWorld();
        original.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 0, 0), 5, VoxelEditOperation.Clear));
        using var stream = new MemoryStream();
        original.SaveEdits(stream, 60, []);

        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => CreateWorld(seed: 18).LoadEdits(stream));
        stream.Position = 0;
        Assert.Throws<InvalidOperationException>(() => original.LoadEdits(stream));
    }

    [Fact]
    public void Invalid_edits_are_rejected_and_not_logged()
    {
        var world = CreateWorld();

        Assert.Throws<ArgumentException>(() => world.Apply(VoxelEdit.Sphere(default, -1, VoxelEditOperation.Clear)));
        Assert.Throws<ArgumentException>(() =>
            world.ApplySequenced(VoxelEdit.Sphere(default, 1, VoxelEditOperation.Clear) with { Sequence = 5 })
        );
        Assert.Throws<ArgumentException>(() =>
            world.Apply(VoxelEdit.Capsule(default, new VoxelCoord(5000, 0, 0), 3, VoxelEditOperation.Clear))
        );
        Assert.Equal(0, world.EditCount);
    }

    [Fact]
    public void A_capsule_bores_its_channel_at_every_level_it_reaches()
    {
        var world = CreateWorld(radius: 2, levels: 3);
        world.SetCenters(Origin);
        SettleAll(world);
        var ground = SlopeGenerator.Height(0, 0, Seed);
        var start = new VoxelCoord(0, ground - 1, 0);
        var end = new VoxelCoord(0, ground - 41, 30);

        world.Apply(VoxelEdit.Capsule(start, end, 6, VoxelEditOperation.Clear));

        Assert.Equal(1, world.EditCount);
        Assert.Equal(VoxelEditShape.Capsule, world.GetEdit(1).Shape);
        // Along its line it is air at every level; beside it, the ground stays.
        foreach (var share in (float[])[0.2f, 0.5f, 0.8f])
        {
            var at = new VoxelCoord(0, (int)(ground - 1 - 40 * share), (int)(30 * share));
            Assert.True(world.TryGetMaterial(at, out var material));
            Assert.Equal(VoxelConstants.Air, material);
        }

        Assert.True(world.TryGetMaterial(new VoxelCoord(20, ground - 21, 15), out var beside));
        Assert.NotEqual(VoxelConstants.Air, beside);
        Assert.True(world.TryGetMaterial(new VoxelCoord(0, ground - 21, 15), 1, out var coarse));
        Assert.Equal(VoxelConstants.Air, coarse);
    }

    [Fact]
    public void Dispose_can_run_twice_and_stops_later_use()
    {
        var world = CreateWorld();
        world.SetCenters(Origin);
        world.Update(0);

        world.Dispose();
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.Update(0));
    }

    [Fact]
    public void Wanted_columns_are_resident_only_once_a_centre_is_set_and_every_level_has_loaded()
    {
        var world = CreateWorld(radius: 2, levels: 3);

        // With no centre nothing is wanted, but nothing has loaded either: not resident.
        world.Update(0);
        Assert.False(world.WantedColumnsResident);
        Assert.Equal(0, world.WantedFraction);

        world.SetCenters(Origin);
        world.Update(0);
        Assert.False(world.WantedColumnsResident);
        Assert.True(world.WantedFraction < 1);

        SettleAll(world);
        world.Update(0);
        Assert.True(world.WantedColumnsResident);
        Assert.Equal(1, world.WantedFraction);

        // A centre that moves away wants other columns.
        world.SetCenters(Away);
        world.Update(0);
        Assert.False(world.WantedColumnsResident);
        SettleAll(world);
        world.Update(0);
        Assert.True(world.WantedColumnsResident);
    }

    [Fact]
    public void Any_solid_in_an_edit_agrees_with_reading_every_voxel()
    {
        var world = CreateWorld(radius: 3);
        world.SetCenters(Origin);
        Settle(world, OriginColumn);
        var random = new Random(11);
        var runs = new List<(byte Material, int Length)>();
        var solid = 0;
        for (var i = 0; i < 200; i++)
        {
            // Spheres and boxes about the slope's surface: some in air, some touching, some deep in the ground.
            var centre = new VoxelCoord(random.Next(-200, 200), 0, random.Next(-200, 200));
            centre = centre with { Y = SlopeGenerator.Height(centre.X, centre.Z, Seed) + random.Next(-40, 40) };
            var size = random.Next(1, 40);
            var edit =
                i % 2 == 0
                    ? VoxelEdit.Sphere(centre, size, VoxelEditOperation.Lift)
                    : VoxelEdit.Box(
                        centre,
                        new VoxelCoord(size, random.Next(1, 40), random.Next(1, 40)),
                        VoxelEditOperation.Lift
                    );
            Assert.True(world.TryCollectRuns(in edit, runs));
            // The runs are air outside a sphere; inside, what is there.
            var expected = runs.Count > 1 || runs[0].Material != VoxelConstants.Air;
            Assert.Equal(expected, world.AnySolidIn(in edit));
            solid += expected ? 1 : 0;
        }

        Assert.InRange(solid, 40, 160);
    }

    [Fact]
    public void An_edit_warm_up_never_started_counts_as_done()
    {
        var world = CreateWorld();

        Assert.True(world.EditWarmUpDone);
    }

    [Fact]
    public void A_world_after_one_whose_edit_warm_up_ran_to_its_end_starts_none()
    {
        var warmUps = new WarmUps();
        var first = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 32,
                WorkerThreads = 2,
            },
            0,
            new SlopeGenerator(),
            NullLogger<VoxelWorld>.Instance,
            warmUps
        );
        _worlds.Add(first);
        first.Initialize();
        first.StartEditWarmUp();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!first.EditWarmUpDone && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.True(first.EditWarmUpDone);
        Assert.True(warmUps.Edits);

        var second = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 3,
                Levels = 1,
                MemoryBudgetMegabytes = 32,
                WorkerThreads = 2,
            },
            0,
            new SlopeGenerator(),
            NullLogger<VoxelWorld>.Instance,
            warmUps
        );
        _worlds.Add(second);
        second.Initialize();
        second.StartEditWarmUp();

        Assert.True(second.EditWarmUpDone);
    }

    /// <summary>
    /// Disposing the world while the edit warm-up runs waits for it before the memory it writes is freed (the wait's
    /// result was ignored).
    /// </summary>
    [Fact]
    public void Disposing_while_the_edit_warm_up_runs_stops_it_before_freeing_the_memory()
    {
        var world = CreateWorld();
        var memory = world.ReadMemory;
        world.StartEditWarmUp();

        world.Dispose();

        Assert.True(world.EditWarmUpDone);
        Assert.Equal(0, memory.ReservedBytes);
    }

    /// <summary>A writer outside the world that did not stop (a character build) keeps the memory from being freed.</summary>
    [Fact]
    public void Memory_left_at_exit_is_not_freed()
    {
        var world = CreateWorld();
        world.SetCenters([Vector3.Zero]);
        SettleAll(world);
        var memory = world.ReadMemory;
        var reserved = memory.ReservedBytes;

        world.LeaveMemoryAtExit();
        world.Dispose();

        Assert.True(reserved > 0);
        Assert.Equal(reserved, memory.ReservedBytes);
    }

    private static byte Expected(int x, int y, int z)
    {
        var height = SlopeGenerator.Height(x, z, Seed);
        return y < height - 50 ? SlopeGenerator.Stone
            : y < height ? SlopeGenerator.Dirt
            : VoxelConstants.Air;
    }

    /// <summary>Updates until nothing is pending, <paramref name="resident"/> is loaded, and <paramref name="gone"/> is not.</summary>
    private static void Settle(VoxelWorld world, ColumnKey resident, ColumnKey gone = default)
    {
        var checkGone = gone != default;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (
            world.PendingColumns > 0
            || world.PendingFrees > 0
            || !world.IsResident(resident)
            || (checkGone && world.IsResident(gone))
        )
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The voxel world did not settle.");
            }

            world.Update(0);
            Thread.Sleep(1);
        }

        // One more pass lets the unload scan finish after the last install.
        world.Update(0);
    }

    /// <summary>Updates until nothing is pending or waiting to be freed, twice in a row.</summary>
    private static void SettleAll(VoxelWorld world)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var quiet = 0;
        while (quiet < 2)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The voxel world did not settle.");
            }

            world.Update(0);
            quiet = world.PendingColumns == 0 && world.PendingFrees == 0 && world.ResidentColumns > 0 ? quiet + 1 : 0;
            Thread.Sleep(1);
        }
    }

    private static void AssertCountsMatch(VoxelWorld world)
    {
        var (mixedChunks, bricks) = world.CountResident();
        Assert.Equal(mixedChunks, world.MixedChunkCount);
        Assert.Equal(bricks, world.StoredBricks);
    }

    /// <summary>Loads around the origin until memory is full and nothing is pending.</summary>
    private static void FillMemory(VoxelWorld world, double now)
    {
        world.SetCenters(Origin);
        UpdateUntil(world, now, () => world.IsMemoryFull && world.PendingColumns == 0);
    }

    private static void UpdateUntil(VoxelWorld world, double now, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The voxel world did not reach the expected state.");
            }

            world.Update(now);
            Thread.Sleep(1);
        }
    }

    private static List<VoxelChange> ReadAll(VoxelWorld world, VoxelChangeCursor cursor)
    {
        var all = new List<VoxelChange>();
        var buffer = new VoxelChange[256];
        int count;
        while ((count = world.ReadChanges(cursor, buffer)) > 0)
        {
            all.AddRange(buffer.Take(count));
        }

        return all;
    }

    /// <summary>The squared horizontal distance in centimetres from (x, z) to a 1 cm grid column's nearest point.</summary>
    private static long NearestSquared(ColumnKey key, long x, long z)
    {
        long size = VoxelConstants.ChunkSize << key.Level;
        long low = key.X * size,
            front = key.Z * size;
        var dx = Math.Max(0, Math.Max(low - x, x - low - size));
        var dz = Math.Max(0, Math.Max(front - z, z - front - size));
        return dx * dx + dz * dz;
    }

    private VoxelWorld CreateWorld(
        double radius = 3,
        int budgetMegabytes = 32,
        ulong seed = Seed,
        ILogger<VoxelWorld>? logger = null,
        int levels = 1,
        int workers = 2,
        double level0Reach = 0,
        double growth = 2
    )
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = radius,
            Levels = levels,
            Level0ReachMetres = level0Reach,
            ReachGrowth = growth,
            MemoryBudgetMegabytes = budgetMegabytes,
            WorkerThreads = workers,
            Seed = seed,
        };
        var world = new VoxelWorld(settings, 0, new SlopeGenerator(), logger ?? NullLogger<VoxelWorld>.Instance);
        world.Initialize();
        _worlds.Add(world);
        return world;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
