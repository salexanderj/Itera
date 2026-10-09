using System.Diagnostics;
using System.Text;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// The edit log's index budget: one edit of the largest size adds tens of thousands of index
/// entries, so a save, a host's world, or a host's live edits once asked for gigabytes. An edit past the budget is
/// refused before anything changes: by the log, by a world loading a stream (as data that cannot be read), and by
/// <see cref="VoxelWorld.TryApply"/> (as a player's edit the world does not take, which also refuses one far outside
/// the map, INPUT-far-tool-edits). The budgets here are a test's, so no test fills 256 MB.
/// </summary>
public sealed class EditIndexBudgetTests
{
    private const int Budget = 200_000;

    private static readonly BoxesGenerator s_generator = new(100);

    [Fact]
    public void The_log_refuses_an_edit_past_its_budget_and_stays_as_it_was()
    {
        var log = new EditLog(5, VoxelGrid.Unit);
        log.LimitIndex(Budget);
        Assert.True(EditLog.IndexEntriesFor(Largest(1), VoxelGrid.Unit) > 30_000);

        while (EditLog.IndexEntriesFor(Largest(log.Count + 1), VoxelGrid.Unit) <= Budget - log.IndexEntries)
        {
            log.Append(Largest(log.Count + 1));
        }

        var count = log.Count;
        var entries = log.IndexEntries;
        Assert.Throws<ArgumentException>(() => log.Append(Largest(count + 1)));
        Assert.Equal(count, log.Count);
        Assert.Equal(entries, log.IndexEntries);
        Assert.True(entries <= Budget);

        // A reset empties the index, and edits fit again.
        log.Append(VoxelEdit.ResetAll() with { Sequence = count + 1 });
        log.Append(Largest(count + 2));
        Assert.Equal(count + 2, log.Count);
    }

    [Fact]
    public void A_stream_of_the_largest_edits_is_refused_quickly_as_one_that_cannot_be_read()
    {
        const int edits = 2000;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("ITVE"u8);
            writer.Write(2);
            writer.Write(s_generator.Id);
            writer.Write(s_generator.Version);
            writer.Write(17UL);
            writer.Write((long)edits);
            for (var sequence = 1; sequence <= edits; sequence++)
            {
                WriteEdit(writer, Largest(sequence));
            }

            writer.Write(0);
        }

        stream.Position = 0;
        using var world = World();
        var watch = Stopwatch.StartNew();

        var failure = Assert.Throws<InvalidDataException>(() => world.LoadEdits(stream));

        Assert.Contains("index entries", failure.Message, StringComparison.Ordinal);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"The refusal took {watch.ElapsedMilliseconds} ms.");
        Assert.True(world.EditCount < edits);
    }

    [Fact]
    public void TryApply_refuses_an_edit_far_outside_the_map_and_changes_nothing()
    {
        using var world = World();

        var far = VoxelEdit.Sphere(new VoxelCoord(1 << 25, 0, 0), 50, VoxelEditOperation.Clear);
        var applied = world.TryApply(far, 0, out var sequence);

        Assert.False(applied);
        Assert.Equal(0, sequence);
        Assert.Equal(0, world.EditCount);
        Assert.False(world.CanApply(far));
        Assert.Throws<ArgumentException>(() => world.Apply(far));
    }

    /// <summary>
    /// A player's edits (<see cref="VoxelWorld.TryApply"/>) stop short of the budget, so the edits that must not fail
    /// (loose bodies settling, through <see cref="VoxelWorld.Apply"/>) still have room; past the whole budget Apply
    /// throws, with nothing changed. A reset frees the budget again.
    /// </summary>
    [Fact]
    public void TryApply_refuses_edits_short_of_the_budget_and_Apply_takes_the_rest()
    {
        using var world = World();

        var applied = 0;
        while (world.TryApply(Largest(0), 0, out _))
        {
            applied++;
        }

        Assert.True(applied > 0);
        Assert.Equal(applied, world.EditCount);
        var settled = 0;
        while (true)
        {
            var count = world.EditCount;
            try
            {
                world.Apply(Largest(0));
                settled++;
            }
            catch (ArgumentException)
            {
                Assert.Equal(count, world.EditCount);
                break;
            }
        }

        Assert.True(settled > 0, "No room was kept back for the edits that must not fail.");
        world.Reset();
        Assert.True(world.TryApply(Largest(0), 0, out _));
    }

    /// <summary>A world of one level whose edit log holds at most <see cref="Budget"/> index entries.</summary>
    private static VoxelWorld World()
    {
        var world = new VoxelWorld(
            new VoxelSettings { Levels = 1, Seed = 17 },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        world.Initialize();
        world.LimitEditIndex(Budget);
        return world;
    }

    /// <summary>A dig of the largest radius an edit may have, at the map's middle.</summary>
    [Fact]
    public void Logs_that_index_different_levels_count_alike_and_refuse_the_same_edit()
    {
        // A host of one level and a client of every level (or a save made by one and loaded by the other) must agree on
        // which edit passes the budget, or the client is dropped.
        var few = new EditLog(1, VoxelGrid.Unit);
        var many = new EditLog(EditLog.CountedLevels, VoxelGrid.Unit);
        few.LimitIndex(Budget);
        many.LimitIndex(Budget);
        var small = VoxelEdit.Sphere(new VoxelCoord(10, 10, 10), 5, VoxelEditOperation.Clear) with { Sequence = 1 };
        Assert.Equal(EditLog.CountedLevels, EditLog.IndexEntriesFor(in small, VoxelGrid.Unit));

        for (var sequence = 1L; ; sequence++)
        {
            var edit = Largest(sequence);
            var fewTook = TryAppend(few, in edit);
            Assert.Equal(fewTook, TryAppend(many, in edit));
            if (!fewTook)
            {
                break;
            }
        }

        Assert.True(few.Count > 0);
        Assert.Equal(few.Count, many.Count);
        Assert.Equal(few.IndexEntries, many.IndexEntries);
    }

    private static bool TryAppend(EditLog log, in VoxelEdit edit)
    {
        try
        {
            log.Append(in edit);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static VoxelEdit Largest(long sequence) =>
        VoxelEdit.Sphere(default, VoxelConstants.MaxEditExtent, VoxelEditOperation.Clear) with
        {
            Sequence = sequence,
        };

    /// <summary>An edit as format 2 of the log stores it.</summary>
    private static void WriteEdit(BinaryWriter writer, in VoxelEdit edit)
    {
        writer.Write(edit.Sequence);
        writer.Write((byte)edit.Shape);
        writer.Write((byte)edit.Operation);
        writer.Write(edit.Material);
        writer.Write(edit.Origin.X);
        writer.Write(edit.Origin.Y);
        writer.Write(edit.Origin.Z);
        writer.Write(edit.Extent.X);
        writer.Write(edit.Extent.Y);
        writer.Write(edit.Extent.Z);
        writer.Write(edit.Piece);
        writer.Write(edit.Turn);
    }
}
