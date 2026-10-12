using System.Numerics;

namespace Itera.Engine.Voxels;

/// <summary>
/// Models: small voxel volumes drawn at any position and turn (as meshes, <c>WorldMeshes</c>), such as one part of a character's
/// body or a loose body's piece. A model is one chunk tree per level of detail, from level 0 up to at most
/// <see cref="MaxLevels"/> - 1, in the models' voxel memory (the world's in a match with voxel terrain, where a lifted
/// piece's tree lies; else their own, freed when they go), and its box in level-0 voxels: from (0, 0, 0) to
/// <see cref="Size"/>, in the tree's own voxels at level 0 and the same box, halved per level, at the others. The owner
/// of the trees keeps them for as long as the model lives (a piece's belong to the edit log), and may change them: a
/// cut body part is a copy (<see cref="Copy"/>) edited in place (<see cref="Apply"/>), and <see cref="Replace"/>
/// uploads its new trees while the old ones stay drawn. The owner frees trees it made (<see cref="Free"/>) only after
/// <see cref="Remove"/> or <see cref="Replace"/> has taken them off the upload list. Thrown balls' pieces are made here
/// too (<see cref="MakeBall"/>). <c>WorldMeshes</c> meshes each new
/// or replaced model and uploads its meshes; a model is drawn once that is done. Handles are reused after <see cref="Remove"/>. Main thread only, except <see cref="Encode(ReadOnlySpan{byte})"/>;
/// allocates nothing once its tables have grown, except <see cref="Copy"/> and <see cref="Apply"/>, which take voxel
/// memory.
/// </summary>
public sealed class VoxelModels : IDisposable
{
    private readonly VoxelWorld? _world;

    // The models' own memory in a match without voxel terrain. Never limited: every model's and ball's encode may go
    // over a budget, as in the world's.
    private readonly VoxelMemory? _own;
    private volatile bool _ownLeft;
    private byte[]? _ballScratch;

    /// <summary>Levels a model may have: 0 to 2.</summary>
    public const int MaxLevels = 3;

    /// <summary>The widest a model's level-0 voxels may be, in centimetres.</summary>
    public const int MostUnitCentimetres = 100;

    /// <summary>
    /// The edge of a model's voxels at level 0, in centimetres: characters' parts, items, and loose bodies' pieces
    /// (a lift reads its piece a centimetre at a time). A model may be given another unit (<see cref="UnitCentimetres(int)"/>).
    /// </summary>
    public const int Centimetres = 1;

    /// <summary>The edge of the voxels at model level <paramref name="level"/> of a model of <see cref="Centimetres"/>, in centimetres.</summary>
    public static int LevelCentimetres(int level) => Centimetres << level;

    /// <summary>The edge of the voxels at model level <paramref name="level"/> of a model of <see cref="Centimetres"/>, in metres.</summary>
    public static float LevelMetres(int level) => LevelCentimetres(level) / (float)VoxelConstants.CentimetresPerMetre;

    /// <summary>
    /// The model voxel coordinate a level-<paramref name="level"/> voxel of a model is sampled at, in its level-0
    /// voxels: the rule fills, edits, heals, and splits of models use (today the world's,
    /// <see cref="VoxelConstants.SampleAt"/>; the world's may change without moving a model's voxels).
    /// </summary>
    public static int SampleAt(int coordinate, int level) => VoxelConstants.SampleAt(coordinate, level);

    private Model[] _models = new Model[64];
    private readonly Stack<int> _free = new();
    private readonly List<int> _pending = [];
    private readonly List<(uint Offset, int Words)> _freed = [];
    private readonly List<RetiredModel> _retired = [];
    private int _count;
    private int _revisions;

    /// <param name="world">The match's voxel world, whose memory the models share; null for a match without terrain.</param>
    public VoxelModels(VoxelWorld? world = null)
    {
        _world = world;
        // It takes no native memory until its first tree.
        _own = world is null ? new VoxelMemory(long.MaxValue) : null;
    }

    /// <summary>Models that exist.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// The memory the trees are in: the voxel world's in a match with terrain (readable once the world is initialised),
    /// else the models' own.
    /// </summary>
    internal VoxelMemory Memory => _own ?? _world!.ReadMemory;

