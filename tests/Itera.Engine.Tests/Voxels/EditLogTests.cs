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
    public void Each_edit_takes_the_same_bytes_in_a_stream()
    {
        using var memory = new VoxelMemory(1 << 20);
        var log = new EditLog(1, VoxelGrid.Unit);
        var lengths = new long[2];
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < 3; i++)
            {
                log.Append(Dig(log.Count + 1, new VoxelCoord(i, 0, 0)));
            }

            using var stream = new MemoryStream();
            EditLogSerializer.Write(
                stream,
                "test.gen",
                3,
                7UL,
                30,
                [],
                log,
                memory,
                new byte[VoxelConstants.ChunkVoxels]
            );
            lengths[pass] = stream.Length;
        }

        Assert.Equal(3 * EditLogSerializer.EditBytes, lengths[1] - lengths[0]);
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
        bytes[4] = 11;

        var ex = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.ReadHeader(new MemoryStream(bytes), "test.gen", 3)
        );

        Assert.Contains("format version 11; this build reads 1 to 10", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sphere clearing some materials reads back from a format 10 log with its set; a format 9 log, the same layout,
    /// holds none (a box clearing some materials it still holds).
    /// </summary>
    [Fact]
    public void A_sphere_clearing_some_materials_reads_back_and_a_format_9_log_has_none()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(VoxelEdit.MaterialsSphere(new VoxelCoord(40, -20, 70), 150, [3, 9, 100]) with { Sequence = 1 });
        var bytes = Written(log).ToArray();
        var boxes = new EditLog(1, VoxelGrid.Unit);
        boxes.Append(
            VoxelEdit.MaterialsBox(new VoxelCoord(40, -20, 70), new VoxelCoord(10, 20, 30), [3]) with
            {
                Sequence = 1,
            }
        );
        var boxBytes = Written(boxes).ToArray();
        var edits = new List<VoxelEdit>();

        EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, edits, []);
        bytes[4] = 9;
        boxBytes[4] = 9;
        var ex = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, [], [])
        );
        var fromFormat9 = new List<VoxelEdit>();
        EditLogSerializer.Read(new MemoryStream(boxBytes), "test.gen", 3, fromFormat9, []);

        Assert.Equal([log[1]], edits);
        Assert.Equal(
            (VoxelEditShape.Sphere, VoxelEditOperation.ClearMaterials, 150),
            (edits[0].Shape, edits[0].Operation, edits[0].Radius)
        );
        Assert.True(edits[0].ClearsMaterial(3) && edits[0].ClearsMaterial(9) && edits[0].ClearsMaterial(100));
        Assert.False(edits[0].ClearsMaterial(4));
        Assert.Contains("cannot hold spheres clearing some materials", ex.Message, StringComparison.Ordinal);
        Assert.Equal([boxes[1]], fromFormat9);
    }

    /// <summary>
    /// An object placed and one removed during play read back from a format 9 log; a format 8 log, the same layout, holds
    /// none.
    /// </summary>
    [Fact]
    public void Objects_placed_and_removed_read_back_and_a_format_8_log_has_none()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        var placement = new Itera.Engine.Objects.ObjectPlacement(
            new Itera.Engine.Objects.ObjectId(0xFEEDF00D),
            new Itera.Engine.Objects.VoxelObjectKindId(3),
            new VoxelCoord(1200, -50, -700),
            1024
        );
        log.Append(VoxelEdit.PlaceObject(in placement, 75, 0, 100) with { Sequence = 1 });
        log.Append(VoxelEdit.RemoveObject(in placement, 75, 0, 100) with { Sequence = 2 });
        var bytes = Written(log).ToArray();
        var edits = new List<VoxelEdit>();

        EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, edits, []);
        bytes[4] = 8;
        var ex = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, [], [])
        );

        Assert.Equal([log[1], log[2]], edits);
        Assert.Equal(placement with { Since = 1 }, edits[0].PlacementOf());
        Assert.Equal(placement, edits[1].PlacementOf());
        Assert.Contains("cannot hold objects placed or removed whole", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A game's sections follow the pieces (format 8): read back by name, version, and bytes, from a world's log and a
    /// world-less one, and left out when not asked for; a format 7 log, the same layout before them, holds none.
    /// </summary>
    [Fact]
    public void Sections_read_back_after_the_pieces_and_a_format_7_log_has_none()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(Dig(1, new VoxelCoord(0, 0, 0)));
        SavedSection[] sections = [new("game.players", 3, [1, 2, 3, 4]), new("game.empty", 1, [])];
        var bytes = WrittenWith(log, sections);

        var edits = new List<VoxelEdit>();
        var read = new List<SavedSection>();
        EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, edits, [], read);
        var withoutWorld = new MemoryStream();
        EditLogSerializer.WriteEmpty(withoutWorld, "mesh.game", 2, 77UL, 30, [], sections);
        var readWithoutWorld = new List<SavedSection>();
        EditLogSerializer.Read(new MemoryStream(withoutWorld.ToArray()), "mesh.game", 2, [], [], readWithoutWorld);
        EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, [], []);

        Assert.Single(edits);
        foreach (var got in new[] { read, readWithoutWorld })
        {
            Assert.Equal(2, got.Count);
            Assert.Equal(("game.players", 3), (got[0].Name, got[0].Version));
            Assert.Equal([1, 2, 3, 4], got[0].Bytes);
            Assert.Equal(("game.empty", 1, 0), (got[1].Name, got[1].Version, got[1].Bytes.Length));
        }

        // A format 7 log: the version byte follows the 4-byte magic, and the sections' count is past its end.
        var format7 = Written(log).ToArray()[..^4];
        format7[4] = 7;
        var fromFormat7 = new List<SavedSection>();
        EditLogSerializer.Read(new MemoryStream(format7), "test.gen", 3, [], [], fromFormat7);
        Assert.Empty(fromFormat7);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("negative count")]
    [InlineData("name")]
    [InlineData("long name")]
    [InlineData("version")]
    [InlineData("twice")]
    [InlineData("length")]
    [InlineData("negative length")]
    [InlineData("short")]
    public void Damaged_sections_are_refused(string damage)
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        var good = WrittenWith(log, [new SavedSection("game.a", 1, [9, 9])]);
        // The sections start after the header, no edits, and no pieces: a count, then the name, version, and length.
        var start = good.Length - (4 + 7 + 4 + 4 + 2);
        var bytes = (byte[])good.Clone();
        switch (damage)
        {
            case "count":
                BitConverter.TryWriteBytes(bytes.AsSpan(start), EditLogSerializer.MaxSections + 1);
                break;
            case "negative count":
                BitConverter.TryWriteBytes(bytes.AsSpan(start), -1);
                break;
            case "name":
                bytes[start + 4 + 5] = (byte)' ';
                break;
            case "long name":
                bytes = WrittenRaw(log, [(new string('a', 65), 1, [])]);
                break;
            case "version":
                BitConverter.TryWriteBytes(bytes.AsSpan(start + 4 + 7), 0);
                break;
            case "twice":
                bytes = WrittenRaw(log, [("game.a", 1, []), ("game.a", 2, [])]);
                break;
            case "length":
                BitConverter.TryWriteBytes(bytes.AsSpan(start + 4 + 7 + 4), 3);
                break;
            case "negative length":
                BitConverter.TryWriteBytes(bytes.AsSpan(start + 4 + 7 + 4), -2);
                break;
            default:
                bytes = good[..^1];
                break;
        }

        Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, [], [], [])
        );
        // Left out, they are still checked.
        Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, [], [])
        );
    }

    [Fact]
    public void Sections_a_writer_may_not_write_are_refused()
    {
        var log = new EditLog(1, VoxelGrid.Unit);

        Assert.Throws<ArgumentException>(() => WrittenWith(log, [new("game a", 1, [])]));
        Assert.Throws<ArgumentException>(() => WrittenWith(log, [new("", 1, [])]));
        Assert.Throws<ArgumentException>(() => WrittenWith(log, [new("game.a", 0, [])]));
        Assert.Throws<ArgumentException>(() => WrittenWith(log, [new("game.a", 1, []), new("game.a", 1, [])]));
        Assert.Throws<ArgumentException>(() =>
            WrittenWith(
                log,
                [
                    .. Enumerable
                        .Range(0, EditLogSerializer.MaxSections + 1)
                        .Select(i => new SavedSection($"s{i}", 1, [])),
                ]
            )
        );
    }

    /// <summary>
    /// A log frozen (<see cref="EditLog.Freeze"/>) holds the edits and pieces it had: edits appended after it are not in
    /// what it writes.
    /// </summary>
    [Fact]
    public void A_frozen_log_writes_the_edits_it_had_and_none_appended_after()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        for (var i = 1; i <= 1500; i++)
        {
            log.Append(Dig(i, new VoxelCoord(i, 0, 0)));
        }

        var frozen = log.Freeze();
        for (var i = 1501; i <= 2100; i++)
        {
            log.Append(Dig(i, new VoxelCoord(i, 0, 0)));
        }

        using var memory = new VoxelMemory(1 << 20);
        using var stream = new MemoryStream();
        EditLogSerializer.Write(
            stream,
            "test.gen",
            3,
            42,
            60,
            [],
            frozen,
            memory,
            new byte[VoxelConstants.ChunkVoxels]
        );
        stream.Position = 0;
        var edits = new List<VoxelEdit>();
        var header = EditLogSerializer.Read(stream, "test.gen", 3, edits, []);

        Assert.Equal(1500, header.Count);
        Assert.Equal(1500, frozen.Count);
        Assert.Equal(log[1500], edits[^1]);
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
    public void A_part_removal_reads_back_from_format_7_and_is_refused_in_a_format_6_log()
    {
        var log = new EditLog(1, VoxelGrid.Unit);
        log.Append(
            VoxelEdit.RemovePart(
                ulong.MaxValue - 5,
                new VoxelCoord(1023, 7, 512),
                new VoxelCoord(-7, 4, 5),
                new VoxelCoord(10, 20, 30)
            ) with
            {
                Sequence = 1,
            }
        );
        var bytes = Written(log).ToArray();
        var edits = new List<VoxelEdit>();

        EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, edits, []);
        // The format version follows the 4-byte magic; formats 6 and 7 have the same layout.
        bytes[4] = 6;
        var ex = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(new MemoryStream(bytes), "test.gen", 3, [], [])
        );

        Assert.Equal([log[1]], edits);
        Assert.Equal((ulong.MaxValue - 5, new VoxelCoord(1023, 7, 512)), ((ulong)edits[0].Piece, edits[0].PartSeed));
        Assert.Contains("cannot hold removals of objects' parts", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A world that now places objects (its id the generator's with the kinds' fingerprint) reads a format 6 log, whose
    /// id was the generator's alone; a format 7 log of the plain id, or of other kinds, is another world's.
    /// </summary>
    [Fact]
    public void An_older_log_of_a_world_now_placing_objects_reads()
    {
        var bytes = Written(new EditLog(1, VoxelGrid.Unit)).ToArray();
        const string objectsId = "test.gen+objects-0123456789abcdef";

        Assert.Throws<InvalidDataException>(() => EditLogSerializer.ReadHeader(new MemoryStream(bytes), objectsId, 3));
        bytes[4] = 6;
        Assert.Equal("test.gen", EditLogSerializer.ReadHeader(new MemoryStream(bytes), objectsId, 3).GeneratorId);
        Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.ReadHeader(new MemoryStream(bytes), "other.gen+objects-0123456789abcdef", 3)
        );
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
        // The last 8 bytes count the pieces and the sections; the one edit before them takes 51 bytes and starts with its
        // sequence.
        var sequenceOffset = bytes.Length - 8 - 51;
        bytes[sequenceOffset] = 7;

        using var stream = new MemoryStream(bytes);
        var ex = Assert.Throws<InvalidDataException>(() => EditLogSerializer.Read(stream, "test.gen", 3, [], []));
        Assert.Contains("sequence 7", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A written log patched into what the reader must refuse: an unknown shape, a sphere's radius past
    /// the largest, a piece edit's radius past any piece's, and a piece count with no lift for it. The one edit takes
    /// 51 bytes before the 4-byte piece count: its sequence (8), shape, operation, material, origin (12), extent (12),
    /// piece (8), and turn (8); the 4-byte section count follows the piece count.
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
        var edit = bytes.Length - 8 - 51;
        switch (damage)
        {
            case "shape":
                bytes[edit + 8] = 9;
                break;
            case "radius":
                BitConverter.TryWriteBytes(bytes.AsSpan(edit + 23), VoxelConstants.MaxEditExtent + 1);
                break;
            default:
                BitConverter.TryWriteBytes(bytes.AsSpan(bytes.Length - 8), 1);
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

    private static byte[] WrittenWith(EditLog log, SavedSection[] sections)
    {
        using var stream = new MemoryStream();
        using var memory = new VoxelMemory(1 << 20);
        EditLogSerializer.Write(
            stream,
            "test.gen",
            3,
            42,
            60,
            [],
            log.Freeze(),
            memory,
            new byte[VoxelConstants.ChunkVoxels],
            sections: sections
        );
        return stream.ToArray();
    }

    /// <summary>A log of no edits, then sections written as they are, with no checks: what a damaged save may hold.</summary>
    private static byte[] WrittenRaw(EditLog log, (string Name, int Version, byte[] Bytes)[] sections)
    {
        var bytes = WrittenWith(log, [])[..^4];
        using var stream = new MemoryStream();
        stream.Write(bytes);
        using var writer = new BinaryWriter(stream);
        writer.Write(sections.Length);
        foreach (var (name, version, data) in sections)
        {
            writer.Write(name);
            writer.Write(version);
            writer.Write(data.Length);
            writer.Write(data);
        }

        writer.Flush();
        return stream.ToArray();
    }

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

    [Fact]
    public void A_sized_worlds_log_indexes_edits_inside_it_and_not_past_its_edge()
    {
        // Two 12.8 m chunks each side of the middle: the edge at 2,560 cm.
        var grid = VoxelGrid.Sized(10, 25.6, -12.8, 12.8);
        var log = new EditLog(1, grid);
        log.Append(Dig(1, new VoxelCoord(2_400, 0, 0)));
        log.Append(Dig(2, new VoxelCoord(2_800, 0, 0)));
        var found = new List<VoxelEdit>();

        log.CollectForColumn(new ColumnKey(0, 1, 0), 0, found);
        log.CollectForColumn(new ColumnKey(0, 2, 0), 0, found);

        Assert.Equal([1L], found.Select(e => e.Sequence));
        Assert.Equal(2, log.Count);
    }
}
