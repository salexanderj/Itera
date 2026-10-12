using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Objects;

/// <summary>
/// One change to a placed object's voxels (<see cref="ObjectDamage.ReadChanges"/>): the object, and the box its kind
/// reaches round its anchor (absolute centimetres across x and z, inclusive), whose collision readers read again.
/// </summary>
internal readonly record struct ObjectDamageChange(
    long Version,
    ObjectId Object,
    int MinX,
    int MaxX,
    int MinZ,
    int MaxZ
);

/// <summary>
/// What the world's edits do to the voxel objects it places (scoped, with voxel terrain). Each edit the world logs that
/// changes objects (<see cref="ObjectEditing"/>: removals by share, paints) is applied at once to the objects of the
/// filled areas it reaches (<see cref="ObjectWorld"/>), in the log's order, through the integer turn, so every peer gets
/// the same voxels; a clear with a maker counts what it takes from them for the game's
/// <see cref="VoxelWorld.Removals"/> first. An object an edit changes keeps a copy of its changed bricks over its kind's
/// voxels (<see cref="DamagedObject"/>), and its parts' models, made again once an update (<see cref="Update"/>) from
/// those voxels by the kind's rule. When an area fills, each of its objects replays the edits since the last reset that
/// reach it, so damage made before it was loaded, or before the match's start (a save, a joiner's download), shows the
/// same; when an area drops, its objects' copies go, and come back by replay. A reset drops every copy. At most
/// <c>Engine:Objects:MostDamaged</c> objects keep a copy at once: past it, the copy of the damaged object farthest from
/// every streaming centre goes (drawn far off from the world's coarse copy, which the same edits change), made again by
/// replay when it is among the nearest again, a few an update. Readers of objects' voxels read through
/// <see cref="Find"/>; collision keeps what it read until <see cref="ReadChanges"/> names the object. Main thread.
/// </summary>
public sealed partial class ObjectDamage : IObjectEdits, IObjectWorldListener, IDisposable
{
    /// <summary>The changes kept for readers; one that falls further behind reads every object again.</summary>
    public const int KeptChanges = 256;

    /// <summary>The dropped copies made again by replay in one update, nearest first.</summary>
    public const int RebuildsPerUpdate = 2;

    /// <summary>
    /// The damaged parts whose models are made again in one update; the rest wait for the next, the objects in the order
    /// they were changed.
    /// </summary>
    public const int PartsPerUpdate = 8;

    private readonly VoxelWorld _world;
    private readonly ObjectWorld _objects;
    private readonly VoxelObjectKinds _kinds;
    private readonly VoxelObjects _kindModels;
    private readonly VoxelModels _models;
    private readonly int _most;
    private readonly ILogger<ObjectDamage> _logger;

    private readonly Dictionary<ObjectId, DamagedObject> _damaged = [];
    private readonly Dictionary<ObjectId, ObjectPlacement> _dropped = [];
    private readonly List<ObjectPlacement> _near = [];
    private readonly List<VoxelEdit> _replay = [];
    private readonly Queue<DamagedObject> _changed = new();
    private readonly long[] _steps = new long[256];
    private readonly int[] _counts = new int[256];
    private readonly ObjectDamageChange[] _changes = new ObjectDamageChange[KeptChanges];
    private byte[] _levelZero = [];
    private byte[] _levelZeroDensities = [];
    private byte[] _level = [];
    private byte[] _levelDensities = [];
    private bool _capLogged;
    private bool _disposed;

    public ObjectDamage(
        VoxelWorld world,
        ObjectWorld objects,
        VoxelObjectKinds kinds,
        VoxelObjects kindModels,
        VoxelModels models,
        IOptions<EngineOptions> options,
        ILogger<ObjectDamage> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        _world = world;
        _objects = objects;
        _kinds = kinds;
        _kindModels = kindModels;
        _models = models;
        _most = options.Value.Objects.MostDamaged;
        _logger = logger;
    }

    /// <summary>The objects that keep a copy of their damage now.</summary>
    public int Count => _damaged.Count;

    /// <summary>The damaged objects of filled areas whose copies went past <c>Engine:Objects:MostDamaged</c>.</summary>
    public int DroppedCount => _dropped.Count;

