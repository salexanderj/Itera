using System.Numerics;
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
/// Gives Bepu the level-0 voxels near its dynamic bodies. Each 32x32x32-voxel cell (32 cm on a 1 cm grid, 3.2 m at 10 cm) that a body's bounds reach,
/// widened by its movement in one step and a margin, becomes one static for each physics material its voxels have
/// (<see cref="PhysicsMaterials.MapVoxel"/>; one where no voxel material is mapped): a compound of boxes that cover the
/// cell's blocking voxels of that material (<see cref="VoxelBoxes"/>, one walk of the cell each; passable foliage gives
/// none). The world's change list rebuilds a cell after
/// an edit, which wakes bodies resting on it: at most <see cref="MaxRebuiltCells"/> a step, the rest in later steps
/// (a 50 cm dig under a pile rebuilt cells for up to 10 ms in one step), so a body may rest on
/// dug-away voxels, or sit in placed ones, for a few steps. A count rather than a time keeps the simulation the same
/// on a busy machine as on an idle one; a cell of several physics materials counts as that many cells. A cell a body reaches for the
/// first time is always built at once. A cell no body has needed for a second is removed. A body cut off a character
/// (<see cref="BodyKind.Cut"/>) whose own level's columns are not resident collides with the finest coarser level that is,
/// up to the coarse level (<see cref="CoarseLevelOf"/>: level 0 itself at 10 cm, where it freezes instead), and goes back to its own once that loads: it falls on
/// the coarse voxels rather than hanging in the air. A body with no such level resident is frozen (made kinematic, its
/// velocity and inertia kept) until the columns load; a ragdoll's bodies freeze and thaw together, and its joints, which
/// Bepu drops between bodies that are all kinematic, come back at the thaw (<see cref="GroupThawed"/>; otherwise it thaws as loose limbs).
/// A large body (one that collides with the level nearest 4 cm, <see cref="CoarseLevelOf"/>, <see cref="BodyKinds"/>)
/// gets cells of that level: on a 1 cm grid 32 of its 4 cm voxels on an edge (1.28 m), built from the world's own
/// level-2 voxels and touched by those bodies alone (2026-09-29: ten large dead bodies in a dug hole met about 570,000 level-0
/// boxes a step, 16.5 ms of physics), and a large edit reaches level 2 after level 0
/// (<c>VoxelWorld.ApplyPendingEdits</c>), so its ground follows a few steps later. At 10 cm that level is level 0, the
/// voxels every body collides with. <see cref="Update"/> runs once per fixed step, before the physics step. Main thread; allocates nothing once its tables
/// have grown.
/// </summary>
public sealed partial class VoxelColliders : IDisposable
{
    /// <summary>Edge of a collision cell in voxels of its level: 32 cm at level 0 of a 1 cm grid, 3.2 m at 10 cm.</summary>
    public const int CellSize = 1 << VoxelConstants.CellShift;

    /// <summary>
    /// The world level a giant's body collides with on a grid of 1 cm (<see cref="CoarseLevelOf"/>; tests use it):
    /// the world's voxels of <see cref="CoarsePartCentimetres"/>.
    /// </summary>
    public const int CoarseLevel = 2;

    /// <summary>
    /// The world level a giant's body collides with (<see cref="LooseBodies"/> sets it for bodies whose parts' model
    /// voxels are <see cref="CoarsePartCentimetres"/> or coarser): the level of <paramref name="grid"/> whose voxels are
    /// nearest that size.
    /// </summary>
    public static int CoarseLevelOf(VoxelGrid grid) => grid.LevelNearest(CoarsePartCentimetres);

    /// <summary>Bodies whose parts' voxels are this many centimetres or coarser (a large body's) collide with <see cref="CoarseLevel"/>.</summary>
    public const int CoarsePartCentimetres = 4;

    /// <summary>Steps a cell is kept after the last body that needed it.</summary>
    public const int IdleSteps = 60;

    /// <summary>
    /// How many cells that edits changed one step may rebuild: cells beside a fresh dig take up to about 0.7 ms each
    /// (Release), so 8 stay within about 5 ms, and the cells a 50 cm dig changes (about 60) are rebuilt within about 8
    /// steps.
    /// </summary>
    public const int MaxRebuiltCells = 8;

    /// <summary>How far past a body's bounds and its movement in one step the cells reach, in metres.</summary>
    public const float MarginMetres = 0.08f;

    /// <summary>
    /// The most a body's movement in one step widens the cells it needs, in metres: a non-finite or huge velocity once
    /// walked cells without end.
    /// </summary>
    public const float MostReachMetres = 8;

