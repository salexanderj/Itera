using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>A kind registered in <see cref="VoxelObjectKinds"/>, by its number.</summary>
public readonly record struct VoxelObjectKindId
{
    /// <summary>Its place in the registry.</summary>
    public readonly ushort Value;

    public VoxelObjectKindId(ushort value) => Value = value;
}

/// <summary>
/// The run's object kinds, by name: trees, bushes, rocks, and props a world places (<see cref="VoxelObjectShape"/>),
/// registered once a run, at the game's <c>IRunStartup</c> (or a mod's), before the first match, which seals them; at
/// most <see cref="MaxKinds"/>. Each kind is split into parts that own at most 112 voxels a side, each kept as the three levels
/// a model draws (its voxels, twice them, and four times them), so every match makes its models from them without
/// building them again (<see cref="VoxelObjects"/>). Main thread.
/// </summary>
public sealed class VoxelObjectKinds
{
    /// <summary>The most kinds a run registers.</summary>
    public const int MaxKinds = 4096;

    private readonly List<VoxelObjectKind> _kinds = [];
    private readonly Dictionary<string, VoxelObjectKindId> _byName = new(StringComparer.Ordinal);

    /// <summary>How many kinds there are.</summary>
    public int Count => _kinds.Count;

    /// <summary>True once a match has begun: no kind is registered from then on.</summary>
    public bool IsSealed { get; private set; }

    /// <summary>The kind <paramref name="id"/> names.</summary>
    public VoxelObjectKind this[VoxelObjectKindId id] => _kinds[id.Value];

    /// <summary>
    /// Registers the kind <paramref name="name"/> of <paramref name="shape"/>'s voxels and returns its number. Far away it
    /// is drawn as a coarse copy in the world's voxels, at the levels whose voxels are no wider than
    /// <paramref name="copiesUpToCentimetres"/> (0: half the widest of its box's sides, so a tree 6 m wide and 9 m tall is
    /// copied in voxels up to 4.5 m; a smaller thing past them is not drawn at all). When an edit cuts it apart, the pieces
    /// <paramref name="roots"/> says stay, and the others fall (<see cref="ObjectRoots"/>; a kind rooted in the ground with
    /// no solid voxel at its foot, its bottom at or below its anchor, keeps its largest piece instead). The shape is read
    /// now and not kept. Throws for a name registered already, a full registry, a shape with no solid voxel, a negative
    /// reach, or once sealed.
    /// </summary>
    public VoxelObjectKindId Register(
        string name,
        VoxelObjectShape shape,
        int copiesUpToCentimetres = 0,
        ObjectRoots roots = ObjectRoots.Ground
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentOutOfRangeException.ThrowIfNegative(copiesUpToCentimetres);
        if (IsSealed)
        {
            throw new InvalidOperationException(
                $"Object kind {name} comes too late: kinds are registered before the first match."
            );
        }

        if (_byName.ContainsKey(name))
        {
            throw new InvalidOperationException($"Object kind {name} is registered already.");
        }

        if (_kinds.Count == MaxKinds)
        {
            throw new InvalidOperationException($"A run registers at most {MaxKinds} object kinds.");
        }

        // A kind rooted in the ground with no voxel at its foot would lose every piece at its first cut: its largest stays.
        if (roots == ObjectRoots.Ground && !HasRoots(shape))
        {
            roots = ObjectRoots.Largest;
        }

        var id = new VoxelObjectKindId((ushort)_kinds.Count);
        var kind = VoxelObjectKind.Build(id, name, shape, copiesUpToCentimetres, roots);
        _kinds.Add(kind);
        _byName.Add(name, id);
        MostReachCentimetres = Math.Max(MostReachCentimetres, kind.ReachCentimetres);
        return id;
    }

