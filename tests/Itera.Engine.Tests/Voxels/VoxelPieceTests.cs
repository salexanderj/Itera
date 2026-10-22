using System.Numerics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// Lifting a piece out of the world and putting it back (<see cref="VoxelEditOperation.Lift"/>,
/// <see cref="VoxelEditShape.Piece"/>): the voxels at every level, turns, filling only air, replay when columns load
/// again, and the save format.
/// </summary>
[RunsLong]
public sealed class VoxelPieceTests : IDisposable
{
    private const int Floor = 200;
    private const int Levels = 3;

    // A block of many materials standing on the floor, so a piece holds mixed voxels.
    private static readonly BoxesGenerator s_generator = new(
        Floor,
        new VoxelBox(-40, 200, -40, 40, 260, 40, BoxesGenerator.Pattern),
        new VoxelBox(-20, 261, -20, 20, 280, 20, BoxesGenerator.Stone)
    );

    private readonly VoxelWorld _world;

    public VoxelPieceTests()
    {
        _world = Load();
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void A_piece_put_back_unturned_where_it_was_gives_the_same_voxels_at_every_level()
    {
        var before = Snapshot(_world, -60, 170, -60, 60, 300, 60);

        var sequence = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(5, 230, -3), 30, VoxelEditOperation.Lift));
        _world.TryGetMaterial(new VoxelCoord(5, 230, -3), out var lifted);
        Assert.True(_world.TryGetPiece(sequence, out var piece));
        _world.Apply(PutBack(piece, piece.Min + piece.Pivot, VoxelEdit.NoTurn));

