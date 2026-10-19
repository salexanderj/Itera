using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// What a world edit does to a placed object's voxels (<see cref="ObjectDamage"/>), the same on every peer: integer
/// maths through the integer turn (<see cref="ObjectTurns.FromObject"/>), each voxel's sample points taken from the
/// object's own axes into the world's centimetres, where the edit's own test (<see cref="VoxelEdit.Contains"/>) reads
/// them. Removals take a voxel by the share of it they cover, as the world's do (<c>VoxelEditing.ChangeByShare</c>, on
/// <c>VoxelEditing.Parts</c> of it: sub-rows along y and z, each centimetre along x): a clear, a clear of some materials,
/// a set of air, and a lift, whose object voxels go rather than into its piece. A paint changes the material of a solid
/// voxel whose middle it covers. Fills, sets of a material, and pieces put back change only the world. Allocates
/// nothing.
/// </summary>
internal static class ObjectEditing
{
    /// <summary>
    /// The version of this rule (the share, the turn, what changes objects), part of the kinds' fingerprint and so of the
    /// match's identity: raise it with any change to what an edit does to objects, which peers must compute alike. 2
    /// since objects are placed and removed whole by edits (2026-10-15).
    /// </summary>
    public const int RuleVersion = 2;

    /// <summary>
    /// True when <paramref name="edit"/> changes objects: a removal or a paint, not of a piece, or the removal of an
    /// object's part (<see cref="VoxelEditOperation.RemovePart"/>, which <c>ObjectDamage</c> applies).
    /// </summary>
    public static bool Changes(in VoxelEdit edit) =>
        edit.Operation == VoxelEditOperation.RemovePart
        || edit.Shape != VoxelEditShape.Piece
            && (
                edit.Operation
                    is VoxelEditOperation.Clear
                        or VoxelEditOperation.ClearMaterials
                        or VoxelEditOperation.Lift
                        or VoxelEditOperation.Paint
                || (edit.Operation == VoxelEditOperation.Set && edit.Material == VoxelConstants.Air)
            )
            && !(edit.Operation == VoxelEditOperation.Paint && edit.Material == VoxelConstants.Air);

