using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Voxels;

// The edit path: edits applied to the resident columns (large ones over fixed steps), the reset, a world that follows a
// host, the edit log saved and loaded, and what players' clears remove.
public sealed partial class VoxelWorld
{
    private readonly long[] _removed = new long[256];
    private readonly long[] _columnCounts = new long[256];

    /// <summary>
    /// Applies a local edit: gives it the next sequence, adds it to the log, and changes the resident columns it
    /// reaches. Returns the sequence. <paramref name="maker"/> is the session slot of the player whose action made it
    /// (<see cref="MakerOf"/>), or none; a clear with a maker is counted for <see cref="Removals"/> first. Throws
    /// <see cref="ArgumentException"/> for an invalid edit or maker.
    /// </summary>
    public long Apply(in VoxelEdit edit, int maker = VoxelEdit.NoMaker)
    {
        ThrowIfFollowingHost();
        var sequenced = edit with { Sequence = _edits.Count + 1 };
        ApplyChecked(in sequenced, maker);
        return sequenced.Sequence;
    }

    /// <summary>
    /// <see cref="Apply"/> for an edit a player's action makes, refused instead of thrown: false, with nothing changed,
    /// for an edit that is not valid (<see cref="VoxelEdit.Validate"/>; one far outside the map among them) or one the
    /// log has no room left to index (<see cref="EditLog.MaxIndexEntries"/>, less <see cref="SettleReserve"/>). Such an edit from a player's tool once ended the run. Throws only on
    /// a world that follows a host, or for a maker out of range. Allocates no more than <see cref="Apply"/>.
    /// </summary>
    public bool TryApply(in VoxelEdit edit, int maker, out long sequence)
    {
        ThrowIfFollowingHost();
        var sequenced = edit with { Sequence = _edits.Count + 1 };
        if (!CanApply(in sequenced))
        {
            sequence = 0;
            return false;
        }

        ApplyChecked(in sequenced, maker);
        sequence = sequenced.Sequence;
        return true;
    }

