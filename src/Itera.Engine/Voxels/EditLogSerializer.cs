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
/// <summary>
/// A named part of a game's state in a save (format 8 on): its <paramref name="Name"/> (1 to 64 letters, digits, '.',
/// '-', or '_'), the <paramref name="Version"/> of the layout its <paramref name="Bytes"/> were written in, from 1, and
/// those bytes.
/// </summary>
internal sealed record SavedSection(string Name, int Version, byte[] Bytes);

/// <summary>
/// A world's edit log frozen for a save (<c>VoxelWorld.FreezeEdits</c>): the id and generator version it is written with,
/// its seed, the log as it was, and the memory its pieces are in.
/// </summary>
internal sealed record FrozenEdits(
    string WorldId,
    int GeneratorVersion,
    ulong Seed,
    EditLogSnapshot Log,
    VoxelMemory Memory
);

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
/// a player's removal of one material). Format 7 has the same layout again, its edits may include removals of voxel
/// objects' parts (<see cref="VoxelEditOperation.RemovePart"/>), and its generator id is the world's
/// (<c>VoxelObjectKinds.WorldId</c>: with the object kinds' fingerprint where the world places objects). Format 8
/// (2026-10-12) adds, after the pieces, a game's sections (<see cref="SavedSection"/>: their count, then each one's
/// name, version, length, and bytes; a join payload has none). Format 1 to 7 streams, which have none (format 1 to 6 no
/// removals of objects' parts, format 1 to 5 no clears of some materials, format 1 to 4 no capsules, format 1 to 3 no
/// resets, formats 1 and 2 no match settings, and format 1 no pieces), still read. A save file is this stream.
/// </summary>
internal static class EditLogSerializer
{
    /// <summary>Format version written into every stream.</summary>
    public const int FormatVersion = 8;

    /// <summary>The most sections a stream may hold.</summary>
    public const int MaxSections = 256;

