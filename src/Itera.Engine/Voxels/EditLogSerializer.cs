using System.Globalization;
using System.Text;

namespace Itera.Engine.Voxels;

/// <summary>What an edit log stream says about the world it belongs to.</summary>
/// <param name="GeneratorId">Id of the generator that built the world (<see cref="IChunkGenerator.Id"/>).</param>
/// <param name="GeneratorVersion">That generator's version. Another version builds different voxels from the seed.</param>
/// <param name="Seed">The world seed.</param>
/// <param name="Count">Edits in the stream.</param>
/// <param name="FixedStepHz">The match's fixed step rate (format 3 on); 0 in older streams.</param>
/// <param name="Game">The game's own match data, its rules (format 3 on); null in older streams.</param>
public readonly record struct EditLogHeader(
    string GeneratorId,
    int GeneratorVersion,
    ulong Seed,
    long Count,
    int FixedStepHz = 0,
    byte[]? Game = null
);

/// <summary>
/// A lifted piece as a saved log holds it: its voxels in a dense box, x fastest, then y, then z, as runs of one material.
/// The runs are kept as read, so a piece costs what its bytes in the stream cost, not its volume, until it is unpacked.
/// </summary>
internal sealed record StoredPiece(long Sequence, VoxelCoord Min, VoxelCoord Size, (byte Material, int Length)[] Runs);

/// <summary>
/// Writes and reads an edit log: the save and join payload of a world, since a world is its seed plus its edits.
/// Little-endian and field by field, so the bytes are the same on every platform. Layout: the magic "ITVE", the
/// format version, the generator id and version, the seed; since format 3, the match's fixed step rate and the game's
/// match data (its length, then its bytes); the edit count, then each edit; since format 2, each edit's piece and
/// turn, then the piece count and each lifted piece (its lift's sequence, lowest voxel, and size, then its voxels as
/// runs of one material: the material and the run's length, x fastest). Format 4 (2026-09-28) has the same layout as
/// format 3, and its edits may include resets (<see cref="VoxelEditOperation.Reset"/>), which an older build cannot read;
/// the new version makes it refuse the stream by its version. Format 5 (2026-10-02) has the same layout again, and its
/// edits may include capsules (<see cref="VoxelEditShape.Capsule"/>: a round's channel). Format 6 (2026-10-02) has the
/// same layout again, and its edits may include clears of some materials (<see cref="VoxelEditOperation.ClearMaterials"/>:
/// a player's removal of one material). Format 1 to 5 streams, which have none (format 1 to 4 no capsules, format 1
/// to 3 no resets, formats 1 and 2 no match settings, and format 1 no pieces), still read. A save file is this stream.
/// </summary>
internal static class EditLogSerializer
{
    /// <summary>Format version written into every stream.</summary>
    public const int FormatVersion = 6;

    /// <summary>The most bytes of game match data a stream may hold (as the welcome, <c>SessionProtocol</c>).</summary>
    public const int MaxGameDataBytes = 4096;

    private static ReadOnlySpan<byte> Magic => "ITVE"u8;

    /// <summary>
    /// Writes the header, with the match's fixed step rate and the game's match data (at most
    /// <see cref="MaxGameDataBytes"/>), and every edit of <paramref name="log"/> (each piece decoded into
    /// <paramref name="scratch"/>, a chunk's size, to collect its runs); with <paramref name="afterLastReset"/>,
    /// only the edits after its last reset, renumbered from 1, and their pieces: a world loaded from it is the one the
    /// reset left, and nothing before it counted. The whole log when a put-back after the reset names
    /// a piece lifted before it. A join payload keeps every edit, since a client keeps the host's numbering. Returns the
    /// edits written.
    /// </summary>
    public static long Write(
        Stream stream,
        string generatorId,
        int generatorVersion,
        ulong seed,
        int fixedStepHz,
        ReadOnlySpan<byte> game,
        EditLog log,
        VoxelMemory memory,
        byte[] scratch,
        bool afterLastReset = false
    )
    {
        var from = afterLastReset && OnlyLaterPieces(log) ? log.LastReset : 0;
        if (game.Length > MaxGameDataBytes)
        {
            throw new ArgumentException(
                $"The game's match data is {game.Length} bytes; at most {MaxGameDataBytes} fit.",
                nameof(game)
            );
        }

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(generatorId);
        writer.Write(generatorVersion);
        writer.Write(seed);
        writer.Write(fixedStepHz);
        writer.Write(game.Length);
        writer.Write(game);
        writer.Write(log.Count - from);
        for (var sequence = from + 1; sequence <= log.Count; sequence++)
        {
            ref readonly var edit = ref log[sequence];
            writer.Write(edit.Sequence - from);
            writer.Write((byte)edit.Shape);
            writer.Write((byte)edit.Operation);
            writer.Write(edit.Material);
            WriteCoord(writer, edit.Origin);
            WriteCoord(writer, edit.Extent);
            writer.Write(edit.Shape == VoxelEditShape.Piece ? edit.Piece - from : edit.Piece);
            writer.Write(edit.Turn);
        }

        var pieces = log.Pieces;
        var kept = 0;
        foreach (var piece in pieces)
        {
            kept += piece.Sequence > from ? 1 : 0;
        }

        writer.Write(kept);
        var runs = new List<(byte Material, int Length)>();
        foreach (var piece in pieces)
        {
            if (piece.Sequence <= from)
            {
                continue;
            }

            writer.Write(piece.Sequence - from);
            WriteCoord(writer, piece.Min);
            WriteCoord(writer, piece.Size);
            runs.Clear();
            ChunkTree.Decode(memory, piece.Tree, scratch, piece.Size);
            PieceRuns.Collect(in piece, scratch, runs);
            foreach (var (material, length) in runs)
            {
                writer.Write(material);
                writer.Write7BitEncodedInt(length);
            }
        }

        return log.Count - from;
    }