    /// <summary>The changes to objects' voxels so far (<see cref="ReadChanges"/>).</summary>
    public long Version { get; private set; }

    /// <summary>True once following the world's edits and areas.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Follows the world's edits and the objects' areas. At the match's "objects" step, after the kinds' models and the
    /// objects' world; areas filled before (none in a match) are not replayed.
    /// </summary>
    public void Initialize()
    {
        _world.ObjectEdits = this;
        _objects.Listener = this;
        IsInitialized = true;
    }

    /// <summary>The damage of the object <paramref name="id"/>, or null while it keeps its kind's voxels (or no copy).</summary>
    internal DamagedObject? Find(ObjectId id) => _damaged.GetValueOrDefault(id);

    /// <summary>
    /// Copies the changes after <paramref name="after"/> into <paramref name="into"/> (from the oldest), returning how
    /// many; <paramref name="lost"/> is true when some went before they were read (read every object again then).
    /// </summary>
    internal int ReadChanges(long after, Span<ObjectDamageChange> into, out bool lost)
    {
        lost = Version - after > KeptChanges;
        var from = Math.Max(after, Version - KeptChanges);
        var count = (int)Math.Min(into.Length, Version - from);
        for (var i = 0; i < count; i++)
        {
            into[i] = _changes[(from + i) % KeptChanges];
        }

        return count;
    }

    /// <summary>
    /// The models of the object's parts to draw, each with its box's corner from the anchor: its damaged parts' own once
    /// on the GPU, its kind's for the others (and until then).
    /// </summary>
    public ReadOnlySpan<ObjectPartModel> PartsOf(in ObjectPlacement placement)
    {
        var parts = _kindModels.PartsOf(placement.Kind);
        if (!_damaged.TryGetValue(placement.Id, out var damaged))
        {
            return parts;
        }

        var drawn = damaged.Drawn ??= new ObjectPartModel[parts.Length];
        for (var p = 0; p < parts.Length; p++)
        {
            var own = damaged.Models[p];
            drawn[p] = own >= 0 && _models.IsUploaded(own) ? parts[p] with { Model = own } : parts[p];
        }

        return drawn;
    }

    void IObjectEdits.CountRemoved(in VoxelEdit edit, Span<long> removed)
    {
        if (!ObjectEditing.Changes(in edit))
        {
            return;
        }

        Array.Clear(_steps);
        Near(in edit);
        foreach (var placement in _near)
        {
            var damaged = _damaged.GetValueOrDefault(placement.Id);
            if (damaged is null && _dropped.ContainsKey(placement.Id))
            {
                continue;
            }

            if (IndexedFor(in edit, in placement))
            {
                ObjectEditing.Apply(in edit, in placement, _kinds[placement.Kind], damaged, false, _steps);
            }
        }

        // Density steps times cubic centimetres, to cubic centimetres, rounded once a material.
        for (var m = 0; m < 256; m++)
        {
            removed[m] += (_steps[m] + VoxelDensity.Full / 2) / VoxelDensity.Full;
        }
    }

    void IObjectEdits.Applied(in VoxelEdit edit)
    {
        if (edit.Operation == VoxelEditOperation.Reset)
        {
            Heal();
            return;
        }

        if (!ObjectEditing.Changes(in edit))
        {
            return;
        }

        Near(in edit);
        foreach (var placement in _near)
        {
            // As a replay finds it: only where the log indexes it under the object.
            if (IndexedFor(in edit, in placement))
            {
                Damage(in edit, in placement);
            }
        }
    }

    void IObjectWorldListener.Filled(List<ObjectPlacement> objects)
    {
        foreach (var placement in objects)
        {
            Replay(in placement);
        }
    }

    void IObjectWorldListener.Dropping(List<ObjectPlacement> objects)
    {
        foreach (var placement in objects)
        {
            _dropped.Remove(placement.Id);
            if (_damaged.Remove(placement.Id, out var damaged))
            {
                Free(damaged);
                Record(in placement);
            }
        }
    }

