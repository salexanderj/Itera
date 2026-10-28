using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Physics;

/// <summary>
/// The voxel objects the world places (<see cref="ObjectWorld"/>) as statics near bodies: each object that reaches into a
/// cell a body needs gets a static for each of its kind's parts (the 112-voxel cubes, <see cref="VoxelObjectKind.PartCount"/>)
/// and each physics material of the part's voxels, a compound of boxes of the part's own voxels (its kind's, shared by
/// every object of the kind; passable voxels give none), posed at its anchor and turned by its heading
/// (<see cref="ObjectTurns.Rotation"/>), touched by bodies of every voxel level (<see cref="BodyKinds.AnyLevel"/>) in the
/// voxels' layer. The kinds' compounds are made at the match's "objects" step (<see cref="MakeObjectShapes"/>), so no step
/// makes one when a body first comes near an object (a kind of 55,000 boxes took 89 ms then); without that step, at their
/// first need. An object's statics stay while a cell reaches it; when the objects change (<see cref="ObjectWorld.Version"/>)
/// every cell finds its objects again. A damaged object (<see cref="ObjectDamage"/>) has compounds of its own for the parts
/// that hold a changed brick, of their voxels as the edits left them, swapped into its statics in place at each change it
/// reads (<see cref="ObjectDamage.ReadChanges"/>) and freed with them; a change makes again only the parts its box reaches
/// across x and z (at every height: the box has none), so a crater in a large object costs its parts' compounds, not the
/// object's. A body round which an area of objects is not filled yet freezes as one where the columns are not resident.
/// </summary>
public sealed partial class VoxelColliders
{
    private readonly ObjectWorld? _objects;
    private readonly VoxelObjectKinds? _kinds;
    private readonly ObjectDamage? _damage;
    private readonly ObjectDamageChange[] _damageChanges = new ObjectDamageChange[64];
    private readonly List<ObjectId> _changedObjects = [];
    private readonly Dictionary<ObjectId, ChangeBox> _changedBoxes = [];
    private long _damageSeen;

    // Each kind part's compound of each physics material (the part of _partMaterials), made at the objects step or at
    // its first need; Exists false for none.
    private readonly Dictionary<(VoxelObjectKindId Kind, int KindPart, int Part), KindShape> _kindShapes = [];

    // The objects with statics, by id: how many cells reach them, and their statics.
    private readonly Dictionary<ObjectId, ObjectStatics> _objectStatics = [];
    private readonly List<ObjectPlacement> _nearObjects = [];
    private readonly Stack<List<ObjectId>> _spareLists = new();
    private readonly List<List<ObjectId>> _oldLists = [];
    private readonly Dictionary<int, Stack<StaticSlot[]>> _spareSlots = [];
    private bool[] _damagedParts = [];
    private bool[] _reachedParts = [];
    private int _objectsVersion;

    /// <summary>The voxel objects a cell near a body reaches now (some may have no static: all their voxels passable).</summary>
    public int ObjectCount => _objectStatics.Count;

    /// <summary>
    /// The statics of voxel objects now, part of <see cref="StaticCount"/>: one for each of an object's kind's parts and
    /// physics materials that has blocking voxels.
    /// </summary>
    public int ObjectStaticCount { get; private set; }

    /// <summary>The objects' compounds made since the start: the kinds' parts', and the damaged objects' parts' own.</summary>
    public long CompoundsMade { get; private set; }

    /// <summary>
    /// Makes every kind's compounds, each part's of each physics material of its voxels, behind the loading screen. At the
    /// match's "objects" step, once the kinds are sealed (<c>VoxelObjects.Initialize</c>) and after <see cref="Initialize"/>;
    /// does nothing without objects.
    /// </summary>
    public void MakeObjectShapes()
    {
        if (_cursor is null)
        {
            throw new InvalidOperationException("The voxel colliders have not been initialised.");
        }

        if (_kinds is null || _kinds.Count == 0)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var compounds = CompoundsMade;
        long boxes = 0;
        for (var k = 0; k < _kinds.Count; k++)
        {
            var id = new VoxelObjectKindId((ushort)k);
            for (var kindPart = 0; kindPart < _kinds[id].PartCount; kindPart++)
            {
                for (var part = 0; part < _partMaterials.Length; part++)
                {
                    var made = CompoundsMade;
                    ShapeOf(id, kindPart, part);
                    boxes += CompoundsMade > made ? _boxes.Count : 0;
                }
            }
        }

        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogObjectShapes(_kinds.Count, CompoundsMade - compounds, boxes, ms);
    }

