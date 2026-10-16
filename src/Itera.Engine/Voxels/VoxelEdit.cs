using System.Globalization;

namespace Itera.Engine.Voxels;

/// <summary>The shape an edit covers.</summary>
public enum VoxelEditShape : byte
{
    /// <summary>Voxels within <see cref="VoxelEdit.Radius"/> of <see cref="VoxelEdit.Origin"/>.</summary>
    Sphere = 0,

    /// <summary>Voxels from <see cref="VoxelEdit.Origin"/> (inclusive) to <c>Origin + Extent</c> (exclusive).</summary>
    Box = 1,

    /// <summary>
    /// A lifted piece (<see cref="VoxelEdit.Piece"/>) put back, its pivot at voxel corner <see cref="VoxelEdit.Origin"/>
    /// and turned by <see cref="VoxelEdit.Turn"/>. <c>Extent.X</c> is the radius of a sphere around the pivot that holds
    /// it. Its operation is <see cref="VoxelEditOperation.Fill"/>, with the piece's own materials.
    /// </summary>
    Piece = 2,

    /// <summary>
    /// Voxels within <see cref="VoxelEdit.Radius"/> of the segment from <see cref="VoxelEdit.Origin"/> to
    /// <see cref="VoxelEdit.CapsuleEnd"/> (<c>Origin + Extent</c>); the radius is held in <see cref="VoxelEdit.Piece"/>.
    /// Models take capsules (the cuts of characters' parts), and so does the world since save format 5 and protocol 28
    /// (a channel along a path).
    /// </summary>
    Capsule = 3,
}

/// <summary>What an edit does to each voxel it covers.</summary>
public enum VoxelEditOperation : byte
{
    /// <summary>Every covered voxel becomes <see cref="VoxelEdit.Material"/>.</summary>
    Set = 0,

    /// <summary>Every covered voxel becomes air (digging).</summary>
    Clear = 1,

    /// <summary>Every covered solid voxel becomes <see cref="VoxelEdit.Material"/>; air stays air.</summary>
    Paint = 2,

    /// <summary>Every covered air voxel becomes <see cref="VoxelEdit.Material"/>; solid voxels stay.</summary>
    Fill = 3,

    /// <summary>
    /// Every covered voxel becomes air, as <see cref="Clear"/> does, and the world keeps the level-0 voxels removed as a
    /// piece named by the edit's sequence (<see cref="VoxelWorld.TryLift"/>).
    /// </summary>
    Lift = 4,

    /// <summary>
    /// The whole world goes back to what the generator makes from the seed: no edit before this one counts any more
    /// (<see cref="VoxelWorld.Reset"/>). Covers no voxels itself (<see cref="VoxelEdit.ResetAll"/>: an empty box at the
    /// origin), so readers that follow edits by their bounds see nothing to do.
    /// </summary>
    Reset = 5,

    /// <summary>
    /// Every covered voxel of a material in the edit's set becomes air; the others stay (since save format 6 and protocol
    /// 31: a player's removal of one material). Only a box of material air takes it, and since save format 10 and
    /// protocol 46 a sphere of material air too (a blast's crater that leaves voxel objects of other materials whole); the
    /// set (<see cref="VoxelEdit.MaterialsBox"/>, <see cref="VoxelEdit.MaterialsSphere"/>,
    /// <see cref="VoxelEdit.ClearsMaterial"/>) holds materials 0 to 127, air never.
    /// </summary>
    ClearMaterials = 6,

    /// <summary>
    /// A part of a voxel object, cut off from the rest, goes (since save format 7 and protocol 39): the object
    /// <see cref="VoxelEdit.Piece"/> names (its <c>ObjectId</c>) loses every solid voxel joined to the seed voxel
    /// <see cref="VoxelEdit.Turn"/> holds (the kind's voxel, 16 bits an axis, x lowest; <see cref="VoxelEdit.PartSeed"/>), and
    /// each game makes a falling body of them. The world's voxels do not change: its box (<see cref="VoxelEdit.Origin"/>,
    /// <see cref="VoxelEdit.Extent"/>, of air) is where the part lay, for the log's index. Made by a session's host, or an
    /// offline game, when an edit cuts a part off (<see cref="VoxelEdit.RemovePart"/>).
    /// </summary>
    RemovePart = 7,

