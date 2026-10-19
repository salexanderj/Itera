using System.Runtime.InteropServices;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Objects;

/// <summary>
/// The objects a match's world places near where it is loaded (scoped, with voxel terrain): its generator's placer
/// (<see cref="IChunkGenerator.Objects"/>), and the objects placed and removed during play by the log's edits
/// (<see cref="TryPlace"/>, <see cref="TryRemove"/>: <see cref="VoxelEditOperation.PlaceObject"/>,
/// <see cref="VoxelEditOperation.RemoveObject"/>), in areas a level-K column wide (K: <c>Engine:Objects:HandOverLevel</c>),
/// each kept while any of its columns of a level below K is resident (the levels drawn finer than the objects' far
/// copies, and level 0, which characters and bodies meet, round every streaming centre). It follows the world's change
/// list each loop iteration (<see cref="Update"/>, after the world's), fills at most <see cref="FillsPerUpdate"/> areas
/// an update, those nearest a streaming centre first, and answers which objects may touch a box (<see cref="Near"/>).
/// The placements are absolute, so a move of the origin changes nothing. Without a placer and with no kinds registered it
/// keeps nothing. With a hand-over level at or past the world's levels every streamed level counts. Main thread;
/// allocates only as its tables grow.
/// </summary>
public sealed partial class ObjectWorld(
    VoxelWorld world,
    VoxelObjectKinds kinds,
    IChunkGenerator generator,
    IOptions<EngineOptions> options,
    ILogger<ObjectWorld> logger
)
{
    /// <summary>The most areas filled from the placer in one update.</summary>
    public const int FillsPerUpdate = 8;

    /// <summary>
    /// The width of the cells an area lists its objects by for <see cref="Near"/>, in centimetres: a query reads the
    /// cells its box meets, not the whole area (512 m wide on 1 m voxels, where it read every block a game placed).
    /// </summary>
    public const int IndexCellCentimetres = 400;

    private readonly Dictionary<(int X, int Z), Area> _areas = [];
    private readonly Stack<Area> _spare = new();
    private readonly List<Area> _waiting = [];
    private readonly List<ColumnKey> _resident = [];
    private readonly List<Area> _gone = [];
    private readonly VoxelChange[] _changes = new VoxelChange[256];
    private readonly List<VoxelEdit> _logged = [];
    private readonly List<int> _candidates = [];
    private int[] _cellCursor = [];
    private int _query;
    private IObjectPlacer? _placer;
    private bool _enabled;
    private VoxelChangeCursor? _cursor;
    private int _level;
    private long _areaCentimetres;

    /// <summary>
    /// The level whose columns the areas are as wide as, from which objects are drawn as copies in the world's voxels.
    /// </summary>
    public int HandOverLevel => _level;

    /// <summary>The width of an area, in centimetres: a level-<see cref="HandOverLevel"/> column's.</summary>
    public long AreaCentimetres => _areaCentimetres;

    /// <summary>True when the world's generator places objects.</summary>
    public bool HasPlacer => _placer is not null;

    /// <summary>
    /// True when the world has objects to keep: its generator places some, or the run registered kinds a game may place
    /// during play. Known before <see cref="Initialize"/>.
    /// </summary>
    public bool IsEnabled => generator.Objects is not null || kinds.Count > 0;

    /// <summary>The world's placer (<see cref="IChunkGenerator.Objects"/>), or null; known before <see cref="Initialize"/>.</summary>
    internal IObjectPlacer? Placer => generator.Objects;

    /// <summary>The run's object kinds.</summary>
    internal VoxelObjectKinds Kinds => kinds;

    /// <summary>
    /// The box, in absolute centimetres across x and z (half open), whose objects the areas keep: the map's areas, each a
    /// level-<c>Engine:Objects:HandOverLevel</c> column wide (an area past the map's edge holds no column, so is never
    /// kept, and its objects never placed). Known before <see cref="Initialize"/>; any thread.
    /// </summary>
    internal (long MinX, long EndX, long MinZ, long EndZ) KeptBox()
    {
        var grid = world.Grid;
        var area = (long)VoxelConstants.ChunkSize * grid.Edge(0) << options.Value.Objects.HandOverLevel;
        var first = Maths.DeterministicMath.FloorDivide(
            (long)grid.MinChunkXZ * VoxelConstants.ChunkSize * grid.Edge(0),
            area
        );
        var last = Maths.DeterministicMath.FloorDivide(
            ((long)grid.MaxChunkXZ + 1) * VoxelConstants.ChunkSize * grid.Edge(0) - 1,
            area
        );
        return (first * area, (last + 1) * area, first * area, (last + 1) * area);
    }

    /// <summary>Areas kept, filled or waiting.</summary>
    public int AreaCount => _areas.Count;

    /// <summary>Areas kept and not filled yet.</summary>
    public int WaitingAreas => _waiting.Count;

    /// <summary>Objects in the filled areas.</summary>
    public int Count { get; private set; }

    /// <summary>A number that changes whenever an area is filled or goes: what is near a place may have changed.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// A number that changes whenever the objects kept change: an area filled or gone (<see cref="Version"/>), or an
    /// object placed or removed in a filled area (which <c>ObjectDamage.ReadChanges</c> also names, with its box).
    /// </summary>
    public int SetVersion { get; private set; }

    /// <summary>Starts following the world's columns, those resident already among them. At the match's start, after the world's.</summary>
    public void Initialize()
    {
        _placer = generator.Objects;
        _enabled = IsEnabled;
        _level = options.Value.Objects.HandOverLevel;
        _areaCentimetres = (long)VoxelConstants.ChunkSize * world.Grid.Edge(0) << _level;
        if (!_enabled)
        {
            return;
        }

        _cursor = world.CreateChangeCursor();
        Resync();
    }

    /// <summary>
    /// Reads the world's changes: an area becomes wanted with its first resident column below the hand-over level and goes
    /// with its last; then fills up to <see cref="FillsPerUpdate"/> waiting areas. Each loop iteration, after the world's
    /// update.
    /// </summary>
    public void Update()
    {
        if (_cursor is null)
        {
            return;
        }

        int read;
        while ((read = world.ReadChanges(_cursor, _changes)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                ref readonly var change = ref _changes[i];
                if (change.Kind == VoxelChangeKind.ColumnLoaded)
                {
                    Tally(change.Column, 1);
                }
                else if (change.Kind == VoxelChangeKind.ColumnUnloaded)
                {
                    Tally(change.Column, -1);
                }
            }
        }

        if (_cursor.NeedsResync)
        {
            Resync();
        }

        for (var fills = 0; fills < FillsPerUpdate && _waiting.Count > 0; fills++)
        {
            var nearest = Nearest();
            var area = _waiting[nearest];
            _waiting.RemoveAt(nearest);
            Fill(area);
        }
    }

    // The waiting area whose middle lies nearest a streaming centre (the first waiting with no centre).
    private int Nearest()
    {
        var centres = world.Centers;
        if (centres.IsEmpty)
        {
            return 0;
        }

        var origin = world.Origin.Centimetres;
        var best = 0;
        var bestSquared = double.MaxValue;
        for (var i = 0; i < _waiting.Count; i++)
        {
            var area = _waiting[i];
            var x = (area.X + 0.5) * _areaCentimetres - origin.X;
            var z = (area.Z + 0.5) * _areaCentimetres - origin.Z;
            foreach (var centre in centres)
            {
                double dx = x - centre.X * VoxelConstants.CentimetresPerMetre,
                    dz = z - centre.Z * VoxelConstants.CentimetresPerMetre;
                var squared = dx * dx + dz * dz;
                if (squared < bestSquared)
                {
                    bestSquared = squared;
                    best = i;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Adds to <paramref name="found"/> every object of the filled areas that may reach into the box from
    /// (<paramref name="minX"/>, <paramref name="minZ"/>) to (<paramref name="maxX"/>, <paramref name="maxZ"/>), absolute
    /// centimetres, inclusive: those whose anchor lies within their kind's reach of it. Returns false when an area under the
    /// box is not filled yet (or not wanted), so some of its objects may be missing; true for a world with no objects.
    /// </summary>
    public bool Near(int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (!_enabled)
        {
            return true;
        }

        var (x0, x1, z0, z1) = AreasRound(minX, maxX, minZ, maxZ);
        var whole = true;
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (!_areas.TryGetValue(((int)x, (int)z), out var area) || !area.Filled)
                {
                    whole = false;
                    continue;
                }

                Gather(area, minX, maxX, minZ, maxZ, found);
            }
        }

        return whole;
    }

    /// <summary>
    /// Adds the area's objects whose kind reaches into the box to <paramref name="found"/>, in the area's order: those its
    /// index lists in the cells the box meets, each once, then each tested by its kind's reach.
    /// </summary>
    private void Gather(Area area, int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found)
    {
        var objects = CollectionsMarshal.AsSpan(area.Objects);
        if (objects.IsEmpty)
        {
            return;
        }

        if (!area.Indexed)
        {
            Index(area);
        }

        if (++_query == int.MaxValue)
        {
            foreach (var kept in _areas.Values)
            {
                Array.Clear(kept.Seen);
            }

            _query = 1;
        }

        var (x0, x1, z0, z1) = CellsOf(area, minX, maxX, minZ, maxZ);
        _candidates.Clear();
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var cell = z * area.CellsAcross + x;
                for (var entry = area.CellStarts[cell]; entry < area.CellStarts[cell + 1]; entry++)
                {
                    var index = area.CellEntries[entry];
                    if (area.Seen[index] != _query)
                    {
                        area.Seen[index] = _query;
                        _candidates.Add(index);
                    }
                }
            }
        }

        var candidates = CollectionsMarshal.AsSpan(_candidates);
        candidates.Sort();
        foreach (var index in candidates)
        {
            ref readonly var placement = ref objects[index];
            var kindReach = kinds[placement.Kind].ReachCentimetres;
            if (
                placement.Anchor.X + kindReach >= minX
                && placement.Anchor.X - kindReach <= maxX
                && placement.Anchor.Z + kindReach >= minZ
                && placement.Anchor.Z - kindReach <= maxZ
            )
            {
                found.Add(placement);
            }
        }
    }

    /// <summary>
    /// Lists each of the area's objects, by its index in the area's list, in every cell its kind's reach meets: counted
    /// per cell, then written in place (a cell's entries from <c>CellStarts[cell]</c> to <c>CellStarts[cell + 1]</c>).
    /// A reach past the area's side is listed in its edge cells, where a box past that side reads too.
    /// </summary>
    private void Index(Area area)
    {
        var across = (int)((_areaCentimetres + IndexCellCentimetres - 1) / IndexCellCentimetres);
        var cells = across * across;
        if (area.CellStarts.Length != cells + 1)
        {
            area.CellStarts = new int[cells + 1];
        }
        else
        {
            Array.Clear(area.CellStarts);
        }

        if (_cellCursor.Length < cells)
        {
            _cellCursor = new int[cells];
        }

        area.CellsAcross = across;
        var objects = CollectionsMarshal.AsSpan(area.Objects);
        if (area.Seen.Length < objects.Length)
        {
            area.Seen = new int[Math.Max(objects.Length, 2 * area.Seen.Length)];
        }
        else
        {
            // Marks of queries before the query count last wrapped (an area kept spare then) mean nothing now.
            Array.Clear(area.Seen);
        }

        var entries = 0;
        foreach (ref readonly var placement in objects)
        {
            var (x0, x1, z0, z1) = CellsOf(area, in placement);
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    area.CellStarts[z * across + x + 1]++;
                }
            }

            entries += (x1 - x0 + 1) * (z1 - z0 + 1);
        }

        for (var cell = 1; cell <= cells; cell++)
        {
            area.CellStarts[cell] += area.CellStarts[cell - 1];
        }

        if (area.CellEntries.Length < entries)
        {
            area.CellEntries = new int[Math.Max(entries, 2 * area.CellEntries.Length)];
        }

        Array.Copy(area.CellStarts, _cellCursor, cells);
        for (var index = 0; index < objects.Length; index++)
        {
            var (x0, x1, z0, z1) = CellsOf(area, in objects[index]);
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    area.CellEntries[_cellCursor[z * across + x]++] = index;
                }
            }
        }

        area.Indexed = true;
    }

    /// <summary>The cells of an area a box meets (absolute centimetres, inclusive), its edge cells for a box past a side.</summary>
    private (int X0, int X1, int Z0, int Z1) CellsOf(Area area, long minX, long maxX, long minZ, long maxZ)
    {
        long x0 = area.X * _areaCentimetres,
            z0 = area.Z * _areaCentimetres;
        var last = area.CellsAcross - 1;
        return (CellAt(minX - x0, last), CellAt(maxX - x0, last), CellAt(minZ - z0, last), CellAt(maxZ - z0, last));
    }

    /// <summary>The cells of an area an object's kind's reach meets round its anchor.</summary>
    private (int X0, int X1, int Z0, int Z1) CellsOf(Area area, in ObjectPlacement placement)
    {
        var reach = kinds[placement.Kind].ReachCentimetres;
        var anchor = placement.Anchor;
        return CellsOf(
            area,
            (long)anchor.X - reach,
            (long)anchor.X + reach,
            (long)anchor.Z - reach,
            (long)anchor.Z + reach
        );
    }

    /// <summary>The cell along one side holding a place <paramref name="offset"/> centimetres in from the area's low side.</summary>
    private static int CellAt(long offset, int last) =>
        (int)Math.Clamp(Maths.DeterministicMath.FloorDivide(offset, IndexCellCentimetres), 0, last);

    /// <summary>
    /// True when every object that may reach into the box from (<paramref name="minX"/>, <paramref name="minZ"/>) to
    /// (<paramref name="maxX"/>, <paramref name="maxZ"/>), absolute centimetres, inclusive, is known: the areas within the
    /// widest kind's reach of it, inside the map, are filled (<see cref="Near"/> would return true). True for a world with
    /// no objects. Allocates nothing.
    /// </summary>
    public bool IsKnown(long minX, long maxX, long minZ, long maxZ)
    {
        if (!_enabled)
        {
            return true;
        }

        var (x0, x1, z0, z1) = AreasRound(minX, maxX, minZ, maxZ);
        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (!_areas.TryGetValue(((int)x, (int)z), out var area) || !area.Filled)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The areas within the widest kind's reach of a box (absolute centimetres, inclusive), inside the map's: an area
    /// past the map's edge holds no column, so it is never kept.
    /// </summary>
    private (long X0, long X1, long Z0, long Z1) AreasRound(long minX, long maxX, long minZ, long maxZ)
    {
        var reach = kinds.MostReachCentimetres;
        var grid = world.Grid;
        var first = Maths.DeterministicMath.FloorDivide(
            (long)grid.MinChunkXZ * VoxelConstants.ChunkSize * grid.Edge(0),
            _areaCentimetres
        );
        var last = Maths.DeterministicMath.FloorDivide(
            ((long)grid.MaxChunkXZ + 1) * VoxelConstants.ChunkSize * grid.Edge(0) - 1,
            _areaCentimetres
        );
        return (
            Math.Max(first, Maths.DeterministicMath.FloorDivide(minX - reach, _areaCentimetres)),
            Math.Min(last, Maths.DeterministicMath.FloorDivide(maxX + reach, _areaCentimetres)),
            Math.Max(first, Maths.DeterministicMath.FloorDivide(minZ - reach, _areaCentimetres)),
            Math.Min(last, Maths.DeterministicMath.FloorDivide(maxZ + reach, _areaCentimetres))
        );
    }

    /// <summary>The area holding the absolute centimetre (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public (int X, int Z) AreaAt(long x, long z) =>
        (
            (int)Maths.DeterministicMath.FloorDivide(x, _areaCentimetres),
            (int)Maths.DeterministicMath.FloorDivide(z, _areaCentimetres)
        );

    /// <summary>True when the area holding (<paramref name="x"/>, <paramref name="z"/>) is kept, filled or waiting.</summary>
    public bool IsKeptAt(long x, long z) => _areas.ContainsKey(AreaAt(x, z));

    /// <summary>True when the area holding (<paramref name="x"/>, <paramref name="z"/>) is kept and filled.</summary>
    public bool IsFilledAt(long x, long z) => _areas.TryGetValue(AreaAt(x, z), out var area) && area.Filled;

    // A column below the hand-over level counts towards the area it lies in.
    private void Tally(ColumnKey column, int change)
    {
        if (column.Level >= _level)
        {
            return;
        }

        var shift = _level - column.Level;
        var key = (column.X >> shift, column.Z >> shift);
        if (!_areas.TryGetValue(key, out var area))
        {
            if (change < 0)
            {
                return;
            }

            area = _spare.Count > 0 ? _spare.Pop() : new Area();
            area.X = key.Item1;
            area.Z = key.Item2;
            area.Columns = 0;
            area.Filled = false;
            _areas.Add(key, area);
            _waiting.Add(area);
        }

        area.Columns += change;
        if (area.Columns <= 0)
        {
            Drop(area);
        }
    }

    private void Drop(Area area)
    {
        _areas.Remove((area.X, area.Z));
        if (area.Filled)
        {
            Listener?.Dropping(area.Objects);
            Count -= area.Objects.Count;
        }
        else
        {
            _waiting.Remove(area);
        }

        area.Objects.Clear();
        area.Indexed = false;
        area.Filled = false;
        _spare.Push(area);
        Version++;
        SetVersion++;
    }

    private void Fill(Area area)
    {
        var x0 = area.X * _areaCentimetres;
        var z0 = area.Z * _areaCentimetres;
        _placer?.Find(
            world.Seed,
            (int)x0,
            (int)(x0 + _areaCentimetres),
            (int)z0,
            (int)(z0 + _areaCentimetres),
            area.Objects
        );
        foreach (ref readonly var placement in CollectionsMarshal.AsSpan(area.Objects))
        {
            var anchor = placement.Anchor;
            if (
                placement.Kind.Value >= kinds.Count
                || (uint)placement.Heading >= ObjectPlacement.HeadingSteps
                || anchor.X < x0
                || anchor.X >= x0 + _areaCentimetres
                || anchor.Z < z0
                || anchor.Z >= z0 + _areaCentimetres
            )
            {
                var bad = placement;
                area.Objects.Clear();
                area.Indexed = false;
                throw new InvalidOperationException(
                    $"The world's placer gave object {bad.Id.Value} kind {bad.Kind.Value} (of {kinds.Count}), heading {bad.Heading}, and anchor {bad.Anchor} for the area from ({x0}, {z0}) {_areaCentimetres} cm wide."
                );
            }
        }

        AddLogged(area, x0, z0);
        area.Indexed = false;
        area.Filled = true;
        Count += area.Objects.Count;
        Version++;
        SetVersion++;
        Listener?.Filled(area.Objects);
    }

    /// <summary>
    /// The area's objects placed and removed during play since the last reset, in the log's order: each placing whose
    /// anchor lies in the area adds its object after the generator's, each removal takes the object it names (the
    /// generator's too). Edits the world took were checked against the kinds.
    /// </summary>
    private void AddLogged(Area area, long x0, long z0)
    {
        _logged.Clear();
        world.CollectEdits(
            new VoxelCoord((int)x0, 0, (int)z0),
            new VoxelCoord((int)(x0 + _areaCentimetres), 0, (int)(z0 + _areaCentimetres)),
            _logged
        );
        foreach (var edit in _logged)
        {
            if (!edit.IsObjectPlacing)
            {
                continue;
            }

            var placement = edit.PlacementOf();
            if (AreaAt(placement.Anchor.X, placement.Anchor.Z) != (area.X, area.Z))
            {
                continue;
            }

            if (edit.Operation == VoxelEditOperation.PlaceObject)
            {
                area.Objects.Add(placement);
            }
            else
            {
                var at = IndexOf(area.Objects, placement.Id);
                if (at >= 0)
                {
                    area.Objects.RemoveAt(at);
                }
            }
        }
    }

    /// <summary>The index of the object <paramref name="id"/> in an area's list, or -1.</summary>
    private static int IndexOf(List<ObjectPlacement> objects, ObjectId id)
    {
        var span = CollectionsMarshal.AsSpan(objects);
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Places an object of <paramref name="kind"/> during play with its anchor at <paramref name="anchor"/> (absolute
    /// centimetres) turned by <paramref name="heading"/>: logs its <see cref="VoxelEditOperation.PlaceObject"/> edit,
    /// which every peer replays (a session's clients as they follow the host, a save's resumption, a joiner), its id
    /// from its place, kind, heading, and edit (<see cref="ObjectId.Placed"/>). False, with nothing changed, on a world
    /// that follows a host, where <see cref="CanPlaceAt"/> says no, for a heading past a turn, or for an edit the world
    /// refuses. Its area need not be filled: it holds the object once it is. Main thread.
    /// </summary>
    public bool TryPlace(
        VoxelObjectKindId kind,
        VoxelCoord anchor,
        int heading,
        int maker,
        out ObjectPlacement placement
    )
    {
        placement = default;
        if (world.FollowsHost || (uint)heading >= ObjectPlacement.HeadingSteps || !CanPlaceAt(kind, anchor))
        {
            return false;
        }

        var sequence = world.EditCount + 1;
        var id = ObjectId.Placed(anchor, kind, heading, sequence);
        for (var attempt = 1; IsLive(id, anchor); attempt++)
        {
            id = ObjectId.Placed(anchor, kind, heading, sequence, attempt);
        }

        var candidate = new ObjectPlacement(id, kind, anchor, heading, sequence);
        var k = kinds[kind];
        if (
            !world.TryApply(
                VoxelEdit.PlaceObject(in candidate, k.ReachCentimetres, k.BottomCentimetres, k.TopCentimetres),
                maker,
                out var applied
            )
        )
        {
            return false;
        }

        placement = candidate with { Since = applied };
        return true;
    }

    /// <summary>
    /// True when an object of <paramref name="kind"/> may stand with its anchor at <paramref name="anchor"/> (absolute
    /// centimetres), as <see cref="TryPlace"/> asks on the world's owner (a client may ask it too, for a preview): a
    /// kind of the run's not copied into the world's coarse levels (<see cref="CanPlaceOrRemove"/>), an anchor within
    /// the map, and the object's box where the log indexes it under the anchor's area, so the area's fill finds it again
    /// (one wholly above or below the map's height would be held while the area stays filled and lost at its next fill,
    /// a joiner's, or a save's).
    /// </summary>
    public bool CanPlaceAt(VoxelObjectKindId kind, VoxelCoord anchor)
    {
        if (!_enabled || !CanPlaceOrRemove(kind))
        {
            return false;
        }

        var (minX, endX, minZ, endZ) = KeptBox();
        if (anchor.X < minX || anchor.X >= endX || anchor.Z < minZ || anchor.Z >= endZ)
        {
            return false;
        }

        var k = kinds[kind];
        var box = VoxelEdit.PlaceObject(
            new ObjectPlacement(default, kind, anchor, 0),
            k.ReachCentimetres,
            k.BottomCentimetres,
            k.TopCentimetres
        );
        var (areaX, areaZ) = AreaAt(anchor.X, anchor.Z);
        long x0 = (long)areaX * _areaCentimetres,
            z0 = (long)areaZ * _areaCentimetres;
        return world.IndexesUnder(
            in box,
            new VoxelCoord((int)x0, 0, (int)z0),
            new VoxelCoord((int)(x0 + _areaCentimetres), 0, (int)(z0 + _areaCentimetres))
        );
    }

    /// <summary>
    /// Removes a whole object during play, one the world's generator placed or one placed during play: logs its
    /// <see cref="VoxelEditOperation.RemoveObject"/> edit, which every peer replays. False, with nothing changed, on a
    /// world that follows a host, for a kind copied into the world's coarse levels, an object not in a filled area as
    /// <paramref name="placement"/> names it (its id, kind, anchor, and heading), or an edit the world refuses. Main
    /// thread.
    /// </summary>
    public bool TryRemove(in ObjectPlacement placement, int maker)
    {
        if (!_enabled || world.FollowsHost || !CanPlaceOrRemove(placement.Kind))
        {
            return false;
        }

        if (!_areas.TryGetValue(AreaAt(placement.Anchor.X, placement.Anchor.Z), out var area) || !area.Filled)
        {
            return false;
        }

        var at = IndexOf(area.Objects, placement.Id);
        if (at < 0)
        {
            return false;
        }

        var kept = area.Objects[at];
        if (kept.Kind != placement.Kind || kept.Anchor != placement.Anchor || kept.Heading != placement.Heading)
        {
            return false;
        }

        var k = kinds[placement.Kind];
        return world.TryApply(
            VoxelEdit.RemoveObject(in kept, k.ReachCentimetres, k.BottomCentimetres, k.TopCentimetres),
            maker,
            out _
        );
    }

    /// <summary>
    /// True when objects of <paramref name="kind"/> may be placed or removed by an edit: a kind of the run's that the
    /// world does not copy into its coarse levels (a copy comes from the generator alone), within an edit's box (its
    /// reach at most half of <see cref="VoxelConstants.MaxEditExtent"/>, its height at most all of it).
    /// </summary>
    public bool CanPlaceOrRemove(VoxelObjectKindId kind)
    {
        if (kind.Value >= kinds.Count)
        {
            return false;
        }

        var k = kinds[kind];
        return 2L * k.ReachCentimetres <= VoxelConstants.MaxEditExtent
            && (long)k.TopCentimetres - k.BottomCentimetres <= VoxelConstants.MaxEditExtent
            && world.Copies?.IsCopied(kind) != true;
    }

    /// <summary>True when the filled area holding <paramref name="anchor"/> keeps an object <paramref name="id"/>.</summary>
    private bool IsLive(ObjectId id, VoxelCoord anchor) =>
        _areas.TryGetValue(AreaAt(anchor.X, anchor.Z), out var area) && area.Filled && IndexOf(area.Objects, id) >= 0;

    /// <summary>
    /// After a reset (<see cref="VoxelEditOperation.Reset"/>): every filled area lets its objects go and fills again at
    /// once, from the generator and the log since the reset, which holds none of the placings and removals before it.
    /// Main thread, as the world applies the reset (after its log takes it).
    /// </summary>
    internal void RefillAll()
    {
        if (!_enabled)
        {
            return;
        }

        foreach (var area in _areas.Values)
        {
            if (!area.Filled)
            {
                continue;
            }

            Listener?.Dropping(area.Objects);
            Count -= area.Objects.Count;
            area.Objects.Clear();
            area.Indexed = false;
            area.Filled = false;
            Fill(area);
        }
    }

    /// <summary>
    /// Takes a logged placing or removal (<see cref="VoxelEdit.IsObjectPlacing"/>) into the filled area holding its
    /// anchor: the object added after the area's others, or the one it names taken out (<paramref name="placement"/> as
    /// kept). False where its area is not filled (its fill reads the edit from the log) or a removal names no object
    /// there. Main thread, as the world applies the edit.
    /// </summary>
    internal bool TakePlacing(in VoxelEdit edit, out ObjectPlacement placement)
    {
        placement = edit.PlacementOf();
        if (
            !_enabled
            || !_areas.TryGetValue(AreaAt(placement.Anchor.X, placement.Anchor.Z), out var area)
            || !area.Filled
        )
        {
            return false;
        }

        if (edit.Operation == VoxelEditOperation.PlaceObject)
        {
            area.Objects.Add(placement);
            Count++;
        }
        else
        {
            var at = IndexOf(area.Objects, placement.Id);
            if (at < 0)
            {
                return false;
            }

            placement = area.Objects[at];
            area.Objects.RemoveAt(at);
            Count--;
        }

        area.Indexed = false;
        SetVersion++;
        return true;
    }

    /// <summary>Hears of the objects as areas fill and drop (<c>ObjectDamage</c>), or null. Main thread.</summary>
    internal IObjectWorldListener? Listener { get; set; }

    // Counts every resident column again, after the change list lost some: the areas still wanted keep what they hold.
    private void Resync()
    {
        foreach (var area in _areas.Values)
        {
            area.Columns = 0;
        }

        _resident.Clear();
        world.CollectResidentColumns(_resident);
        foreach (var column in _resident)
        {
            Tally(column, 1);
        }

        _gone.Clear();
        foreach (var area in _areas.Values)
        {
            if (area.Columns == 0)
            {
                _gone.Add(area);
            }
        }

        foreach (var area in _gone)
        {
            Drop(area);
        }

        _cursor!.EndResync();
        LogResync(_areas.Count);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Objects: following {Areas} area(s) of resident columns afresh")]
    private partial void LogResync(int areas);

    /// <summary>The kept areas, filled or waiting, for the renderer to walk. Main thread.</summary>
    internal Dictionary<(int X, int Z), Area>.ValueCollection Areas => _areas.Values;

    /// <summary>
    /// One kept area: a level-K column's square, the resident columns below K in it, its objects once filled, and their
    /// index by cells (<see cref="IndexCellCentimetres"/>) for <see cref="Near"/>, made again at the first query after
    /// the objects change.
    /// </summary>
    internal sealed class Area
    {
        public int X;
        public int Z;
        public int Columns;
        public bool Filled;
        public readonly List<ObjectPlacement> Objects = [];

        /// <summary>True while the index lists <see cref="Objects"/> as they are.</summary>
        public bool Indexed;

        /// <summary>The cells along each side.</summary>
        public int CellsAcross;

        /// <summary>Where each cell's entries start in <see cref="CellEntries"/>, one more for the end.</summary>
        public int[] CellStarts = [];

        /// <summary>The objects' indices in <see cref="Objects"/>, cell after cell.</summary>
        public int[] CellEntries = [];

        /// <summary>Each object's last query, so a query takes an object listed in several cells once.</summary>
        public int[] Seen = [];
    }
}

/// <summary>Hears of the placed objects as their areas fill and drop (<see cref="ObjectWorld.Listener"/>). Main thread.</summary>
internal interface IObjectWorldListener
{
    /// <summary>An area's objects, just filled.</summary>
    void Filled(List<ObjectPlacement> objects);

    /// <summary>An area's objects, about to go.</summary>
    void Dropping(List<ObjectPlacement> objects);
}