    /// <summary>
    /// True when <see cref="TryApply"/> would take <paramref name="edit"/> (its sequence aside): it is valid and the log
    /// has room for it. A refusal is logged: at Debug for an edit that is not valid, at Warning once for a full log.
    /// </summary>
    public bool CanApply(in VoxelEdit edit)
    {
        var problem = (edit with { Sequence = _edits.Count + 1 }).Validate();
        if (problem is not null)
        {
            LogRefusedEdit(problem);
            return false;
        }

        if (EditLog.IndexEntriesFor(in edit, Grid) > _edits.MostIndexEntries - SettleReserve - _edits.IndexEntries)
        {
            if (!_logFullLogged)
            {
                _logFullLogged = true;
                LogEditLogFull(_edits.IndexEntries, _edits.MostIndexEntries);
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Hears what each clear a player made removes (<see cref="IVoxelRemovals"/>), or null for none. The game sets it
    /// for its match. Main thread.
    /// </summary>
    public IVoxelRemovals? Removals { get; set; }

    /// <summary>
    /// The voxel objects the world places, as its edits reach them (<c>ObjectDamage</c>), or null for none. Main thread.
    /// </summary>
    internal IObjectEdits? ObjectEdits { get; set; }

    /// <summary>
    /// Adds to <paramref name="edits"/> every edit since the last reset that reaches a level-0 column under the box from
    /// <paramref name="min"/> to <paramref name="end"/> (absolute centimetres, end exclusive, across x and z), in sequence
    /// order, each once. Main thread.
    /// </summary>
    internal void CollectEdits(VoxelCoord min, VoxelCoord end, List<VoxelEdit> edits)
    {
        var first = edits.Count;
        BoxColumns(min, end, out var firstX, out var firstZ, out var lastX, out var lastZ);
        for (var z = firstZ; z <= lastZ; z++)
        {
            for (var x = firstX; x <= lastX; x++)
            {
                _edits.CollectForColumn(new ColumnKey(0, x, z), _edits.LastReset, edits);
            }
        }

        // The columns' lists, each in order, merged: sorted, then each sequence once.
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(edits)[first..];
        span.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        var kept = first;
        for (var i = first; i < edits.Count; i++)
        {
            if (kept == first || edits[i].Sequence != edits[kept - 1].Sequence)
            {
                edits[kept++] = edits[i];
            }
        }

        edits.RemoveRange(kept, edits.Count - kept);
    }

    /// <summary>
    /// True when the log indexes <paramref name="edit"/> in a level-0 column under the box from <paramref name="min"/>
    /// to <paramref name="end"/> (absolute centimetres, end exclusive, across x and z): when <see cref="CollectEdits"/>
    /// for that box would find it. An edit wholly past the map's sides, or above or below its height, is in no column.
    /// </summary>
    internal bool IndexesUnder(in VoxelEdit edit, VoxelCoord min, VoxelCoord end)
    {
        Grid.GetVoxelBounds(in edit, out var low, out var high);
        if (low.Y >= Grid.EndVoxelY || high.Y <= Grid.MinVoxelY)
        {
            return false;
        }

        ColumnsOf(0, low, high, out var editX, out var editZ, out var editLastX, out var editLastZ);
        BoxColumns(min, end, out var boxX, out var boxZ, out var boxLastX, out var boxLastZ);
        return Math.Max(editX, boxX) <= Math.Min(editLastX, boxLastX)
            && Math.Max(editZ, boxZ) <= Math.Min(editLastZ, boxLastZ);
    }

    /// <summary>The level-0 columns, within the map, under the box from min to end (absolute centimetres, end exclusive).</summary>
    private void BoxColumns(
        VoxelCoord min,
        VoxelCoord end,
        out int firstX,
        out int firstZ,
        out int lastX,
        out int lastZ
    )
    {
        var edge = Grid.Edge(0);
        var low = new VoxelCoord(
            (int)Maths.DeterministicMath.FloorDivide(min.X, edge),
            0,
            (int)Maths.DeterministicMath.FloorDivide(min.Z, edge)
        );
        var high = new VoxelCoord(
            (int)Maths.DeterministicMath.FloorDivide(end.X - 1L, edge) + 1,
            1,
            (int)Maths.DeterministicMath.FloorDivide(end.Z - 1L, edge) + 1
        );
        ColumnsOf(0, low, high, out firstX, out firstZ, out lastX, out lastZ);
    }

    /// <summary>
    /// The session slot of the player whose action made edit <paramref name="sequence"/> (1 to <see cref="EditCount"/>),
    /// or <see cref="VoxelEdit.NoMaker"/>. Makers are kept for this match only: a save or a joiner's download has none.
    /// </summary>
    public int MakerOf(long sequence) => _edits.MakerOf(sequence);

    /// <summary>
    /// Puts the whole world back as the generator makes it from the seed: appends a reset
    /// (<see cref="VoxelEditOperation.Reset"/>), after which no earlier edit counts, and unloads every column, resident
    /// or loading, so each is generated again as the streaming centres want it. Returns the reset's sequence. The log
    /// keeps the earlier edits, so sequences go on and every reader of the log, a session's clients and a save among
    /// them, gets the reset in its place. Lifted pieces are kept too. Throws <see cref="InvalidOperationException"/> on a
    /// world that follows a host.
    /// </summary>
    public long Reset() => Apply(VoxelEdit.ResetAll());

    /// <summary>
    /// Applies an edit that already has its sequence, which must be the next one: edits from a loaded log, and the
    /// host's with their makers (see <see cref="Apply"/>). A lift's piece must have been added first.
    /// </summary>
    public void ApplySequenced(in VoxelEdit edit, int maker = VoxelEdit.NoMaker)
    {
        if (edit.Sequence != _edits.Count + 1)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Edit sequence {edit.Sequence} is not the next one ({_edits.Count + 1})."
                ),
                nameof(edit)
            );
        }

        ApplyChecked(in edit, maker);
    }

    /// <summary>
    /// Makes this world a session client's copy of the host's: from now on <see cref="Apply"/> and
    /// <see cref="TryLift"/> throw <see cref="InvalidOperationException"/>, so the log keeps the host's numbering.
    /// </summary>
    public void FollowHost() => _followsHost = true;