    /// <summary>
    /// Every voxel of the world's level <see cref="VoxelEdit.Material"/> names (1 or coarser) whose sample point the box
    /// covers and whose material is in the edit's set (as <see cref="ClearMaterials"/>'s) becomes air; no other level,
    /// and no voxel object, changes (since save format 7 and protocol 39). Made by a session's host, or an offline game,
    /// when a part of an object falls (<see cref="RemovePart"/>), to clear the far copies only that part filled
    /// (<see cref="VoxelEdit.ClearCopies"/>).
    /// </summary>
    ClearCopies = 8,

    /// <summary>
    /// A voxel object is placed during play (since save format 9 and protocol 45): the object <see cref="VoxelEdit.Piece"/>
    /// names (its <c>ObjectId</c>), of the kind and at the heading <see cref="VoxelEdit.Turn"/> holds, its anchor in the
    /// middle of the box across x and z and its kind's bottom under it at the box's floor
    /// (<see cref="VoxelEdit.PlacementOf"/>). The world's voxels do not change: its box (of air) is the object's reach and
    /// height, for the log's index. Made by a session's host, or an offline game (<c>ObjectWorld.TryPlace</c>); its kind
    /// has no far copies.
    /// </summary>
    PlaceObject = 9,

    /// <summary>
    /// A voxel object goes whole (since save format 9 and protocol 45), placed by the world's generator or during play:
    /// the object <see cref="VoxelEdit.Piece"/> names, with its placement in the edit as
    /// <see cref="PlaceObject"/>'s. The world's voxels do not change. Made by a session's host, or an offline game
    /// (<c>ObjectWorld.TryRemove</c>); its kind has no far copies.
    /// </summary>
    RemoveObject = 10,
}