    /// <summary>The longest name a section may have, in characters.</summary>
    public const int MaxSectionNameLength = 64;

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
    ) =>
        Write(
            stream,
            generatorId,
            generatorVersion,
            seed,
            fixedStepHz,
            game,
            log.Freeze(),
            memory,
            scratch,
            afterLastReset
        );

    /// <summary>
    /// Writes a frozen log (<see cref="EditLog.Freeze"/>) as the overload with a live one does, and after it the game's
    /// <paramref name="sections"/> (none when null). It reads only the snapshot and the pieces' memory, which never
    /// moves, so it may run on another thread while the world goes on, with a <paramref name="scratch"/> of its own.
    /// </summary>
    public static long Write(
        Stream stream,
        string generatorId,
        int generatorVersion,
        ulong seed,
        int fixedStepHz,
        ReadOnlySpan<byte> game,
        EditLogSnapshot log,
        VoxelMemory memory,
        byte[] scratch,
        bool afterLastReset = false,
        IReadOnlyList<SavedSection>? sections = null
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

        CheckSections(sections);

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

        WriteSections(writer, sections);
        return log.Count - from;
    }

    /// <summary>
    /// Writes the header of a match without a voxel world (its identity, <c>IMatchIdentity</c>, where a world writes its
    /// generator), with no edits and no pieces: a world-less save, or a world-less host's join payload. Read back as any
    /// other stream.
    /// </summary>
    public static void WriteEmpty(
        Stream stream,
        string identity,
        int identityVersion,
        ulong seed,
        int fixedStepHz,
        ReadOnlySpan<byte> game,
        IReadOnlyList<SavedSection>? sections = null
    )
    {
        if (game.Length > MaxGameDataBytes)
        {
            throw new ArgumentException(
                $"The game's match data is {game.Length} bytes; at most {MaxGameDataBytes} fit.",
                nameof(game)
            );
        }

        CheckSections(sections);

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(identity);
        writer.Write(identityVersion);
        writer.Write(seed);
        writer.Write(fixedStepHz);
        writer.Write(game.Length);
        writer.Write(game);
        writer.Write(0L);
        writer.Write(0);
        WriteSections(writer, sections);
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="sections"/> are more than <see cref="MaxSections"/>,
    /// or one has a bad name or version (<see cref="SectionProblem"/>) or another's name.
    /// </summary>
    private static void CheckSections(IReadOnlyList<SavedSection>? sections)
    {
        if (sections is null)
        {
            return;
        }

        if (sections.Count > MaxSections)
        {
            throw new ArgumentException($"{sections.Count} sections; at most {MaxSections} fit.", nameof(sections));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var problem = SectionProblem(section.Name, section.Version);
            if (problem is not null || !names.Add(section.Name))
            {
                throw new ArgumentException(problem ?? $"Two sections are named {section.Name}.", nameof(sections));
            }
        }
    }

    /// <summary>
    /// What is wrong with a section's name or version, or null when nothing is: a name is 1 to
    /// <see cref="MaxSectionNameLength"/> letters, digits, '.', '-', or '_', and a version is 1 or more.
    /// </summary>
    public static string? SectionProblem(string name, int version)
    {
        if (name.Length is 0 or > MaxSectionNameLength)
        {
            return $"A section's name has {name.Length} characters; 1 to {MaxSectionNameLength} fit.";
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return $"The section name {name} holds a character other than a letter, digit, '.', '-', or '_'.";
            }
        }

        return version < 1 ? $"The section {name} has version {version}; versions start at 1." : null;
    }

    private static void WriteSections(BinaryWriter writer, IReadOnlyList<SavedSection>? sections)
    {
        writer.Write(sections?.Count ?? 0);
        if (sections is null)
        {
            return;
        }

        foreach (var section in sections)
        {
            writer.Write(section.Name);
            writer.Write(section.Version);
            writer.Write(section.Bytes.Length);
            writer.Write(section.Bytes);
        }
    }

    /// <summary>True when every put-back after the log's last reset puts back a piece lifted after it.</summary>
    private static bool OnlyLaterPieces(EditLogSnapshot log)
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
    /// Reads a stream written by <c>Write</c> into <paramref name="edits"/> and <paramref name="pieces"/>, and its
    /// sections into <paramref name="sections"/> (read and checked, and left out when it is null). Throws
    /// <see cref="InvalidDataException"/> when the stream is not an edit log, has another format version, was built by
    /// another generator or generator version, holds an edit that is out of order or invalid, holds a piece whose box is
    /// not its lift's, holds a section with a bad name or version, a name twice, or more bytes than the stream has left,
    /// ends early, or cannot be read. The stream is not trusted (a save file, or a peer's join payload): nothing read is
    /// larger than what the bytes say, and a piece takes only its runs.
    /// </summary>
    public static EditLogHeader Read(
        Stream stream,
        string expectedGeneratorId,
        int expectedGeneratorVersion,
        List<VoxelEdit> edits,
        List<StoredPiece> pieces,
        List<SavedSection>? sections = null
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

                if (format < 7 && edit.Operation is VoxelEditOperation.RemovePart or VoxelEditOperation.ClearCopies)
                {
                    throw Invalid(
                        $"A format {format} edit log cannot hold removals of objects' parts or clears of their copies (edit {i})."
                    );
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

            if (format >= 8)
            {
                ReadSections(reader, sections);
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
    /// Reads only the header of a stream written by <c>Write</c>: the generator, the seed, the match settings, and
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
        // Before format 7 the id was the generator's alone, as a world's whose objects it did not name.
        var sameId =
            string.Equals(generatorId, expectedGeneratorId, StringComparison.Ordinal)
            || (format < 7 && expectedGeneratorId.StartsWith(generatorId + "+objects-", StringComparison.Ordinal));
        if (!sameId || generatorVersion != expectedGeneratorVersion)
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

    /// <summary>
    /// Reads the sections, each checked: a good name and version, no name twice, and no more bytes than the stream has
    /// left (where it can tell, as a save's and a payload's memory can).
    /// </summary>
    private static void ReadSections(BinaryReader reader, List<SavedSection>? sections)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > MaxSections)
        {
            throw Invalid($"The edit log claims {count} sections; at most {MaxSections} fit.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var version = reader.ReadInt32();
            var length = reader.ReadInt32();
            if (SectionProblem(name, version) is { } problem)
            {
                throw new InvalidDataException(problem);
            }

            if (!names.Add(name))
            {
                throw Invalid($"The edit log holds the section {name} twice.");
            }

            var stream = reader.BaseStream;
            if (length < 0 || (stream.CanSeek && length > stream.Length - stream.Position))
            {
                throw Invalid($"The section {name} claims {length} bytes, more than the stream has left.");
            }

            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }

            sections?.Add(new SavedSection(name, version, bytes));
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
