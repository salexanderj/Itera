using System.Globalization;
using System.Runtime.InteropServices;

namespace Itera.Engine.Voxels;

/// <summary>
/// Every edit applied to the world, in sequence order, plus an index from each column, at every level the world streams,
/// to the edits since the last reset that reach it. The world is the seed plus this log: a column is rebuilt by generating it and replaying
/// its edits. Edits are kept in fixed segments and the index in growable arrays, so appending allocates only when a
/// segment or an array fills. The log also keeps every lifted piece (<see cref="VoxelPiece"/>), by its lift's sequence:
/// a lift is added only with its piece, and a piece edit only after the lift it names. Beside each edit it keeps its
/// maker (<see cref="MakerOf"/>), which is never saved. Main thread only.
/// </summary>
internal sealed class EditLog
{
    /// <summary>
    /// The most index entries the log counts (since its last reset, over <see cref="CountedLevels"/>): 256 MB of them. An
    /// edit that would pass it is refused, so a save, a host's world, or a host's live edits cannot ask for more memory
    /// than this (one 51-byte edit of the largest size adds about 48,000 entries, and a 1 MB
    /// stream of them asked for over 10 GB). Honest play is far below it: a 5 m build cube counts about 51, a small dig
    /// 10, and losing resets the log.
    /// </summary>
    public const int MaxIndexEntries = 1 << 24;

    /// <summary>
    /// The levels the limit counts each edit's columns over: every level a log can index, whatever the world streams, so
    /// every game (a host and its clients, a save and the game that loads it) agrees whether an edit fits (counted over the world's own levels, a log near the limit made with fewer
    /// could not load with more, and a client of more levels than its host was dropped). The index itself holds the
    /// entries of the levels the world streams, never more than those counted.
    /// </summary>
    public const int CountedLevels = VoxelConstants.MaxLevel + 1;

    private const int SegmentShift = 10;
    private const int SegmentSize = 1 << SegmentShift;
    private const int NoEntry = -1;
    private const byte NoMakerByte = byte.MaxValue;

    private readonly int _levels;
    private readonly VoxelGrid _grid;
    private readonly List<VoxelEdit[]> _segments = [];
    private readonly List<byte[]> _makerSegments = [];
    private readonly Dictionary<ColumnKey, (int Head, int Tail)> _byColumn = [];
    private readonly List<VoxelPiece> _pieces = [];
    private readonly Dictionary<long, int> _pieceBySequence = [];
    private IndexEntry[] _entries = new IndexEntry[SegmentSize];
    private int _entryCount;
    private int _countedEntries;