        Assert.Equal(VoxelConstants.Air, lifted);
        Assert.Equal(new VoxelCoord(61, 61, 61), piece.Size);
        Assert.Equal(before, Snapshot(_world, -60, 170, -60, 60, 300, 60));
    }

    [Fact]
    public void The_largest_piece_radius_is_a_full_chunk_lifts()
    {
        // Every box size a lift allows: the farthest corner from the pivot is a corner of the box.
        var largest = 0;
        for (var size = 1; size <= VoxelConstants.ChunkSize; size++)
        {
            var pivot = new VoxelCoord(size >> 1, size >> 1, size >> 1);
            var squared = Math.Max(
                VoxelPiece.FarthestCornerSquared(pivot, 0, 0, 0),
                VoxelPiece.FarthestCornerSquared(pivot, size - 1, size - 1, size - 1)
            );
            largest = Math.Max(largest, VoxelPiece.RadiusOf(squared));
        }

        Assert.Equal(VoxelPiece.MaxRadius, largest);
    }

    [Fact]
    public void A_lift_with_a_bad_maker_is_refused_before_its_piece_is_kept()
    {
        var edits = _world.EditCount;
        var edit = VoxelEdit.Sphere(new VoxelCoord(5, 230, -3), 10, VoxelEditOperation.Lift);

        // A throw after the piece was kept left a piece with no lift, and every later save unreadable.
        Assert.Throws<ArgumentOutOfRangeException>(() => _world.TryLift(in edit, out _, maker: 300));
        Assert.Equal(edits, _world.EditCount);
        Assert.False(_world.TryGetPiece(edits + 1, out _));

        var sequence = Lift(_world, edit);
        Assert.Equal(edits + 1, sequence);
        Assert.True(_world.TryGetPiece(sequence, out _));
    }

    [Fact]
    public void A_quarter_turn_about_y_turns_the_voxels()
    {
        // An uneven box (13 by 9 by 6) of the mixed block and the stone on top, put back 1.5 m away in the air.
        var sequence = Lift(
            _world,
            VoxelEdit.Box(new VoxelCoord(-10, 255, -2), new VoxelCoord(13, 9, 6), VoxelEditOperation.Lift)
        );
        Assert.True(_world.TryGetPiece(sequence, out var piece));
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var pivot = new VoxelCoord(150, 320, 0);

        _world.Apply(PutBack(piece, pivot, VoxelEdit.PackTurn(turn)));

        var memory = _world.ReadMemory;
        var placed = 0;
        for (var z = -20; z <= 20; z++)
        {
            for (var y = -20; y <= 20; y++)
            {
                for (var x = -20; x <= 20; x++)
                {
                    // A world voxel's centre, turned back into the piece.
                    var local = Vector3.Transform(
                        new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                        Quaternion.Conjugate(turn)
                    );
                    var expected = piece.Read(
                        memory,
                        (int)MathF.Floor(local.X + 0.001f) + piece.Pivot.X,
                        (int)MathF.Floor(local.Y + 0.001f) + piece.Pivot.Y,
                        (int)MathF.Floor(local.Z + 0.001f) + piece.Pivot.Z
                    );
                    _world.TryGetMaterial(new VoxelCoord(pivot.X + x, pivot.Y + y, pivot.Z + z), out var actual);
                    Assert.True(expected == actual, $"Voxel ({x}, {y}, {z}) from the pivot: {actual}, not {expected}.");
                    placed += actual != VoxelConstants.Air ? 1 : 0;
                }
            }
        }

        // The mixed block holds some air, so the piece has fewer solid voxels than its box.
        Assert.Equal(piece.SolidVoxels, placed);
        Assert.InRange(placed, 13 * 9 * 6 * 9 / 10, 13 * 9 * 6);
    }

    [Fact]
    public void A_piece_fills_only_air()
    {
        var sequence = Lift(
            _world,
            VoxelEdit.Box(new VoxelCoord(-20, 261, -20), new VoxelCoord(41, 20, 41), VoxelEditOperation.Lift)
        );
        Assert.True(_world.TryGetPiece(sequence, out var piece));

        // Half of it sunk into the floor away from the block: the floor's dirt stays, the stone fills the air above.
        _world.Apply(PutBack(piece, new VoxelCoord(200, Floor, 200), VoxelEdit.NoTurn));

        _world.TryGetMaterial(new VoxelCoord(200, Floor - 5, 200), out var below);
        _world.TryGetMaterial(new VoxelCoord(200, Floor + 5, 200), out var above);
        Assert.Equal(BoxesGenerator.Dirt, below);
        Assert.Equal(BoxesGenerator.Stone, above);
    }

    [Fact]
    public void Columns_that_load_again_replay_lifts_and_pieces_to_the_same_voxels()
    {
        var sequence = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 25, VoxelEditOperation.Lift));
        Assert.True(_world.TryGetPiece(sequence, out var piece));
        var turn = VoxelEdit.PackTurn(Quaternion.Normalize(new Quaternion(0.3f, 0.5f, 0.1f, 0.8f)));
        _world.Apply(PutBack(piece, new VoxelCoord(120, 260, 90), turn));
        var before = Snapshot(_world, -40, 200, -40, 160, 300, 130);

        _world.SetCenters([new Vector3(60, 2, 60)]);
        TestWorlds.WaitUntilLoaded(_world);
        for (var i = 0; i < 20; i++)
        {
            _world.Update(0);
        }

        Assert.False(_world.TryGetMaterial(new VoxelCoord(0, 240, 0), out _));
        _world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(_world);

        Assert.Equal(before, Snapshot(_world, -40, 200, -40, 160, 300, 130));
    }

    [Fact]
    public void A_turned_piece_gives_pinned_voxels()
    {
        var sequence = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(-10, 240, 12), 22, VoxelEditOperation.Lift));
        Assert.True(_world.TryGetPiece(sequence, out var piece));
        // The turn as its packed integers, so the voxels' pin does not lean on float maths: it is
        // Quaternion.Normalize(new Quaternion(-0.21f, 0.64f, 0.33f, 0.66f)) packed.
        const long turn = 0x2A47_1524_28FF_F28CL;

        _world.Apply(PutBack(piece, new VoxelCoord(130, 250, -70), turn));

        var hash = 14695981039346656037UL;
        var solid = 0;
        foreach (var material in Snapshot(_world, 100, 220, -100, 160, 280, -40))
        {
            hash = (hash ^ material) * 1099511628211UL;
            solid += material != VoxelConstants.Air ? 1 : 0;
        }

        // Pinned: the same edits must give the same voxels on every machine and in every later build.
        Assert.True(solid > piece.SolidVoxels);
        Assert.Equal(13190679175521472451UL, hash);
    }

    [Fact]
    public void Putting_a_piece_back_allocates_nothing()
    {
        var sequence = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 25, VoxelEditOperation.Lift));
        Assert.True(_world.TryGetPiece(sequence, out var piece));
        _world.Apply(PutBack(piece, new VoxelCoord(120, 260, 90), VoxelEdit.NoTurn));
        // A new pivot each time, in the air beside the last, so every put-back measured changes voxels (the same
        // put-back again changed nothing).
        var edits = new VoxelEdit[3];
        for (var i = 0; i < edits.Length; i++)
        {
            edits[i] = PutBack(piece, new VoxelCoord(120 + 60 * (i + 1), 260, 90), VoxelEdit.NoTurn);
        }

        var solidBefore = SolidAround(new VoxelCoord(180, 260, 90));
        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < edits.Length; i++)
        {
            _world.Apply(in edits[i]);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(SolidAround(new VoxelCoord(180, 260, 90)) > solidBefore, "The measured put-back changed nothing.");
        Assert.Equal(0, allocated);
    }

    /// <summary>The solid level-0 voxels in a 50-voxel cube round <paramref name="centre"/>, every second one.</summary>
    private int SolidAround(VoxelCoord centre)
    {
        var solid = 0;
        for (var z = -25; z <= 25; z += 2)
        {
            for (var y = -25; y <= 25; y += 2)
            {
                for (var x = -25; x <= 25; x += 2)
                {
                    if (
                        _world.TryGetMaterial(centre + new VoxelCoord(x, y, z), out var material)
                        && material != VoxelConstants.Air
                    )
                    {
                        solid++;
                    }
                }
            }
        }

        return solid;
    }

    [Fact]
    public void A_saved_log_with_pieces_rebuilds_the_same_world()
    {
        var sequence = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 25, VoxelEditOperation.Lift));
        Assert.True(_world.TryGetPiece(sequence, out var piece));
        _world.Apply(
            PutBack(piece, new VoxelCoord(120, 260, 90), VoxelEdit.PackTurn(new Quaternion(0.3f, 0.5f, 0.1f, 0.8f)))
        );
        using var stream = new MemoryStream();
        _world.SaveEdits(stream, 60, []);
        stream.Position = 0;

        using var copy = Load(edits: stream);

        Assert.Equal(2, copy.EditCount);
        Assert.Equal(Snapshot(_world, -40, 200, -40, 160, 300, 130), Snapshot(copy, -40, 200, -40, 160, 300, 130));
    }

    [Fact]
    public void A_saved_log_with_pieces_keeps_its_pinned_bytes()
    {
        var sequence = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(3, 238, -4), 27, VoxelEditOperation.Lift));
        Assert.True(_world.TryGetPiece(sequence, out var piece));
        _world.Apply(
            PutBack(piece, new VoxelCoord(110, 262, 80), VoxelEdit.PackTurn(new Quaternion(0.3f, 0.5f, 0.1f, 0.8f)))
        );
        Lift(_world, VoxelEdit.Box(new VoxelCoord(-18, 262, -9), new VoxelCoord(21, 13, 17), VoxelEditOperation.Lift));
        using var stream = new MemoryStream();
        _world.SaveEdits(stream, 60, []);

        var written = stream.ToArray();
        // The format version follows the 4-byte magic; the last 4 bytes count the sections, none in a world's log alone.
        Assert.Equal(10, written[4]);
        Assert.Equal([0, 0, 0, 0], written[^4..]);
        var bytes = written[..^4];
        bytes[4] = 4;
        var asFormat4 = Fnv(bytes);
        bytes[4] = 3;
        var asFormat3 = Fnv(bytes);

        // Pinned: the bytes are the save file and the join payload; a change here needs a new format version. Formats 4
        // to 7 changed only the version from format 3 (resets, then capsules, then clears of some materials, then removals
        // of objects' parts may follow; this world places no objects, so its id is its generator's), format 8 added
        // the sections' count at the end, and formats 9 and 10 only the version again (objects placed and removed, then
        // spheres clearing some materials), so with 4 or 3 put back and the count left off the old pins hold.
        Assert.Equal(7552161417393775388UL, asFormat4);
        Assert.Equal(14899856787725782161UL, asFormat3);

        static ulong Fnv(byte[] bytes)
        {
            var hash = 14695981039346656037UL;
            foreach (var value in bytes)
            {
                hash = (hash ^ value) * 1099511628211UL;
            }

            return hash;
        }
    }

    [Fact]
    public void Piece_runs_fill_their_box_exactly()
    {
        var size = new VoxelCoord(4, 3, 2);
        Assert.Null(PieceRuns.Check(size, [(0, 10), (7, 14)]));
        Assert.NotNull(PieceRuns.Check(size, [(0, 10), (7, 13)]));
        Assert.NotNull(PieceRuns.Check(size, [(0, 10), (7, 15)]));
        Assert.NotNull(PieceRuns.Check(size, [(0, 0), (7, 24)]));
        Assert.NotNull(PieceRuns.Check(new VoxelCoord(0, 3, 2), []));
        Assert.NotNull(PieceRuns.Check(new VoxelCoord(129, 1, 1), [(0, 129)]));
    }

    [Fact]
    public void A_host_lift_with_its_runs_gives_the_host_voxels()
    {
        var sequence = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 25, VoxelEditOperation.Lift));
        var runs = new List<(byte Material, int Length)>();
        Assert.True(_world.CollectPieceRuns(sequence, runs));
        Assert.False(_world.CollectPieceRuns(sequence + 1, runs));
        Assert.True(_world.CollectPieceRuns(sequence, runs));
        Assert.True(_world.TryGetPiece(sequence, out var piece));
        var putBack = PutBack(piece, new VoxelCoord(120, 260, 90), VoxelEdit.NoTurn);
        _world.Apply(in putBack);
        using var copy = Load();

        copy.ApplyHostLift(_world.GetEdit(1), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(runs));
        copy.ApplySequenced(putBack with { Sequence = 2 });

        Assert.True(copy.TryGetPiece(sequence, out var copied));
        Assert.Equal(piece.SolidVoxels, copied.SolidVoxels);
        Assert.Equal(Snapshot(_world, -40, 200, -40, 160, 300, 130), Snapshot(copy, -40, 200, -40, 160, 300, 130));
    }

    [Fact]
    public void A_host_lift_needs_runs_that_fill_its_box_and_the_next_sequence()
    {
        var lift = VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 5, VoxelEditOperation.Lift) with { Sequence = 1 };
        var box = 11 * 11 * 11;

        Assert.Throws<ArgumentException>(() => _world.ApplyHostLift(lift, [(0, box - 1)]));
        Assert.Throws<ArgumentException>(() => _world.ApplyHostLift(lift with { Sequence = 2 }, [(0, box)]));
        Assert.Throws<ArgumentException>(() =>
            _world.ApplyHostLift(lift with { Operation = VoxelEditOperation.Clear }, [(0, box)])
        );
        Assert.Equal(0, _world.EditCount);

        _world.ApplyHostLift(lift, [(0, box)]);
        Assert.Equal(1, _world.EditCount);
    }

    [Fact]
    public void Runs_read_ahead_of_a_lift_are_the_lifted_pieces_and_lift_it_elsewhere()
    {
        var lift = VoxelEdit.Sphere(new VoxelCoord(4, 238, -3), 12, VoxelEditOperation.Lift);
        var read = new List<(byte Material, int Length)>();
        Assert.True(_world.TryCollectRuns(in lift, read));
        Assert.Equal(0, _world.EditCount);
        using var copy = Load();

        var sequence = Lift(_world, lift);
        var kept = new List<(byte Material, int Length)>();
        Assert.True(_world.CollectPieceRuns(sequence, kept));
        var lifted = copy.LiftWithRuns(in lift, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(read));

        Assert.Equal(kept, read);
        Assert.Equal(1, lifted);
        Assert.Equal(Snapshot(_world, -40, 200, -40, 40, 280, 40), Snapshot(copy, -40, 200, -40, 40, 280, 40));
        Assert.False(
            _world.TryCollectRuns(VoxelEdit.Sphere(new VoxelCoord(5000, 238, 0), 12, VoxelEditOperation.Lift), read)
        );
        copy.FollowHost();
        Assert.Throws<InvalidOperationException>(() =>
            copy.LiftWithRuns(in lift, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(read))
        );
    }

    /// <summary>
    /// A lift after a put-back, whose piece filled the shared scratch, holds only its own voxels: the scratch is cleared
    /// over the new piece's box (only that box is cleared now, not the whole 2 MB).
    /// </summary>
    [Fact]
    public void A_lift_after_a_put_back_holds_only_its_own_voxels()
    {
        var big = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 30, VoxelEditOperation.Lift));
        Assert.True(_world.TryGetPiece(big, out var piece));
        _world.Apply(PutBack(piece, new VoxelCoord(80, 262, 60), VoxelEdit.PackTurn(Quaternion.Identity)));
        var lift = VoxelEdit.Sphere(new VoxelCoord(-60, 238, -40), 12, VoxelEditOperation.Lift);
        var read = new List<(byte Material, int Length)>();
        Assert.True(_world.TryCollectRuns(in lift, read));

        var sequence = Lift(_world, lift);

        var kept = new List<(byte Material, int Length)>();
        Assert.True(_world.CollectPieceRuns(sequence, kept));
        Assert.Equal(read, kept);
    }

    [Fact]
    public void A_world_that_follows_a_host_refuses_its_own_edits()
    {
        _world.FollowHost();

        Assert.True(_world.FollowsHost);
        Assert.Throws<InvalidOperationException>(() =>
            _world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 5, VoxelEditOperation.Clear))
        );
        Assert.Throws<InvalidOperationException>(() =>
            _world.TryLift(VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 5, VoxelEditOperation.Lift), out _)
        );
        _world.ApplySequenced(
            VoxelEdit.Sphere(new VoxelCoord(0, 240, 0), 5, VoxelEditOperation.Clear) with
            {
                Sequence = 1,
            }
        );
        Assert.Equal(1, _world.EditCount);
    }

    [Fact]
    public void A_format_1_log_still_loads()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteHeader(writer, format: 1, edits: 1);
            WriteEdit(
                writer,
                VoxelEdit.Sphere(new VoxelCoord(0, 230, 0), 20, VoxelEditOperation.Clear) with
                {
                    Sequence = 1,
                },
                format: 1
            );
        }

        stream.Position = 0;
        using var world = Load(edits: stream);

        world.TryGetMaterial(new VoxelCoord(0, 230, 0), out var material);
        Assert.Equal(1, world.EditCount);
        Assert.Equal(VoxelConstants.Air, material);
    }

    [Fact]
    public void Lifts_need_their_pieces_and_piece_edits_an_earlier_lift()
    {
        // A lift through Apply would keep no piece; a piece edit must name a lift the log holds.
        Assert.Throws<ArgumentException>(() =>
            _world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 230, 0), 10, VoxelEditOperation.Lift))
        );
        var dig = _world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, 230, 0), 10, VoxelEditOperation.Clear));
        Assert.Throws<ArgumentException>(() =>
            _world.Apply(VoxelEdit.PieceAt(dig, 10, new VoxelCoord(0, 300, 0), VoxelEdit.NoTurn))
        );
        // Too big for one piece.
        Assert.Throws<ArgumentException>(() =>
            Lift(_world, VoxelEdit.Sphere(new VoxelCoord(0, 230, 0), 70, VoxelEditOperation.Lift))
        );

        // A saved log whose piece edit names an edit that lifted nothing.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteHeader(writer, format: 2, edits: 2);
            WriteEdit(
                writer,
                VoxelEdit.Sphere(new VoxelCoord(0, 230, 0), 10, VoxelEditOperation.Clear) with
                {
                    Sequence = 1,
                }
            );
            WriteEdit(
                writer,
                VoxelEdit.PieceAt(1, 10, new VoxelCoord(0, 300, 0), VoxelEdit.NoTurn) with
                {
                    Sequence = 2,
                }
            );
            writer.Write(0);
        }

        stream.Position = 0;
        using var fresh = new VoxelWorld(
            new VoxelSettings { Levels = 1, Seed = 17 },
            0,
            s_generator,
            NullLogger<VoxelWorld>.Instance
        );
        fresh.Initialize();
        Assert.Throws<InvalidDataException>(() => fresh.LoadEdits(stream));
        Assert.Equal(1, _world.EditCount);
    }

    [Fact]
    public void A_saved_piece_whose_box_is_not_its_lifts_is_refused()
    {
        // The reader trusts nothing: a piece may claim no more voxels than its lift's box (a 128^3 piece written as one
        // run takes 36 bytes), and it must lie where the lift was.
        var lift = VoxelEdit.Box(new VoxelCoord(0, 230, 0), new VoxelCoord(10, 10, 10), VoxelEditOperation.Lift) with
        {
            Sequence = 1,
        };
        var right = Written(lift, new VoxelCoord(0, 230, 0), new VoxelCoord(10, 10, 10));
        var tooBig = Written(lift, new VoxelCoord(0, 230, 0), new VoxelCoord(128, 128, 128));
        var moved = Written(lift, new VoxelCoord(1, 230, 0), new VoxelCoord(10, 10, 10));

        var pieces = new List<StoredPiece>();
        EditLogSerializer.Read(right, s_generator.Id, s_generator.Version, [], pieces);
        var big = Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(tooBig, s_generator.Id, s_generator.Version, [], [])
        );
        Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(moved, s_generator.Id, s_generator.Version, [], [])
        );

        var piece = Assert.Single(pieces);
        Assert.Equal((BoxesGenerator.Stone, 1000), Assert.Single(piece.Runs));
        Assert.Contains("its lift's box", big.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_run_length_is_refused_as_invalid_data()
    {
        var lift = VoxelEdit.Box(new VoxelCoord(0, 230, 0), new VoxelCoord(10, 10, 10), VoxelEditOperation.Lift) with
        {
            Sequence = 1,
        };
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteHeader(writer, format: 2, edits: 1);
            WriteEdit(writer, lift);
            writer.Write(1);
            writer.Write(1L);
            WriteCoord(writer, new VoxelCoord(0, 230, 0));
            WriteCoord(writer, new VoxelCoord(10, 10, 10));
            writer.Write(BoxesGenerator.Stone);
            // A 7-bit length whose continuation bits never end: BinaryReader throws FormatException.
            writer.Write((ReadOnlySpan<byte>)[0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        }

        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() =>
            EditLogSerializer.Read(stream, s_generator.Id, s_generator.Version, [], [])
        );
    }

    /// <summary>A format 2 log of one lift and its piece of stone, the piece's box as given.</summary>
    private static MemoryStream Written(in VoxelEdit lift, VoxelCoord min, VoxelCoord size)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteHeader(writer, format: 2, edits: 1);
            WriteEdit(writer, lift);
            writer.Write(1);
            writer.Write(lift.Sequence);
            WriteCoord(writer, min);
            WriteCoord(writer, size);
            writer.Write(BoxesGenerator.Stone);
            writer.Write7BitEncodedInt(size.X * size.Y * size.Z);
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteCoord(BinaryWriter writer, VoxelCoord coord)
    {
        writer.Write(coord.X);
        writer.Write(coord.Y);
        writer.Write(coord.Z);
    }

    private static void WriteHeader(BinaryWriter writer, int format, long edits)
    {
        writer.Write("ITVE"u8);
        writer.Write(format);
        writer.Write(s_generator.Id);
        writer.Write(s_generator.Version);
        writer.Write(17UL);
        writer.Write(edits);
    }

    private static void WriteEdit(BinaryWriter writer, in VoxelEdit edit, int format = 2)
    {
        writer.Write(edit.Sequence);
        writer.Write((byte)edit.Shape);
        writer.Write((byte)edit.Operation);
        writer.Write(edit.Material);
        foreach (
            var value in (ReadOnlySpan<int>)
                [edit.Origin.X, edit.Origin.Y, edit.Origin.Z, edit.Extent.X, edit.Extent.Y, edit.Extent.Z]
        )
        {
            writer.Write(value);
        }

        if (format >= 2)
        {
            writer.Write(edit.Piece);
            writer.Write(edit.Turn);
        }
    }

    private static VoxelEdit PutBack(in VoxelPiece piece, VoxelCoord pivot, long turn) =>
        VoxelEdit.PieceAt(piece.Sequence, piece.Radius, pivot, turn);

    /// <summary>The materials of a box at levels 0 to 2, level 0 first, x fastest.</summary>
    private static List<byte> Snapshot(VoxelWorld world, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        var materials = new List<byte>();
        for (var level = 0; level < Levels; level++)
        {
            var step = 1 << level;
            for (var z = z0; z <= z1; z += step)
            {
                for (var y = y0; y <= y1; y += step)
                {
                    for (var x = x0; x <= x1; x += step)
                    {
                        Assert.True(world.TryGetMaterial(new VoxelCoord(x, y, z), level, out var material));
                        materials.Add(material);
                    }
                }
            }
        }

        return materials;
    }

    private static VoxelWorld Load(Stream? edits = null)
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = 3,
            Levels = Levels,
            MemoryBudgetMegabytes = 256,
            WorkerThreads = 2,
            Seed = 17,
        };
        var world = new VoxelWorld(settings, 0, s_generator, NullLogger<VoxelWorld>.Instance);
        world.Initialize();
        if (edits is not null)
        {
            world.LoadEdits(edits);
        }

        world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(world);
        return world;
    }

    private static long Lift(VoxelWorld world, in VoxelEdit edit)
    {
        Assert.True(world.TryLift(in edit, out var sequence), "The lift's columns are not resident.");
        return sequence;
    }
}