    /// <summary>True when the trees are in <paramref name="world"/>'s memory.</summary>
    internal bool Follows(VoxelWorld world) => ReferenceEquals(_world, world);

    /// <summary>
    /// Has the models' memory left to the OS at exit (the voxel world's, <see cref="VoxelWorld.LeaveMemoryAtExit"/>), for
    /// a thread that encodes models (<see cref="Encode(ReadOnlySpan{byte})"/>) and did not stop in time. Any thread.
    /// </summary>
    public void LeaveMemoryAtExit()
    {
        if (_world is not null)
        {
            _world.LeaveMemoryAtExit();
        }
        else
        {
            _ownLeft = true;
        }
    }

    /// <summary>
    /// Frees the models' own memory, with every tree in it, unless it was left (<see cref="LeaveMemoryAtExit"/>); the
    /// world's stays the world's. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_own is not null && !_ownLeft)
        {
            _own.Dispose();
        }
    }

    /// <summary>
    /// A ball of <paramref name="material"/> made from nothing, for a thrown ball's body: the centimetres a sphere edit of
    /// <paramref name="radius"/> covers about its pivot's voxel (<see cref="VoxelEdit.Contains"/>), in a box
    /// <c>2 * radius + 1</c> centimetres a side. No lift made it, so its sequence is 0 and no edit log keeps it; its
    /// tree stays in the models' memory until they go, so a caller makes each ball once. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a radius outside 0 to 63 (the box must fit a chunk) and
    /// <see cref="ArgumentException"/> for air. Main thread.
    /// </summary>
    internal VoxelPiece MakeBall(int radius, byte material)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, (VoxelConstants.ChunkSize - 1) / 2);
        if (material == VoxelConstants.Air)
        {
            throw new ArgumentException("A ball of air holds nothing.", nameof(material));
        }

        var centre = new VoxelCoord(radius, radius, radius);
        var sphere = VoxelEdit.Sphere(centre, radius, VoxelEditOperation.Fill, material);
        var side = 2 * radius + 1;
        var size = new VoxelCoord(side, side, side);
        var scratch = _ballScratch ??= new byte[VoxelConstants.ChunkVoxels];
        VoxelWorld.ClearBox(scratch, size);
        var solid = 0;
        var farthest = 0L;
        for (var z = 0; z < side; z++)
        {
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    if (sphere.Contains(x, y, z))
                    {
                        scratch[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = material;
                        solid++;
                        farthest = Math.Max(farthest, VoxelPiece.FarthestCornerSquared(centre, x, y, z));
                    }
                }
            }
        }

        var tree = ChunkTree.Encode(Memory, scratch, size, mayExceedBudget: true, out _);
        return new VoxelPiece(0, default, size, tree, solid, VoxelPiece.RadiusOf(farthest));
    }

    /// <summary>
    /// Stores a chunk of voxels (128^3 materials at index <c>x + 128y + 16384z</c>) as a tree in the models' voxel
    /// memory, for <see cref="Add(ReadOnlySpan{ModelTree}, VoxelCoord, int)"/>. Any thread, once the voxel world is
    /// initialised in a match with terrain. The memory may go over its budget: models are few and small. The tree is
    /// kept until it is freed (<see cref="Free"/>) or the memory goes with the match.
    /// </summary>
    public ModelTree Encode(ReadOnlySpan<byte> voxels)
    {
        if (voxels.Length != VoxelConstants.ChunkVoxels)
        {
            throw new ArgumentException("A model level is a whole chunk of voxels.", nameof(voxels));
        }

        return new ModelTree(ChunkTree.Encode(Memory, voxels, mayExceedBudget: true, out _));
    }

    /// <summary>
    /// <see cref="Encode(ReadOnlySpan{byte})"/> for voxels that lie within the box from (0, 0, 0) to
    /// <paramref name="extent"/>: voxels outside it count as air, and bricks wholly outside it are not read, so a small
    /// part at a coarse level is encoded at a fraction of a chunk's cost.
    /// </summary>
    public ModelTree Encode(ReadOnlySpan<byte> voxels, VoxelCoord extent)
    {
        if (voxels.Length != VoxelConstants.ChunkVoxels)
        {
            throw new ArgumentException("A model level is a whole chunk of voxels.", nameof(voxels));
        }

        return new ModelTree(ChunkTree.Encode(Memory, voxels, extent, mayExceedBudget: true, out _));
    }

    /// <summary>
    /// <see cref="Encode(ReadOnlySpan{byte}, VoxelCoord)"/> with each voxel's density (<see cref="VoxelDensity"/>: how
    /// much of it is solid, 128 to 255 for a solid voxel and 0 to 127 for air, at the same index as its material), for
    /// a model meshed with densities as the world is: its surface lies where the density crosses one half, so a smooth
    /// shape shows no voxel steps. Any thread, as the other encodes.
    /// </summary>
    public ModelTree Encode(ReadOnlySpan<byte> voxels, ReadOnlySpan<byte> densities, VoxelCoord extent)
    {
        if (voxels.Length != VoxelConstants.ChunkVoxels || densities.Length != VoxelConstants.ChunkVoxels)
        {
            throw new ArgumentException("A model level is a whole chunk of voxels and of densities.", nameof(voxels));
        }

        return new ModelTree(ChunkTree.Encode(Memory, voxels, densities, extent, mayExceedBudget: true, out _));
    }

    /// <summary>
    /// A copy of <paramref name="tree"/>, so edits to either leave the other alone: a character's own copy of a part it
    /// was cut in. The owner frees it (<see cref="Free"/>). Main thread.
    /// </summary>
    public ModelTree Copy(ModelTree tree) => new(ChunkTree.Copy(Memory, tree.State, ChunkTree.RootDepth));

    /// <summary>
    /// Applies <paramref name="edit"/>, a sphere, capsule, or box given in the model's level-0 voxels, to
    /// <paramref name="tree"/>, the model's tree at <paramref name="level"/>, in place, by the world's rule (through the
    /// edit code models share with the world; see <see cref="SampleAt"/>): a voxel of level k changes when its sample
    /// point (<see cref="VoxelConstants.SampleAt"/>) lies in the edit, so every machine
    /// agrees. Only the bricks the edit reaches are decoded. Returns true when any voxel changed. The tree must be one the
    /// caller owns (a <see cref="Copy"/>), never one other models share; its model shows the change once
    /// <see cref="Replace"/>d. Main thread.
    /// </summary>
    public bool Apply(ref ModelTree tree, int level, in VoxelEdit edit)
    {
        if (edit.Shape == VoxelEditShape.Piece || edit.Operation is VoxelEditOperation.Lift or VoxelEditOperation.Reset)
        {
            throw new ArgumentException("A model takes sphere, capsule, and box edits only.", nameof(edit));
        }

        if ((uint)level >= MaxLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "A model has levels 0 to 2.");
        }

        Span<int> bricks = stackalloc int[VoxelConstants.NodeChildren * VoxelConstants.NodeChildren];
        var state = VoxelEditing.ApplyToChunk(
            Memory,
            VoxelGrid.Unit,
            tree.State,
            new ChunkKey(level, 0, 0, 0),
            in edit,
            bricks,
            out var changed
        );
        tree = new ModelTree(state);
        return changed > 0;
    }

    /// <summary>
    /// Adds to <paramref name="counts"/> (one entry per material, 256) the solid voxels of <paramref name="tree"/>, the
    /// model's tree at <paramref name="level"/>, that <paramref name="edit"/> (a sphere, capsule, or box in the model's level-0
    /// voxels) covers: what <see cref="Apply"/> with a clear would remove, counted without changing anything. Main
    /// thread; allocates nothing.
    /// </summary>
    public void CountInside(ModelTree tree, int level, in VoxelEdit edit, Span<int> counts)
    {
        if ((uint)level >= MaxLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "A model has levels 0 to 2.");
        }

        VoxelEditing.CountInChunk(Memory, VoxelGrid.Unit, tree.State, new ChunkKey(level, 0, 0, 0), in edit, counts);
    }

    /// <summary>The material of a tree's voxel (x, y, z); air outside 0 to 127. Main thread.</summary>
    public byte ReadVoxel(ModelTree tree, int x, int y, int z) =>
        (uint)x < VoxelConstants.ChunkSize && (uint)y < VoxelConstants.ChunkSize && (uint)z < VoxelConstants.ChunkSize
            ? ChunkTree.ReadVoxel(Memory, tree.State, x, y, z)
            : VoxelConstants.Air;

    /// <summary>Writes a tree's 128^3 materials into <paramref name="voxels"/>, index <c>x + 128y + 16384z</c>.</summary>
    public void Decode(ModelTree tree, Span<byte> voxels)
    {
        if (voxels.Length < VoxelConstants.ChunkVoxels)
        {
            throw new ArgumentException("A model level is a whole chunk of voxels.", nameof(voxels));
        }

        ChunkTree.Decode(Memory, tree.State, voxels);
    }

    /// <summary>
    /// Writes a tree's materials into <paramref name="voxels"/> for the bricks that reach into the box from (0, 0, 0) to
    /// <paramref name="extent"/>, leaving the rest of <paramref name="voxels"/> as it was.
    /// </summary>
    public void Decode(ModelTree tree, Span<byte> voxels, VoxelCoord extent)
    {
        if (voxels.Length < VoxelConstants.ChunkVoxels)
        {
            throw new ArgumentException("A model level is a whole chunk of voxels.", nameof(voxels));
        }

        ChunkTree.Decode(Memory, tree.State, voxels, extent);
    }

    /// <summary>
    /// Writes a tree's densities into <paramref name="densities"/> for the bricks that reach into the box from (0, 0, 0)
    /// to <paramref name="extent"/> (a voxel whose density was never given reads its material's default), leaving the
    /// rest as it was.
    /// </summary>
    public void DecodeDensities(ModelTree tree, Span<byte> densities, VoxelCoord extent)
    {
        if (densities.Length < VoxelConstants.ChunkVoxels)
        {
            throw new ArgumentException("A model level is a whole chunk of densities.", nameof(densities));
        }

        ChunkTree.DecodeDensities(Memory, tree.State, densities, extent);
    }

    /// <summary>
    /// Frees a tree the caller made (<see cref="Encode(ReadOnlySpan{byte})"/>, <see cref="Copy"/>) and no model waits to upload: after
    /// <see cref="Remove"/>, or <see cref="Replace"/> with other trees. Main thread.
    /// </summary>
    public void Free(ModelTree tree) => ChunkTree.Free(Memory, tree.State, ChunkTree.RootDepth);

    /// <summary>
    /// Finds the first solid voxel of <paramref name="tree"/> (a model's level-0 tree whose box is
    /// <paramref name="size"/>) along a ray from <paramref name="origin"/> along <paramref name="direction"/>, both in
    /// the model's level-0 voxels, within <paramref name="maxVoxels"/>. The ray is clipped to the box: it starts where it
    /// enters it (or at its origin, inside it) and stops where it leaves, so a ray that misses the box takes no step.
    /// Main thread; allocates nothing.
    /// </summary>
    public bool Raycast(
        ModelTree tree,
        VoxelCoord size,
        (double X, double Y, double Z) origin,
        (double X, double Y, double Z) direction,
        double maxVoxels,
        out ModelHit hit
    )
    {
        hit = default;
        var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z);
        if (!(length > 0) || !double.IsFinite(length))
        {
            throw new ArgumentException("The ray direction must be finite and not zero.", nameof(direction));
        }

        double dx = direction.X / length,
            dy = direction.Y / length,
            dz = direction.Z / length;
        if (
            !ChunkTree.Raycast(
                Memory,
                tree.State,
                size,
                origin.X,
                origin.Y,
                origin.Z,
                dx,
                dy,
                dz,
                maxVoxels,
                out var voxel,
                out var axis,
                out var material,
                out var t
            )
        )
        {
            return false;
        }

        var normal = axis switch
        {
            0 => new VoxelCoord(dx > 0 ? -1 : 1, 0, 0),
            1 => new VoxelCoord(0, dy > 0 ? -1 : 1, 0),
            2 => new VoxelCoord(0, 0, dz > 0 ? -1 : 1),
            _ => default,
        };
        hit = new ModelHit(voxel, normal, material, t);
        return true;
    }

    /// <summary>
    /// Adds a model of the trees in <paramref name="levels"/> (level <paramref name="baseLevel"/> first, up to level
    /// <see cref="MaxLevels"/> - 1) and the box <paramref name="size"/> in voxels of <paramref name="baseLevel"/> (1 to
    /// 128 on each axis), and returns its handle. A model whose finest level is coarser than 0 holds a larger volume in
    /// its 128 voxels: a whole character's body at 4 cm (level 2) stands up to 5.12 m. It is drawn once the uploader has
    /// copied it.
    /// </summary>
    public int Add(ReadOnlySpan<ModelTree> levels, VoxelCoord size, int baseLevel = 0)
    {
        CheckLevels(levels.Length, baseLevel);
        Span<ChildState> states = stackalloc ChildState[MaxLevels];
        for (var level = 0; level < levels.Length; level++)
        {
            states[level] = levels[level].State;
        }

        return Add(states[..levels.Length], size, baseLevel);
    }

    /// <summary>
    /// <see cref="Add(ReadOnlySpan{ModelTree}, VoxelCoord, int)"/> for a model whose level-0 voxels are
    /// <paramref name="unitCentimetres"/> across (1 to <see cref="MostUnitCentimetres"/>; its tree <c>i</c>'s are that
    /// times 2^(base + i)), meshed with the densities its trees keep when <paramref name="densities"/> is true (trees
    /// made by <see cref="Encode(ReadOnlySpan{byte}, ReadOnlySpan{byte}, VoxelCoord)"/>).
    /// </summary>
    public int Add(
        ReadOnlySpan<ModelTree> levels,
        VoxelCoord size,
        int baseLevel,
        int unitCentimetres,
        bool densities = false
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(unitCentimetres, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(unitCentimetres, MostUnitCentimetres);
        CheckLevels(levels.Length, baseLevel);
        Span<ChildState> states = stackalloc ChildState[MaxLevels];
        for (var level = 0; level < levels.Length; level++)
        {
            states[level] = levels[level].State;
        }

        var handle = Add(states[..levels.Length], size, baseLevel, unitCentimetres);
        _models[handle].Densities = densities;
        return handle;
    }

    /// <summary>
    /// <see cref="Add(ReadOnlySpan{ModelTree}, VoxelCoord, int)"/> for trees the engine holds, such as a lifted piece's,
    /// whose level-0 voxels are <paramref name="unitCentimetres"/> across.
    /// </summary>
    internal int Add(
        ReadOnlySpan<ChildState> levels,
        VoxelCoord size,
        int baseLevel = 0,
        int unitCentimetres = Centimetres
    )
    {
        CheckLevels(levels.Length, baseLevel);
        const int edge = VoxelConstants.ChunkSize;
        if (size.X is < 1 or > edge || size.Y is < 1 or > edge || size.Z is < 1 or > edge)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A model's box is 1 to 128 voxels on each axis.");
        }

        int handle;
        if (_free.Count > 0)
        {
            handle = _free.Pop();
        }
        else
        {
            if (_count == _models.Length)
            {
                Array.Resize(ref _models, _models.Length * 2);
            }

            handle = _count++;
        }

        ref var model = ref _models[handle];
        model = default;
        model.Live = true;
        model.Revision = ++_revisions;
        model.Levels = levels.Length;
        model.BaseLevel = baseLevel;
        model.Unit = unitCentimetres;
        model.Size = size;
        for (var level = 0; level < levels.Length; level++)
        {
            model.Trees[level] = levels[level];
        }

        _pending.Add(handle);
        Count++;
        return handle;
    }

    /// <summary>
    /// Removes a model and frees its trees: for a model whose trees the caller made for it alone and does not keep, such
    /// as a piece cut off a character, owned by the loose body that carries it.
    /// </summary>
    public void RemoveAndFree(int handle)
    {
        ref var model = ref Get(handle);
        var levels = model.Levels;
        Span<ChildState> trees = stackalloc ChildState[MaxLevels];
        for (var level = 0; level < levels; level++)
        {
            trees[level] = model.Trees[level];
        }

        // Off the upload list first, then the trees go.
        Remove(handle);
        for (var level = 0; level < levels; level++)
        {
            ChunkTree.Free(Memory, trees[level], ChunkTree.RootDepth);
        }
    }

    /// <summary>
    /// Removes a model. Its GPU ranges are freed at the next upload; frames already built may still draw it, as the
    /// world's freed ranges (the copies of later frames wait for their reads).
    /// </summary>
    public void Remove(int handle)
    {
        ref var model = ref Get(handle);
        for (var level = 0; level < model.Levels; level++)
        {
            if (model.RangeWords[level] > 0)
            {
                _freed.Add((model.RangeOffsets[level], model.RangeWords[level]));
            }
        }

        // Waiting for its first upload, or with replaced trees.
        _pending.Remove(handle);
        model = default;
        _free.Push(handle);
        Count--;
    }

    /// <summary>
    /// Makes <paramref name="handle"/> share the meshes of <paramref name="baseHandle"/> (a model of the same box, unit,
    /// levels, densities, and owned window) for each 32-voxel block of each level that <paramref name="changed"/> does not
    /// mark (<c>changed[level][block]</c>, blocks as <see cref="ModelBlocks"/> counts them): the renderer meshes only the
    /// marked blocks and draws the base's meshes for the rest, which it never frees with this model. The base stays
    /// unchanged and live while this model is (a damaged object part's kind's part, <c>ObjectDamage</c>). The arrays stay
    /// the caller's and are read at each meshing: change them, then <see cref="Replace"/>. Main thread.
    /// </summary>
    public void ShareBlocks(int handle, int baseHandle, bool[][] changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ref var model = ref Get(handle);
        ref readonly var shared = ref Get(baseHandle);
        if (shared.Size != model.Size || shared.Levels != model.Levels || shared.Unit != model.Unit)
        {
            throw new ArgumentException(
                "A model shares meshes only with a model of its box, levels, and unit.",
                nameof(baseHandle)
            );
        }

        model.Base = baseHandle + 1;
        model.Changed = changed;
    }

    /// <summary>The model whose meshes <paramref name="handle"/> shares (<see cref="ShareBlocks"/>), or -1.</summary>
    internal int BaseOf(int handle) => Get(handle).Base - 1;

    /// <summary>True when block <paramref name="block"/> of level <paramref name="tree"/> is meshed for this model itself.</summary>
    internal bool MeshesBlock(int handle, int tree, int block) =>
        Get(handle).Changed is not { } changed || changed[tree][block];

    /// <summary>
    /// Gives a model new trees, as many levels as it has, such as a cut part's after <see cref="Apply"/>. A model on
    /// the GPU stays drawn from its old ranges until the uploader has copied the new trees, then those ranges are freed,
    /// so it never vanishes for a frame. The old trees are no longer read: the owner may free them now, unless they are
    /// the new ones edited in place.
    /// </summary>
    public void Replace(int handle, ReadOnlySpan<ModelTree> levels)
    {
        ref var model = ref Get(handle);
        if (levels.Length != model.Levels)
        {
            throw new ArgumentException("A model keeps its number of levels.", nameof(levels));
        }

        for (var level = 0; level < levels.Length; level++)
        {
            model.Trees[level] = levels[level].State;
        }

        model.Revision = ++_revisions;
        if (!_pending.Contains(handle))
        {
            _pending.Add(handle);
        }
    }

    /// <summary>
    /// Retires a model whose voxels an edit has just put into the world (a loose body going back into the grid): it
    /// stays drawn at level <paramref name="level"/> with its box's corner at <paramref name="corner"/> (world metres),
    /// turned by <paramref name="turn"/> and blended <paramref name="tint"/> towards its tint colour, until the world's GPU
    /// copy holds every edit made so far, and is removed then. So no frame shows neither the model nor its voxels. In a
    /// run without graphics it is removed after the next frame.
    /// </summary>
    public void Retire(int handle, int level, Vector3 corner, Quaternion turn, float tint)
    {
        Get(handle);
        _retired.Add(new RetiredModel(handle, level, corner, turn, tint));
    }

    /// <summary>Moves the retired models' corners with the match's origin, each less <paramref name="metres"/>.</summary>
    internal void ShiftRetired(Vector3 metres)
    {
        var retired = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_retired);
        for (var i = 0; i < retired.Length; i++)
        {
            retired[i] = retired[i] with { Corner = retired[i].Corner - metres };
        }
    }

    /// <summary>Models retired and still drawn at their last place.</summary>
    public ReadOnlySpan<RetiredModel> Retired => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_retired);

    /// <summary>
    /// Removes the retired models: the world's GPU copy now holds the edits made before they were retired. For the
    /// uploader, and after a frame without graphics (no GPU copy to wait for).
    /// </summary>
    internal void RemoveRetired()
    {
        foreach (var retired in _retired)
        {
            Remove(retired.Handle);
        }

        _retired.Clear();
    }

    /// <summary>True when the model's every level is on the GPU.</summary>
    public bool IsUploaded(int handle) => Get(handle).Uploaded;

    /// <summary>
    /// A number that changes whenever the handle gets a model (<see cref="Add(ReadOnlySpan{ModelTree}, VoxelCoord, int)"/>)
    /// or new trees (<see cref="Replace"/>), never the same twice in a run; 0 for a handle with no model. For an uploader
    /// that works on a model over several frames, to tell whether what it copies is still the model's.
    /// </summary>
    internal int Revision(int handle) =>
        (uint)handle < (uint)_count && _models[handle].Live ? _models[handle].Revision : 0;

    /// <summary>Levels of detail the model has.</summary>
    public int LevelCount(int handle) => Get(handle).Levels;

    /// <summary>
    /// The level of a model's first tree: 0 for most models, so its levels are 0 up; the voxels of its tree
    /// <c>i</c> are its unit times 2^(base + i) cm (<see cref="VoxelMetres"/>), and <see cref="Size"/> is in voxels of
    /// the base level.
    /// </summary>
    public int BaseLevel(int handle) => Get(handle).BaseLevel;

    /// <summary>The edge of a model's level-0 voxels in centimetres: <see cref="Centimetres"/> unless it was given another.</summary>
    public int UnitCentimetres(int handle) => Get(handle).Unit;

    /// <summary>True when the model's trees keep densities and it is meshed with them.</summary>
    public bool HasDensities(int handle) => Get(handle).Densities;

    /// <summary>
    /// Makes a model one part of a larger whole whose neighbours' voxels lie round it in its own box (a border it reads
    /// but does not draw): it owns the box of its level-0 voxels from <paramref name="ownedMin"/> up to but not including
    /// <paramref name="ownedMax"/>, so the faces between parts are made once, by the part that holds their lower voxel.
    /// On each axis whose <paramref name="openLow"/> is 1 (no neighbour there) it also owns the faces against the voxel
    /// before <paramref name="ownedMin"/>; a coarser tree owns the same box halved per level. Before the model is meshed.
    /// </summary>
    public void SetOwned(int handle, VoxelCoord ownedMin, VoxelCoord ownedMax, VoxelCoord openLow)
    {
        ref var model = ref Get(handle);
        model.Owned = true;
        model.OwnedMin = ownedMin;
        model.OwnedMax = ownedMax;
        model.OpenLow = openLow;
    }

    /// <summary>
    /// The box of tree <paramref name="tree"/>'s voxels the model owns the faces of (their lower voxel): from its first
    /// voxel's low neighbour to its box's end for a whole model, or its part's own box (<see cref="SetOwned"/>).
    /// </summary>
    internal (VoxelCoord Min, VoxelCoord Max) OwnedAt(int handle, int tree)
    {
        ref readonly var model = ref Get(handle);
        var cell = (1 << tree) - 1;
        if (!model.Owned)
        {
            return (
                new VoxelCoord(-1, -1, -1),
                new VoxelCoord(
                    (model.Size.X + cell) >> tree,
                    (model.Size.Y + cell) >> tree,
                    (model.Size.Z + cell) >> tree
                )
            );
        }

        return (
            new VoxelCoord(
                (model.OwnedMin.X >> tree) - model.OpenLow.X,
                (model.OwnedMin.Y >> tree) - model.OpenLow.Y,
                (model.OwnedMin.Z >> tree) - model.OpenLow.Z
            ),
            new VoxelCoord(
                (model.OwnedMax.X + cell) >> tree,
                (model.OwnedMax.Y + cell) >> tree,
                (model.OwnedMax.Z + cell) >> tree
            )
        );
    }

    /// <summary>True when the model is a part that owns only a box of its voxels (<see cref="SetOwned"/>).</summary>
    public bool IsPart(int handle) => Get(handle).Owned;

    /// <summary>The edge of the voxels of a model's tree <paramref name="tree"/> (counted from its base level), in metres.</summary>
    public float VoxelMetres(int handle, int tree)
    {
        ref readonly var model = ref Get(handle);
        return (model.Unit << (model.BaseLevel + tree)) / (float)VoxelConstants.CentimetresPerMetre;
    }

    /// <summary>The model's box in level-0 voxels.</summary>
    public VoxelCoord Size(int handle) => Get(handle).Size;

    /// <summary>
    /// The GPU entry of level <paramref name="level"/>'s tree: a heap word offset, one material, or 0 for air. Valid
    /// once <see cref="IsUploaded"/>.
    /// </summary>
    public uint Entry(int handle, int level)
    {
        ref var model = ref Get(handle);
        if ((uint)level >= (uint)model.Levels)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "The model has no such level.");
        }

        return model.Entries[level];
    }

    /// <summary>Handles of models added and not yet on the GPU, oldest first. For the uploader.</summary>
    internal List<int> Pending => _pending;

    /// <summary>GPU ranges of removed models, for the uploader to free. It clears the list.</summary>
    internal List<(uint Offset, int Words)> Freed => _freed;

    /// <summary>The tree of level <paramref name="level"/>. For the uploader.</summary>
    internal ChildState Tree(int handle, int level) => Get(handle).Trees[level];

    /// <summary>
    /// Records that level <paramref name="level"/> is on the GPU with its entry and range. A range the level had before
    /// (a replaced model's) is freed at the next upload, after this frame's reads. For the uploader.
    /// </summary>
    internal void SetLevel(int handle, int level, uint entry, uint rangeOffset, int rangeWords)
    {
        ref var model = ref Get(handle);
        if (model.RangeWords[level] > 0)
        {
            _freed.Add((model.RangeOffsets[level], model.RangeWords[level]));
        }

        model.Entries[level] = entry;
        model.RangeOffsets[level] = rangeOffset;
        model.RangeWords[level] = rangeWords;
    }

    /// <summary>
    /// Records that every level is on the GPU and takes the model off the pending list. For the uploader.
    /// </summary>
    internal void SetUploaded(int handle)
    {
        Get(handle).Uploaded = true;
        _pending.Remove(handle);
    }

    private static void CheckLevels(int count, int baseLevel = 0)
    {
        if (baseLevel is < 0 || count < 1 || baseLevel + count > MaxLevels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                "A model has 1 to 3 levels, from its base level up to level 2."
            );
        }
    }

    private ref Model Get(int handle)
    {
        if ((uint)handle >= (uint)_count || !_models[handle].Live)
        {
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "No such model.");
        }

        return ref _models[handle];
    }

    private struct Model
    {
        public bool Live;
        public bool Uploaded;
        public int Revision;
        public int Levels;
        public int BaseLevel;
        public int Unit;
        public bool Densities;
        public bool Owned;
        public VoxelCoord OwnedMin;
        public VoxelCoord OwnedMax;
        public VoxelCoord OpenLow;
        public VoxelCoord Size;

        /// <summary>The model whose meshes it shares, plus one (0 for none), and its blocks that differ (per level).</summary>
        public int Base;
        public bool[][]? Changed;
        public Levels<ChildState> Trees;
        public Levels<uint> Entries;
        public Levels<uint> RangeOffsets;
        public Levels<int> RangeWords;
    }

    /// <summary>One value per level of a model.</summary>
    [System.Runtime.CompilerServices.InlineArray(MaxLevels)]
    private struct Levels<T>
    {
        private T _first;
    }
}

/// <summary>One level of a model: a chunk tree in the voxel memory, made by <see cref="VoxelModels.Encode(ReadOnlySpan{byte})"/>.</summary>
public readonly struct ModelTree
{
    internal ModelTree(ChildState state)
    {
        State = state;
    }

    internal ChildState State { get; }
}

/// <summary>Where a ray met a model's solid voxel (<see cref="VoxelModels.Raycast"/>), in the model's level-0 voxels.</summary>
/// <param name="Voxel">The solid voxel.</param>
/// <param name="Normal">
/// The face the ray entered through, as a unit step; zero when the ray started inside the voxel.
/// </param>
/// <param name="Material">The voxel's material.</param>
/// <param name="Distance">Distance along the ray to the entry point, in level-0 voxels.</param>
public readonly record struct ModelHit(VoxelCoord Voxel, VoxelCoord Normal, byte Material, double Distance);

/// <summary>
/// A model drawn at its last place until the world's GPU copy holds its voxels (<see cref="VoxelModels.Retire"/>).
/// </summary>
public readonly record struct RetiredModel(int Handle, int Level, Vector3 Corner, Quaternion Turn, float Tint);