    private const int CellShift = VoxelConstants.CellShift;

    private readonly PhysicsWorld _physics;
    private readonly VoxelWorld _voxels;
    private readonly MaterialSet _passable;
    private readonly ILogger<VoxelColliders> _logger;
    private readonly VoxelBoxes _boxes = new();

    // The physics materials of a cell's statics, and for each the materials its walk leaves out: the passable ones and
    // the voxels of the other physics materials.
    private PhysicsMaterialId[] _partMaterials = [PhysicsMaterialId.Default];
    private MaterialSet[] _partExcluded;

    // A cell's columns' tops and surfaces where the world keeps densities (voxels coarser than 1 cm).
    private readonly int[] _columnTops = new int[CellSize * CellSize];
    private readonly double[] _surfaces = new double[CellSize * CellSize];
    private Buffer<CompoundChild> _children;
    private readonly Dictionary<CellKey, Cell> _cells = [];
    private readonly Dictionary<BodyHandle, Frozen> _frozen = [];
    private readonly List<CellKey> _keys = [];
    private readonly List<BodyHandle> _freeze = [];
    private readonly List<BodyHandle> _thaw = [];
    private readonly List<int> _groups = [];
    private readonly VoxelChange[] _changes = new VoxelChange[256];
    private VoxelChangeCursor? _cursor;
    private long _step;
    private bool _disposed;

    /// <param name="physics">The match's physics.</param>
    /// <param name="voxels">The match's voxel world.</param>
    /// <param name="passability">The materials that give no boxes.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="objects">The voxel objects the world places, statics near bodies too; none without them.</param>
    /// <param name="kinds">The run's object kinds, with <paramref name="objects"/>.</param>
    public VoxelColliders(
        PhysicsWorld physics,
        VoxelWorld voxels,
        IVoxelPassability passability,
        ILogger<VoxelColliders> logger,
        ObjectWorld? objects = null,
        VoxelObjectKinds? kinds = null
    )
    {
        _physics = physics;
        // Kept whether or not the world places objects: its placer is known only at the match's "objects" step, after
        // this is made, and without one it gives none and knows every area.
        _objects = kinds is not null ? objects : null;
        _kinds = kinds;
        _voxels = voxels;
        _passable = MaterialSet.Passable(passability);
        _partExcluded = [_passable];
        _logger = logger;
        // A body removed through the physics world is forgotten here first.
        physics.BodyRemoving = Forget;
    }

    /// <summary>The physics materials a cell's statics may have: one static for each its voxels have.</summary>
    public int PartCount => _partMaterials.Length;

    /// <summary>Statics built for bodies.</summary>
    public int StaticCount { get; private set; }

    /// <summary>Boxes in those statics.</summary>
    public int BoxCount { get; private set; }

    /// <summary>Cells built or rebuilt since the start.</summary>
    public long CellsBuilt { get; private set; }

    /// <summary>How many changed cells one step may rebuild (<see cref="MaxRebuiltCells"/>; a test sets 1).</summary>
    internal int RebuildLimit { get; set; } = MaxRebuiltCells;

    /// <summary>Cells an edit changed that wait to be rebuilt.</summary>
    public int DirtyCount
    {
        get
        {
            var count = 0;
            foreach (var cell in _cells.Values)
            {
                count += cell.Dirty ? 1 : 0;
            }

            return count;
        }
    }

    /// <summary>Bodies frozen because a column around them is not resident.</summary>
    public int FrozenCount => _frozen.Count;

    /// <summary>
    /// Called with a ragdoll's group (<see cref="BodyKinds.Ragdoll"/>) once all of its bodies thawed together, before the
    /// step, so its joints come back (<see cref="LooseBodies"/> sets it).
    /// </summary>
    public Action<int>? GroupThawed { get; set; }

    /// <summary>True when the body is frozen until the columns around it load.</summary>
    public bool IsFrozen(BodyHandle body) => _frozen.ContainsKey(body);

    /// <summary>Starts reading the world's change list. Call after the physics world and the voxel world are initialised.</summary>
    public void Initialize()
    {
        _cursor = _voxels.CreateChangeCursor();
        ReadParts();
        var coarseLevel = CoarseLevelOf(_voxels.Grid);
        LogInitialized(CellSize, coarseLevel, _partMaterials.Length);
    }

