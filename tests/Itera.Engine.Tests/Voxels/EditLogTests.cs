using System.Text;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

public sealed class EditLogTests
{
    [Fact]
    public void Edits_must_arrive_in_sequence()
    {
        var log = new EditLog(1, VoxelGrid.Unit);

        log.Append(Dig(1, new VoxelCoord(0, 0, 0)));

        Assert.Throws<ArgumentException>(() => log.Append(Dig(3, new VoxelCoord(0, 0, 0))));
        Assert.Equal(1, log.Count);
    }

    [Fact]
    public void Edits_are_kept_across_segments()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        for (var i = 1; i <= 2500; i++)
        {
            log.Append(Dig(i, new VoxelCoord(i, 0, 0)));
        }

        Assert.Equal(2500, log.Count);
        Assert.Equal(1777, log[1777].Origin.X);
        Assert.Throws<ArgumentOutOfRangeException>(() => log[2501]);
    }

    [Fact]
    public void Each_edits_maker_is_kept_beside_it_across_segments()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        for (var i = 1; i <= 2500; i++)
        {
            log.Append(Dig(i, new VoxelCoord(i, 0, 0)), i % 3 == 0 ? VoxelEdit.NoMaker : i % VoxelEdit.MaxMaker);
        }

        Assert.Equal(1777 % VoxelEdit.MaxMaker, log.MakerOf(1777));
        Assert.Equal(VoxelEdit.NoMaker, log.MakerOf(1500));
        Assert.Equal(2500 % VoxelEdit.MaxMaker, log.MakerOf(2500));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.MakerOf(2501));
        Assert.Throws<ArgumentOutOfRangeException>(() => log.Append(Dig(2501, default), VoxelEdit.MaxMaker + 1));
        Assert.Equal(2500, log.Count);
    }

    [Fact]
    public void An_edit_across_a_column_edge_is_found_from_both_columns()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(126, 0, 5)));
        log.Append(Dig(2, new VoxelCoord(600, 0, 5)));
        log.Append(Dig(3, new VoxelCoord(130, 0, 5)));
        var found = new List<VoxelEdit>();

        log.CollectForColumn(new ColumnKey(0, 0, 0), 0, found);
        Assert.Equal([1L, 3L], found.Select(e => e.Sequence));

        found.Clear();
        log.CollectForColumn(new ColumnKey(0, 1, 0), 0, found);
        Assert.Equal([1L, 3L], found.Select(e => e.Sequence));

        found.Clear();
        log.CollectForColumn(new ColumnKey(0, 1, 0), 1, found);
        Assert.Equal([3L], found.Select(e => e.Sequence));
    }

    [Fact]
    public void Edits_outside_the_world_are_logged_but_not_indexed()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(VoxelGrid.Unit.EndVoxelXZ + 100, 0, 0)));
        log.Append(Dig(2, new VoxelCoord(0, VoxelGrid.Unit.EndVoxelY + 100, 0)));
        var found = new List<VoxelEdit>();

        log.CollectForColumn(new ColumnKey(0, VoxelGrid.Unit.MaxChunkXZ, 0), 0, found);
        log.CollectForColumn(new ColumnKey(0, 0, 0), 0, found);

        Assert.Empty(found);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void Coarse_columns_find_their_edits_in_their_own_index()
    {
        var log = new EditLog(3, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(10, 0, 10)));
        log.Append(Dig(2, new VoxelCoord(300, 0, 10)));
        log.Append(Dig(3, new VoxelCoord(250, 0, 10)));
        log.Append(Dig(4, new VoxelCoord(-50, 0, 10)));
        var found = new List<VoxelEdit>();

        // A level-1 column covers level-0 x and z from 0 to 255, a level-2 column 0 to 511.
        log.CollectForColumn(new ColumnKey(1, 0, 0), 0, found);
        Assert.Equal([1L, 3L], found.Select(e => e.Sequence));

        found.Clear();
        log.CollectForColumn(new ColumnKey(2, 0, 0), 1, found);
        Assert.Equal([2L, 3L], found.Select(e => e.Sequence));

        found.Clear();
        log.CollectForColumn(new ColumnKey(2, -1, 0), 0, found);
        Assert.Equal([4L], found.Select(e => e.Sequence));
    }

    [Fact]
    public void Columns_of_a_level_that_is_not_indexed_are_refused()
    {
        var log = new EditLog(2, VoxelGrid.Unit);

        Assert.Throws<ArgumentOutOfRangeException>(() => log.CollectForColumn(new ColumnKey(2, 0, 0), 0, []));
    }

    [Fact]
    public void Serialised_logs_read_back_unchanged()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(-5, 6, -7)));
        log.Append(
            VoxelEdit.Box(new VoxelCoord(1, 2, 3), new VoxelCoord(4, 5, 6), VoxelEditOperation.Paint, 200) with
            {
                Sequence = 2,
            }
        );
        using var stream = new MemoryStream();

        using var memory = new VoxelMemory(1 << 20);
        EditLogSerializer.Write(
            stream,
            "test.gen",
            3,
            0xDEADBEEF_12345678UL,
            30,
            [7, 8, 9],
            log,
            memory,
            new byte[VoxelConstants.ChunkVoxels]
        );
        stream.Position = 0;
        var edits = new List<VoxelEdit>();
        var header = EditLogSerializer.Read(stream, "test.gen", 3, edits, []);

        Assert.Equal(
            ("test.gen", 3, 0xDEADBEEF_12345678UL, 2L),
            (header.GeneratorId, header.GeneratorVersion, header.Seed, header.Count)
        );
        Assert.Equal(30, header.FixedStepHz);
        Assert.Equal([7, 8, 9], header.Game);
        Assert.Equal([log[1], log[2]], edits);
    }

    [Fact]
    public void A_match_without_a_world_is_written_as_a_worlds_log_of_no_edits()
    {
        using var memory = new VoxelMemory(1 << 20);
        using var worlds = new MemoryStream();
        EditLogSerializer.Write(
            worlds,
            "mesh.game",
            2,
            77UL,
            30,
            [5, 6],
            new EditLog(1, VoxelGrid.Unit),
            memory,
            new byte[VoxelConstants.ChunkVoxels]
        );
        using var empty = new MemoryStream();

        EditLogSerializer.WriteEmpty(empty, "mesh.game", 2, 77UL, 30, [5, 6]);

        Assert.Equal(worlds.ToArray(), empty.ToArray());
        empty.Position = 0;
        var edits = new List<VoxelEdit>();
        var header = EditLogSerializer.Read(empty, "mesh.game", 2, edits, []);
        Assert.Equal((77UL, 0L, 30), (header.Seed, header.Count, header.FixedStepHz));
        Assert.Empty(edits);
    }

    [Fact]
    public void The_header_reads_alone_with_the_match_settings()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(0, 0, 0)));
        using var stream = Written(log);

        var header = EditLogSerializer.ReadHeader(stream, "test.gen", 3);

        Assert.Equal(42UL, header.Seed);
        Assert.Equal(1, header.Count);
        Assert.Equal(60, header.FixedStepHz);
        Assert.Equal([1, 2], header.Game);
    }

    [Fact]
    public void A_format_2_log_reads_without_match_settings()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(3, 4, 5)));
        var bytes = Written(log).ToArray();
        // Format 4 without its match settings is format 2: the magic, the version, "test.gen" (a length byte and 8
        // bytes), the generator version, and the seed take 29 bytes; the step rate, the length, and 2 bytes follow.
        var format2 = bytes[..29].Concat(bytes[39..]).ToArray();
        format2[4] = 2;
        using var stream = new MemoryStream(format2);
        var edits = new List<VoxelEdit>();

        var header = EditLogSerializer.Read(stream, "test.gen", 3, edits, []);

        Assert.Equal(0, header.FixedStepHz);
        Assert.Null(header.Game);
        Assert.Equal([log[1]], edits);
    }

    [Fact]
    public void A_format_3_log_reads_and_a_reset_in_one_is_refused()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(3, 4, 5)));
        var plain = Written(log).ToArray();
        log.Append(VoxelEdit.ResetAll() with { Sequence = 2 });
        var withReset = Written(log).ToArray();
        // The format version follows the 4-byte magic; formats 3 and 4 have the same layout.
        plain[4] = 3;
        withReset[4] = 3;
        var edits = new List<VoxelEdit>();

        EditLogSerializer.Read(new MemoryStream(plain), "test.gen", 3, edits, []);
        var ex = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(new MemoryStream(withReset), "test.gen", 3, [], [])
        );
        var current = new List<VoxelEdit>();
        withReset[4] = 4;
        EditLogSerializer.Read(new MemoryStream(withReset), "test.gen", 3, current, []);

        Assert.Equal([log[1]], edits);
        Assert.Contains("cannot hold resets", ex.Message, StringComparison.Ordinal);
        Assert.Equal(VoxelEditOperation.Reset, current[1].Operation);
    }

    [Fact]
    public void A_log_of_a_newer_format_is_refused_by_its_version()
    {
        var bytes = Written(new EditLog(1, VoxelGrid.Unit)).ToArray();
        bytes[4] = 7;

        var ex = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.ReadHeader(new MemoryStream(bytes), "test.gen", 3)
        );

        Assert.Contains("format version 7; this build reads 1 to 6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_capsule_reads_back_from_format_5_and_is_refused_in_a_format_4_log()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(
            VoxelEdit.Capsule(new VoxelCoord(3, 4, 5), new VoxelCoord(3, -20, 30), 2, VoxelEditOperation.Clear) with
            {
                Sequence = 1,
            }
        );
        var bytes = Written(log).ToArray();
        var edits = new List<VoxelEdit>();

        EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, edits, []);
        // The format version follows the 4-byte magic; formats 4 and 5 have the same layout.
        bytes[4] = 4;
        var ex = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, [], [])
        );

        Assert.Equal([log[1]], edits);
        Assert.Contains("cannot hold capsules", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clear_of_some_materials_reads_back_from_format_6_and_is_refused_in_a_format_5_log()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(
            VoxelEdit.MaterialsBox(new VoxelCoord(-7, 4, 5), new VoxelCoord(10, 20, 30), [3, 63, 64, 100]) with
            {
                Sequence = 1,
            }
        );
        var bytes = Written(log).ToArray();
        var edits = new List<VoxelEdit>();

        EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, edits, []);
        // The format version follows the 4-byte magic; formats 5 and 6 have the same layout.
        bytes[4] = 5;
        var ex = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, [], [])
        );

        Assert.Equal([log[1]], edits);
        Assert.True(edits[0].ClearsMaterial(64) && edits[0].ClearsMaterial(100) && !edits[0].ClearsMaterial(4));
        Assert.Contains("cannot hold clears of some materials", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Match_data_over_the_limit_is_refused_when_written_and_when_read()
    {
        using var memory = new VoxelMemory(1 << 20);
        var tooMuch = new byte[EditLogSerializer.MaxGameDataBytes + 1];
        Assert.Throws<ArgumentException>(() =>
            EditLogSerializer.Write(
                new MemoryStream(),
                "test.gen",
                3,
                42,
                60,
                tooMuch,
                new EditLog(1, VoxelGrid.Unit),
                memory,
                new byte[VoxelConstants.ChunkVoxels]
            )
        );

        var bytes = Written(new EditLog(1, VoxelGrid.Unit)).ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(33), EditLogSerializer.MaxGameDataBytes + 1);
        using var stream = new MemoryStream(bytes);
        var ex = Assert.Throws<InvalidDataException>(() => EditLogSerializer.ReadHeader(stream, "test.gen", 3));
        Assert.Contains("match settings", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other.gen", 3)]
    [InlineData("test.gen", 4)]
    public void Logs_from_another_generator_are_refused(string id, int version)
    {
        using var stream = Written(new EditLog(1, VoxelGrid.Unit));

        var ex = Assert.Throws<InvalidDataException>(() => EditLogSerializer.Read(stream, id, version, [], []));

        Assert.Contains("test.gen version 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Streams_that_are_not_edit_logs_are_refused()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("NOPE and more bytes"));

        Assert.Throws<InvalidDataException>(() => EditLogSerializer.Read(stream, "test.gen", 3, [], []));
    }

    [Fact]
    public void Truncated_logs_are_refused()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(0, 0, 0)));
        var bytes = Written(log).ToArray();
        using var stream = new MemoryStream(bytes, 0, bytes.Length - 3);

        Assert.Throws<InvalidDataException>(() => EditLogSerializer.Read(stream, "test.gen", 3, [], []));
    }

    [Fact]
    public void Edits_out_of_order_are_refused()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(0, 0, 0)));
        var bytes = Written(log).ToArray();
        // The last 4 bytes count the pieces; the one edit before them takes 51 bytes and starts with its sequence.
        var sequenceOffset = bytes.Length - 4 - 51;
        bytes[sequenceOffset] = 7;

        using var stream = new MemoryStream(bytes);
        var ex = Assert.Throws<InvalidDataException>(() => EditLogSerializer.Read(stream, "test.gen", 3, [], []));
        Assert.Contains("sequence 7", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A written log patched into what the reader must refuse: an unknown shape, a sphere's radius past
    /// the largest, a piece edit's radius past any piece's, and a piece count with no lift for it. The one edit takes
    /// 51 bytes before the 4-byte piece count: its sequence (8), shape, operation, material, origin (12), extent (12),
    /// piece (8), and turn (8).
    /// </summary>
    [Theory]
    [InlineData("shape")]
    [InlineData("radius")]
    [InlineData("pieces")]
    public void A_log_with_a_damaged_edit_or_piece_count_is_refused(string damage)
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(0, 0, 0)));
        var bytes = Written(log).ToArray();
        var edit = bytes.Length - 4 - 51;
        switch (damage)
        {
            case "shape":
                bytes[edit + 8] = 9;
                break;
            case "radius":
                BitConverter.TryWriteBytes(bytes.AsSpan(edit + 23), VoxelConstants.MaxEditExtent + 1);
                break;
            default:
                BitConverter.TryWriteBytes(bytes.AsSpan(bytes.Length - 4), 1);
                break;
        }

        using var stream = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => EditLogSerializer.Read(stream, "test.gen", 3, [], []));
    }

    private static VoxelEdit Dig(long sequence, VoxelCoord centre) =>
        VoxelEdit.Sphere(centre, 5, VoxelEditOperation.Clear) with
        {
            Sequence = sequence,
        };

    private static MemoryStream Written(EditLog log)
    {
        var stream = new MemoryStream();
        using var memory = new VoxelMemory(1 << 20);
        EditLogSerializer.Write(
            stream,
            "test.gen",
            3,
            42,
            60,
            [1, 2],
            log,
            memory,
            new byte[VoxelConstants.ChunkVoxels]
        );
        stream.Position = 0;
        return stream;
    }
}