    /// <param name="levels">Levels whose columns are indexed, from level 0: the levels the world streams.</param>
    /// <param name="grid">The world's grid, whose columns an edit's centimetres reach.</param>
    public EditLog(int levels, VoxelGrid grid)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, CountedLevels);
        _levels = levels;
        _grid = grid;
    }

    /// <summary>Edits in the log; also the sequence of the newest edit.</summary>
    public long Count { get; private set; }

    /// <summary>The sequence of the newest reset (<see cref="VoxelEditOperation.Reset"/>), or 0 when there is none.</summary>
    public long LastReset { get; private set; }

    /// <summary>
    /// The index entries counted against the limit: those of the edits since the last reset, over
    /// <see cref="CountedLevels"/> (<see cref="IndexEntriesFor"/>). The index holds no more than these.
    /// </summary>
    public int IndexEntries => _countedEntries;

    /// <summary>The most index entries this log counts: <see cref="MaxIndexEntries"/>, or less for a test.</summary>
    public int MostIndexEntries { get; private set; } = MaxIndexEntries;

    /// <summary>For tests: holds the index to <paramref name="entries"/> entries, so a test need not fill 256 MB.</summary>
    public void LimitIndex(int entries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entries, MaxIndexEntries);
        MostIndexEntries = entries;
    }

    /// <summary>The lifted pieces, in the order of their lifts.</summary>
    public ReadOnlySpan<VoxelPiece> Pieces => CollectionsMarshal.AsSpan(_pieces);

    /// <summary>Finds the piece lifted by edit <paramref name="sequence"/>.</summary>
    public bool TryGetPiece(long sequence, out VoxelPiece piece)
    {
        if (_pieceBySequence.TryGetValue(sequence, out var index))
        {
            piece = _pieces[index];
            return true;
        }

        piece = default;
        return false;
    }

    /// <summary>Keeps a piece for the lift that is appended next (its sequence is <c>Count + 1</c>).</summary>
    public void AddPiece(in VoxelPiece piece)
    {
        if (piece.Sequence != Count + 1)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Piece {piece.Sequence} is not for the next edit ({Count + 1})."
                ),
                nameof(piece)
            );
        }

        _pieceBySequence[piece.Sequence] = _pieces.Count;
        _pieces.Add(piece);
    }

    /// <summary>Adds to <paramref name="pieces"/> the piece of every piece edit in <paramref name="edits"/>.</summary>
    public void CollectPieces(List<VoxelEdit> edits, List<VoxelPiece> pieces)
    {
        foreach (var edit in edits)
        {
            if (edit.Shape == VoxelEditShape.Piece)
            {
                pieces.Add(_pieces[_pieceBySequence[edit.Piece]]);
            }
        }
    }

    /// <summary>The edit with sequence <paramref name="sequence"/> (1 to <see cref="Count"/>).</summary>
    public ref readonly VoxelEdit this[long sequence]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, Count);
            var index = sequence - 1;
            return ref _segments[(int)(index >> SegmentShift)][index & (SegmentSize - 1)];
        }
    }

    /// <summary>The maker of edit <paramref name="sequence"/> (1 to <see cref="Count"/>), or <see cref="VoxelEdit.NoMaker"/>.</summary>
    public int MakerOf(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, Count);
        var index = sequence - 1;
        var maker = _makerSegments[(int)(index >> SegmentShift)][index & (SegmentSize - 1)];
        return maker == NoMakerByte ? VoxelEdit.NoMaker : maker;
    }

    /// <summary>
    /// Appends an edit whose <see cref="VoxelEdit.Sequence"/> is <c>Count + 1</c>, made by <paramref name="maker"/>
    /// (<see cref="VoxelEdit.NoMaker"/>, or 0 to <see cref="VoxelEdit.MaxMaker"/>), and indexes it under every column, at
    /// every indexed level, that its bounds reach inside the world. Throws <see cref="ArgumentException"/>, with the log
    /// unchanged, when those entries would take the index past <see cref="MostIndexEntries"/>.
    /// </summary>
    public void Append(in VoxelEdit edit, int maker = VoxelEdit.NoMaker)
    {
        if (maker is < VoxelEdit.NoMaker or > VoxelEdit.MaxMaker)
        {
            throw new ArgumentOutOfRangeException(nameof(maker), maker, "A maker is a slot from 0 to 254, or none.");
        }

        if (edit.Sequence != Count + 1)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Edit sequence {edit.Sequence} is not the next one ({Count + 1})."
                ),
                nameof(edit)
            );
        }

        if (edit.Operation == VoxelEditOperation.Lift && !_pieceBySequence.ContainsKey(edit.Sequence))
        {
            throw new ArgumentException("A lift is added only after its piece (AddPiece).", nameof(edit));
        }

        if (edit.Shape == VoxelEditShape.Piece && !_pieceBySequence.ContainsKey(edit.Piece))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Edit {edit.Sequence} puts back piece {edit.Piece}, which the log does not hold."
                ),
                nameof(edit)
            );
        }

        // Checked before anything changes, so a refused edit leaves the log as it was.
        var needed = IndexEntriesFor(in edit, _grid);
        if (needed > MostIndexEntries - _countedEntries)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Edit {edit.Sequence} needs {needed} index entries; the log holds {_countedEntries} of at most {MostIndexEntries}."
                ),
                nameof(edit)
            );
        }

        var index = Count;
        if ((index & (SegmentSize - 1)) == 0)
        {
            _segments.Add(new VoxelEdit[SegmentSize]);
            _makerSegments.Add(new byte[SegmentSize]);
        }

        _segments[^1][index & (SegmentSize - 1)] = edit;
        _makerSegments[^1][index & (SegmentSize - 1)] = maker == VoxelEdit.NoMaker ? NoMakerByte : (byte)maker;
        Count++;

        if (edit.Operation == VoxelEditOperation.Reset)
        {
            // No edit before a reset is replayed again: every column is generated afresh, then takes the edits after it.
            LastReset = edit.Sequence;
            _byColumn.Clear();
            _entryCount = 0;
            _countedEntries = 0;
            return;
        }

        if (!Reaches(in edit, _grid, out var min, out var end))
        {
            return;
        }

        _countedEntries += (int)needed;

        for (var level = 0; level < _levels; level++)
        {
            ColumnsAt(_grid, level, min, end, out var minX, out var minZ, out var lastX, out var lastZ);
            for (var z = minZ; z <= lastZ; z++)
            {
                for (var x = minX; x <= lastX; x++)
                {
                    AddToColumn(new ColumnKey(level, x, z), edit.Sequence);
                }
            }
        }
    }

    /// <summary>
    /// The index entries <paramref name="edit"/> counts against the limit: one for each column, at each of the
    /// <see cref="CountedLevels"/> levels, that its bounds reach inside the world (the index adds those of the levels the
    /// world streams); none for a reset, which empties the index. The same on every game. Allocates nothing.
    /// </summary>
    public static long IndexEntriesFor(in VoxelEdit edit, VoxelGrid grid)
    {
        if (edit.Operation == VoxelEditOperation.Reset || !Reaches(in edit, grid, out var min, out var end))
        {
            return 0;
        }

        var entries = 0L;
        for (var level = 0; level < CountedLevels; level++)
        {
            ColumnsAt(grid, level, min, end, out var minX, out var minZ, out var lastX, out var lastZ);
            entries += (long)Math.Max(0, lastX - minX + 1) * Math.Max(0, lastZ - minZ + 1);
        }

        return entries;
    }

    /// <summary>The level-0 voxels the edit's bounds reach; false when they lie wholly above or below the world.</summary>
    private static bool Reaches(in VoxelEdit edit, VoxelGrid grid, out VoxelCoord min, out VoxelCoord end)
    {
        grid.GetVoxelBounds(in edit, out min, out end);
        return min.Y < grid.EndVoxelY && end.Y > grid.MinVoxelY;
    }

    /// <summary>
    /// The columns of <paramref name="level"/> that the level-0 voxel bounds reach inside the world (none when last &lt;
    /// min).
    /// </summary>
    private static void ColumnsAt(
        VoxelGrid grid,
        int level,
        VoxelCoord min,
        VoxelCoord end,
        out int minX,
        out int minZ,
        out int lastX,
        out int lastZ
    )
    {
        var shift = VoxelConstants.ChunkShift + level;
        minX = Math.Max(min.X, grid.MinVoxelXZ) >> shift;
        minZ = Math.Max(min.Z, grid.MinVoxelXZ) >> shift;
        lastX = (Math.Min(end.X, grid.EndVoxelXZ) - 1) >> shift;
        lastZ = (Math.Min(end.Z, grid.EndVoxelXZ) - 1) >> shift;
    }

    /// <summary>
    /// Adds to <paramref name="edits"/>, in sequence order, every edit after <paramref name="afterSequence"/> that
    /// reaches the column, which must be at an indexed level.
    /// </summary>
    public void CollectForColumn(ColumnKey column, long afterSequence, List<VoxelEdit> edits)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column.Level, _levels);
        if (!_byColumn.TryGetValue(column, out var list))
        {
            return;
        }

        for (var i = list.Head; i != NoEntry; i = _entries[i].Next)
        {
            if (_entries[i].Sequence > afterSequence)
            {
                edits.Add(this[_entries[i].Sequence]);
            }
        }
    }

    private void AddToColumn(ColumnKey column, long sequence)
    {
        if (_entryCount == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
        }

        var entry = _entryCount++;
        _entries[entry] = new IndexEntry(sequence, NoEntry);
        if (_byColumn.TryGetValue(column, out var list))
        {
            _entries[list.Tail] = _entries[list.Tail] with { Next = entry };
            _byColumn[column] = (list.Head, entry);
        }
        else
        {
            _byColumn[column] = (entry, entry);
        }
    }

    private readonly record struct IndexEntry(long Sequence, int Next);
}