    private void ThrowIfFollowingHost()
    {
        if (_followsHost)
        {
            throw new InvalidOperationException(
                "This world follows a session host: only the host's edits change it (VoxelWorld.FollowHost)."
            );
        }
    }

    /// <summary>
    /// Writes the save and join payload: the generator, the seed, the match's fixed step rate and the game's match data
    /// (at most <see cref="EditLogSerializer.MaxGameDataBytes"/>), and every edit with its pieces; with
    /// <paramref name="afterLastReset"/> (a save), only the edits after the last reset, renumbered from 1.
    /// Returns the edits written.
    /// </summary>
    public long SaveEdits(Stream stream, int fixedStepHz, ReadOnlySpan<byte> game, bool afterLastReset = false)
    {
        // The pieces are decoded into the piece scratch one after another: no piece stays unpacked after.
        _unpackedPiece = 0;
        var scratch = _pieceScratch ??= new byte[VoxelConstants.ChunkVoxels];
        var written = EditLogSerializer.Write(
            stream,
            _generator.Id,
            _generator.Version,
            _seed,
            fixedStepHz,
            game,
            _edits,
            Memory,
            scratch,
            afterLastReset
        );
        _unpackedPiece = 0;
        return written;
    }

    /// <summary>
    /// Reads only the header of a stream written by <see cref="SaveEdits"/> (a save file): its seed, match settings, and
    /// edit count. Throws <see cref="InvalidDataException"/> when it is not one or comes from another generator or
    /// generator version. Needs no initialised world: a save's seed is read before its world starts.
    /// </summary>
    public EditLogHeader ReadSaveHeader(Stream stream) =>
        EditLogSerializer.ReadHeader(stream, _generator.Id, _generator.Version);