    /// <summary>True when every put-back after the log's last reset puts back a piece lifted after it.</summary>
    private static bool OnlyLaterPieces(EditLog log)
    {
        for (var sequence = log.LastReset + 1; sequence <= log.Count; sequence++)
        {
            ref readonly var edit = ref log[sequence];
            if (edit.Shape == VoxelEditShape.Piece && edit.Piece <= log.LastReset)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a stream written by <see cref="Write"/> into <paramref name="edits"/> and <paramref name="pieces"/>. Throws
    /// <see cref="InvalidDataException"/> when the stream is not an edit log, has another format version, was built by
    /// another generator or generator version, holds an edit that is out of order or invalid, holds a piece whose box is
    /// not its lift's, ends early, or cannot be read. The stream is not trusted (a save file, or a peer's join payload):
    /// nothing read is larger than what the bytes say, and a piece takes only its runs.
    /// </summary>
    public static EditLogHeader Read(
        Stream stream,
        string expectedGeneratorId,
        int expectedGeneratorVersion,
        List<VoxelEdit> edits,
        List<StoredPiece> pieces
    )
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            var (header, format) = ReadHeader(reader, expectedGeneratorId, expectedGeneratorVersion);
            for (long i = 1; i <= header.Count; i++)
            {
                var edit = new VoxelEdit(
                    reader.ReadInt64(),
                    (VoxelEditShape)reader.ReadByte(),
                    (VoxelEditOperation)reader.ReadByte(),
                    reader.ReadByte(),
                    ReadCoord(reader),
                    ReadCoord(reader),
                    format >= 2 ? reader.ReadInt64() : 0,
                    format >= 2 ? reader.ReadInt64() : 0
                );
                if (edit.Sequence != i)
                {
                    throw Invalid($"Edit {i} has sequence {edit.Sequence}.");
                }

                var problem = edit.Validate();
                if (problem is not null)
                {
                    throw Invalid($"Edit {i} is invalid: {problem}.");
                }

                if (format < 6 && edit.Operation == VoxelEditOperation.ClearMaterials)
                {
                    throw Invalid($"A format {format} edit log cannot hold clears of some materials (edit {i}).");
                }

                if (format < 5 && edit.Shape == VoxelEditShape.Capsule)
                {
                    throw Invalid($"A format {format} edit log cannot hold capsules (edit {i}).");
                }

                if (format < 4 && edit.Operation == VoxelEditOperation.Reset)
                {
                    throw Invalid($"A format {format} edit log cannot hold resets (edit {i}).");
                }

                edits.Add(edit);
            }

            if (format >= 2)
            {
                ReadPieces(reader, edits, pieces);
            }
            else if (edits.Exists(edit => edit.Operation == VoxelEditOperation.Lift))
            {
                throw Invalid($"A format {format} edit log cannot hold lifts.");
            }

            return header;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The edit log ends early.", ex);
        }
        catch (FormatException ex)
        {
            // A 7-bit length (a string's, a run's) that does not end within five bytes.
            throw new InvalidDataException("The edit log is malformed.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException("The edit log could not be read.", ex);
        }
    }

    /// <summary>
    /// Reads only the header of a stream written by <see cref="Write"/>: the generator, the seed, the match settings, and
    /// the edit count, with the checks <see cref="Read"/> makes of them. For a save, whose seed must be known before its
    /// world starts.
    /// </summary>
    public static EditLogHeader ReadHeader(Stream stream, string expectedGeneratorId, int expectedGeneratorVersion)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            return ReadHeader(reader, expectedGeneratorId, expectedGeneratorVersion).Header;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The edit log ends early.", ex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The edit log is malformed.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException("The edit log could not be read.", ex);
        }
    }

    private static (EditLogHeader Header, int Format) ReadHeader(
        BinaryReader reader,
        string expectedGeneratorId,
        int expectedGeneratorVersion
    )
    {
        Span<byte> magic = stackalloc byte[4];
        reader.BaseStream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("The stream is not an Itera edit log.");
        }

        var format = reader.ReadInt32();
        if (format is < 1 or > FormatVersion)
        {
            throw Invalid($"The edit log has format version {format}; this build reads 1 to {FormatVersion}.");
        }

        var generatorId = reader.ReadString();
        var generatorVersion = reader.ReadInt32();
        var seed = reader.ReadUInt64();
        if (
            !string.Equals(generatorId, expectedGeneratorId, StringComparison.Ordinal)
            || generatorVersion != expectedGeneratorVersion
        )
        {
            throw Invalid(
                $"The edit log was made with generator {generatorId} version {generatorVersion}; this world uses {expectedGeneratorId} version {expectedGeneratorVersion}."
            );
        }

        var fixedStepHz = 0;
        byte[]? game = null;
        if (format >= 3)
        {
            fixedStepHz = reader.ReadInt32();
            var length = reader.ReadInt32();
            if (fixedStepHz < 1 || length < 0 || length > MaxGameDataBytes)
            {
                throw Invalid(
                    $"The edit log's match settings are not valid (step rate {fixedStepHz}, {length} bytes)."
                );
            }

            game = reader.ReadBytes(length);
            if (game.Length != length)
            {
                throw new EndOfStreamException();
            }
        }

        var count = reader.ReadInt64();
        if (count < 0)
        {
            throw Invalid($"The edit log claims {count} edits.");
        }

        return (new EditLogHeader(generatorId, generatorVersion, seed, count, fixedStepHz, game), format);
    }

    /// <summary>
    /// Reads the pieces and checks that each lift has its piece, with the box of that lift, and each piece edit an
    /// earlier lift.
    /// </summary>
    private static void ReadPieces(BinaryReader reader, List<VoxelEdit> edits, List<StoredPiece> pieces)
    {
        var count = reader.ReadInt32();
        var lifts = new HashSet<long>();
        var expected = new List<VoxelEdit>();
        foreach (var edit in edits)
        {
            if (edit.Operation == VoxelEditOperation.Lift)
            {
                lifts.Add(edit.Sequence);
                expected.Add(edit);
            }
            else if (edit.Shape == VoxelEditShape.Piece && !lifts.Contains(edit.Piece))
            {
                throw Invalid($"Edit {edit.Sequence} puts back piece {edit.Piece}, which no earlier edit lifted.");
            }
        }

        if (count != expected.Count)
        {
            throw Invalid($"The edit log holds {count} pieces for {expected.Count} lifts.");
        }

        var runs = new List<(byte Material, int Length)>();
        for (var i = 0; i < count; i++)
        {
            var sequence = reader.ReadInt64();
            var min = ReadCoord(reader);
            var size = ReadCoord(reader);
            var lift = expected[i];
            if (sequence != lift.Sequence)
            {
                throw Invalid($"Piece {i} belongs to edit {sequence}, not to lift {lift.Sequence}.");
            }

            // The lift wrote the piece as its own box (VoxelWorld.TryLift), at most a chunk on each side, so a piece
            // can claim no more voxels than its lift's box holds.
            lift.GetBounds(out var liftMin, out var liftEnd);
            var liftSize = new VoxelCoord(liftEnd.X - liftMin.X, liftEnd.Y - liftMin.Y, liftEnd.Z - liftMin.Z);
            if (min != liftMin || size != liftSize)
            {
                throw Invalid($"Piece {sequence} has box {min} + {size}; its lift's box is {liftMin} + {liftSize}.");
            }

            var volume = size.X * size.Y * size.Z;
            var filled = 0;
            runs.Clear();
            while (filled < volume)
            {
                var material = reader.ReadByte();
                var run = reader.Read7BitEncodedInt();
                if (run < 1 || run > volume - filled)
                {
                    throw Invalid($"Piece {sequence} has a run of {run} voxels with {volume - filled} left.");
                }

                runs.Add((material, run));
                filled += run;
            }

            pieces.Add(new StoredPiece(sequence, min, size, [.. runs]));
        }
    }

    private static void WriteCoord(BinaryWriter writer, VoxelCoord coord)
    {
        writer.Write(coord.X);
        writer.Write(coord.Y);
        writer.Write(coord.Z);
    }

    private static VoxelCoord ReadCoord(BinaryReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

    private static InvalidDataException Invalid(FormattableString message) =>
        new(message.ToString(CultureInfo.InvariantCulture));
}