    /// <summary>
    /// True when the objects that may reach into the box from local (<paramref name="min"/>) to (<paramref name="max"/>)
    /// metres are known: always without objects.
    /// </summary>
    private bool ObjectsKnown(System.Numerics.Vector3 min, System.Numerics.Vector3 max)
    {
        if (_objects is null)
        {
            return true;
        }

        var origin = _voxels.Origin;
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        return _objects.IsKnown(
            (long)Math.Floor(origin.WorldX(min.X) * perMetre),
            (long)Math.Ceiling(origin.WorldX(max.X) * perMetre),
            (long)Math.Floor(origin.WorldZ(min.Z) * perMetre),
            (long)Math.Ceiling(origin.WorldZ(max.Z) * perMetre)
        );
    }

    /// <summary>When the objects changed, makes every cell find its objects again; their statics stay where kept.</summary>
    private void FollowObjects()
    {
        if (_objects is null || _objects.SetVersion == _objectsVersion)
        {
            return;
        }

        _objectsVersion = _objects.SetVersion;
        // Every cell's new objects first, then the old ones let go, so an object both reach keeps its statics.
        _keys.Clear();
        _keys.AddRange(_cells.Keys);
        foreach (var key in _keys)
        {
            ref var cell = ref CollectionsMarshal.GetValueRefOrNullRef(_cells, key);
            if (cell.Objects is not { } old)
            {
                continue;
            }

            _oldLists.Add(old);
            cell.Objects = null;
            Link(key, ref cell);
        }

        foreach (var old in _oldLists)
        {
            Release(old);
        }

        _oldLists.Clear();
    }

    /// <summary>Gives the cell the objects that reach into it, with their statics.</summary>
    private void Link(CellKey key, ref Cell cell)
    {
        if (_objects is null || cell.Objects is not null)
        {
            return;
        }

        var list = _spareLists.Count > 0 ? _spareLists.Pop() : [];
        cell.Objects = list;
        // The cell's box in absolute centimetres.
        var edge = (long)_cellVoxels * _voxels.Grid.Edge(key.Level);
        long x0 = key.X * edge,
            y0 = key.Y * edge,
            z0 = key.Z * edge;
        _nearObjects.Clear();
        _objects.Near((int)x0, (int)(x0 + edge - 1), (int)z0, (int)(z0 + edge - 1), _nearObjects);
        foreach (var placement in _nearObjects)
        {
            var kind = _kinds![placement.Kind];
            if (
                placement.Anchor.Y + kind.TopCentimetres <= y0
                || placement.Anchor.Y + kind.BottomCentimetres >= y0 + edge
            )
            {
                continue;
            }

            list.Add(placement.Id);
            Acquire(in placement);
        }
    }

    /// <summary>Lets go of a cell's objects: an object no cell reaches loses its statics.</summary>
    private void Unlink(ref Cell cell)
    {
        if (cell.Objects is { } list)
        {
            cell.Objects = null;
            Release(list);
        }
    }

    private void Release(List<ObjectId> list)
    {
        foreach (var id in list)
        {
            ref var held = ref CollectionsMarshal.GetValueRefOrNullRef(_objectStatics, id);
            if (Unsafe.IsNullRef(ref held) || --held.Cells > 0)
            {
                continue;
            }

            foreach (ref var slot in held.Statics.AsSpan())
            {
                RemoveObjectStatic(ref slot);
            }

            _spareSlots[held.Statics.Length].Push(held.Statics);
            _objectStatics.Remove(id);
        }

        list.Clear();
        _spareLists.Push(list);
    }

    /// <summary>One more cell reaches the object: its statics are made with the first.</summary>
    private void Acquire(in ObjectPlacement placement)
    {
        ref var held = ref CollectionsMarshal.GetValueRefOrAddDefault(_objectStatics, placement.Id, out var exists);
        held.Cells++;
        if (exists)
        {
            return;
        }

        var kind = _kinds![placement.Kind];
        var materials = _partMaterials.Length;
        held.Placement = placement;
        held.Statics = SlotsFor(kind.PartCount * materials);
        var damaged = _damage?.Find(placement.Id);
        var own = DamagedParts(kind, damaged);
        for (var kindPart = 0; kindPart < kind.PartCount; kindPart++)
        {
            for (var part = 0; part < materials; part++)
            {
                var shape = own[kindPart]
                    ? OwnShape(damaged!, kindPart, part)
                    : ShapeOf(placement.Kind, kindPart, part);
                if (shape.Exists)
                {
                    AddObjectStatic(
                        in placement,
                        ref held.Statics[kindPart * materials + part],
                        shape,
                        part,
                        owned: own[kindPart]
                    );
                }
            }
        }
    }