    /// <summary>
    /// Reads an edit log written by <see cref="SaveEdits"/> and applies it. The world must have no edits yet, and the
    /// log must come from the same generator, generator version, and seed; otherwise nothing is applied and
    /// <see cref="InvalidDataException"/> or <see cref="InvalidOperationException"/> is thrown. Returns the edit count.
    /// </summary>
    public long LoadEdits(Stream stream)
    {
        if (_edits.Count != 0)
        {
            throw new InvalidOperationException("Edits can only be loaded into a world that has none.");
        }

        var edits = new List<VoxelEdit>();
        var pieces = new List<StoredPiece>();
        var header = EditLogSerializer.Read(stream, _generator.Id, _generator.Version, edits, pieces);
        if (header.Seed != _seed)
        {
            throw new InvalidDataException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The edit log is for seed {header.Seed}; this world has seed {_seed}."
                )
            );
        }

        var nextPiece = 0;
        var scratch = _pieceScratch ??= new byte[VoxelConstants.ChunkVoxels];
        foreach (var edit in edits)
        {
            try
            {
                if (edit.Operation == VoxelEditOperation.Lift)
                {
                    // The serializer checked that every lift has its piece, in order.
                    var stored = pieces[nextPiece++];
                    ThrowIfNoRoom(in edit);
                    AddStoredPiece(stored.Sequence, stored.Min, stored.Size, stored.Runs, scratch);
                }

                ApplySequenced(in edit);
            }
            catch (ArgumentException ex)
            {
                // An edit the log cannot take (one that needs more index entries than it has room for): the stream is
                // refused as one that cannot be read, which a joiner and a save's start report.
                throw new InvalidDataException("The edit log cannot be loaded: " + ex.Message, ex);
            }
        }

        return edits.Count;
    }

    /// <summary>
    /// Applies waiting large edits to <paramref name="columns"/> of their resident columns (at least one), a column at a
    /// time with all its waiting edits in order, level 0 first and the columns nearest a streaming centre first: a count,
    /// so every game applies them at the same pace. The time it takes goes to the voxel stats line. Main
    /// thread, once a fixed step, after the systems that edit. Allocates nothing.
    /// </summary>
    public void ApplyPendingEdits(int columns)
    {
        if (_pendingEdits.Count == 0)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var left = Math.Max(1, columns);
        if (_pendingUnsorted)
        {
            _pendingEdits.Sort(_pendingOrder);
            _pendingUnsorted = false;
        }

        var done = 0;
        while (done < _pendingEdits.Count)
        {
            var key = _pendingEdits[done++];
            if (TryGetResident(key, out var column) && column.EditsPending)
            {
                CatchUp(column);
                _stats.DeferredColumns++;
                if (--left == 0)
                {
                    break;
                }
            }
        }

        _pendingEdits.RemoveRange(0, done);
        _stats.MaxDeferredTicks = Math.Max(_stats.MaxDeferredTicks, Stopwatch.GetTimestamp() - start);
    }

    /// <summary>
    /// Applies every waiting edit of the level-0 columns that hold the level-0 voxels from <paramref name="min"/> to
    /// <paramref name="end"/> (exclusive).
    /// </summary>
    private void CatchUpLevel0(VoxelCoord min, VoxelCoord end)
    {
        if (_pendingEdits.Count == 0)
        {
            return;
        }

        const int shift = VoxelConstants.ChunkShift;
        for (var z = min.Z >> shift; z <= (end.Z - 1) >> shift; z++)
        {
            for (var x = min.X >> shift; x <= (end.X - 1) >> shift; x++)
            {
                if (_columns.TryGetValue(new ColumnKey(0, x, z), out var column) && column.EditsPending)
                {
                    CatchUp(column);
                }
            }
        }
    }

    /// <summary>Applies to a resident column, in order, every edit that reaches it and is not in its voxels yet.</summary>
    private void CatchUp(Column column)
    {
        _replay.Clear();
        _edits.CollectForColumn(column.Key, column.AppliedEdit, _replay);
        foreach (var edit in _replay)
        {
            ApplyToColumn(column, in edit);
        }

        column.AppliedEdit = _edits.Count;
        column.EditsPending = false;
    }

    /// <summary>
    /// Counts by material, into <paramref name="byMaterial"/> (one entry per material, 256; cleared first), what a
    /// sphere, capsule, or box clear (a clear of some materials, only those) would take out of the world, in cubic
    /// centimetres, and returns the level it read: for each voxel the density it would lose
    /// (<see cref="VoxelDensity"/>: the share of it the shape covers, of what is solid in it) times its volume,
    /// <see cref="Grid"/>'s edge at that level cubed, rounded once for each material. Entry 0 holds what it would take
    /// from air voxels' densities, the solid of a neighbour's surface that reaches into them, which no material owns. The
    /// level is the finest whose columns under the edit are all resident, after the large edits still waiting in them are
    /// applied (so what it counts is what the log says is there). Returns -1, counting nothing, when no level's columns
    /// there are all resident, or the edit lies outside the world. Changes no voxel but by applying those waiting edits,
    /// which the log already holds. Allocates nothing. Main thread.
    /// </summary>
    public int CountRemoved(in VoxelEdit edit, Span<long> byMaterial)
    {
        if (edit.Shape == VoxelEditShape.Piece)
        {
            throw new ArgumentException("Only sphere, capsule, and box edits are counted.", nameof(edit));
        }

        if (byMaterial.Length < 256)
        {
            throw new ArgumentException("The counts hold one entry per material.", nameof(byMaterial));
        }

        byMaterial[..256].Clear();
        Grid.GetVoxelBounds(in edit, out var min, out var end);
        if (
            end.X <= Grid.MinVoxelXZ
            || end.Z <= Grid.MinVoxelXZ
            || min.X >= Grid.EndVoxelXZ
            || min.Z >= Grid.EndVoxelXZ
            || end.Y <= Grid.MinVoxelY
            || min.Y >= Grid.EndVoxelY
        )
        {
            return -1;
        }

        var level = 0;
        while (level < _levels && !ColumnsResidentUnder(level, min, end))
        {
            level++;
        }

        if (level == _levels)
        {
            return -1;
        }

        // In density steps times cubic centimetres, over all the columns, rounded to cubic centimetres at the end.
        var shift = VoxelConstants.ChunkShift + level;
        var counts = _columnCounts;
        Array.Clear(counts);
        ColumnsOf(level, min, end, out var firstX, out var firstZ, out var lastX, out var lastZ);
        for (var z = firstZ; z <= lastZ; z++)
        {
            for (var x = firstX; x <= lastX; x++)
            {
                var column = _columns[new ColumnKey(level, x, z)];
                if (column.EditsPending)
                {
                    CatchUp(column);
                }

                var key = column.Key;
                var layerOffset = Grid.MinChunkYAt(level);
                var first = Math.Max(0, (min.Y >> shift) - layerOffset);
                var last = Math.Min(column.LayerCount - 1, ((end.Y - 1) >> shift) - layerOffset);
                for (var layer = first; layer <= last; layer++)
                {
                    VoxelEditing.CountInWorldChunk(
                        Memory,
                        Grid,
                        column.LayerAt(layer),
                        key.Chunk(layer, Grid),
                        in edit,
                        counts
                    );
                }
            }
        }

        for (var material = 0; material < counts.Length; material++)
        {
            if (counts[material] != 0 && (material == VoxelConstants.Air || Removes(in edit, (byte)material)))
            {
                byMaterial[material] = (counts[material] + VoxelDensity.Full / 2) / VoxelDensity.Full;
            }
        }

        return level;
    }

    /// <summary>
    /// The columns of <paramref name="level"/> under the level-0 voxels [<paramref name="min"/>, <paramref name="end"/>)
    /// inside the world, as inclusive ranges.
    /// </summary>
    private void ColumnsOf(
        int level,
        VoxelCoord min,
        VoxelCoord end,
        out int firstX,
        out int firstZ,
        out int lastX,
        out int lastZ
    )
    {
        var shift = VoxelConstants.ChunkShift + level;
        firstX = Math.Max(min.X, Grid.MinVoxelXZ) >> shift;
        firstZ = Math.Max(min.Z, Grid.MinVoxelXZ) >> shift;
        lastX = (Math.Min(end.X, Grid.EndVoxelXZ) - 1) >> shift;
        lastZ = (Math.Min(end.Z, Grid.EndVoxelXZ) - 1) >> shift;
    }

    /// <summary>The column <paramref name="key"/> when it is resident.</summary>
    private bool TryGetResident(
        ColumnKey key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Column? column
    ) => _columns.TryGetValue(key, out column) && column.State == ColumnState.Resident;

    /// <summary>Refuses a maker that is neither a session slot (0 to 254) nor none.</summary>
    private static void ThrowIfBadMaker(int maker)
    {
        if (maker is < VoxelEdit.NoMaker or > VoxelEdit.MaxMaker)
        {
            throw new ArgumentOutOfRangeException(nameof(maker), maker, "A maker is a slot from 0 to 254, or none.");
        }
    }

    /// <summary>True when a clear of <paramref name="edit"/>'s kind removes voxels of <paramref name="material"/>: any solid one, or one of a clear of some materials' set.</summary>
    private static bool Removes(in VoxelEdit edit, byte material) =>
        edit.Operation != VoxelEditOperation.ClearMaterials || edit.ClearsMaterial(material);

    /// <summary>True when every column of <paramref name="level"/> under the level-0 box [min, end) inside the world is resident.</summary>
    private bool ColumnsResidentUnder(int level, VoxelCoord min, VoxelCoord end)
    {
        var shift = VoxelConstants.ChunkShift + level;
        ColumnsOf(level, min, end, out var firstX, out var firstZ, out var lastX, out var lastZ);
        for (var z = firstZ; z <= lastZ; z++)
        {
            for (var x = firstX; x <= lastX; x++)
            {
                if (
                    !_columns.TryGetValue(new ColumnKey(level, x, z), out var column)
                    || column.State != ColumnState.Resident
                )
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>For tests: holds the edit log's index to <paramref name="entries"/> entries (<see cref="EditLog.LimitIndex"/>).</summary>
    internal void LimitEditIndex(int entries) => _edits.LimitIndex(entries);

    /// <summary>
    /// Index entries <see cref="CanApply"/> keeps back for edits that must not be refused: the edits of the loose bodies
    /// still to settle, a ball's sphere or a lifted piece's (at most 1,024 bodies, each at most a chunk, so at most about
    /// 45 entries each).
    /// </summary>
    private const int SettleReserve = 1 << 16;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when the log has no room to index <paramref name="edit"/>
    /// (<see cref="EditLog.MaxIndexEntries"/>): before anything changes, so a host's edit or a loaded log that asks for
    /// more is refused whole. A session client then drops the session; a save or a download is refused.
    /// </summary>
    private void ThrowIfNoRoom(in VoxelEdit edit)
    {
        var needed = EditLog.IndexEntriesFor(in edit, Grid);
        if (needed > _edits.MostIndexEntries - _edits.IndexEntries)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Edit {edit.Sequence} needs {needed} index entries; the edit log holds {_edits.IndexEntries} of at most {_edits.MostIndexEntries}."
                ),
                nameof(edit)
            );
        }
    }

    private void ApplyChecked(in VoxelEdit edit, int maker)
    {
        var problem = edit.Validate();
        if (problem is not null)
        {
            throw new ArgumentException("Invalid voxel edit: " + problem + ".", nameof(edit));
        }

        ThrowIfBadMaker(maker);

        ThrowIfNoRoom(in edit);

        // Counted before anything changes, so what it removes is what the log says was there. Only this path counts:
        // replays (CatchUp, a column loaded again, generation) never do.
        if (
            maker != VoxelEdit.NoMaker
            && Removals is { } removals
            && edit.Operation is VoxelEditOperation.Clear or VoxelEditOperation.ClearMaterials
            && edit.Shape != VoxelEditShape.Piece
        )
        {
            CountRemoved(in edit, _removed);
            // And what it takes from the objects' voxels, before they change too.
            ObjectEdits?.CountRemoved(in edit, _removed);
            removals.Removed(in edit, maker, _removed);
        }

        _edits.Append(in edit, maker);
        _stats.Edits++;
        ObjectEdits?.Applied(in edit);
        if (edit.Operation == VoxelEditOperation.Reset)
        {
            _logFullLogged = false;
            UnloadAll(edit.Sequence);
            return;
        }

        // Every level's resident columns that the edit reaches, so coarse chunks show it too. A large edit waits for
        // ApplyPendingEdits; a small one applies at once, after a column's waiting edits, so each column takes edits in
        // the log's order.
        Grid.GetVoxelBounds(in edit, out var min, out var end);
        var defer = (long)(end.X - min.X) * (end.Y - min.Y) * (end.Z - min.Z) > DeferVoxels;
        for (var level = 0; level < _levels; level++)
        {
            ColumnsOf(level, min, end, out var firstX, out var firstZ, out var lastX, out var lastZ);
            for (var z = firstZ; z <= lastZ; z++)
            {
                for (var x = firstX; x <= lastX; x++)
                {
                    if (!TryGetResident(new ColumnKey(level, x, z), out var column))
                    {
                        continue;
                    }

                    if (defer)
                    {
                        if (!column.EditsPending)
                        {
                            column.EditsPending = true;
                            _pendingEdits.Add(column.Key);
                            _pendingUnsorted = true;
                        }
                    }
                    else if (column.EditsPending)
                    {
                        CatchUp(column);
                    }
                    else
                    {
                        ApplyToColumn(column, in edit);
                        column.AppliedEdit = edit.Sequence;
                    }
                }
            }
        }
    }

    /// <summary>Applies an edit to a resident column's chunks and records the bricks that changed.</summary>
    private void ApplyToColumn(Column column, in VoxelEdit edit)
    {
        var piece = default(VoxelPiece);
        var unpacked = ReadOnlySpan<byte>.Empty;
        if (edit.Shape == VoxelEditShape.Piece && _edits.TryGetPiece(edit.Piece, out piece))
        {
            // Unpacked once for all the columns and chunks it reaches: then each voxel is one read, not a walk down the
            // tree. Only the bricks of its box: the view reads air outside it.
            unpacked = Unpacked(in piece);
        }

        var view = new PieceView(in piece, Memory, unpacked);

        Grid.GetVoxelBounds(in edit, out var min, out var end);
        var key = column.Key;
        var shift = VoxelConstants.ChunkShift + key.Level;
        var layerOffset = Grid.MinChunkYAt(key.Level);
        var first = Math.Max(0, (min.Y >> shift) - layerOffset);
        var last = Math.Min(column.LayerCount - 1, ((end.Y - 1) >> shift) - layerOffset);
        var fillChanged = false;
        for (var layer = first; layer <= last; layer++)
        {
            var before = column.LayerAt(layer);
            var wasMixed = before.Kind == ChildKind.Handle;
            var after = VoxelEditing.ApplyToWorldChunk(
                Memory,
                Grid,
                before,
                key.Chunk(layer, Grid),
                in edit,
                in view,
                _changedBricks,
                out var count
            );
            // A layer outside the stored ones is stored once the edit changes it (the column's window widens).
            if (after != before || layer >= column.First && layer < column.End)
            {
                column.Widen(layer) = after;
            }

            fillChanged |= after != before && before.Kind != ChildKind.Handle;

            if (count > ChunkChangeBricks)
            {
                // Which blocks of 4x4x4 bricks hold a changed brick (brick cells are x + 16y + 256z).
                var blocks = 0UL;
                for (var i = 0; i < count; i++)
                {
                    var brick = _changedBricks[i];
                    blocks |= 1UL << (((brick & 15) >> 2) + (((brick >> 4) & 15) >> 2 << 2) + ((brick >> 8) >> 2 << 4));
                }

                _changes.Add(new VoxelChange(VoxelChangeKind.ChunkChanged, key, layer, -1, blocks));
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    _changes.Add(new VoxelChange(VoxelChangeKind.BrickChanged, key, layer, _changedBricks[i]));
                }
            }

            if (count > 0)
            {
                var isMixed = after.Kind == ChildKind.Handle;
                _mixedChunks += (isMixed ? 1 : 0) - (wasMixed ? 1 : 0);
            }
        }

        if (fillChanged)
        {
            _changes.Add(new VoxelChange(VoxelChangeKind.ColumnLayersChanged, key, -1, -1));
        }
    }

    /// <summary>
    /// The order <see cref="ApplyPendingEdits"/> takes waiting columns in: level 0 first, then by the distance from the
    /// column's middle to the nearest streaming centre, as the centres stood when the columns were sorted (when a large
    /// edit added some).
    /// </summary>
    private sealed class PendingOrder(VoxelWorld world)
    {
        public int Compare(ColumnKey a, ColumnKey b)
        {
            if (a.Level != b.Level)
            {
                return a.Level.CompareTo(b.Level);
            }

            return Distance(a).CompareTo(Distance(b));
        }

        private double Distance(ColumnKey key)
        {
            // In absolute centimetres, the column's middle and the centres' alike.
            var size = (double)world.Grid.ColumnCentimetres(key.Level);
            var x = (key.X + 0.5) * size;
            var z = (key.Z + 0.5) * size;
            var nearest = double.MaxValue;
            for (var i = 0; i < world._centreCount; i++)
            {
                var centre = world._centreCentimetres[i];
                double dx = x - centre.X,
                    dz = z - centre.Z;
                nearest = Math.Min(nearest, dx * dx + dz * dz);
            }

            return nearest;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Voxels: an edit was refused: {Problem}")]
    private partial void LogRefusedEdit(string problem);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Voxels: the edit log's index is full ({Entries} of {Most} entries); players' edits are refused until the next reset"
    )]
    private partial void LogEditLogFull(int entries, int most);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Voxels: reset to the seed's world at edit {Sequence}; {Columns} column(s) unloaded to generate again"
    )]
    private partial void LogReset(long sequence, int columns);
}

/// <summary>
/// The voxel objects a world places, as its edits reach them (<see cref="VoxelWorld.ObjectEdits"/>). Main thread.
/// </summary>
internal interface IObjectEdits
{
    /// <summary>
    /// Adds to <paramref name="removed"/> (256 entries, in cubic centimetres) what a clear with a maker takes out of the
    /// objects' voxels, before anything changes.
    /// </summary>
    void CountRemoved(in VoxelEdit edit, Span<long> removed);

    /// <summary>Applies an edit just logged (a reset among them) to the objects' voxels.</summary>
    void Applied(in VoxelEdit edit);
}