    /// <summary>
    /// Once a loop iteration, after the objects' world: makes again, nearest first, the dropped copies now among the
    /// nearest, and the models of the parts the changes since the last update reach.
    /// </summary>
    public void Update()
    {
        if (!IsInitialized)
        {
            return;
        }

        for (var i = 0; i < RebuildsPerUpdate && _dropped.Count > 0; i++)
        {
            var nearest = NearestDropped(out var distance);
            if (double.IsPositiveInfinity(distance))
            {
                // No streaming centre: none is nearer than another.
                break;
            }

            if (_damaged.Count >= _most)
            {
                var farthest = FarthestKept(out var far);
                if (farthest is null || far <= distance)
                {
                    break;
                }

                DropCopy(farthest);
            }

            _dropped.Remove(nearest.Id);
            Replay(in nearest);
        }

        var budget = PartsPerUpdate;
        while (budget > 0 && _changed.TryPeek(out var damaged))
        {
            if (!_damaged.TryGetValue(damaged.Placement.Id, out var kept) || !ReferenceEquals(kept, damaged))
            {
                // Its copy went since.
                _changed.Dequeue();
                damaged.Queued = false;
                continue;
            }

            damaged.MarkChangedParts();
            for (var p = 0; p < damaged.PartDirty.Length && budget > 0; p++)
            {
                if (damaged.PartDirty[p])
                {
                    damaged.PartDirty[p] = false;
                    MakePart(damaged, p);
                    budget--;
                }
            }

            if (Array.IndexOf(damaged.PartDirty, true) < 0)
            {
                _changed.Dequeue();
                damaged.Queued = false;
            }
        }
    }

    /// <summary>
    /// True when the log indexes the edit under the object's reach: what a replay of the object finds (an edit wholly past
    /// the map's sides, or above or below its height, is in none), so damage made live and replayed are the same.
    /// </summary>
    private bool IndexedFor(in VoxelEdit edit, in ObjectPlacement placement)
    {
        var reach = _kinds[placement.Kind].ReachCentimetres;
        var anchor = placement.Anchor;
        return _world.IndexesUnder(
            in edit,
            new VoxelCoord(anchor.X - reach, 0, anchor.Z - reach),
            new VoxelCoord(anchor.X + reach + 1, 0, anchor.Z + reach + 1)
        );
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ReferenceEquals(_world.ObjectEdits, this))
        {
            _world.ObjectEdits = null;
        }

        if (ReferenceEquals(_objects.Listener, this))
        {
            _objects.Listener = null;
        }