    /// <summary>
    /// Gives each object whose voxels changed since the last step (once, however many changes it had: the union of their
    /// boxes), and that has statics, statics of its voxels as they are now in the parts the boxes reach.
    /// </summary>
    private void FollowDamage()
    {
        if (_damage is null)
        {
            return;
        }

        _changedObjects.Clear();
        _changedBoxes.Clear();
        while (_damage.Version != _damageSeen)
        {
            var read = _damage.ReadChanges(_damageSeen, _damageChanges, out var lost);
            if (lost)
            {
                // Every object with statics, every part, as it is now.
                _damageSeen = _damage.Version;
                _changedObjects.Clear();
                _changedBoxes.Clear();
                foreach (var id in _objectStatics.Keys)
                {
                    _changedObjects.Add(id);
                    _changedBoxes[id] = ChangeBox.Everything;
                }

                break;
            }

            for (var i = 0; i < read; i++)
            {
                var change = _damageChanges[i];
                _damageSeen = change.Version;
                var box = new ChangeBox(change.MinX, change.MaxX, change.MinZ, change.MaxZ);
                ref var kept = ref CollectionsMarshal.GetValueRefOrAddDefault(
                    _changedBoxes,
                    change.Object,
                    out var exists
                );
                if (exists)
                {
                    kept = kept.With(box);
                }
                else
                {
                    kept = box;
                    _changedObjects.Add(change.Object);
                }
            }
        }

        foreach (var id in _changedObjects)
        {
            Refresh(id, _changedBoxes[id]);
        }
    }

    /// <summary>
    /// Makes an object's statics, if it has any, its voxels' as they are now in each of its kind's parts the change's box
    /// reaches: a compound of its own for a part that holds a changed brick, the kind's for one that does not (healed, or
    /// never reached), swapped into each static in place (which wakes the bodies touching either), a static removed where
    /// its material's voxels are gone.
    /// </summary>
    private void Refresh(ObjectId id, ChangeBox box)
    {
        ref var held = ref CollectionsMarshal.GetValueRefOrNullRef(_objectStatics, id);
        if (Unsafe.IsNullRef(ref held))
        {
            return;
        }

        var kind = _kinds![held.Placement.Kind];
        var damaged = _damage!.Find(id);
        var own = DamagedParts(kind, damaged);
        var reached = Grown(ref _reachedParts, kind.PartCount);
        ReachedParts(kind, in held.Placement, box.MinX, box.MaxX, box.MinZ, box.MaxZ, reached);
        var simulation = _physics.Simulation;
        var materials = _partMaterials.Length;
        for (var kindPart = 0; kindPart < kind.PartCount; kindPart++)
        {
            if (!reached[kindPart])
            {
                continue;
            }

            for (var part = 0; part < materials; part++)
            {
                ref var slot = ref held.Statics[kindPart * materials + part];
                var shape = own[kindPart]
                    ? OwnShape(damaged!, kindPart, part)
                    : ShapeOf(held.Placement.Kind, kindPart, part);
                if (!shape.Exists)
                {
                    RemoveObjectStatic(ref slot);
                    continue;
                }

                if (slot.Value == 0)
                {
                    AddObjectStatic(in held.Placement, ref slot, shape, part, owned: own[kindPart]);
                    continue;
                }

                if (!slot.Owned && !own[kindPart] && slot.Shape.Packed == shape.Shape.Packed)
                {
                    // Still its kind's: nothing to swap, and no body woken.
                    continue;
                }

                // Not Statics.SetShape: in this Bepu it moves the bounds but keeps the old shape.
                var handle = new StaticHandle(slot.Value - 1);
                simulation.Statics.GetDescription(handle, out var description);
                description.Shape = shape.Shape;
                simulation.Statics.ApplyDescription(handle, description);
                if (slot.Owned)
                {
                    simulation.Shapes.RemoveAndDispose(slot.Shape, _physics.Pool);
                }

                slot = new StaticSlot(slot.Value, shape.Shape, own[kindPart]);
            }
        }
    }

