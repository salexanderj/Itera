using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed class VoxelChangeLogTests
{
    [Fact]
    public void A_cursor_reads_changes_in_order_across_small_buffers()
    {
        var log = new VoxelChangeLog(16);
        var cursor = log.CreateCursor();
        for (var i = 0; i < 10; i++)
        {
            log.Add(Brick(i));
        }

        var read = new List<VoxelChange>();
        var buffer = new VoxelChange[4];
        int count;
        while ((count = log.Read(cursor, buffer)) > 0)
        {
            read.AddRange(buffer.Take(count));
        }

        Assert.Equal(Enumerable.Range(0, 10).Select(Brick), read);
        Assert.False(cursor.NeedsResync);
    }

    [Fact]
    public void A_new_cursor_sees_only_later_changes()
    {
        var log = new VoxelChangeLog(16);
        log.Add(Brick(1));
        var cursor = log.CreateCursor();
        log.Add(Brick(2));
        var buffer = new VoxelChange[8];

        Assert.Equal(1, log.Read(cursor, buffer));
        Assert.Equal(Brick(2), buffer[0]);
    }

    [Fact]
    public void Cursors_read_independently()
    {
        var log = new VoxelChangeLog(16);
        var fast = log.CreateCursor();
        var slow = log.CreateCursor();
        var buffer = new VoxelChange[8];
        log.Add(Brick(1));

        Assert.Equal(1, log.Read(fast, buffer));
        log.Add(Brick(2));

        Assert.Equal(1, log.Read(fast, buffer));
        Assert.Equal(2, log.Read(slow, buffer));
    }

    /// <summary>
    /// A cursor exactly a full ring behind reads every change, in order, with no resync (only one past the
    /// ring was tested).
    /// </summary>
    [Fact]
    public void A_cursor_exactly_a_full_ring_behind_reads_every_change()
    {
        var log = new VoxelChangeLog(8);
        var cursor = log.CreateCursor();
        var buffer = new VoxelChange[8];
        for (var i = 0; i < 8; i++)
        {
            log.Add(Brick(i));
        }

        Assert.Equal(8, log.Read(cursor, buffer));
        Assert.False(cursor.NeedsResync);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(Brick(i), buffer[i]);
        }

        Assert.Equal(0, log.Read(cursor, buffer));
    }

    [Fact]
    public void A_cursor_that_falls_a_full_ring_behind_must_resync()
    {
        var log = new VoxelChangeLog(8);
        var cursor = log.CreateCursor();
        var buffer = new VoxelChange[8];
        for (var i = 0; i < 9; i++)
        {
            log.Add(Brick(i));
        }

        Assert.Equal(0, log.Read(cursor, buffer));
        Assert.True(cursor.NeedsResync);

        cursor.EndResync();
        log.Add(Brick(100));
        Assert.Equal(1, log.Read(cursor, buffer));
        Assert.Equal(Brick(100), buffer[0]);
        Assert.False(cursor.NeedsResync);
    }

    [Fact]
    public void Capacity_must_be_a_power_of_two()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoxelChangeLog(12));
    }

    private static VoxelChange Brick(int cell) => new(VoxelChangeKind.BrickChanged, new ColumnKey(0, 1, 2), 3, cell);
}
