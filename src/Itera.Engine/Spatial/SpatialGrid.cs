using System.Numerics;
using Arch.Core;
using Itera.Engine.Maths;
using Itera.Engine.Voxels;

namespace Itera.Engine.Spatial;

/// <summary>
/// Where a set of entities stand, kept by cells of <see cref="CellMetres"/> over the ground, so a game finds those near a
/// point (<see cref="Within{TVisitor}"/>, <see cref="Across{TVisitor}"/> on the ground alone,
/// <see cref="Nearest{TFilter}(Vector3, float, SpatialMatch, in TFilter, Span{SpatialHit})"/>, <see cref="Any"/>) or a line
/// (<see cref="Along{TVisitor}"/>) without going over every one. Each entry is an upright cylinder at an entity's feet
/// with the game's tags (<see cref="SpatialEntry"/>): a game sets the things that move afresh each step after
/// <see cref="Clear"/>, and sets and removes the things that stay as they come and go. An entry whose radius is at most half a cell
/// is kept in the cell its feet are in; a wider one (a building, a giant) on a list every query goes over. A query
/// passes over entries whose entity is no longer alive, so one removed between the setting and the asking is never
/// handed out. Entries are kept from a whole-metre point of their own (the match's origin when the grid was made or last
/// cleared), so a move of the origin between the setting and the asking changes nothing. Setting, removing, and asking
/// allocate nothing once the grid has held as many entries; it doubles its room as it needs more, with no limit. Scoped
/// to what a game keeps it for; main thread; it is not changed during a query.
/// </summary>
public sealed partial class SpatialGrid
{
    private const int FirstCapacity = 64;

    // What a slot's bucket is when it holds no entry, and when its entry is on the wide list.
    private const int Unused = -2;
    private const int Wide = -1;

    private readonly WorldOrigin _origin;
    private readonly World _world;
    private readonly Dictionary<Entity, int> _slots = [];
    private SpatialEntry[] _entries = new SpatialEntry[FirstCapacity];
    private int[] _cellX = new int[FirstCapacity];
    private int[] _cellZ = new int[FirstCapacity];
    private int[] _bucketOf = new int[FirstCapacity];
    private int[] _next = new int[FirstCapacity];
    private int[] _previous = new int[FirstCapacity];
    private int[] _free = new int[FirstCapacity];
    private int[] _heads = new int[2 * FirstCapacity];
    private int _freeCount;
    private int _used;
    private int _wideHead = -1;
    private int _binned;
    private float _widestBinned;
    private VoxelCoord _frame;

    /// <summary>A grid of cells <paramref name="cellMetres"/> across, for the entities of <paramref name="world"/>.</summary>
    /// <param name="origin">The match's origin, which the positions set and asked are local to.</param>
    /// <param name="world">The world whose entities it holds: a query passes over those no longer alive.</param>
    /// <param name="cellMetres">
    /// How wide a cell is: about the radius of the queries asked most, and at least twice the radius of most entries.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The cell's width is not a number above 0.</exception>
    public SpatialGrid(WorldOrigin origin, World world, float cellMetres)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(world);
        if (!(cellMetres > 0) || !float.IsFinite(cellMetres))
        {
            throw new ArgumentOutOfRangeException(nameof(cellMetres), cellMetres, "A cell is a width above 0.");
        }