        // The models go with the match's voxel memory.
        _damaged.Clear();
        _dropped.Clear();
    }

    /// <summary>The objects of filled areas whose kind's reach meets the edit's bounds, into <see cref="_near"/>.</summary>
    private void Near(in VoxelEdit edit)
    {
        _near.Clear();
        edit.GetBounds(out var min, out var end);
        _objects.Near(min.X, end.X - 1, min.Z, end.Z - 1, _near);
    }

    /// <summary>Applies an edit to one object: through its copy, made when the edit first changes it.</summary>
    private void Damage(in VoxelEdit edit, in ObjectPlacement placement)
    {
        if (_dropped.ContainsKey(placement.Id))
        {
            // Its copy went: the edit is in the log, which a replay reads.
            return;
        }

        var kind = _kinds[placement.Kind];
        if (!_damaged.TryGetValue(placement.Id, out var damaged))
        {
            if (!ObjectEditing.Apply(in edit, in placement, kind, null, false, default))
            {
                return;
            }

            damaged = Make(in placement);
            if (damaged is null)
            {
                return;
            }
        }

        if (ObjectEditing.Apply(in edit, in placement, kind, damaged, true, default))
        {
            damaged.Touched();
            Record(in placement);
            if (!damaged.Queued)
            {
                damaged.Queued = true;
                _changed.Enqueue(damaged);
            }
        }
    }

    /// <summary>
    /// A copy for the object, making room past <c>Engine:Objects:MostDamaged</c> by dropping the farthest one's; null
    /// (the object noted as dropped) when it is itself the farthest.
    /// </summary>
    private DamagedObject? Make(in ObjectPlacement placement)
    {
        if (_damaged.Count >= _most)
        {
            if (!_capLogged)
            {
                _capLogged = true;
                LogCapReached(_most);
            }

            var farthest = FarthestKept(out var far);
            if (farthest is null || far <= DistanceOf(in placement))
            {
                _dropped[placement.Id] = placement;
                return null;
            }

            DropCopy(farthest);
        }

        var damaged = new DamagedObject(in placement, _kinds[placement.Kind]);
        _damaged.Add(placement.Id, damaged);
        return damaged;
    }

    /// <summary>Applies every edit since the last reset that reaches the object, in order: its copy, if any.</summary>
    private void Replay(in ObjectPlacement placement)
    {
        var kind = _kinds[placement.Kind];
        var reach = kind.ReachCentimetres;
        var anchor = placement.Anchor;
        _replay.Clear();
        _world.CollectEdits(
            new VoxelCoord(anchor.X - reach, 0, anchor.Z - reach),
            new VoxelCoord(anchor.X + reach + 1, 0, anchor.Z + reach + 1),
            _replay
        );
        foreach (var edit in _replay)
        {
            if (ObjectEditing.Changes(in edit) && Reaches(in edit, in placement, kind))
            {
                Damage(in edit, in placement);
            }
        }
    }

    /// <summary>True when the edit's bounds meet the object's reach round its anchor and its height.</summary>
    private static bool Reaches(in VoxelEdit edit, in ObjectPlacement placement, VoxelObjectKind kind)
    {
        edit.GetBounds(out var min, out var end);
        var anchor = placement.Anchor;
        var reach = kind.ReachCentimetres;
        return (long)end.X > anchor.X - reach
            && min.X <= (long)anchor.X + reach
            && (long)end.Z > anchor.Z - reach
            && min.Z <= (long)anchor.Z + reach
            && (long)end.Y > anchor.Y + kind.BottomCentimetres
            && min.Y < (long)anchor.Y + kind.TopCentimetres;
    }

    /// <summary>Drops every copy: after a reset, the objects are their kinds' again.</summary>
    private void Heal()
    {
        foreach (var damaged in _damaged.Values)
        {
            Free(damaged);
            Record(damaged.Placement);
        }

        _damaged.Clear();
        _dropped.Clear();
        _changed.Clear();
    }

    /// <summary>Drops one object's copy, keeping it noted as damaged, for a replay when it is near again.</summary>
    private void DropCopy(DamagedObject damaged)
    {
        _damaged.Remove(damaged.Placement.Id);
        _dropped[damaged.Placement.Id] = damaged.Placement;
        Free(damaged);
        Record(damaged.Placement);
    }

    private void Free(DamagedObject damaged)
    {
        for (var p = 0; p < damaged.Models.Length; p++)
        {
            if (damaged.Models[p] >= 0)
            {
                _models.RemoveAndFree(damaged.Models[p]);
                damaged.Models[p] = -1;
            }
        }
    }

    /// <summary>Notes a change to the object's voxels for the readers that keep what they read.</summary>
    private void Record(in ObjectPlacement placement)
    {
        var reach = _kinds[placement.Kind].ReachCentimetres;
        var anchor = placement.Anchor;
        _changes[Version % KeptChanges] = new ObjectDamageChange(
            Version + 1,
            placement.Id,
            anchor.X - reach,
            anchor.X + reach,
            anchor.Z - reach,
            anchor.Z + reach
        );
        Version++;
    }

    /// <summary>The kept copy farthest from every streaming centre, with its distance; null with none.</summary>
    private DamagedObject? FarthestKept(out double distance)
    {
        DamagedObject? farthest = null;
        distance = double.NegativeInfinity;
        foreach (var damaged in _damaged.Values)
        {
            var d = DistanceOf(damaged.Placement);
            if (d > distance)
            {
                distance = d;
                farthest = damaged;
            }
        }

        return farthest;
    }

    /// <summary>The dropped damaged object nearest a streaming centre, with its distance.</summary>
    private ObjectPlacement NearestDropped(out double distance)
    {
        var nearest = default(ObjectPlacement);
        distance = double.PositiveInfinity;
        foreach (var placement in _dropped.Values)
        {
            var d = DistanceOf(in placement);
            if (d < distance)
            {
                distance = d;
                nearest = placement;
            }
        }

        return nearest;
    }

    /// <summary>
    /// The squared distance across x and z, in square metres, from the object's anchor to the nearest streaming centre:
    /// only compared.
    /// </summary>
    private double DistanceOf(in ObjectPlacement placement)
    {
        var at = _world.Origin.ToLocal(placement.Anchor);
        var best = double.PositiveInfinity;
        foreach (ref readonly var centre in _world.StreamingCentres)
        {
            double dx = centre.Position.X - at.X,
                dz = centre.Position.Z - at.Z;
            best = Math.Min(best, dx * dx + dz * dz);
        }

        return best;
    }

    /// <summary>
    /// Makes part <paramref name="index"/>'s model from the object's voxels: its kind's levels with the changed bricks
    /// over level 0 and the coarse voxels over them made again by the kind's rule; a model of its own the first time,
    /// owning what its kind's owns, else its trees replaced.
    /// </summary>
    private void MakePart(DamagedObject damaged, int index)
    {
        const int edge = VoxelConstants.ChunkSize;
        const int border = ObjectPart.BorderVoxels;
        const int brick = VoxelConstants.BrickSize;
        if (_levelZero.Length == 0)
        {
            _levelZero = new byte[VoxelConstants.ChunkVoxels];
            _levelZeroDensities = new byte[VoxelConstants.ChunkVoxels];
            _level = new byte[VoxelConstants.ChunkVoxels];
            _levelDensities = new byte[VoxelConstants.ChunkVoxels];
        }

        var kind = damaged.Kind;
        var part = kind.Parts[index];
        var corner = new VoxelCoord(part.Offset.X - border, part.Offset.Y - border, part.Offset.Z - border);
        Span<ModelTree> trees = stackalloc ModelTree[VoxelModels.MaxLevels];

        // Level 0: the kind's, with every changed brick in the part's box over it.
        part.Decode(0, _levelZero, _levelZeroDensities);
        foreach (var key in damaged.Bricks)
        {
            int bx = ((key & 255) << 3) - corner.X,
                by = (((key >> 8) & 255) << 3) - corner.Y,
                bz = ((key >> 16) << 3) - corner.Z;
            if (bx < 0 || by < 0 || bz < 0 || bx >= part.Size.X || by >= part.Size.Y || bz >= part.Size.Z)
            {
                continue;
            }

            var voxels = damaged.BrickVoxelsOf(key);
            for (var j = 0; j < brick * brick * brick; j++)
            {
                var i = bx + (j & 7) + edge * (by + ((j >> 3) & 7) + edge * (bz + (j >> 6)));
                _levelZero[i] = voxels[j];
                _levelZeroDensities[i] = voxels[brick * brick * brick + j];
            }
        }

        trees[0] = _models.Encode(_levelZero, _levelZeroDensities, part.SizeAt(0));

        // Coarser levels: the kind's, with the coarse voxels over each changed brick sampled again from level 0.
        var source = new ChunkVoxels(_levelZero, _levelZeroDensities);
        for (var level = 1; level < VoxelModels.MaxLevels; level++)
        {
            part.Decode(level, _level, _levelDensities);
            var step = 1 << level;
            var size = part.SizeAt(level);
            foreach (var key in damaged.Bricks)
            {
                int bx = ((key & 255) << 3) - corner.X,
                    by = (((key >> 8) & 255) << 3) - corner.Y,
                    bz = ((key >> 16) << 3) - corner.Z;
                if (bx < 0 || by < 0 || bz < 0 || bx >= part.Size.X || by >= part.Size.Y || bz >= part.Size.Z)
                {
                    continue;
                }

                for (var cz = bz / step; cz <= (bz + brick - 1) / step && cz < size.Z; cz++)
                {
                    for (var cy = by / step; cy <= (by + brick - 1) / step && cy < size.Y; cy++)
                    {
                        for (var cx = bx / step; cx <= (bx + brick - 1) / step && cx < size.X; cx++)
                        {
                            ObjectPart.PartLevel.Sample(
                                in source,
                                default,
                                cx,
                                cy,
                                cz,
                                step,
                                _counts,
                                out var material,
                                out var density
                            );
                            var i = cx + edge * (cy + edge * cz);
                            _level[i] = material;
                            _levelDensities[i] = density;
                        }
                    }
                }
            }

            trees[level] = _models.Encode(_level, _levelDensities, size);
        }

        var changed = MarkChangedBlocks(damaged, index, corner);
        var model = damaged.Models[index];
        if (model < 0)
        {
            model = _models.Add(trees, part.Size, 0, kind.UnitCentimetres, densities: true);
            _models.SetOwned(
                model,
                new VoxelCoord(border, border, border),
                new VoxelCoord(border + part.OwnSize.X, border + part.OwnSize.Y, border + part.OwnSize.Z),
                part.OpenLow
            );
            // Its kind's part's meshes for every block the changes do not reach.
            _models.ShareBlocks(model, _kindModels.PartsOf(damaged.Placement.Kind)[index].Model, changed);
            damaged.Models[index] = model;
            return;
        }

        // The old trees are no longer read once replaced.
        Span<ChildState> old = stackalloc ChildState[VoxelModels.MaxLevels];
        for (var level = 0; level < VoxelModels.MaxLevels; level++)
        {
            old[level] = _models.Tree(model, level);
        }

        _models.Replace(model, trees);
        for (var level = 0; level < VoxelModels.MaxLevels; level++)
        {
            _models.Free(new ModelTree(old[level]));
        }
    }

    /// <summary>
    /// Marks, at each level of part <paramref name="index"/> (whose box starts at <paramref name="corner"/> of the kind),
    /// the blocks whose reads hold a voxel of a changed brick: the rest read what the kind's part's do, and mesh as they do.
    /// </summary>
    private static bool[][] MarkChangedBlocks(DamagedObject damaged, int index, VoxelCoord corner)
    {
        var part = damaged.Kind.Parts[index];
        var changed = damaged.ChangedBlocks[index];
        if (changed is null)
        {
            changed = new bool[VoxelModels.MaxLevels][];
            for (var level = 0; level < changed.Length; level++)
            {
                var (x, y, z) = ModelBlocks.Counts(part.SizeAt(level));
                changed[level] = new bool[x * y * z];
            }

            damaged.ChangedBlocks[index] = changed;
        }

        foreach (var blocks in changed)
        {
            Array.Clear(blocks);
        }

        foreach (var key in damaged.Bricks)
        {
            int bx = ((key & 255) << 3) - corner.X,
                by = (((key >> 8) & 255) << 3) - corner.Y,
                bz = ((key >> 16) << 3) - corner.Z;
            if (bx < 0 || by < 0 || bz < 0 || bx >= part.Size.X || by >= part.Size.Y || bz >= part.Size.Z)
            {
                continue;
            }

            const int last = VoxelConstants.BrickSize - 1;
            for (var level = 0; level < changed.Length; level++)
            {
                var (countX, countY, countZ) = ModelBlocks.Counts(part.SizeAt(level));
                var (x0, _) = ModelBlocks.Reading(bx >> level);
                var (_, x1) = ModelBlocks.Reading((bx + last) >> level);
                var (y0, _) = ModelBlocks.Reading(by >> level);
                var (_, y1) = ModelBlocks.Reading((by + last) >> level);
                var (z0, _) = ModelBlocks.Reading(bz >> level);
                var (_, z1) = ModelBlocks.Reading((bz + last) >> level);
                for (var z = Math.Max(0, z0); z <= Math.Min(countZ - 1, z1); z++)
                {
                    for (var y = Math.Max(0, y0); y <= Math.Min(countY - 1, y1); y++)
                    {
                        for (var x = Math.Max(0, x0); x <= Math.Min(countX - 1, x1); x++)
                        {
                            changed[level][x + countX * (y + countY * z)] = true;
                        }
                    }
                }
            }
        }

        return changed;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Objects: {Most} damaged objects keep copies (Engine:Objects:MostDamaged); the farthest's copies go, made again near"
    )]
    private partial void LogCapReached(int most);
}