    /// <summary>
    /// The physics materials the voxels have (<see cref="PhysicsMaterials.OfVoxel"/>): the default first where a voxel
    /// material is unmapped, then the others by their first voxel material; and what each walk leaves out.
    /// </summary>
    private void ReadParts()
    {
        if (_physics.Materials is not { } materials)
        {
            return;
        }

        Span<bool> seen = stackalloc bool[PhysicsMaterials.MaxMaterials];
        var parts = new List<PhysicsMaterialId>();
        for (var pass = 0; pass < 2; pass++)
        {
            for (var voxel = 1; voxel < 256; voxel++)
            {
                var material = materials.OfVoxel((byte)voxel);
                if ((pass == 0) == (material == PhysicsMaterialId.Default) && !seen[material.Value])
                {
                    seen[material.Value] = true;
                    parts.Add(material);
                }
            }
        }

        _partMaterials = [.. parts];
        _partExcluded = new MaterialSet[parts.Count];
        Span<byte> others = stackalloc byte[255];
        for (var part = 0; part < parts.Count; part++)
        {
            var count = 0;
            for (var voxel = 1; voxel < 256; voxel++)
            {
                if (materials.OfVoxel((byte)voxel) != parts[part])
                {
                    others[count++] = (byte)voxel;
                }
            }

            _partExcluded[part] = _passable.Union(MaterialSet.Of(others[..count]));
        }
    }

    /// <summary>
    /// Forgets a body that is about to leave the simulation, so a frozen body's kept state does not pass to a new body
    /// that reuses its handle. <see cref="PhysicsWorld.RemoveBody"/> calls it; call it before a <c>Bodies.Remove</c> of
    /// one's own.
    /// </summary>
    public void Forget(BodyHandle body) => _frozen.Remove(body);

    /// <summary>Brings the statics up to date with the world's changes and the bodies' positions, before a step.</summary>
    public void Update(float stepSeconds)
    {
        if (_cursor is null)
        {
            throw new InvalidOperationException("The voxel colliders have not been initialised.");
        }

        _step++;
        ReadChanges();
        FollowObjects();

        var simulation = _physics.Simulation;
        var bodies = simulation.Bodies;
        for (var set = 0; set < bodies.Sets.Length; set++)
        {
            ref var bodySet = ref bodies.Sets[set];
            if (!bodySet.Allocated)
            {
                continue;
            }

            for (var i = 0; i < bodySet.Count; i++)
            {
                Need(bodies[bodySet.IndexToHandle[i]], stepSeconds);
            }
        }

        // A ragdoll freezes and thaws as one: every body of it with one that must freeze, and none until all may thaw.
        if (_freeze.Count > 0 || _thaw.Count > 0)
        {
            JoinGroups();
        }

        // Outside the loop over the sets: changing a body's inertia wakes it, which moves it between sets.
        foreach (var handle in _freeze)
        {
            Freeze(bodies[handle]);
        }

        foreach (var handle in _thaw)
        {
            Thaw(bodies[handle]);
        }

        foreach (var group in _groups)
        {
            GroupThawed?.Invoke(group);
        }

        _freeze.Clear();
        _thaw.Clear();
        _groups.Clear();

        _keys.Clear();
        foreach (var (key, cell) in _cells)
        {
            if (_step - cell.LastNeeded > IdleSteps || cell.Dirty)
            {
                _keys.Add(key);
            }
        }

        var rebuilt = 0;
        foreach (var key in _keys)
        {
            ref var cell = ref CollectionsMarshal.GetValueRefOrNullRef(_cells, key);
            if (_step - cell.LastNeeded > IdleSteps)
            {
                RemoveStatic(ref cell);
                _cells.Remove(key);
            }
            else if (!cell.Built)
            {
                Build(key, ref cell);
            }
            else if (rebuilt < RebuildLimit)
            {
                // A changed cell; past the limit it stays dirty for the next step.
                Build(key, ref cell);
                rebuilt += _partMaterials.Length;
            }
        }
    }