    /// <summary>
    /// Marks in <paramref name="reached"/> (one for each of the kind's parts) the parts whose own voxels the box from
    /// (<paramref name="minX"/>, <paramref name="minZ"/>) to (<paramref name="maxX"/>, <paramref name="maxZ"/>), absolute
    /// centimetres inclusive, reaches across x and z, at every height (a change's box has none), with a voxel to spare:
    /// the box's corners turned onto the object's own axes (<see cref="ObjectTurns.ToObject"/>, which rounds down).
    /// </summary>
    internal static void ReachedParts(
        VoxelObjectKind kind,
        in ObjectPlacement placement,
        int minX,
        int maxX,
        int minZ,
        int maxZ,
        Span<bool> reached
    )
    {
        reached.Clear();
        var anchor = placement.Anchor;
        long lowX = long.MaxValue,
            highX = long.MinValue,
            lowZ = long.MaxValue,
            highZ = long.MinValue;
        for (var corner = 0; corner < 4; corner++)
        {
            var (x, z) = ObjectTurns.ToObject(
                placement.Heading,
                ((corner & 1) == 0 ? minX : maxX + 1L) - anchor.X,
                ((corner & 2) == 0 ? minZ : maxZ + 1L) - anchor.Z
            );
            lowX = Math.Min(lowX, x);
            highX = Math.Max(highX, x);
            lowZ = Math.Min(lowZ, z);
            highZ = Math.Max(highZ, z);
        }

        // In the kind's voxels from its box's corner.
        var unit = kind.UnitCentimetres;
        long x0 = FloorDivide(lowX + kind.AnchorCentimetres.X, unit) - 1,
            x1 = FloorDivide(highX + kind.AnchorCentimetres.X, unit) + 1,
            z0 = FloorDivide(lowZ + kind.AnchorCentimetres.Z, unit) - 1,
            z1 = FloorDivide(highZ + kind.AnchorCentimetres.Z, unit) + 1;
        var parts = kind.Parts;
        for (var p = 0; p < parts.Length; p++)
        {
            var part = parts[p];
            reached[p] =
                part.Offset.X <= x1
                && part.Offset.X + part.OwnSize.X - 1 >= x0
                && part.Offset.Z <= z1
                && part.Offset.Z + part.OwnSize.Z - 1 >= z0;
        }

        static long FloorDivide(long value, int divisor) =>
            value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
    }

    /// <summary>
    /// Marks the parts of <paramref name="kind"/> whose own voxels hold a changed brick of <paramref name="damaged"/>
    /// (none without it): those have compounds of their own. A brick stays changed while the copy lasts, so a part once
    /// marked stays marked.
    /// </summary>
    private ReadOnlySpan<bool> DamagedParts(VoxelObjectKind kind, DamagedObject? damaged)
    {
        var marks = Grown(ref _damagedParts, kind.PartCount);
        marks.Clear();
        if (damaged is null)
        {
            return marks;
        }

        // A part's own voxels are 14 bricks along each axis from a multiple of 14.
        const int own = ObjectPart.OwnVoxels / VoxelConstants.BrickSize;
        foreach (var key in damaged.Bricks)
        {
            var index = kind.PartIndexAt((key & 255) / own, ((key >> 8) & 255) / own, (key >> 16) / own);
            if (index >= 0)
            {
                marks[index] = true;
            }
        }

        return marks;
    }

    /// <summary>The first <paramref name="count"/> of <paramref name="array"/>, grown to hold them.</summary>
    private static Span<bool> Grown(ref bool[] array, int count)
    {
        if (array.Length < count)
        {
            array = new bool[count];
        }

        return array.AsSpan(0, count);
    }

    /// <summary>Empty slots for an object's statics, reused from an object that had as many.</summary>
    private StaticSlot[] SlotsFor(int count)
    {
        ref var spare = ref CollectionsMarshal.GetValueRefOrAddDefault(_spareSlots, count, out _);
        spare ??= new Stack<StaticSlot[]>();
        return spare.Count > 0 ? spare.Pop() : new StaticSlot[count];
    }

    private void AddObjectStatic(
        in ObjectPlacement placement,
        ref StaticSlot slot,
        KindShape shape,
        int part,
        bool owned
    )
    {
        var anchor = _voxels.Origin.ToLocal(placement.Anchor);
        var turn = ObjectTurns.Rotation(placement.Heading);
        var handle = _physics.Simulation.Statics.Add(new StaticDescription(anchor, turn, shape.Shape));
        _physics.Kinds.SetStatic(handle, BodyKinds.AnyLevel, _partMaterials[part], _physics.VoxelLayer);
        slot = new StaticSlot(handle.Value + 1, shape.Shape, owned);
        StaticCount++;
        ObjectStaticCount++;
    }

