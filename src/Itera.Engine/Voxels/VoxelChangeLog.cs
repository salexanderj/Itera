namespace Itera.Engine.Voxels;

/// <summary>What happened to the voxel world.</summary>
public enum VoxelChangeKind : byte
{
    /// <summary>A column became resident. Read all its layers.</summary>
    ColumnLoaded = 0,

    /// <summary>A column was unloaded. Drop everything held for it.</summary>
    ColumnUnloaded = 1,

    /// <summary>
    /// One brick cell of one chunk may hold different voxels: a brick was created, changed, turned uniform, or removed.
    /// Read the cell again.
    /// </summary>
    BrickChanged = 2,

    /// <summary>
    /// Many brick cells of one chunk may hold different voxels: an edit changed more than
    /// <c>VoxelWorld.ChunkChangeBricks</c> of its bricks, so it is recorded once for the chunk, not once per brick (a
    /// large edit would otherwise overrun the list and make every reader reload the world). Read the chunk again, or
    /// the parts of it <see cref="VoxelChange.Blocks"/> names.
    /// </summary>
    ChunkChanged = 3,

    /// <summary>
    /// An edit stored layers of the column past the ones it stored (<c>VoxelWorld.TryGetStoredLayers</c>): what keeps a
    /// layer's blocks may need to make room for them. The edit's brick and chunk changes come before it.
    /// </summary>
    ColumnWidened = 4,
}

/// <summary>One entry of the world's change list.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Column">The column it happened in.</param>
/// <param name="Layer">
/// For <see cref="VoxelChangeKind.BrickChanged"/> and <see cref="VoxelChangeKind.ChunkChanged"/>, the chunk's layer in
/// the column; otherwise -1.
/// </param>
/// <param name="Brick">
/// For <see cref="VoxelChangeKind.BrickChanged"/>, the brick's cell in the chunk (<c>x + 16y + 256z</c>, 0 to 4095), not
/// a brick handle: handles are reused after a brick is freed. Otherwise -1.
/// </param>
/// <param name="Blocks">
/// For <see cref="VoxelChangeKind.ChunkChanged"/>, which of the chunk's 64 blocks of 32 voxels on a side (4 bricks) hold
/// a changed brick: bit <c>x + 4y + 16z</c> for the block at (x, y, z), each 0 to 3. Otherwise 0.
/// </param>
public readonly record struct VoxelChange(
    VoxelChangeKind Kind,
    ColumnKey Column,
    int Layer,
    int Brick,
    ulong Blocks = 0
);

/// <summary>
/// One consumer's place in the world's change list, for example the renderer's upload or physics' collidable rebuild.
/// Each consumer reads the changes since its last read at its own pace.
/// </summary>
public sealed class VoxelChangeCursor
{
    internal VoxelChangeCursor(long position)
    {
        Position = position;
    }

    /// <summary>
    /// True after the consumer fell so far behind that changes were lost. It must then reload everything it mirrors
    /// from the world and call <see cref="EndResync"/>.
    /// </summary>
    public bool NeedsResync { get; internal set; }

    internal long Position { get; set; }

    /// <summary>Marks the resync as done.</summary>
    public void EndResync() => NeedsResync = false;
}

/// <summary>
/// A fixed ring of the world's changes that any number of cursors read. Adding and reading never allocate. Main thread
/// only.
/// </summary>
internal sealed class VoxelChangeLog
{
    private readonly VoxelChange[] _ring;
    private readonly int _mask;

    /// <param name="capacity">Entries kept; a power of two.</param>
    public VoxelChangeLog(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be a power of two.");
        }

        _ring = new VoxelChange[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Entries added since the log was created.</summary>
    public long Written { get; private set; }

    /// <summary>Adds a change.</summary>
    public void Add(in VoxelChange change)
    {
        _ring[Written & _mask] = change;
        Written++;
    }

    /// <summary>A cursor that sees the changes added from now on.</summary>
    public VoxelChangeCursor CreateCursor() => new(Written);

    /// <summary>
    /// Copies the cursor's unread changes, oldest first, into <paramref name="buffer"/> and returns how many. Call again
    /// while it fills the buffer. When the cursor fell more than the ring's capacity behind, it returns 0, sets
    /// <see cref="VoxelChangeCursor.NeedsResync"/>, and moves the cursor to the newest change.
    /// </summary>
    public int Read(VoxelChangeCursor cursor, Span<VoxelChange> buffer)
    {
        var unread = Written - cursor.Position;
        if (unread > _ring.Length)
        {
            cursor.NeedsResync = true;
            cursor.Position = Written;
            return 0;
        }

        var count = (int)Math.Min(unread, buffer.Length);
        for (var i = 0; i < count; i++)
        {
            buffer[i] = _ring[(cursor.Position + i) & _mask];
        }

        cursor.Position += count;
        return count;
    }
}