    /// <summary>True when a solid voxel of <paramref name="shape"/> has its bottom at or below its anchor.</summary>
    private static bool HasRoots(VoxelObjectShape shape)
    {
        var top = Math.Min(shape.Size.Y - 1, shape.AnchorCentimetres.Y / shape.UnitCentimetres);
        for (var y = 0; y <= top; y++)
        {
            for (var z = 0; z < shape.Size.Z; z++)
            {
                for (var x = 0; x < shape.Size.X; x++)
                {
                    if (shape.MaterialAt(x, y, z) != VoxelConstants.Air)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>The farthest any kind reaches across from its anchor (<see cref="VoxelObjectKind.ReachCentimetres"/>); 0 with none.</summary>
    public int MostReachCentimetres { get; private set; }

    /// <summary>The kind called <paramref name="name"/>, when one is registered.</summary>
    public bool TryFind(string name, out VoxelObjectKindId id) => _byName.TryGetValue(name, out id);

    /// <summary>
    /// The id of the world <paramref name="generator"/> makes with <paramref name="kinds"/>: the generator's, and where
    /// it places objects of any kind, the kinds' <see cref="Fingerprint"/> after it (<c>game.world+objects-…</c>), so
    /// games, saves, and joins whose kinds or rules differ refuse each other. The match's identity and a save's header
    /// carry it.
    /// </summary>
    public static string WorldId(IChunkGenerator generator, VoxelObjectKinds? kinds)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return generator.Objects is not null && kinds is { Count: > 0 }
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{generator.Id}+objects-{kinds.Fingerprint:x16}"
            )
            : generator.Id;
    }

    /// <summary>
    /// A number that stands for every kind's name and voxels, in their order, and the engine's rules for their copies in
    /// the world's voxels (<see cref="ObjectCopies.RuleVersion"/>) and for what edits do to them
    /// (<see cref="ObjectEditing.RuleVersion"/>): peers whose kinds or rules differ build different worlds. 0 with no kinds.
    /// </summary>
    public ulong Fingerprint
    {
        get
        {
            if (_kinds.Count == 0)
            {
                return 0;
            }

            var hash = Maths.DeterministicMath.Hash((ulong)ObjectCopies.RuleVersion, (ulong)ObjectEditing.RuleVersion);
            foreach (var kind in _kinds)
            {
                hash = Maths.DeterministicMath.Hash(hash, kind.Fingerprint);
            }

            return hash;
        }
    }

    /// <summary>
    /// Seals the kinds for a world of <paramref name="grid"/>: no kind is registered from then on, and every kind's voxels
    /// must be finer than the world's. Throws naming the first kind whose voxels are not. At each match's start.
    /// </summary>
    internal void Seal(VoxelGrid grid)
    {
        var edge = grid.Edge(0);
        foreach (var kind in _kinds)
        {
            if (kind.UnitCentimetres >= edge)
            {
                throw new InvalidOperationException(
                    $"Object kind {kind.Name} has voxels of {kind.UnitCentimetres} cm, not finer than the world's {edge} cm."
                );
            }
        }

        IsSealed = true;
    }
}

/// <summary>
/// An object kind: its voxels split into parts that own at most 112 a side (<see cref="PartCount"/>), each kept as three
/// levels of materials and densities, and the point it stands on.
/// </summary>
public sealed class VoxelObjectKind : IObjectVoxelSource
{
    private VoxelObjectKind(
        VoxelObjectKindId id,
        string name,
        int unit,
        VoxelCoord size,
        VoxelCoord anchor,
        ObjectPart[] parts,
        ulong fingerprint,
        int copiesUpTo,
        MaterialSet materials,
        ObjectRoots roots
    )
    {
        Id = id;
        Roots = roots;
        CopiesUpToCentimetres = copiesUpTo;
        Materials = materials;
        Name = name;
        UnitCentimetres = unit;
        Size = size;
        AnchorCentimetres = anchor;
        Parts = parts;
        Fingerprint = fingerprint;
        // The farthest corner of its box across from the anchor, which a heading turns round the anchor.
        long most = 0;
        foreach (var x in (ReadOnlySpan<long>)[0, (long)size.X * unit])
        {
            foreach (var z in (ReadOnlySpan<long>)[0, (long)size.Z * unit])
            {
                long dx = x - anchor.X,
                    dz = z - anchor.Z;
                most = Math.Max(most, dx * dx + dz * dz);
            }
        }

        var reach = Maths.DeterministicMath.SquareRoot(most);
        ReachCentimetres = (long)reach * reach == most ? reach : reach + 1;
        BottomCentimetres = -anchor.Y;
        TopCentimetres = size.Y * unit - anchor.Y;
    }

    /// <summary>Its number.</summary>
    public VoxelObjectKindId Id { get; }

    /// <summary>Its name.</summary>
    public string Name { get; }

    /// <summary>The edge of its voxels, in centimetres.</summary>
    public int UnitCentimetres { get; }

    /// <summary>Its box, in voxels.</summary>
    public VoxelCoord Size { get; }

    /// <summary>The point placed on a placement's spot, in centimetres from the box's corner.</summary>
    public VoxelCoord AnchorCentimetres { get; }

    /// <summary>
    /// The farthest its box reaches across (in x and z) from its anchor at any heading, in centimetres, rounded up: a
    /// placement's object lies within this of its anchor.
    /// </summary>
    public int ReachCentimetres { get; }

    /// <summary>The bottom of its box from its anchor, in centimetres (below 0 when the box starts below the anchor).</summary>
    public int BottomCentimetres { get; }

    /// <summary>The top of its box from its anchor, in centimetres.</summary>
    public int TopCentimetres { get; }

    /// <summary>The widest world voxels it is copied into far away, in centimetres.</summary>
    public int CopiesUpToCentimetres { get; }

    /// <summary>Its voxels' materials.</summary>
    internal MaterialSet Materials { get; }

    /// <summary>Which of its pieces stay when an edit cuts it apart (<see cref="ObjectRoots"/>).</summary>
    public ObjectRoots Roots { get; }

    /// <summary>
    /// Makes its tables of parts and bricks (<see cref="Voxel"/>'s), so other threads may read its voxels. Main thread,
    /// before any other thread reads it (<c>VoxelObjects.Initialize</c>).
    /// </summary>
    internal void MakeTables()
    {
        PartIndexAt(0, 0, 0);
        foreach (var part in Parts)
        {
            part.Voxel(0, 0, 0, out _, out _);
        }
    }

    /// <summary>How many parts it is: the 112-voxel cubes of its box that hold anything.</summary>
    public int PartCount => Parts.Length;

    /// <summary>A number that stands for its name, voxels, and how far it is copied.</summary>
    public ulong Fingerprint { get; }

    internal ObjectPart[] Parts { get; }

    // Its parts by their place in the grid of 112-voxel cubes over its box: the part's index, or -1 for an empty cube.
    private int[]? _partAt;

    /// <summary>
    /// The index in <see cref="Parts"/> of the part at (px, py, pz) of the grid of 112-voxel cubes over its box, or -1 for
    /// an empty cube or one outside. Main thread at the first read, which makes its table.
    /// </summary>
    internal int PartIndexAt(int px, int py, int pz)
    {
        const int own = ObjectPart.OwnVoxels;
        int across = (Size.X + own - 1) / own,
            up = (Size.Y + own - 1) / own,
            deep = (Size.Z + own - 1) / own;
        if ((uint)px >= (uint)across || (uint)py >= (uint)up || (uint)pz >= (uint)deep)
        {
            return -1;
        }

        if (_partAt is null)
        {
            var table = new int[across * up * deep];
            Array.Fill(table, -1);
            for (var p = 0; p < Parts.Length; p++)
            {
                var offset = Parts[p].Offset;
                table[offset.X / own + across * (offset.Y / own + up * (offset.Z / own))] = p;
            }

            _partAt = table;
        }

        return _partAt[px + across * (py + up * pz)];
    }

    void IObjectVoxelSource.Voxel(int x, int y, int z, out byte material, out byte density) =>
        Voxel(x, y, z, out material, out density);

    /// <summary>
    /// The material and density of its level-0 voxel (x, y, z), in its own voxels from its box's corner: air of no
    /// density outside it. Main thread at the first read, which makes its tables.
    /// </summary>
    internal void Voxel(int x, int y, int z, out byte material, out byte density)
    {
        material = VoxelConstants.Air;
        density = VoxelDensity.Empty;
        if ((uint)x >= (uint)Size.X || (uint)y >= (uint)Size.Y || (uint)z >= (uint)Size.Z)
        {
            return;
        }

        const int own = ObjectPart.OwnVoxels;
        var index = PartIndexAt(x / own, y / own, z / own);
        if (index < 0)
        {
            return;
        }

        var part = Parts[index];
        const int border = ObjectPart.BorderVoxels;
        part.Voxel(
            x - part.Offset.X + border,
            y - part.Offset.Y + border,
            z - part.Offset.Z + border,
            out material,
            out density
        );
    }

    private readonly Dictionary<int, ObjectCopyGrid> _copies = [];

    /// <summary>Its copy for world voxels <paramref name="edge"/> centimetres across, made at the first ask. Main thread.</summary>
    internal ObjectCopyGrid CopyFor(int edge)
    {
        if (!_copies.TryGetValue(edge, out var copy))
        {
            copy = ObjectCopyGrid.Build(this, edge);
            _copies.Add(edge, copy);
        }

        return copy;
    }

    internal static VoxelObjectKind Build(
        VoxelObjectKindId id,
        string name,
        VoxelObjectShape shape,
        int copiesUpTo,
        ObjectRoots roots = ObjectRoots.Ground
    )
    {
        const int own = ObjectPart.OwnVoxels;
        var size = shape.Size;
        var parts = new List<ObjectPart>();
        for (var pz = 0; pz < size.Z; pz += own)
        {
            for (var py = 0; py < size.Y; py += own)
            {
                for (var px = 0; px < size.X; px += own)
                {
                    var offset = new VoxelCoord(px, py, pz);
                    var ownSize = new VoxelCoord(
                        Math.Min(own, size.X - px),
                        Math.Min(own, size.Y - py),
                        Math.Min(own, size.Z - pz)
                    );
                    if (ObjectPart.TryBuild(shape, offset, ownSize, out var part))
                    {
                        parts.Add(part);
                    }
                }
            }
        }

        if (parts.Count == 0)
        {
            throw new ArgumentException($"Object kind {name} has no solid voxel.", nameof(shape));
        }

        // A side with no part beyond it owns the faces against its border, which holds only air.
        var offsets = parts.Select(part => part.Offset).ToHashSet();
        foreach (var part in parts)
        {
            var at = part.Offset;
            part.OpenLow = new VoxelCoord(
                offsets.Contains(at with { X = at.X - own }) ? 0 : 1,
                offsets.Contains(at with { Y = at.Y - own }) ? 0 : 1,
                offsets.Contains(at with { Z = at.Z - own }) ? 0 : 1
            );
        }

        var used = new bool[256];
        foreach (var material in shape.Materials)
        {
            used[material] = true;
        }

        var materials = new List<byte>();
        for (var m = 1; m < used.Length; m++)
        {
            if (used[m])
            {
                materials.Add((byte)m);
            }
        }

        var widest = Math.Max(size.X, Math.Max(size.Y, size.Z)) * shape.UnitCentimetres;
        var copied = copiesUpTo > 0 ? copiesUpTo : widest / 2;
        return new VoxelObjectKind(
            id,
            name,
            shape.UnitCentimetres,
            size,
            shape.AnchorCentimetres,
            [.. parts],
            Maths.DeterministicMath.Hash(FingerprintOf(name, shape), (ulong)copied),
            copied,
            MaterialSet.Of([.. materials]),
            roots
        );
    }

    private static ulong FingerprintOf(string name, VoxelObjectShape shape)
    {
        // FNV-1a over the name, the unit, the box, the anchor, and every voxel's material and density.
        var hash = 14695981039346656037UL;
        void Add(ulong value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        foreach (var c in name)
        {
            Add(c);
        }

        Add((ulong)shape.UnitCentimetres);
        Add((ulong)(uint)shape.Size.X);
        Add((ulong)(uint)shape.Size.Y);
        Add((ulong)(uint)shape.Size.Z);
        Add((ulong)(uint)shape.AnchorCentimetres.X);
        Add((ulong)(uint)shape.AnchorCentimetres.Y);
        Add((ulong)(uint)shape.AnchorCentimetres.Z);
        var materials = shape.Materials;
        var densities = shape.Densities;
        for (var i = 0; i < materials.Length; i++)
        {
            Add(materials[i] | ((ulong)densities[i] << 8));
        }

        return hash;
    }
}

/// <summary>
/// One part of a kind: a box of at most 128 voxels a side at <see cref="Offset"/> (in the kind's voxels), kept at three
/// levels as the 8-voxel bricks that hold anything, each a material and density per voxel, or one material and density
/// for a brick that is all one (a crown's inside).
/// </summary>
internal sealed class ObjectPart
{
    /// <summary>The most voxels a part owns along each axis: 112, with its border 128, a chunk.</summary>
    public const int OwnVoxels = VoxelConstants.ChunkSize - 2 * BorderVoxels;

    /// <summary>
    /// The voxels of its neighbours a part holds round its own at level 0 (4 at level 1, 2 at level 2): the mesher reads
    /// two voxels past an edge for a vertex's normal and openness, so at every level a vertex at a seam is the same in
    /// both parts; a multiple of the coarsest level's voxel, so every level's box starts at one corner.
    /// </summary>
    public const int BorderVoxels = 8;

    private const int Brick = VoxelConstants.BrickSize;
    private const int BrickVoxels = Brick * Brick * Brick;

    private ObjectPart(VoxelCoord offset, VoxelCoord ownSize, PartLevel[] levels)
    {
        Offset = offset;
        OwnSize = ownSize;
        Size = new VoxelCoord(ownSize.X + 2 * BorderVoxels, ownSize.Y + 2 * BorderVoxels, ownSize.Z + 2 * BorderVoxels);
        Levels = levels;
    }

    /// <summary>The corner of the voxels it owns, in the kind's voxels; its box starts <see cref="BorderVoxels"/> before it.</summary>
    public VoxelCoord Offset { get; }

    /// <summary>The voxels it owns along each axis (level 0).</summary>
    public VoxelCoord OwnSize { get; }

    /// <summary>Its box with its border, in the kind's voxels (level 0): its model's box.</summary>
    public VoxelCoord Size { get; }

    /// <summary>1 on each axis whose low side has no part beyond it (it owns the faces against its border there).</summary>
    public VoxelCoord OpenLow { get; set; }

    public PartLevel[] Levels { get; }

    // Level 0's bricks by their packed place (x, y, z of 4 bits each): the brick's index, or -1 for none.
    private int[]? _brickAt;

    /// <summary>
    /// The material and density of level-0 voxel (x, y, z) of its box (its border's corner at 0): air of no density
    /// outside the box or in a brick it does not keep. Any thread once made (the first read, on the main thread, makes
    /// its table).
    /// </summary>
    public void Voxel(int x, int y, int z, out byte material, out byte density)
    {
        material = VoxelConstants.Air;
        density = VoxelDensity.Empty;
        if ((uint)x >= (uint)Size.X || (uint)y >= (uint)Size.Y || (uint)z >= (uint)Size.Z)
        {
            return;
        }

        var level = Levels[0];
        if (_brickAt is null)
        {
            var table = new int[16 * 16 * 16];
            Array.Fill(table, -1);
            for (var b = 0; b < level.Bricks.Length; b++)
            {
                table[level.Bricks[b]] = b;
            }

            _brickAt = table;
        }

        var brick = _brickAt[(x >> 3) | ((y >> 3) << 4) | ((z >> 3) << 8)];
        if (brick < 0)
        {
            return;
        }

        var payload = level.PayloadStart[brick];
        if (level.Uniform[brick])
        {
            material = level.Payload[payload];
            density = level.Payload[payload + 1];
            return;
        }

        var j = (x & 7) + Brick * ((y & 7) + Brick * (z & 7));
        material = level.Payload[payload + j];
        density = level.Payload[payload + BrickVoxels + j];
    }

    /// <summary>The part's box at level <paramref name="level"/>: its box halved per level, rounded up.</summary>
    public VoxelCoord SizeAt(int level)
    {
        var cell = (1 << level) - 1;
        return new VoxelCoord((Size.X + cell) >> level, (Size.Y + cell) >> level, (Size.Z + cell) >> level);
    }

    /// <summary>
    /// Writes level <paramref name="level"/>'s materials and densities into the 128^3 chunks (index
    /// <c>x + 128y + 16384z</c>), clearing them first within the level's box.
    /// </summary>
    public void Decode(int level, Span<byte> materials, Span<byte> densities) =>
        Levels[level].Decode(SizeAt(level), materials, densities);

    /// <summary>
    /// The part owning <paramref name="ownSize"/> voxels from <paramref name="offset"/> of <paramref name="shape"/>, with its
    /// border, at three levels; false when it owns no solid voxel.
    /// </summary>
    public static bool TryBuild(VoxelObjectShape shape, VoxelCoord offset, VoxelCoord ownSize, out ObjectPart part)
    {
        var levels = new PartLevel[VoxelModels.MaxLevels];
        var corner = new VoxelCoord(offset.X - BorderVoxels, offset.Y - BorderVoxels, offset.Z - BorderVoxels);
        var box = new VoxelCoord(
            ownSize.X + 2 * BorderVoxels,
            ownSize.Y + 2 * BorderVoxels,
            ownSize.Z + 2 * BorderVoxels
        );
        var source = new ShapeVoxels(shape);
        for (var level = 0; level < levels.Length; level++)
        {
            levels[level] = PartLevel.Build(in source, corner, box, level);
        }

        part = new ObjectPart(offset, ownSize, levels);
        return OwnsSolid(shape, offset, ownSize);
    }

    private static bool OwnsSolid(VoxelObjectShape shape, VoxelCoord offset, VoxelCoord ownSize)
    {
        for (var z = 0; z < ownSize.Z; z++)
        {
            for (var y = 0; y < ownSize.Y; y++)
            {
                for (var x = 0; x < ownSize.X; x++)
                {
                    if (shape.MaterialAt(offset.X + x, offset.Y + y, offset.Z + z) != VoxelConstants.Air)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// One level of a part: its non-empty bricks (packed x, y, z of 4 bits each) and their voxels. A coarse voxel holds
    /// the mean density of the voxels it covers within the shape; it is solid when that reaches one half, of the material
    /// at its sample point when that is solid, else the commonest solid material it covers.
    /// </summary>
    internal sealed class PartLevel
    {
        public required int[] Bricks { get; init; }
        public required bool[] Uniform { get; init; }
        public required int[] PayloadStart { get; init; }
        public required byte[] Payload { get; init; }

        /// <summary>Its solid voxels.</summary>
        public required long Solid { get; init; }

        /// <summary>
        /// Writes its materials and densities into the 128^3 chunks (index <c>x + 128y + 16384z</c>), clearing them first
        /// within <paramref name="size"/>, its level's box.
        /// </summary>
        public void Decode(VoxelCoord size, Span<byte> materials, Span<byte> densities)
        {
            const int edge = VoxelConstants.ChunkSize;
            for (var z = 0; z < size.Z; z++)
            {
                for (var y = 0; y < size.Y; y++)
                {
                    materials.Slice(edge * (y + edge * z), size.X).Clear();
                    densities.Slice(edge * (y + edge * z), size.X).Clear();
                }
            }

            for (var b = 0; b < Bricks.Length; b++)
            {
                var packed = Bricks[b];
                var bx = (packed & 15) * Brick;
                var by = ((packed >> 4) & 15) * Brick;
                var bz = ((packed >> 8) & 15) * Brick;
                var uniform = Uniform[b];
                var payload = Payload.AsSpan(PayloadStart[b]);
                for (var z = 0; z < Brick; z++)
                {
                    for (var y = 0; y < Brick; y++)
                    {
                        for (var x = 0; x < Brick; x++)
                        {
                            var i = bx + x + edge * (by + y + edge * (bz + z));
                            var j = x + Brick * (y + Brick * z);
                            materials[i] = uniform ? payload[0] : payload[j];
                            densities[i] = uniform ? payload[1] : payload[BrickVoxels + j];
                        }
                    }
                }
            }
        }

        public static PartLevel Build<TSource>(in TSource source, VoxelCoord offset, VoxelCoord partSize, int level)
            where TSource : IObjectVoxelSource, allows ref struct
        {
            var step = 1 << level;
            var size = new VoxelCoord(
                (partSize.X + step - 1) / step,
                (partSize.Y + step - 1) / step,
                (partSize.Z + step - 1) / step
            );
            var bricks = new List<int>();
            var uniform = new List<bool>();
            var starts = new List<int>();
            var payload = new List<byte>();
            long solid = 0;
            Span<byte> materials = stackalloc byte[BrickVoxels];
            Span<byte> densities = stackalloc byte[BrickVoxels];
            Span<int> counts = stackalloc int[256];
            for (var bz = 0; bz * Brick < size.Z; bz++)
            {
                for (var by = 0; by * Brick < size.Y; by++)
                {
                    for (var bx = 0; bx * Brick < size.X; bx++)
                    {
                        var empty = true;
                        for (var z = 0; z < Brick; z++)
                        {
                            for (var y = 0; y < Brick; y++)
                            {
                                for (var x = 0; x < Brick; x++)
                                {
                                    var j = x + Brick * (y + Brick * z);
                                    int cx = bx * Brick + x,
                                        cy = by * Brick + y,
                                        cz = bz * Brick + z;
                                    if (cx >= size.X || cy >= size.Y || cz >= size.Z)
                                    {
                                        materials[j] = VoxelConstants.Air;
                                        densities[j] = VoxelDensity.Empty;
                                        continue;
                                    }

                                    Sample(
                                        in source,
                                        offset,
                                        cx,
                                        cy,
                                        cz,
                                        step,
                                        counts,
                                        out materials[j],
                                        out densities[j]
                                    );
                                    solid += materials[j] != VoxelConstants.Air ? 1 : 0;
                                    empty &= densities[j] == VoxelDensity.Empty;
                                }
                            }
                        }

                        if (empty)
                        {
                            continue;
                        }

                        bricks.Add(bx | (by << 4) | (bz << 8));
                        starts.Add(payload.Count);
                        var one =
                            materials.IndexOfAnyExcept(materials[0]) < 0
                            && densities.IndexOfAnyExcept(densities[0]) < 0;
                        uniform.Add(one);
                        if (one)
                        {
                            payload.Add(materials[0]);
                            payload.Add(densities[0]);
                        }
                        else
                        {
                            foreach (var m in materials)
                            {
                                payload.Add(m);
                            }

                            foreach (var d in densities)
                            {
                                payload.Add(d);
                            }
                        }
                    }
                }
            }

            return new PartLevel
            {
                Bricks = [.. bricks],
                Uniform = [.. uniform],
                PayloadStart = [.. starts],
                Payload = [.. payload],
                Solid = solid,
            };
        }

        /// <summary>
        /// Coarse voxel (cx, cy, cz), of <paramref name="step"/> level-0 voxels a side, of a part whose box starts at
        /// <paramref name="offset"/> of <paramref name="source"/>: the mean density of the voxels it covers, solid from one
        /// half, of the material at its sample point when that is solid, else the commonest solid one it covers.
        /// <paramref name="counts"/> is 256 entries of room.
        /// </summary>
        internal static void Sample<TSource>(
            in TSource source,
            VoxelCoord offset,
            int cx,
            int cy,
            int cz,
            int step,
            Span<int> counts,
            out byte material,
            out byte density
        )
            where TSource : IObjectVoxelSource, allows ref struct
        {
            int x0 = offset.X + cx * step,
                y0 = offset.Y + cy * step,
                z0 = offset.Z + cz * step;
            if (step == 1)
            {
                source.Voxel(x0, y0, z0, out material, out density);
                return;
            }

            counts.Clear();
            var sum = 0;
            for (var z = 0; z < step; z++)
            {
                for (var y = 0; y < step; y++)
                {
                    for (var x = 0; x < step; x++)
                    {
                        source.Voxel(x0 + x, y0 + y, z0 + z, out var each, out var share);
                        sum += share;
                        counts[each]++;
                    }
                }
            }

            var cells = step * step * step;
            var mean = (byte)((sum + cells / 2) / cells);
            if (mean < VoxelDensity.Solid)
            {
                material = VoxelConstants.Air;
                density = mean;
                return;
            }

            var half = step >> 1;
            source.Voxel(x0 + half, y0 + half, z0 + half, out material, out _);
            if (material == VoxelConstants.Air)
            {
                var most = 0;
                for (var m = 1; m < counts.Length; m++)
                {
                    if (counts[m] > most)
                    {
                        most = counts[m];
                        material = (byte)m;
                    }
                }
            }

            density = mean;
        }
    }
}
