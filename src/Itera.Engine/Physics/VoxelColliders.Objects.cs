using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Physics;

/// <summary>
/// The voxel objects the world places (<see cref="ObjectWorld"/>) as statics near bodies: each object that reaches into a
/// cell a body needs gets a static for each physics material of its voxels, a compound of boxes of its own voxels (its
/// kind's, made once a match and shared by every object of the kind; passable voxels give none), posed at its anchor and
/// turned by its heading (<see cref="ObjectTurns.Rotation"/>), touched by bodies of every voxel level
/// (<see cref="BodyKinds.AnyLevel"/>) in the voxels' layer. An object's statics stay while a cell reaches it; when the
/// objects change (<see cref="ObjectWorld.Version"/>) every cell finds its objects again. A damaged object
/// (<see cref="ObjectDamage"/>) has compounds of its own, of its voxels as the edits left them, swapped into its statics
/// in place at each change it reads (<see cref="ObjectDamage.ReadChanges"/>) and freed with them. A body round which an
/// area of objects is not filled yet freezes as one where the columns are not resident.
/// </summary>
public sealed partial class VoxelColliders
{
    private readonly ObjectWorld? _objects;
    private readonly VoxelObjectKinds? _kinds;
    private readonly ObjectDamage? _damage;
    private readonly ObjectDamageChange[] _damageChanges = new ObjectDamageChange[64];
    private readonly List<ObjectId> _changedObjects = [];
    private readonly HashSet<ObjectId> _changedSet = [];
    private long _damageSeen;

    // Each kind's compound of each physics material (KindPart), made at its first need; Exists false for none.
    private readonly Dictionary<(VoxelObjectKindId Kind, int Part), KindShape> _kindShapes = [];

    // The objects with statics, by id: how many cells reach them, and their statics.
    private readonly Dictionary<ObjectId, ObjectStatics> _objectStatics = [];
    private readonly List<ObjectPlacement> _nearObjects = [];
    private readonly Stack<List<ObjectId>> _spareLists = new();
    private readonly List<List<ObjectId>> _oldLists = [];
    private int _objectsVersion;

    /// <summary>The voxel objects a cell near a body reaches now (some may have no static: all their voxels passable).</summary>
    public int ObjectCount => _objectStatics.Count;

    /// <summary>The statics of voxel objects now, part of <see cref="StaticCount"/>.</summary>
    public int ObjectStaticCount { get; private set; }

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
        if (_objects is null || _objects.Version == _objectsVersion)
        {
            return;
        }

        _objectsVersion = _objects.Version;
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
        var edge = (long)CellSize * _voxels.Grid.Edge(key.Level);
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

            for (var part = 0; part < _partMaterials.Length; part++)
            {
                RemoveObjectStatic(ref held.Statics[part]);
            }

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

        held.Placement = placement;
        var damaged = _damage?.Find(placement.Id);
        for (var part = 0; part < _partMaterials.Length; part++)
        {
            var shape = damaged is null ? ShapeOf(placement.Kind, part) : OwnShape(damaged, part);
            if (shape.Exists)
            {
                AddObjectStatic(in placement, ref held.Statics[part], shape, part, owned: damaged is not null);
            }
        }
    }

    /// <summary>
    /// Gives each object whose voxels changed since the last step (once, however many changes it had), and that has
    /// statics, statics of its voxels as they are now: a shape of its own while damaged, its kind's again once not, swapped into each static in place (which
    /// wakes the bodies touching either), a static removed where its material's voxels are gone.
    /// </summary>
    private void FollowDamage()
    {
        if (_damage is null)
        {
            return;
        }

        // Each object named once, however many changes it had since the last step.
        _changedObjects.Clear();
        _changedSet.Clear();
        while (_damage.Version != _damageSeen)
        {
            var read = _damage.ReadChanges(_damageSeen, _damageChanges, out var lost);
            if (lost)
            {
                // Every object with statics, as it is now.
                _damageSeen = _damage.Version;
                _changedObjects.Clear();
                foreach (var id in _objectStatics.Keys)
                {
                    _changedObjects.Add(id);
                }

                break;
            }

            for (var i = 0; i < read; i++)
            {
                _damageSeen = _damageChanges[i].Version;
                if (_changedSet.Add(_damageChanges[i].Object))
                {
                    _changedObjects.Add(_damageChanges[i].Object);
                }
            }
        }

        foreach (var id in _changedObjects)
        {
            Refresh(id);
        }
    }

    /// <summary>Makes an object's statics, if it has any, its voxels' as they are now.</summary>
    private void Refresh(ObjectId id)
    {
        ref var held = ref CollectionsMarshal.GetValueRefOrNullRef(_objectStatics, id);
        if (Unsafe.IsNullRef(ref held))
        {
            return;
        }

        var damaged = _damage!.Find(id);
        var simulation = _physics.Simulation;
        for (var part = 0; part < _partMaterials.Length; part++)
        {
            ref var slot = ref held.Statics[part];
            var shape = damaged is null ? ShapeOf(held.Placement.Kind, part) : OwnShape(damaged, part);
            if (!shape.Exists)
            {
                RemoveObjectStatic(ref slot);
                continue;
            }

            if (slot.Value == 0)
            {
                AddObjectStatic(in held.Placement, ref slot, shape, part, owned: damaged is not null);
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

            slot = new StaticSlot(slot.Value, shape.Shape, damaged is not null);
        }
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

    /// <summary>The compound of a kind's voxels of physics material <paramref name="part"/>, from its anchor.</summary>
    private KindShape ShapeOf(VoxelObjectKindId id, int part)
    {
        ref var shape = ref CollectionsMarshal.GetValueRefOrAddDefault(_kindShapes, (id, part), out var exists);
        if (exists)
        {
            return shape;
        }

        var kind = _kinds![id];
        _boxes.Build(kind, null, in _partExcluded[part]);
        shape = Compound(kind);
        return shape;
    }

    /// <summary>A damaged object's own compound of its voxels of physics material <paramref name="part"/>, as they are now.</summary>
    private KindShape OwnShape(DamagedObject damaged, int part)
    {
        _boxes.Build(damaged.Kind, damaged, in _partExcluded[part]);
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
        return new KindShape(simulation.Shapes.Add(new BigCompound(children, simulation.Shapes, pool)), true);
    }

    private readonly record struct KindShape(TypedIndex Shape, bool Exists);

    /// <summary>
    /// An object's static of one physics material: its handle's value plus one (0 for none), its shape, and whether the
    /// shape is the object's own (a damaged object's), freed with it.
    /// </summary>
    private readonly record struct StaticSlot(int Value, TypedIndex Shape, bool Owned);

    [InlineArray(PhysicsMaterials.MostVoxelMaterials)]
    private struct StaticSlots
    {
        private StaticSlot _slot;
    }

    /// <summary>An object's statics, the object, and how many cells reach it.</summary>
    private struct ObjectStatics
    {
        public int Cells;
        public ObjectPlacement Placement;
        public StaticSlots Statics;
    }
}