    private void RemoveObjectStatic(ref StaticSlot slot)
    {
        if (slot.Value == 0)
        {
            return;
        }

        // A kind's shape is kept for the others; a damaged object's own goes with it.
        _physics.RemoveStatic(new StaticHandle(slot.Value - 1));
        if (slot.Owned)
        {
            _physics.Simulation.Shapes.RemoveAndDispose(slot.Shape, _physics.Pool);
        }

        slot = default;
        StaticCount--;
        ObjectStaticCount--;
    }

    /// <summary>The compound of a kind part's voxels of physics material <paramref name="part"/>, from the kind's anchor.</summary>
    private KindShape ShapeOf(VoxelObjectKindId id, int kindPart, int part)
    {
        ref var shape = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _kindShapes,
            (id, kindPart, part),
            out var exists
        );
        if (exists)
        {
            return shape;
        }

        var kind = _kinds![id];
        _boxes.Build(kind, kindPart, null, in _partExcluded[part]);
        shape = Compound(kind);
        return shape;
    }

    /// <summary>
    /// A damaged object's own compound of a part's voxels of physics material <paramref name="part"/>, as they are now.
    /// </summary>
    private KindShape OwnShape(DamagedObject damaged, int kindPart, int part)
    {
        _boxes.Build(damaged.Kind, kindPart, damaged, in _partExcluded[part]);
        return Compound(damaged.Kind);
    }

    /// <summary>A compound of the boxes just built, in the kind's voxels, from its anchor along its own axes.</summary>
    private KindShape Compound(VoxelObjectKind kind)
    {
        if (_boxes.Count == 0)
        {
            return default;
        }

        var metres = kind.UnitCentimetres / (float)VoxelConstants.CentimetresPerMetre;
        var anchor = kind.AnchorCentimetres;
        var boxes = _boxes.Boxes;
        var pool = _physics.Pool;
        pool.Take(boxes.Length, out Buffer<CompoundChild> children);
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            // Its centre from the anchor, along the object's own axes.
            var centre = new System.Numerics.Vector3(
                (box.X0 + box.X1 + 1) * 0.5f * metres - anchor.X / (float)VoxelConstants.CentimetresPerMetre,
                (box.Y0 + box.Y1 + 1) * 0.5f * metres - anchor.Y / (float)VoxelConstants.CentimetresPerMetre,
                (box.Z0 + box.Z1 + 1) * 0.5f * metres - anchor.Z / (float)VoxelConstants.CentimetresPerMetre
            );
            children[i] = new CompoundChild(
                new RigidPose(centre),
                _physics.UnitBox(kind.UnitCentimetres, box.SizeX, box.SizeY, box.SizeZ)
            );
        }

        var simulation = _physics.Simulation;
        CompoundsMade++;
        return new KindShape(simulation.Shapes.Add(new BigCompound(children, simulation.Shapes, pool)), true);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Voxel colliders: {Kinds} object kind(s) made into {Compounds} compound(s) of {Boxes} boxes in {Ms:F1} ms"
    )]
    private partial void LogObjectShapes(int kinds, long compounds, long boxes, double ms);

    private readonly record struct KindShape(TypedIndex Shape, bool Exists);

    /// <summary>A change's box in absolute centimetres across x and z, inclusive.</summary>
    private readonly record struct ChangeBox(int MinX, int MaxX, int MinZ, int MaxZ)
    {
        /// <summary>Every part of an object.</summary>
        public static ChangeBox Everything => new(int.MinValue, int.MaxValue, int.MinValue, int.MaxValue);

        /// <summary>The box holding this one and <paramref name="other"/>.</summary>
        public ChangeBox With(ChangeBox other) =>
            new(
                Math.Min(MinX, other.MinX),
                Math.Max(MaxX, other.MaxX),
                Math.Min(MinZ, other.MinZ),
                Math.Max(MaxZ, other.MaxZ)
            );
    }

    /// <summary>
    /// An object's static of one kind part and physics material: its handle's value plus one (0 for none), its shape, and
    /// whether the shape is the object's own (a damaged object's), freed with it.
    /// </summary>
    private readonly record struct StaticSlot(int Value, TypedIndex Shape, bool Owned);

    /// <summary>An object's statics, the object, and how many cells reach it.</summary>
    private struct ObjectStatics
    {
        public int Cells;
        public ObjectPlacement Placement;

        // Its statics: kind part k's of physics material part m at k * PartCount + m.
        public StaticSlot[] Statics;
    }
}