        _origin = origin;
        _world = world;
        CellMetres = cellMetres;
        _frame = origin.Centimetres;
        _heads.AsSpan().Fill(-1);
    }

    /// <summary>How wide a cell is, in metres.</summary>
    public float CellMetres { get; }

    /// <summary>How many entries it holds.</summary>
    public int Count => _slots.Count;

    /// <summary>
    /// Sets where <paramref name="entity"/> stands and what it is: its cylinder's feet (local metres), radius, and height,
    /// and the game's tags. Setting an entity again replaces its entry.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The entity is null, the feet are not finite, or the radius or height is below 0 or not finite.
    /// </exception>
    public void Set(Entity entity, Vector3 feet, float radius, float height, ulong tags)
    {
        if (entity == Entity.Null)
        {
            throw new ArgumentException("An entry needs an entity.", nameof(entity));
        }

        if (!float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
        {
            throw new ArgumentException($"An entry's feet are finite, not {feet}.", nameof(feet));
        }

        if (!(radius >= 0) || !float.IsFinite(radius) || !(height >= 0) || !float.IsFinite(height))
        {
            throw new ArgumentException($"An entry's radius ({radius}) and height ({height}) are 0 or more.");
        }

        if (_slots.TryGetValue(entity, out var slot))
        {
            Unlink(slot);
        }
        else
        {
            slot = Take();
            _slots.Add(entity, slot);
        }

        _entries[slot] = new SpatialEntry(entity, feet + Offset(), radius, height, tags);
        Link(slot);
    }

    /// <summary>Removes <paramref name="entity"/>'s entry; false when it has none.</summary>
    public bool Remove(Entity entity)
    {
        if (!_slots.Remove(entity, out var slot))
        {
            return false;
        }

        Unlink(slot);
        _entries[slot] = default;
        _bucketOf[slot] = Unused;
        _free[_freeCount++] = slot;
        return true;
    }

    /// <summary>Removes every entry, and keeps the entries set after it from the match's origin as it is now.</summary>
    public void Clear()
    {
        _slots.Clear();
        _heads.AsSpan().Fill(-1);
        _entries.AsSpan(0, _used).Clear();
        _bucketOf.AsSpan(0, _used).Fill(Unused);
        _used = 0;
        _freeCount = 0;
        _wideHead = -1;
        _binned = 0;
        _widestBinned = 0;
        _frame = _origin.Centimetres;
    }

    /// <summary>The entry of <paramref name="entity"/>, its feet local to the origin as it is now; false when it has none.</summary>
    public bool TryGet(Entity entity, out SpatialEntry entry)
    {
        if (_slots.TryGetValue(entity, out var slot))
        {
            var stored = _entries[slot];
            entry = stored with { Feet = stored.Feet - Offset() };
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>
    /// What a local position has to have added to be in the grid's own frame: the whole metres from the point the entries
    /// are kept from to the match's origin as it is now.
    /// </summary>
    private Vector3 Offset()
    {
        var origin = _origin.Centimetres;
        const int perMetre = VoxelConstants.CentimetresPerMetre;
        return new Vector3(
            (origin.X - _frame.X) / perMetre,
            (origin.Y - _frame.Y) / perMetre,
            (origin.Z - _frame.Z) / perMetre
        );
    }

    private int Cell(float metres) => (int)MathF.Floor(metres / CellMetres);

    private int Bucket(int cellX, int cellZ) =>
        (int)((uint)(cellX * 73_856_093 ^ cellZ * 19_349_663) & (uint)(_heads.Length - 1));

    private int Take()
    {
        if (_freeCount > 0)
        {
            return _free[--_freeCount];
        }

        if (_used == _entries.Length)
        {
            Grow();
        }

        return _used++;
    }

    /// <summary>Puts the slot's entry in its cell's bucket, or on the wide list when it is wider than half a cell.</summary>
    private void Link(int slot)
    {
        ref readonly var entry = ref _entries[slot];
        if (entry.Radius > CellMetres / 2)
        {
            _bucketOf[slot] = Wide;
            Push(ref _wideHead, slot);
            return;
        }

        var cellX = Cell(entry.Feet.X);
        var cellZ = Cell(entry.Feet.Z);
        var bucket = Bucket(cellX, cellZ);
        _cellX[slot] = cellX;
        _cellZ[slot] = cellZ;
        _bucketOf[slot] = bucket;
        Push(ref _heads[bucket], slot);
        _binned++;
        _widestBinned = MathF.Max(_widestBinned, entry.Radius);
    }

    private void Push(ref int head, int slot)
    {
        _previous[slot] = -1;
        _next[slot] = head;
        if (head >= 0)
        {
            _previous[head] = slot;
        }

        head = slot;
    }

    private void Unlink(int slot)
    {
        var bucket = _bucketOf[slot];
        ref var head = ref bucket == Wide ? ref _wideHead : ref _heads[bucket];
        var previous = _previous[slot];
        var next = _next[slot];
        if (previous >= 0)
        {
            _next[previous] = next;
        }
        else
        {
            head = next;
        }

        if (next >= 0)
        {
            _previous[next] = previous;
        }

        if (bucket != Wide)
        {
            _binned--;
        }
    }

    /// <summary>Doubles the room for entries and the buckets, and puts every binned entry in its new bucket.</summary>
    private void Grow()
    {
        var capacity = _entries.Length * 2;
        Array.Resize(ref _entries, capacity);
        Array.Resize(ref _cellX, capacity);
        Array.Resize(ref _cellZ, capacity);
        Array.Resize(ref _bucketOf, capacity);
        Array.Resize(ref _next, capacity);
        Array.Resize(ref _previous, capacity);
        Array.Resize(ref _free, capacity);
        _heads = new int[2 * capacity];
        _heads.AsSpan().Fill(-1);
        for (var slot = 0; slot < _used; slot++)
        {
            if (_bucketOf[slot] < 0)
            {
                continue;
            }

            var bucket = Bucket(_cellX[slot], _cellZ[slot]);
            _bucketOf[slot] = bucket;
            Push(ref _heads[bucket], slot);
        }
    }
}