    /// <summary>Adds the outline of every cell that has a static, dim, to the frame's debug lines.</summary>
    public void AddOutlines(Rendering.DebugLines lines)
    {
        var colour = Rendering.DebugLines.Colour(90, 90, 110);
        foreach (var (key, cell) in _cells)
        {
            if (cell.HasStatic)
            {
                var edge = CellSize * _voxels.Grid.Edge(key.Level);
                var corner = _voxels.Origin.ToLocal(new VoxelCoord(key.X * edge, key.Y * edge, key.Z * edge));
                var size = edge / (float)VoxelConstants.CentimetresPerMetre;
                lines.AddBox(corner, Quaternion.Identity, Vector3.Zero, new Vector3(size), colour);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // The simulation frees its statics and shapes when it is disposed; only this side's tables are dropped here, and
        // the children's buffer goes back to the pool while it is there (a disposed world cleared it).
        _cells.Clear();
        _frozen.Clear();
        _objectStatics.Clear();
        _kindShapes.Clear();
        if (_children.Allocated && _physics.IsInitialized)
        {
            _physics.Pool.Return(ref _children);
        }
    }

    /// <summary>Marks the cells a dynamic body (or one frozen here) needs, and freezes or thaws it.</summary>
    private void Need(BodyReference body, float stepSeconds)
    {
        var handle = body.Handle;
        var frozen = _frozen.TryGetValue(handle, out var kept);
        // Stand-ins do not touch the voxels; the character mover moves them.
        var kind = _physics.Kinds[handle.Value];
        if ((body.Kinematic && !frozen) || kind == BodyKind.StandIn)
        {
            return;
        }

        var bounds = body.BoundingBox;
        var velocity = frozen ? kept.Velocity.Linear : body.Velocity.Linear;
        var move = Vector3.Abs(velocity) * stepSeconds;
        if (!float.IsFinite(move.X + move.Y + move.Z))
        {
            move = Vector3.Zero;
        }

        var reach = new Vector3(MarginMetres) + Vector3.Min(move, new Vector3(MostReachMetres));
        if (!float.IsFinite(bounds.Min.X + bounds.Min.Y + bounds.Min.Z + bounds.Max.X + bounds.Max.Y + bounds.Max.Z))
        {
            // A body gone out of the world's numbers needs no cells (LooseBodySystem removes one that fell out).
            return;
        }

        // The finest level whose columns are resident round it, from its own: for a body cut off a character, up to
        // the coarse level. A frozen body, or one going back to a finer level, needs the columns a cell farther out, so
        // a body at the edge of a level does not move between them in turn.
        var own = _physics.Kinds.BaseLevel(handle.Value);
        var now = _physics.Kinds.Level(handle.Value);
        var coarsest =
            kind == BodyKind.Cut ? Math.Max(own, Math.Min(CoarseLevelOf(_voxels.Grid), _voxels.Levels - 1)) : own;
        var level = -1;
        int x0 = 0,
            y0 = 0,
            z0 = 0,
            x1 = 0,
            y1 = 0,
            z1 = 0;
        for (var candidate = own; candidate <= coarsest; candidate++)
        {
            // In absolute voxels of the level, from the body's local bounds through the origin.
            var perMetre = (double)VoxelConstants.CentimetresPerMetre / _voxels.Grid.Edge(candidate);
            var origin = _voxels.Origin;
            x0 = (int)Math.Floor(origin.WorldX(bounds.Min.X - reach.X) * perMetre);
            y0 = (int)Math.Floor(origin.WorldY(bounds.Min.Y - reach.Y) * perMetre);
            z0 = (int)Math.Floor(origin.WorldZ(bounds.Min.Z - reach.Z) * perMetre);
            x1 = (int)Math.Floor(origin.WorldX(bounds.Max.X + reach.X) * perMetre);
            y1 = (int)Math.Floor(origin.WorldY(bounds.Max.Y + reach.Y) * perMetre);
            z1 = (int)Math.Floor(origin.WorldZ(bounds.Max.Z + reach.Z) * perMetre);
            var extra = frozen || candidate < now ? CellSize : 0;
            if (_voxels.ColumnsResident(candidate, x0 - extra, z0 - extra, x1 + extra, z1 + extra))
            {
                level = candidate;
                break;
            }
        }

        if (level < 0 || !ObjectsKnown(bounds.Min - reach, bounds.Max + reach))
        {
            // A body a game keeps moving goes on, meeting no voxels until they load (nor objects until they are known).
            if (!frozen && (_physics.Kinds.Marks(handle.Value) & PhysicsMarks.KeepsMovingUnloaded) == 0)
            {
                _freeze.Add(handle);
            }

            return;
        }

        if (frozen)
        {
            _thaw.Add(handle);
        }

        if (level != now)
        {
            _physics.Kinds.SetLevel(handle, level);
        }

        for (var cz = z0 >> CellShift; cz <= z1 >> CellShift; cz++)
        {
            for (var cy = y0 >> CellShift; cy <= y1 >> CellShift; cy++)
            {
                for (var cx = x0 >> CellShift; cx <= x1 >> CellShift; cx++)
                {
                    ref var cell = ref CollectionsMarshal.GetValueRefOrAddDefault(
                        _cells,
                        new CellKey(level, cx, cy, cz),
                        out var exists
                    );
                    if (!exists)
                    {
                        cell.Dirty = true;
                    }

                    cell.LastNeeded = _step;
                }
            }
        }
    }

    /// <summary>
    /// Makes each ragdoll freeze and thaw as one (<see cref="BodyKinds.Ragdoll"/>): every body of a ragdoll one of whose
    /// bodies is to freeze freezes with it, and a frozen ragdoll's bodies thaw only when all of them may, noting it in
    /// <see cref="_groups"/> for its joints. Walks the bodies once for each ragdoll concerned; that is rare.
    /// </summary>
    private void JoinGroups()
    {
        var bodies = _physics.Simulation.Bodies;
        var count = _freeze.Count;
        for (var i = 0; i < count; i++)
        {
            var group = _physics.Kinds.Ragdoll(_freeze[i].Value);
            if (group == 0 || IndexOf(_freeze, group, i) >= 0)
            {
                continue;
            }

            ForEachMember(bodies, group, freeze: true);
        }

        // Thawing: a ragdoll whose bodies do not all thaw now stays frozen.
        for (var i = _thaw.Count - 1; i >= 0; i--)
        {
            var group = _physics.Kinds.Ragdoll(_thaw[i].Value);
            if (group == 0 || _groups.Contains(group))
            {
                continue;
            }

            if (ForEachMember(bodies, group, freeze: false))
            {
                _groups.Add(group);
            }
        }

        for (var i = _thaw.Count - 1; i >= 0; i--)
        {
            var group = _physics.Kinds.Ragdoll(_thaw[i].Value);
            if (group != 0 && !_groups.Contains(group))
            {
                _thaw.RemoveAt(i);
            }
        }
    }

    /// <summary>The index before <paramref name="before"/> in <paramref name="handles"/> of a body of <paramref name="group"/>, or -1.</summary>
    private int IndexOf(List<BodyHandle> handles, int group, int before)
    {
        for (var i = 0; i < before; i++)
        {
            if (_physics.Kinds.Ragdoll(handles[i].Value) == group)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// With <paramref name="freeze"/>, adds every dynamic body of <paramref name="group"/> not yet frozen or freezing to
    /// the freezing; else returns whether every frozen body of it thaws this step and none freezes.
    /// </summary>
    private bool ForEachMember(Bodies bodies, int group, bool freeze)
    {
        for (var set = 0; set < bodies.Sets.Length; set++)
        {
            ref var bodySet = ref bodies.Sets[set];
            if (!bodySet.Allocated)
            {
                continue;
            }

            for (var i = 0; i < bodySet.Count; i++)
            {
                var handle = bodySet.IndexToHandle[i];
                if (_physics.Kinds.Ragdoll(handle.Value) != group)
                {
                    continue;
                }

                var frozen = _frozen.ContainsKey(handle);
                if (freeze)
                {
                    if (!frozen && !_freeze.Contains(handle) && !bodies[handle].Kinematic)
                    {
                        _freeze.Add(handle);
                    }
                }
                else if (_freeze.Contains(handle) || (frozen && !_thaw.Contains(handle)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void Freeze(BodyReference body)
    {
        _frozen.Add(body.Handle, new Frozen(body.LocalInertia, body.Velocity));
        body.SetLocalInertia(default);
        body.Velocity = default;
        var position = body.Pose.Position;
        LogFrozen(body.Handle.Value, position.X, position.Y, position.Z);
    }

    private void Thaw(BodyReference body)
    {
        if (!_frozen.Remove(body.Handle, out var kept))
        {
            return;
        }

        body.SetLocalInertia(kept.Inertia);
        body.Velocity = kept.Velocity;
        body.Awake = true;
        LogThawed(body.Handle.Value);
    }

    /// <summary>Marks the cells the world's changes touched since the last step.</summary>
    private void ReadChanges()
    {
        int read;
        do
        {
            read = _voxels.ReadChanges(_cursor!, _changes);
            for (var i = 0; i < read; i++)
            {
                ref readonly var change = ref _changes[i];
                // Cells of the finest levels to the coarse one: a giant's, and a cut body's where its own is not resident.
                var level = change.Column.Level;
                if (level > CoarseLevelOf(_voxels.Grid))
                {
                    continue;
                }

                if (change.Kind == VoxelChangeKind.BrickChanged)
                {
                    var chunk = change.Column.Chunk(change.Layer, _voxels.Grid);
                    // Brick coordinates in the chunk are 0 to 15; a cell holds 4 bricks along each axis.
                    const int BricksPerCellShift = CellShift - VoxelConstants.BrickShift;
                    var key = new CellKey(
                        level,
                        (chunk.X << (VoxelConstants.ChunkShift - CellShift))
                            + ((change.Brick & 15) >> BricksPerCellShift),
                        (chunk.Y << (VoxelConstants.ChunkShift - CellShift))
                            + (((change.Brick >> 4) & 15) >> BricksPerCellShift),
                        (chunk.Z << (VoxelConstants.ChunkShift - CellShift))
                            + ((change.Brick >> 8) >> BricksPerCellShift)
                    );
                    ref var cell = ref CollectionsMarshal.GetValueRefOrNullRef(_cells, key);
                    if (!Unsafe.IsNullRef(ref cell))
                    {
                        cell.Dirty = true;
                    }
                }
                else if (change.Kind == VoxelChangeKind.ChunkChanged)
                {
                    // The change's blocks of 32 voxels are the cells: 4 along each axis of the chunk.
                    const int CellsPerChunkShift = VoxelConstants.ChunkShift - CellShift;
                    const int CellsPerChunk = 1 << CellsPerChunkShift;
                    var chunk = change.Column.Chunk(change.Layer, _voxels.Grid);
                    for (var z = 0; z < CellsPerChunk; z++)
                    {
                        for (var y = 0; y < CellsPerChunk; y++)
                        {
                            for (var x = 0; x < CellsPerChunk; x++)
                            {
                                if ((change.Blocks & (1UL << (x + CellsPerChunk * (y + CellsPerChunk * z)))) == 0)
                                {
                                    continue;
                                }

                                var key = new CellKey(
                                    level,
                                    (chunk.X << CellsPerChunkShift) + x,
                                    (chunk.Y << CellsPerChunkShift) + y,
                                    (chunk.Z << CellsPerChunkShift) + z
                                );
                                ref var cell = ref CollectionsMarshal.GetValueRefOrNullRef(_cells, key);
                                if (!Unsafe.IsNullRef(ref cell))
                                {
                                    cell.Dirty = true;
                                }
                            }
                        }
                    }
                }
                else if (change.Kind == VoxelChangeKind.ColumnUnloaded)
                {
                    DropColumn(change.Column);
                }
            }
        } while (read == _changes.Length);

        if (_cursor!.NeedsResync)
        {
            // Changes were lost: every cell may be out of date.
            _keys.Clear();
            _keys.AddRange(_cells.Keys);
            foreach (var key in _keys)
            {
                CollectionsMarshal.GetValueRefOrNullRef(_cells, key).Dirty = true;
            }

            _cursor.EndResync();
            LogResync(_keys.Count);
        }
    }

    /// <summary>Removes the statics of a column of their level that unloaded.</summary>
    private void DropColumn(ColumnKey column)
    {
        const int CellsPerChunkShift = VoxelConstants.ChunkShift - CellShift;
        _keys.Clear();
        foreach (var key in _cells.Keys)
        {
            if (
                key.Level == column.Level
                && key.X >> CellsPerChunkShift == column.X
                && key.Z >> CellsPerChunkShift == column.Z
            )
            {
                _keys.Add(key);
            }
        }

        foreach (var key in _keys)
        {
            RemoveStatic(ref CollectionsMarshal.GetValueRefOrNullRef(_cells, key));
            _cells.Remove(key);
        }
    }

    /// <summary>
    /// Builds a cell's statics from the voxels, one for each physics material, removing those whose voxels hold nothing
    /// that blocks.
    /// </summary>
    private void Build(CellKey key, ref Cell cell)
    {
        cell.Dirty = false;
        cell.Built = true;
        CellsBuilt++;
        // The columns' surfaces are read once for the cell, at its first part with boxes: -1 until then.
        var surfaces = -1;
        for (var part = 0; part < _partMaterials.Length; part++)
        {
            BuildPart(key, ref cell.Parts[part], part, ref surfaces);
        }

        // Its objects at its first build: an edit changes the world's voxels, not theirs.
        Link(key, ref cell);
    }

    /// <summary>
    /// Builds the static of the cell's voxels of physics material <paramref name="index"/>, or removes it when they hold
    /// nothing that blocks.
    /// </summary>
    private void BuildPart(CellKey key, ref Part cell, int index, ref int surfacesRead)
    {
        int x0 = key.X << CellShift,
            y0 = key.Y << CellShift,
            z0 = key.Z << CellShift;
        _boxes.Build(
            _voxels,
            key.Level,
            x0,
            y0,
            z0,
            x0 + CellSize - 1,
            y0 + CellSize - 1,
            z0 + CellSize - 1,
            in _partExcluded[index]
        );
        if (_boxes.Count == 0)
        {
            RemovePart(ref cell);
            return;
        }

        var simulation = _physics.Simulation;
        var pool = _physics.Pool;
        // The edge of one of the cell's voxels.
        var metres = _voxels.Grid.EdgeMetres(key.Level);
        var boxes = _boxes.Boxes;
        if (surfacesRead < 0)
        {
            surfacesRead = key.Level == 0 && ReadSurfaces(x0, y0, z0) ? 1 : 0;
        }

        var surfaces = surfacesRead == 1;
        var count = 0;
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            if (surfaces && HasSurface(in box, x0, z0))
            {
                // The box less its top layer, and that layer column by column, each column whose surface lies inside its
                // top voxel (or over it) up to the surface.
                if (box.Y0 < box.Y1)
                {
                    AddBox(ref count, box with { Y1 = box.Y1 - 1 }, x0, y0, z0, key.Level, metres);
                }

                for (var z = box.Z0; z <= box.Z1; z++)
                {
                    for (var x = box.X0; x <= box.X1; x++)
                    {
                        var column = x - x0 + CellSize * (z - z0);
                        if (_columnTops[column] == box.Y1 + 1 && !double.IsNaN(_surfaces[column]))
                        {
                            AddSurfaceBox(ref count, x, box.Y1, z, _surfaces[column], x0, y0, z0, metres);
                        }
                        else
                        {
                            AddBox(ref count, new SolidBox(x, box.Y1, z, x, box.Y1, z), x0, y0, z0, key.Level, metres);
                        }
                    }
                }

                continue;
            }

            AddBox(ref count, box, x0, y0, z0, key.Level, metres);
        }

        pool.Take(count, out Buffer<CompoundChild> children);
        _children.CopyTo(0, children, 0, count);
        var shape = simulation.Shapes.Add(new BigCompound(children, simulation.Shapes, pool));
        BoxCount += count;
        if (cell.HasStatic)
        {
            // Changing the shape wakes bodies resting on the old one or touching the new one. Not Statics.SetShape: in
            // Bepu 2.5.0-beta.29 it moves the bounds but keeps the old shape, which is freed next.
            simulation.Statics.GetDescription(cell.Static, out var description);
            description.Shape = shape;
            simulation.Statics.ApplyDescription(cell.Static, description);
            simulation.Shapes.RemoveAndDispose(cell.Shape, pool);
            BoxCount -= cell.Boxes;
        }
        else
        {
            // The cell's corner, absolute, posed at its local place; its boxes are from the corner.
            var edge = _voxels.Grid.Edge(key.Level);
            var corner = _voxels.Origin.ToLocal(new VoxelCoord(x0 * edge, y0 * edge, z0 * edge));
            cell.Static = simulation.Statics.Add(new StaticDescription(corner, Quaternion.Identity, shape));
            _physics.Kinds.SetStatic(cell.Static, key.Level, _partMaterials[index], _physics.VoxelLayer);
            cell.HasStatic = true;
            StaticCount++;
        }

        cell.Shape = shape;
        cell.Boxes = count;
    }

    /// <summary>
    /// Where the world keeps densities (voxels coarser than 1 cm), reads the surface of each column of the level-0 cell
    /// from (x0, y0, z0) whose highest blocking voxel lies in it: where its density crosses one half
    /// (<see cref="VoxelWorld.SurfaceOver"/>), NaN where that is the voxel's top. False on a grid of 1 cm.
    /// </summary>
    private bool ReadSurfaces(int x0, int y0, int z0)
    {
        if (_voxels.Grid.BaseCentimetres == 1)
        {
            return false;
        }

        // One voxel over the cell too, so a column's top voxel at the cell's top is seen to be its highest.
        _voxels.ColumnTops(x0, z0, CellSize, CellSize, y0, y0 + CellSize, in _passable, _columnTops);
        var any = false;
        for (var i = 0; i < _columnTops.Length; i++)
        {
            var top = _columnTops[i];
            _surfaces[i] = double.NaN;
            if (top <= y0 || top > y0 + CellSize)
            {
                continue;
            }

            var surface = _voxels.SurfaceOver(x0 + i % CellSize, top, z0 + i / CellSize, in _passable);
            if (Math.Abs(surface - top) > 1e-3)
            {
                _surfaces[i] = surface;
                any = true;
            }
        }

        return any;
    }

    /// <summary>True when a column of the box has its highest voxel in the box's top layer and a surface off its top.</summary>
    private bool HasSurface(in SolidBox box, int x0, int z0)
    {
        for (var z = box.Z0; z <= box.Z1; z++)
        {
            for (var x = box.X0; x <= box.X1; x++)
            {
                var column = x - x0 + CellSize * (z - z0);
                if (_columnTops[column] == box.Y1 + 1 && !double.IsNaN(_surfaces[column]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Adds a box of whole voxels to the cell's children, its centre from the cell's corner.</summary>
    private void AddBox(ref int count, SolidBox box, int x0, int y0, int z0, int level, float metres)
    {
        var centre = new Vector3(
            (box.X0 + box.X1 + 1 - 2 * x0) * 0.5f * metres,
            (box.Y0 + box.Y1 + 1 - 2 * y0) * 0.5f * metres,
            (box.Z0 + box.Z1 + 1 - 2 * z0) * 0.5f * metres
        );
        Child(ref count) = new CompoundChild(
            new RigidPose(centre),
            _physics.VoxelBox(_voxels.Grid, level, box.SizeX, box.SizeY, box.SizeZ)
        );
    }

    /// <summary>
    /// Adds a level-0 column's top from voxel height <paramref name="y"/> up to its <paramref name="surface"/> (voxels),
    /// rounded to a millimetre.
    /// </summary>
    private void AddSurfaceBox(ref int count, int x, int y, int z, double surface, int x0, int y0, int z0, float metres)
    {
        var millimetres = Math.Max(1, (int)Math.Round((surface - y) * metres * 1000));
        var height = millimetres / 1000f;
        var centre = new Vector3((x - x0 + 0.5f) * metres, (y - y0) * metres + height * 0.5f, (z - z0 + 0.5f) * metres);
        Child(ref count) = new CompoundChild(new RigidPose(centre), _physics.SurfaceBox(_voxels.Grid, millimetres));
    }

    /// <summary>The next child of the cell being built, growing the buffer as needed.</summary>
    private ref CompoundChild Child(ref int count)
    {
        if (!_children.Allocated || count == _children.Length)
        {
            _physics.Pool.ResizeToAtLeast(ref _children, Math.Max(1024, count * 2), count);
        }

        return ref _children[count++];
    }

    private void RemoveStatic(ref Cell cell)
    {
        for (var part = 0; part < _partMaterials.Length; part++)
        {
            RemovePart(ref cell.Parts[part]);
        }

        Unlink(ref cell);
    }

    private void RemovePart(ref Part cell)
    {
        if (!cell.HasStatic)
        {
            return;
        }

        // Removing a static wakes the bodies resting on it; its record is reset, so a static that takes its handle next
        // is not of this level.
        _physics.RemoveStatic(cell.Static);
        _physics.Simulation.Shapes.RemoveAndDispose(cell.Shape, _physics.Pool);
        cell.HasStatic = false;
        StaticCount--;
        BoxCount -= cell.Boxes;
        cell.Boxes = 0;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Voxel colliders ready: cells of {Cell} voxels near bodies, at level 0 and at level {Coarse} for giants' bodies; {Parts} physics material(s) of voxels"
    )]
    private partial void LogInitialized(int cell, int coarse, int parts);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Body {Body} frozen at ({X:F2}, {Y:F2}, {Z:F2}) m: a column around it is not resident, or the objects round it not known yet"
    )]
    private partial void LogFrozen(int body, float x, float y, float z);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Body {Body} moves on: the columns and the objects around it are ready"
    )]
    private partial void LogThawed(int body);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Voxel colliders fell behind the change list; rebuilding {Cells} cells"
    )]
    private partial void LogResync(int cells);

    /// <summary>A cell: its level, and its place in cells of that level.</summary>
    private readonly record struct CellKey(int Level, int X, int Y, int Z);

    private readonly record struct Frozen(BodyInertia Inertia, BodyVelocity Velocity);

    private struct Cell
    {
        /// <summary>Its statics, one for each physics material of the voxels (<see cref="_partMaterials"/>).</summary>
        public Parts Parts;
        public bool Dirty;

        /// <summary>True once built: a cell never built holds nothing for bodies yet, so it is built at once.</summary>
        public bool Built;
        public long LastNeeded;

        /// <summary>The voxel objects that reach into it, given statics (<see cref="Link"/>); null before its first build.</summary>
        public List<ObjectId>? Objects;

        public readonly bool HasStatic
        {
            get
            {
                foreach (var part in Parts)
                {
                    if (part.HasStatic)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    [InlineArray(PhysicsMaterials.MostVoxelMaterials)]
    private struct Parts
    {
        private Part _part;
    }

    /// <summary>A cell's static of one physics material.</summary>
    private struct Part
    {
        public StaticHandle Static;
        public TypedIndex Shape;
        public bool HasStatic;
        public int Boxes;
    }
}