/// <summary>
/// One change to the voxel world. The world is its seed plus the ordered list of these, so every field is an integer
/// and the struct is blittable: it can be saved and sent as it is, and every peer that applies the same edits in the
/// same order gets the same voxels. <see cref="Sequence"/> is the edit's position in the world's edit log, starting at
/// 1; it is 0 until the world assigns it. <see cref="Piece"/> and <see cref="Turn"/> are 0 except for
/// <see cref="VoxelEditShape.Piece"/> edits; a capsule holds its radius in <see cref="Piece"/>, and a
/// <see cref="VoxelEditOperation.ClearMaterials"/> box or sphere its set of materials in both (0 to 63 in
/// <see cref="Piece"/>, 64 to 127 in <see cref="Turn"/>, a bit each).
/// </summary>
public readonly record struct VoxelEdit(
    long Sequence,
    VoxelEditShape Shape,
    VoxelEditOperation Operation,
    byte Material,
    VoxelCoord Origin,
    VoxelCoord Extent,
    long Piece = 0,
    long Turn = 0
)
{
    /// <summary>A turn's quaternion components are whole numbers of 1/16384.</summary>
    public const int TurnScale = 1 << 14;

    /// <summary>
    /// The maker of an edit no player made (a settled body, a reset, a loaded log): see <see cref="VoxelWorld.MakerOf"/>.
    /// A maker is otherwise a session slot, 0 to <see cref="MaxMaker"/>.
    /// </summary>
    public const int NoMaker = -1;

    /// <summary>The largest maker an edit may name; the log keeps one byte each, with 255 for none.</summary>
    public const int MaxMaker = 254;

    /// <summary>The <see cref="Turn"/> of no turn.</summary>
    public const long NoTurn = (long)TurnScale << 48;

    /// <summary>A sphere edit. <paramref name="radius"/> is in centimetres.</summary>
    public static VoxelEdit Sphere(
        VoxelCoord centre,
        int radius,
        VoxelEditOperation operation,
        byte material = VoxelConstants.Air
    ) => new(0, VoxelEditShape.Sphere, operation, material, centre, new VoxelCoord(radius, radius, radius));

    /// <summary>
    /// A capsule edit: the voxels within <paramref name="radius"/> of the segment from <paramref name="start"/> to
    /// <paramref name="end"/>, all in centimetres. Models and the world take it.
    /// </summary>
    public static VoxelEdit Capsule(
        VoxelCoord start,
        VoxelCoord end,
        int radius,
        VoxelEditOperation operation,
        byte material = VoxelConstants.Air
    ) =>
        new(
            0,
            VoxelEditShape.Capsule,
            operation,
            material,
            start,
            new VoxelCoord(end.X - start.X, end.Y - start.Y, end.Z - start.Z),
            radius
        );

    /// <summary>The most a capsule's end may lie from its start on each axis, so its maths stays within a long.</summary>
    public const int MaxCapsuleOffset = 4096;

    /// <summary>The reset of the whole world (<see cref="VoxelEditOperation.Reset"/>): an empty box at the origin.</summary>
    public static VoxelEdit ResetAll() =>
        new(0, VoxelEditShape.Box, VoxelEditOperation.Reset, VoxelConstants.Air, default, default);

    /// <summary>A box edit from <paramref name="min"/> covering <paramref name="size"/> centimetres.</summary>
    public static VoxelEdit Box(
        VoxelCoord min,
        VoxelCoord size,
        VoxelEditOperation operation,
        byte material = VoxelConstants.Air
    ) => new(0, VoxelEditShape.Box, operation, material, min, size);

    /// <summary>The most materials a <see cref="VoxelEditOperation.ClearMaterials"/> set holds: 0 to 127.</summary>
    public const int MaterialSetSize = 128;

    /// <summary>
    /// A box from <paramref name="min"/> covering <paramref name="size"/> centimetres that clears only the materials in
    /// <paramref name="materials"/> (<see cref="VoxelEditOperation.ClearMaterials"/>), each below
    /// <see cref="MaterialSetSize"/>.
    /// </summary>
    public static VoxelEdit MaterialsBox(VoxelCoord min, VoxelCoord size, ReadOnlySpan<byte> materials)
    {
        var (low, high) = MaterialSet(materials);
        return new(0, VoxelEditShape.Box, VoxelEditOperation.ClearMaterials, VoxelConstants.Air, min, size, low, high);
    }

    /// <summary>
    /// A sphere of <paramref name="radius"/> centimetres round <paramref name="centre"/> that clears only the materials in
    /// <paramref name="materials"/> (<see cref="VoxelEditOperation.ClearMaterials"/>), each below
    /// <see cref="MaterialSetSize"/>: a crater that leaves voxel objects of other materials whole.
    /// </summary>
    public static VoxelEdit MaterialsSphere(VoxelCoord centre, int radius, ReadOnlySpan<byte> materials)
    {
        var (low, high) = MaterialSet(materials);
        return new(
            0,
            VoxelEditShape.Sphere,
            VoxelEditOperation.ClearMaterials,
            VoxelConstants.Air,
            centre,
            new VoxelCoord(radius, radius, radius),
            low,
            high
        );
    }

    /// <summary>A clear's set of materials: 0 to 63 a bit each in the first, 64 to 127 in the second.</summary>
    private static (long Low, long High) MaterialSet(ReadOnlySpan<byte> materials)
    {
        long low = 0;
        long high = 0;
        foreach (var material in materials)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(material, MaterialSetSize, nameof(materials));
            if (material < 64)
            {
                low |= 1L << material;
            }
            else
            {
                high |= 1L << (material - 64);
            }
        }

        return (low, high);
    }

    /// <summary>True when a <see cref="VoxelEditOperation.ClearMaterials"/> edit clears voxels of <paramref name="material"/>.</summary>
    public bool ClearsMaterial(byte material) =>
        material < 64
            ? ((Piece >> material) & 1) != 0
            : material < MaterialSetSize && ((Turn >> (material - 64)) & 1) != 0;

    /// <summary>
    /// The removal of the part of object <paramref name="objectId"/> joined to its kind's voxel <paramref name="seed"/>
    /// (<see cref="VoxelEditOperation.RemovePart"/>), which lay in the box from <paramref name="min"/> covering
    /// <paramref name="size"/> centimetres.
    /// </summary>
    public static VoxelEdit RemovePart(ulong objectId, VoxelCoord seed, VoxelCoord min, VoxelCoord size)
    {
        if ((uint)seed.X >= MostSeed || (uint)seed.Y >= MostSeed || (uint)seed.Z >= MostSeed)
        {
            throw new ArgumentOutOfRangeException(nameof(seed), seed, "A part's seed lies in an object kind's box.");
        }

        return new(
            0,
            VoxelEditShape.Box,
            VoxelEditOperation.RemovePart,
            VoxelConstants.Air,
            min,
            size,
            (long)objectId,
            (long)(ushort)seed.X | ((long)(ushort)seed.Y << 16) | ((long)(ushort)seed.Z << 32)
        );
    }

    /// <summary>
    /// A clear of the far copies of objects at world level <paramref name="level"/> (1 or coarser) in the box from
    /// <paramref name="min"/> covering <paramref name="size"/> centimetres, of the materials in
    /// <paramref name="materials"/> (each below <see cref="MaterialSetSize"/>; <see cref="VoxelEditOperation.ClearCopies"/>).
    /// </summary>
    public static VoxelEdit ClearCopies(int level, VoxelCoord min, VoxelCoord size, ReadOnlySpan<byte> materials)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, VoxelConstants.MaxLevel);
        return MaterialsBox(min, size, materials) with
        {
            Operation = VoxelEditOperation.ClearCopies,
            Material = (byte)level,
        };
    }

    /// <summary>
    /// The placing of an object during play (<see cref="VoxelEditOperation.PlaceObject"/>) at
    /// <paramref name="placement"/>, of a kind that reaches <paramref name="reachCentimetres"/> across its anchor and
    /// from <paramref name="bottomCentimetres"/> to <paramref name="topCentimetres"/> over it (its kind's).
    /// </summary>
    public static VoxelEdit PlaceObject(
        in Objects.ObjectPlacement placement,
        int reachCentimetres,
        int bottomCentimetres,
        int topCentimetres
    ) => OfObject(VoxelEditOperation.PlaceObject, in placement, reachCentimetres, bottomCentimetres, topCentimetres);

    /// <summary>
    /// The removal of a whole object (<see cref="VoxelEditOperation.RemoveObject"/>) at <paramref name="placement"/>, as
    /// <see cref="PlaceObject"/> names one.
    /// </summary>
    public static VoxelEdit RemoveObject(
        in Objects.ObjectPlacement placement,
        int reachCentimetres,
        int bottomCentimetres,
        int topCentimetres
    ) => OfObject(VoxelEditOperation.RemoveObject, in placement, reachCentimetres, bottomCentimetres, topCentimetres);

    private static VoxelEdit OfObject(
        VoxelEditOperation operation,
        in Objects.ObjectPlacement placement,
        int reach,
        int bottom,
        int top
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reach);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, bottom);
        if ((uint)placement.Heading >= Objects.ObjectPlacement.HeadingSteps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement),
                placement.Heading,
                "A heading is a step of a turn."
            );
        }

        var anchor = placement.Anchor;
        return new(
            0,
            VoxelEditShape.Box,
            operation,
            VoxelConstants.Air,
            new VoxelCoord(anchor.X - reach, anchor.Y + bottom, anchor.Z - reach),
            new VoxelCoord(2 * reach, top - bottom, 2 * reach),
            (long)placement.Id.Value,
            placement.Kind.Value | ((long)placement.Heading << 16) | ((long)-bottom << 32)
        );
    }

    /// <summary>
    /// The object a <see cref="VoxelEditOperation.PlaceObject"/> or <see cref="VoxelEditOperation.RemoveObject"/> edit
    /// names, as placed: its id, kind, anchor, and heading, and its edit's sequence as where its edits start (from its
    /// placing on, for one placed during play).
    /// </summary>
    public Objects.ObjectPlacement PlacementOf() =>
        new(
            new Objects.ObjectId((ulong)Piece),
            new Objects.VoxelObjectKindId((ushort)(Turn & 0xFFFF)),
            new VoxelCoord(Origin.X + Extent.X / 2, Origin.Y + (int)(Turn >> 32), Origin.Z + Extent.Z / 2),
            (int)((Turn >> 16) & 0xFFFF),
            Operation == VoxelEditOperation.PlaceObject ? Sequence : 0
        );

    /// <summary>
    /// For a <see cref="VoxelEditOperation.PlaceObject"/> or <see cref="VoxelEditOperation.RemoveObject"/> edit: null
    /// when its box is its kind's reach and height round its anchor (<paramref name="reachCentimetres"/>,
    /// <paramref name="bottomCentimetres"/>, <paramref name="topCentimetres"/>), or the reason it is not.
    /// </summary>
    public string? CheckObjectBox(int reachCentimetres, int bottomCentimetres, int topCentimetres) =>
        Extent.X == 2 * reachCentimetres
        && Extent.Z == 2 * reachCentimetres
        && Extent.Y == topCentimetres - bottomCentimetres
        && (int)(Turn >> 32) == -bottomCentimetres
            ? null
            : "an object's edit's box is its kind's reach and height round its anchor";

    /// <summary>
    /// True when the edit can change the world's voxels of <paramref name="level"/>: a clear of copies only its own level
    /// (<see cref="VoxelEditOperation.ClearCopies"/>), any other edit every level.
    /// </summary>
    public bool AppliesAtLevel(int level) => Operation != VoxelEditOperation.ClearCopies || Material == level;

    /// <summary>The kind's voxel a <see cref="VoxelEditOperation.RemovePart"/> edit's part is joined to.</summary>
    public VoxelCoord PartSeed => new((int)(Turn & 0xFFFF), (int)((Turn >> 16) & 0xFFFF), (int)((Turn >> 32) & 0xFFFF));

    /// <summary>The most voxels along each axis a part's seed may lie at: an object kind's box.</summary>
    public const int MostSeed = Objects.VoxelObjectShape.MostVoxelsAcross;

    /// <summary>
    /// True when the edit can change the world's own voxels: not a reset (which unloads them instead) nor the removal of
    /// an object's part. Columns replay only these.
    /// </summary>
    public bool ChangesWorld =>
        Operation
            is not VoxelEditOperation.Reset
                and not VoxelEditOperation.RemovePart
                and not VoxelEditOperation.PlaceObject
                and not VoxelEditOperation.RemoveObject;

    /// <summary>True for an edit that places or removes a whole object (<see cref="VoxelEditOperation.PlaceObject"/>, <see cref="VoxelEditOperation.RemoveObject"/>).</summary>
    public bool IsObjectPlacing => Operation is VoxelEditOperation.PlaceObject or VoxelEditOperation.RemoveObject;

    /// <summary>
    /// A piece edit: puts the piece lifted by edit <paramref name="piece"/> back with its pivot at voxel corner
    /// <paramref name="pivot"/>, turned by <paramref name="turn"/> (<see cref="PackTurn"/>), into air only.
    /// <paramref name="radius"/> is the distance from its pivot to its farthest voxel corner, rounded up
    /// (<see cref="VoxelPiece.Radius"/>).
    /// </summary>
    public static VoxelEdit PieceAt(long piece, int radius, VoxelCoord pivot, long turn) =>
        new(
            0,
            VoxelEditShape.Piece,
            VoxelEditOperation.Fill,
            VoxelConstants.Air,
            pivot,
            new VoxelCoord(radius, radius, radius),
            piece,
            turn
        );

    /// <summary>
    /// Packs a turn: the quaternion's components rounded to whole numbers of 1/<see cref="TurnScale"/>, 16 bits each, x
    /// lowest. Rounding happens once, where the edit is made; every peer then works from the same integers.
    /// </summary>
    public static long PackTurn(System.Numerics.Quaternion turn)
    {
        var length = turn.Length();
        if (!(length > 0))
        {
            return NoTurn;
        }

        static long Part(float value, float length, int shift) =>
            (long)(ushort)(short)Math.Round(value / length * TurnScale) << shift;

        return Part(turn.X, length, 0) | Part(turn.Y, length, 16) | Part(turn.Z, length, 32) | Part(turn.W, length, 48);
    }

    /// <summary>The components of a packed turn, x, y, z, w, in whole numbers of 1/<see cref="TurnScale"/>.</summary>
    public static (int X, int Y, int Z, int W) UnpackTurn(long turn) =>
        ((short)turn, (short)(turn >> 16), (short)(turn >> 32), (short)(turn >> 48));

    /// <summary>The sphere's or capsule's radius in centimetres.</summary>
    public int Radius => Shape == VoxelEditShape.Capsule ? (int)Piece : Extent.X;

    /// <summary>A capsule's second end: <c>Origin + Extent</c>.</summary>
    public VoxelCoord CapsuleEnd => new(Origin.X + Extent.X, Origin.Y + Extent.Y, Origin.Z + Extent.Z);

    /// <summary>The covered box in centimetres: <paramref name="min"/> inclusive, <paramref name="end"/> exclusive.</summary>
    public void GetBounds(out VoxelCoord min, out VoxelCoord end)
    {
        if (Shape == VoxelEditShape.Capsule)
        {
            var r = Radius;
            var far = CapsuleEnd;
            min = new VoxelCoord(
                Math.Min(Origin.X, far.X) - r,
                Math.Min(Origin.Y, far.Y) - r,
                Math.Min(Origin.Z, far.Z) - r
            );
            end = new VoxelCoord(
                Math.Max(Origin.X, far.X) + r + 1,
                Math.Max(Origin.Y, far.Y) + r + 1,
                Math.Max(Origin.Z, far.Z) + r + 1
            );
        }
        else if (Shape != VoxelEditShape.Box)
        {
            // A sphere around a voxel, or around a piece's pivot (a voxel corner): the voxels whose centres it can reach.
            var r = Radius;
            min = new VoxelCoord(Origin.X - r, Origin.Y - r, Origin.Z - r);
            end = new VoxelCoord(Origin.X + r + 1, Origin.Y + r + 1, Origin.Z + r + 1);
        }
        else
        {
            min = Origin;
            end = new VoxelCoord(Origin.X + Extent.X, Origin.Y + Extent.Y, Origin.Z + Extent.Z);
        }
    }

    /// <summary>
    /// True when the level-0 point is covered. Integer maths only, so every machine agrees. A piece edit covers what its
    /// piece holds, which only <see cref="PiecePlacement"/> knows, so this is false for one.
    /// </summary>
    public bool Contains(int x, int y, int z)
    {
        if (Shape == VoxelEditShape.Piece)
        {
            return false;
        }

        if (Shape == VoxelEditShape.Sphere)
        {
            long dx = x - Origin.X;
            long dy = y - Origin.Y;
            long dz = z - Origin.Z;
            long r = Radius;
            return dx * dx + dy * dy + dz * dz <= r * r;
        }

        if (Shape == VoxelEditShape.Capsule)
        {
            return CapsuleContains(x, y, z);
        }

        return x >= Origin.X
            && y >= Origin.Y
            && z >= Origin.Z
            && x < Origin.X + Extent.X
            && y < Origin.Y + Extent.Y
            && z < Origin.Z + Extent.Z;
    }

    /// <summary>
    /// The capsule's test: the point's squared distance to the segment against the radius's square, in longs. With
    /// w = P - A, D = B - A: past A (w.D &lt;= 0) the distance is |w|; past B (w.D &gt;= D.D) it is |P - B|; between them
    /// |w|^2 - (w.D)^2 / D.D, compared multiplied by D.D so nothing is divided. Points outside the bounds are refused
    /// first, which keeps every product under 2^62 within <see cref="Validate"/>'s limits.
    /// </summary>
    private bool CapsuleContains(int x, int y, int z)
    {
        GetBounds(out var min, out var end);
        if (x < min.X || y < min.Y || z < min.Z || x >= end.X || y >= end.Y || z >= end.Z)
        {
            return false;
        }

        long wx = x - Origin.X;
        long wy = y - Origin.Y;
        long wz = z - Origin.Z;
        long r = Radius;
        long dd = (long)Extent.X * Extent.X + (long)Extent.Y * Extent.Y + (long)Extent.Z * Extent.Z;
        var wd = wx * Extent.X + wy * Extent.Y + wz * Extent.Z;
        var ww = wx * wx + wy * wy + wz * wz;
        if (dd == 0 || wd <= 0)
        {
            return ww <= r * r;
        }

        if (wd >= dd)
        {
            var bx = wx - Extent.X;
            var by = wy - Extent.Y;
            var bz = wz - Extent.Z;
            return bx * bx + by * by + bz * bz <= r * r;
        }

        return ww * dd - wd * wd <= r * r * dd;
    }

    /// <summary>The material a covered voxel of material <paramref name="current"/> ends with.</summary>
    public byte Apply(byte current) =>
        Operation switch
        {
            VoxelEditOperation.Set => Material,
            VoxelEditOperation.Clear or VoxelEditOperation.Lift => VoxelConstants.Air,
            VoxelEditOperation.Fill => current == VoxelConstants.Air ? Material : current,
            VoxelEditOperation.Reset
            or VoxelEditOperation.RemovePart
            or VoxelEditOperation.PlaceObject
            or VoxelEditOperation.RemoveObject => current,
            VoxelEditOperation.ClearMaterials or VoxelEditOperation.ClearCopies => ClearsMaterial(current)
                ? VoxelConstants.Air
                : current,
            _ => current == VoxelConstants.Air ? VoxelConstants.Air : Material,
        };

    /// <summary>True when the edit cannot change a chunk that is all <paramref name="material"/>.</summary>
    public bool LeavesUniform(byte material) =>
        Shape == VoxelEditShape.Piece
            ? material != VoxelConstants.Air
            : Operation switch
            {
                VoxelEditOperation.Set => Material == material,
                VoxelEditOperation.Clear or VoxelEditOperation.Lift => material == VoxelConstants.Air,
                VoxelEditOperation.Fill => material != VoxelConstants.Air || Material == VoxelConstants.Air,
                VoxelEditOperation.Reset
                or VoxelEditOperation.RemovePart
                or VoxelEditOperation.PlaceObject
                or VoxelEditOperation.RemoveObject => true,
                VoxelEditOperation.ClearMaterials or VoxelEditOperation.ClearCopies => !ClearsMaterial(material),
                _ => material == VoxelConstants.Air || Material == material,
            };

    /// <summary>Returns null when the edit can be applied, or the reason it cannot.</summary>
    public string? Validate()
    {
        // Plain comparisons, not Enum.IsDefined: that keeps its value list in a runtime cache that any garbage
        // collection can drop, and rebuilding it allocates about 480 bytes inside the next edit.
        if (Shape > VoxelEditShape.Capsule || Operation > VoxelEditOperation.RemoveObject)
        {
            return "unknown shape or operation";
        }

        if (Operation == VoxelEditOperation.ClearCopies)
        {
            if (Shape != VoxelEditShape.Box || Material is < 1 or > VoxelConstants.MaxLevel)
            {
                return "a clear of copies is a box of one world level, 1 or coarser";
            }

            if ((Piece == 0 && Turn == 0) || (Piece & 1) != 0)
            {
                return "a clear of copies names at least one material, and never air";
            }
        }

        if (Operation == VoxelEditOperation.RemovePart)
        {
            if (Shape != VoxelEditShape.Box || Material != VoxelConstants.Air)
            {
                return "a part's removal is a box of air";
            }

            var seed = PartSeed;
            if ((Turn >> 48) != 0 || seed.X >= MostSeed || seed.Y >= MostSeed || seed.Z >= MostSeed)
            {
                return "a part's seed lies in an object kind's box";
            }
        }

        if (IsObjectPlacing)
        {
            if (Shape != VoxelEditShape.Box || Material != VoxelConstants.Air)
            {
                return "an object's placing or removal is a box of air";
            }

            if (Extent.X != Extent.Z || (Extent.X & 1) != 0 || ((Turn >> 28) & 0xF) != 0)
            {
                return "an object's edit's box is square across x and z, even, round its anchor";
            }

            if (((Turn >> 16) & 0xFFFF) >= Objects.ObjectPlacement.HeadingSteps)
            {
                return "an object's heading is a step of a turn";
            }
        }

        if (Operation == VoxelEditOperation.ClearMaterials)
        {
            if (Shape is not VoxelEditShape.Box and not VoxelEditShape.Sphere || Material != VoxelConstants.Air)
            {
                return "a clear of some materials is a box or sphere of air";
            }

            if ((Piece == 0 && Turn == 0) || (Piece & 1) != 0)
            {
                return "a clear of some materials names at least one, and never air";
            }
        }

        if (
            Operation == VoxelEditOperation.Reset
            && (
                Shape != VoxelEditShape.Box
                || Material != VoxelConstants.Air
                || Origin != default
                || Extent != default
                || Piece != 0
                || Turn != 0
            )
        )
        {
            return "a reset is an empty box of air at the origin";
        }

        if (Shape == VoxelEditShape.Piece)
        {
            var (x, y, z, w) = UnpackTurn(Turn);
            if (
                Operation != VoxelEditOperation.Fill
                || Material != VoxelConstants.Air
                || Piece <= 0
                || (Sequence != 0 && Piece >= Sequence)
            )
            {
                return "a piece edit fills air with an earlier lift's piece";
            }

            if ((long)x * x + (long)y * y + (long)z * z + (long)w * w == 0)
            {
                return "a piece edit's turn is zero";
            }

            // Its bounds reach this far: a radius of 10,000 made every chunk within 100 m be filled and tested voxel by
            // voxel for a piece of at most a chunk.
            if (Extent.X > VoxelPiece.MaxRadius)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"a piece edit's radius {Extent.X} is past any piece's ({VoxelPiece.MaxRadius})"
                );
            }
        }
        else if (Shape == VoxelEditShape.Capsule)
        {
            if (Turn != 0)
            {
                return "a capsule has no turn";
            }

            if (Piece < 0 || Piece > VoxelConstants.MaxEditExtent)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"capsule radius {Piece} is outside 0..{VoxelConstants.MaxEditExtent}"
                );
            }

            if (
                !IsWithin(Extent.X, MaxCapsuleOffset)
                || !IsWithin(Extent.Y, MaxCapsuleOffset)
                || !IsWithin(Extent.Z, MaxCapsuleOffset)
            )
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"capsule offset {Extent} is outside -{MaxCapsuleOffset}..{MaxCapsuleOffset}"
                );
            }
        }
        else if (
            (Piece != 0 || Turn != 0)
            && Operation
                is not VoxelEditOperation.ClearMaterials
                    and not VoxelEditOperation.RemovePart
                    and not VoxelEditOperation.ClearCopies
                    and not VoxelEditOperation.PlaceObject
                    and not VoxelEditOperation.RemoveObject
        )
        {
            return "only a piece edit names a piece or a turn";
        }

        if (Operation == VoxelEditOperation.Lift)
        {
            GetBounds(out var liftMin, out var liftEnd);
            // A box with an empty side lifts a piece of no voxels, which a save or a join payload cannot hold: every
            // later one was unreadable.
            if (Shape == VoxelEditShape.Box && (Extent.X <= 0 || Extent.Y <= 0 || Extent.Z <= 0))
            {
                return "a lift's box has an empty side";
            }

            if (
                Shape is VoxelEditShape.Piece or VoxelEditShape.Capsule
                || liftEnd.X - liftMin.X > VoxelConstants.ChunkSize
                || liftEnd.Y - liftMin.Y > VoxelConstants.ChunkSize
                || liftEnd.Z - liftMin.Z > VoxelConstants.ChunkSize
            )
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"a lift is a sphere or box of at most {VoxelConstants.ChunkSize} voxels on each side"
                );
            }
        }

        if (Shape != VoxelEditShape.Box && (Radius < 0 || Radius > VoxelConstants.MaxEditExtent))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"sphere radius {Radius} is outside 0..{VoxelConstants.MaxEditExtent}"
            );
        }

        if (
            Shape == VoxelEditShape.Box
            && (
                Extent.X < 0
                || Extent.Y < 0
                || Extent.Z < 0
                || Extent.X > VoxelConstants.MaxEditExtent
                || Extent.Y > VoxelConstants.MaxEditExtent
                || Extent.Z > VoxelConstants.MaxEditExtent
            )
        )
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"box size {Extent} is outside 0..{VoxelConstants.MaxEditExtent}"
            );
        }

        // Far enough outside the map that no edit near it can matter, and small enough that bounds cannot overflow.
        const int limit = 1 << 24;
        if (!IsWithin(Origin.X, limit) || !IsWithin(Origin.Y, limit) || !IsWithin(Origin.Z, limit))
        {
            return string.Create(CultureInfo.InvariantCulture, $"origin {Origin} is too far from the map");
        }

        return null;
    }

    private static bool IsWithin(int value, int limit) => value >= -limit && value <= limit;
}