    /// <summary>
    /// Applies <paramref name="edit"/> to the object of <paramref name="placement"/> (its voxels read and written through
    /// <paramref name="target"/>; with <paramref name="write"/> false, only counted) and adds what it takes out of each
    /// material into <paramref name="removed"/> (256 entries, in density steps times cubic centimetres; none when
    /// empty). Returns true when a voxel changed (or, counting, would).
    /// </summary>
    public static bool Apply(
        in VoxelEdit edit,
        in ObjectPlacement placement,
        VoxelObjectKind kind,
        DamagedObject? target,
        bool write,
        Span<long> removed
    )
    {
        if (!Changes(in edit) || edit.Operation == VoxelEditOperation.RemovePart)
        {
            return false;
        }

        // A clear of some materials changes only voxels of them: an object that holds none (its kind's materials, and any
        // a paint gave it since) is passed over unread, as each of its voxels would be (a blast's crater of the ground's
        // materials by a wall of its own).
        if (
            edit.Operation == VoxelEditOperation.ClearMaterials
            && !edit.ClearedMaterials.Overlaps(target?.Materials ?? kind.Materials)
        )
        {
            return false;
        }

        // A lift's object voxels go as a clear's do; a set of air is a clear (ChangeByShare).
        var removal =
            edit.Operation == VoxelEditOperation.Lift ? edit with { Operation = VoxelEditOperation.Clear } : edit;
        var unit = kind.UnitCentimetres;
        var anchor = placement.Anchor;
        var offset = kind.AnchorCentimetres;
        var heading = placement.Heading;
        if (!Range(in edit, in placement, kind, out var min, out var max))
        {
            return false;
        }

        var rows = VoxelEditing.SubRows(unit);
        var total = VoxelEditing.Parts(unit);
        var volume = (long)unit * unit * unit;
        var changed = false;
        for (var z = min.Z; z <= max.Z; z++)
        {
            for (var y = min.Y; y <= max.Y; y++)
            {
                for (var x = min.X; x <= max.X; x++)
                {
                    byte material,
                        density;
                    if (target is not null)
                    {
                        target.Voxel(x, y, z, out material, out density);
                    }
                    else
                    {
                        kind.Voxel(x, y, z, out material, out density);
                    }

                    // Removals and paints change only what is there: air of no density stays. A clear of some materials
                    // changes only a solid voxel of one of them (ChangeByShare), so the rest are passed over before their
                    // shares are counted: a crater of the ground's materials then costs an object of others a read a voxel.
                    if (
                        (density == VoxelDensity.Empty && material == VoxelConstants.Air)
                        || (
                            edit.Operation == VoxelEditOperation.ClearMaterials
                            && (material == VoxelConstants.Air || !edit.ClearsMaterial(material))
                        )
                    )
                    {
                        continue;
                    }

                    // The voxel's low corner along the object's own axes, in centimetres from its anchor.
                    long ox = (long)x * unit - offset.X,
                        oy = (long)y * unit - offset.Y,
                        oz = (long)z * unit - offset.Z;
                    var before = material;
                    var lost = 0;
                    if (edit.Operation == VoxelEditOperation.Paint)
                    {
                        if (
                            material == VoxelConstants.Air
                            || !Covers(in edit, heading, anchor, ox + unit / 2, oy + unit / 2, oz + unit / 2)
                        )
                        {
                            continue;
                        }

                        material = edit.Material;
                    }
                    else
                    {
                        var covered = Covered(in edit, heading, anchor, ox, oy, oz, unit, rows, total);
                        if (covered == 0)
                        {
                            continue;
                        }

                        lost = VoxelEditing.ChangeByShare(in removal, ref material, ref density, covered, total);
                    }

                    if (material == before && lost == 0)
                    {
                        continue;
                    }

                    changed = true;
                    if (!write && removed.IsEmpty)
                    {
                        // Only asked whether it would change: it would.
                        return true;
                    }

                    if (!removed.IsEmpty && lost > 0)
                    {
                        removed[before] += lost * volume;
                    }

                    if (write)
                    {
                        target!.Set(x, y, z, material, density);
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// The kind's voxels the edit's box can reach, from its bounds' corners turned into the object (one voxel wider each
    /// way, for the turn's rounding), within the kind's box; false for none.
    /// </summary>
    internal static bool Range(
        in VoxelEdit edit,
        in ObjectPlacement placement,
        VoxelObjectKind kind,
        out VoxelCoord min,
        out VoxelCoord max
    )
    {
        edit.GetBounds(out var low, out var end);
        var anchor = placement.Anchor;
        long x0 = long.MaxValue,
            x1 = long.MinValue,
            z0 = long.MaxValue,
            z1 = long.MinValue;
        for (var corner = 0; corner < 4; corner++)
        {
            var wx = ((corner & 1) == 0 ? low.X : end.X) - (long)anchor.X;
            var wz = ((corner & 2) == 0 ? low.Z : end.Z) - (long)anchor.Z;
            var (ox, oz) = ObjectTurns.ToObject(placement.Heading, wx, wz);
            x0 = Math.Min(x0, ox);
            x1 = Math.Max(x1, ox);
            z0 = Math.Min(z0, oz);
            z1 = Math.Max(z1, oz);
        }

        var unit = kind.UnitCentimetres;
        var offset = kind.AnchorCentimetres;
        var size = kind.Size;
        min = new VoxelCoord(
            (int)Math.Max(0, Maths.DeterministicMath.FloorDivide(x0 + offset.X, unit) - 1),
            (int)Math.Max(0, Maths.DeterministicMath.FloorDivide((long)low.Y - anchor.Y + offset.Y, unit) - 1),
            (int)Math.Max(0, Maths.DeterministicMath.FloorDivide(z0 + offset.Z, unit) - 1)
        );
        max = new VoxelCoord(
            (int)Math.Min(size.X - 1, Maths.DeterministicMath.FloorDivide(x1 + offset.X, unit) + 1),
            (int)Math.Min(size.Y - 1, Maths.DeterministicMath.FloorDivide((long)end.Y - anchor.Y + offset.Y, unit) + 1),
            (int)Math.Min(size.Z - 1, Maths.DeterministicMath.FloorDivide(z1 + offset.Z, unit) + 1)
        );
        return min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
    }

    /// <summary>
    /// How many of a voxel's <paramref name="total"/> parts the edit covers: all when its eight extreme sample points are
    /// all inside the edit (each shape is convex), none when their box misses the edit's bounds, else counted.
    /// </summary>
    private static int Covered(
        in VoxelEdit edit,
        int heading,
        VoxelCoord anchor,
        long ox,
        long oy,
        long oz,
        int unit,
        int rows,
        int total
    )
    {
        long firstY = VoxelEditing.SubRowAt(oy, unit, rows, 0),
            lastY = VoxelEditing.SubRowAt(oy, unit, rows, rows - 1),
            firstZ = VoxelEditing.SubRowAt(oz, unit, rows, 0),
            lastZ = VoxelEditing.SubRowAt(oz, unit, rows, rows - 1);
        long minX = long.MaxValue,
            maxX = long.MinValue,
            minZ = long.MaxValue,
            maxZ = long.MinValue;
        var inside = 0;
        for (var corner = 0; corner < 8; corner++)
        {
            var px = (corner & 1) == 0 ? ox : ox + unit - 1;
            var py = (corner & 2) == 0 ? firstY : lastY;
            var pz = (corner & 4) == 0 ? firstZ : lastZ;
            var (wx, wz) = ObjectTurns.FromObject(heading, px, pz);
            long x = anchor.X + wx,
                z = anchor.Z + wz;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minZ = Math.Min(minZ, z);
            maxZ = Math.Max(maxZ, z);
            inside += edit.Contains((int)x, (int)(anchor.Y + py), (int)z) ? 1 : 0;
        }

        if (inside == 8)
        {
            return total;
        }

        edit.GetBounds(out var low, out var end);
        if (
            maxX < low.X
            || minX >= end.X
            || maxZ < low.Z
            || minZ >= end.Z
            || anchor.Y + lastY < low.Y
            || anchor.Y + firstY >= end.Y
        )
        {
            return 0;
        }

        if (
            edit.Shape == VoxelEditShape.Sphere
            && MissesSphere(in edit, minX, maxX, anchor.Y + firstY, anchor.Y + lastY, minZ, maxZ)
        )
        {
            return 0;
        }

        var covered = 0;
        for (var rz = 0; rz < rows; rz++)
        {
            var pz = VoxelEditing.SubRowAt(oz, unit, rows, rz);
            for (var ry = 0; ry < rows; ry++)
            {
                var y = anchor.Y + VoxelEditing.SubRowAt(oy, unit, rows, ry);
                for (var i = 0; i < unit; i++)
                {
                    var (wx, wz) = ObjectTurns.FromObject(heading, ox + i, pz);
                    covered += edit.Contains((int)(anchor.X + wx), (int)y, (int)(anchor.Z + wz)) ? 1 : 0;
                }
            }
        }

        return covered;
    }

    /// <summary>
    /// True when no point of the box (absolute centimetres, inclusive) lies in the sphere: its nearest point to the centre
    /// is farther than the radius. Every sample point of a voxel lies in the box of its eight extreme ones (the turn is the
    /// floor of a sum linear in each axis, so each coordinate's least and most are at the corners), so a voxel whose box
    /// misses the sphere has none covered, as counting them would find; most of a sphere's bounds are such voxels.
    /// </summary>
    private static bool MissesSphere(
        in VoxelEdit edit,
        long minX,
        long maxX,
        long minY,
        long maxY,
        long minZ,
        long maxZ
    )
    {
        var centre = edit.Origin;
        long dx = Math.Clamp(centre.X, minX, maxX) - centre.X,
            dy = Math.Clamp(centre.Y, minY, maxY) - centre.Y,
            dz = Math.Clamp(centre.Z, minZ, maxZ) - centre.Z,
            r = edit.Radius;
        return dx * dx + dy * dy + dz * dz > r * r;
    }

    /// <summary>True when the edit covers the point (ox, oy, oz) along the object's own axes from its anchor.</summary>
    private static bool Covers(in VoxelEdit edit, int heading, VoxelCoord anchor, long ox, long oy, long oz)
    {
        var (wx, wz) = ObjectTurns.FromObject(heading, ox, oz);
        return edit.Contains((int)(anchor.X + wx), (int)(anchor.Y + oy), (int)(anchor.Z + wz));
    }
}
