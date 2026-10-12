using System.Runtime.InteropServices;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Objects;

/// <summary>
/// The objects a match's world places near where it is loaded (scoped, with voxel terrain): its generator's placer
/// (<see cref="IChunkGenerator.Objects"/>) asked for areas a level-K column wide (K: <c>Engine:Objects:HandOverLevel</c>),
/// each kept while any of its columns of a level below K is resident (the levels drawn finer than the objects' far
/// copies, and level 0, which characters and bodies meet, round every streaming centre). It follows the world's change
/// list each loop iteration (<see cref="Update"/>, after the world's), fills at most <see cref="FillsPerUpdate"/> areas
/// an update, those nearest a streaming centre first, and answers which objects may touch a box (<see cref="Near"/>).
/// The placements are absolute, so a move of the origin changes nothing. Without a placer it keeps nothing. With a
/// hand-over level at or past the world's levels every streamed level counts. Main thread; allocates only as its tables
/// grow.
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

    private readonly Dictionary<(int X, int Z), Area> _areas = [];
    private readonly Stack<Area> _spare = new();
    private readonly List<Area> _waiting = [];
    private readonly List<ColumnKey> _resident = [];
    private readonly List<Area> _gone = [];
    private readonly VoxelChange[] _changes = new VoxelChange[256];
    private IObjectPlacer? _placer;
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

    /// <summary>Starts following the world's columns, those resident already among them. At the match's start, after the world's.</summary>
    public void Initialize()
    {
        _placer = generator.Objects;
        _level = options.Value.Objects.HandOverLevel;
        _areaCentimetres = (long)VoxelConstants.ChunkSize * world.Grid.Edge(0) << _level;
        if (_placer is null)
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
    /// box is not filled yet (or not wanted), so some of its objects may be missing; true without a placer, which places
    /// none.
    /// </summary>
    public bool Near(int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (_placer is null)
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

                foreach (ref readonly var placement in CollectionsMarshal.AsSpan(area.Objects))
                {
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
        }

        return whole;
    }

    /// <summary>
    /// True when every object that may reach into the box from (<paramref name="minX"/>, <paramref name="minZ"/>) to
    /// (<paramref name="maxX"/>, <paramref name="maxZ"/>), absolute centimetres, inclusive, is known: the areas within the
    /// widest kind's reach of it, inside the map, are filled (<see cref="Near"/> would return true). True without a placer.
    /// Allocates nothing.
    /// </summary>
    public bool IsKnown(long minX, long maxX, long minZ, long maxZ)
    {
        if (_placer is null)
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
        area.Filled = false;
        _spare.Push(area);
        Version++;
    }

    private void Fill(Area area)
    {
        var x0 = area.X * _areaCentimetres;
        var z0 = area.Z * _areaCentimetres;
        _placer!.Find(
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
                throw new InvalidOperationException(
                    $"The world's placer gave object {bad.Id.Value} kind {bad.Kind.Value} (of {kinds.Count}), heading {bad.Heading}, and anchor {bad.Anchor} for the area from ({x0}, {z0}) {_areaCentimetres} cm wide."
                );
            }
        }

        area.Filled = true;
        Count += area.Objects.Count;
        Version++;
        Listener?.Filled(area.Objects);
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

    /// <summary>One kept area: a level-K column's square, the resident columns below K in it, and its objects once filled.</summary>
    internal sealed class Area
    {
        public int X;
        public int Z;
        public int Columns;
        public bool Filled;
        public readonly List<ObjectPlacement> Objects = [];
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
