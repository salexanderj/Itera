using System.Numerics;

namespace Itera.Engine.Voxels;

/// <summary>Where a ray first met a solid voxel.</summary>
/// <param name="Voxel">
/// The low corner, in centimetres, of the solid level-0 voxel; for a coarser hit, of the level-0 voxel where the ray
/// entered the coarse one. At a grid of 1 cm, that voxel's own centimetre.
/// </param>
/// <param name="Normal">The face the ray entered through, as a unit step (for example (0, 1, 0) for the top face);
/// zero when the ray started inside the voxel.</param>
/// <param name="Material">The voxel's material.</param>
/// <param name="DistanceMetres">Distance along the ray to the entry point.</param>
/// <param name="Level">The level of the voxel it met: 0, or a coarser one where level 0 was not resident.</param>
/// <param name="BoxMin">The low corner, in centimetres, of the voxel it met, at its level.</param>
/// <param name="BoxCentimetres">The edge of the voxel it met, in centimetres.</param>
public readonly record struct VoxelHit(
    VoxelCoord Voxel,
    VoxelCoord Normal,
    byte Material,
    float DistanceMetres,
    int Level = 0,
    VoxelCoord BoxMin = default,
    int BoxCentimetres = 1
)
{
    /// <summary>
    /// The middle of the voxel it met, in centimetres, rounded down: where a tool aimed at it centres its edit. At a grid
    /// of 1 cm, a level-0 hit's <see cref="Voxel"/>.
    /// </summary>
    public VoxelCoord BoxMiddle =>
        new(BoxMin.X + BoxCentimetres / 2, BoxMin.Y + BoxCentimetres / 2, BoxMin.Z + BoxCentimetres / 2);

    /// <summary>
    /// The centimetre a small edit struck along this ray centres on: the ray's entry point into the voxel it met
    /// (<paramref name="originMetres"/> and <paramref name="direction"/> are the ray's), moved
    /// <see cref="EntryDepthMetres"/> further along the ray so it lies inside that voxel, where the material is read,
    /// rounded down. Where the voxels are larger than a centimetre this is where the ray met the struck face, not the
    /// voxel's corner up to a voxel away; at a grid of 1 cm, a level-0 hit's <see cref="Voxel"/> or its neighbour.
    /// </summary>
    public VoxelCoord Entry(Vector3 originMetres, Vector3 direction) =>
        VoxelCoord.FromMetres(originMetres + Vector3.Normalize(direction) * (DistanceMetres + EntryDepthMetres));

    /// <summary>How far past the entry point <see cref="Entry"/> looks, in metres: half a centimetre.</summary>
    public const float EntryDepthMetres = 0.005f;
}

public sealed partial class VoxelWorld
{
    /// <summary>More steps than any ray within the map can need; a guard against a bug, not a limit in practice.</summary>
    private const int MaxRaySteps = 1 << 20;

    /// <summary>
    /// Finds the first solid level-0 voxel along a ray, within <paramref name="maxMetres"/>. Columns that are not
    /// resident count as air, unless <paramref name="coarse"/> is true: then where level 0 is not resident the walk reads
    /// the finest level that is (a round's flight past the level-0 ring, which would otherwise pass through every hill),
    /// and the hit says its level. It walks the largest empty or solid part of the tree that holds the current voxel
    /// (chunk, cell, brick, a quarter or an eighth of a brick, or a voxel), in double precision, so it costs little in
    /// open air. Main thread;
    /// allocates nothing.
    /// </summary>
    /// <param name="originMetres">Start of the ray, in metres.</param>
    /// <param name="direction">Direction of the ray; need not be unit length, must not be zero.</param>
    /// <param name="maxMetres">Longest distance to look.</param>
    /// <param name="hit">The hit, when there is one.</param>
    /// <param name="coarse">True to read coarser levels where level 0 is not resident.</param>
    public bool Raycast(Vector3 originMetres, Vector3 direction, float maxMetres, out VoxelHit hit, bool coarse = false)
    {
        hit = default;
        var length = direction.Length();
        if (!(length > 0) || !float.IsFinite(length))
        {
            throw new ArgumentException("The ray direction must be finite and not zero.", nameof(direction));
        }

        var memory = Memory;
        // Metres to level-0 voxels of the grid; the walk itself is in double precision.
        var perVoxel = (double)Grid.BaseCentimetres;
        var origin = originMetres * (VoxelConstants.CentimetresPerMetre / (float)perVoxel);
        double ox = origin.X,
            oy = origin.Y,
            oz = origin.Z;
        double dx = direction.X / (double)length,
            dy = direction.Y / (double)length,
            dz = direction.Z / (double)length;
        int vx = (int)Math.Floor(ox),
            vy = (int)Math.Floor(oy),
            vz = (int)Math.Floor(oz);
        var maxT = maxMetres * VoxelConstants.CentimetresPerMetre / perVoxel;
        var t = 0.0;
        var axis = -1;
        for (var step = 0; step < MaxRaySteps && t <= maxT; step++)
        {
            var size = coarse
                ? ProbeCoarse(memory, vx, vy, vz, out var material, out var level)
                : Probe(memory, vx, vy, vz, out material, out level);
            if (material != VoxelConstants.Air)
            {
                var normal = axis switch
                {
                    0 => new VoxelCoord(dx > 0 ? -1 : 1, 0, 0),
                    1 => new VoxelCoord(0, dy > 0 ? -1 : 1, 0),
                    2 => new VoxelCoord(0, 0, dz > 0 ? -1 : 1),
                    _ => default,
                };
                var box = new VoxelCoord((vx >> level) << level, (vy >> level) << level, (vz >> level) << level);
                hit = new VoxelHit(
                    Grid.ToCentimetres(new VoxelCoord(vx, vy, vz)),
                    normal,
                    material,
                    (float)(t * perVoxel / VoxelConstants.CentimetresPerMetre),
                    level,
                    Grid.ToCentimetres(box),
                    Grid.Edge(level)
                );
                return true;
            }

            // Leave the air block that holds the voxel through its nearest face; the voxel index steps across that
            // face, and no coordinate moves against the ray, so every step makes progress.
            int lx = vx & ~(size - 1),
                ly = vy & ~(size - 1),
                lz = vz & ~(size - 1);
            var ex = ChunkTree.RayExit(lx, size, ox, dx);
            var ey = ChunkTree.RayExit(ly, size, oy, dy);
            var ez = ChunkTree.RayExit(lz, size, oz, dz);
            var next = Math.Min(ex, Math.Min(ey, ez));
            axis =
                next == ex ? 0
                : next == ey ? 1
                : 2;
            t = Math.Max(next, t);
            vx = ChunkTree.RayForward((int)Math.Floor(ox + dx * t), vx, dx);
            vy = ChunkTree.RayForward((int)Math.Floor(oy + dy * t), vy, dy);
            vz = ChunkTree.RayForward((int)Math.Floor(oz + dz * t), vz, dz);
            switch (axis)
            {
                case 0:
                    vx = dx > 0 ? lx + size : lx - 1;
                    break;
                case 1:
                    vy = dy > 0 ? ly + size : ly - 1;
                    break;
                default:
                    vz = dz > 0 ? lz + size : lz - 1;
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// True when voxels block the line from <paramref name="originMetres"/> along <paramref name="direction"/> for
    /// <paramref name="maxMetres"/>: cover from a blast's fragments, or a line of sight. Where level 0 is not resident it
    /// reads the finest level that is (<see cref="Raycast"/> with <c>coarse</c>; columns that are not resident count as
    /// air otherwise, so a far wall or hill hid nothing). A coarse voxel's surface lies up to its own size above level 0's,
    /// so a coarse hit counts only if the same line lifted by that size and half a centimetre meets voxels too: a line
    /// that skims coarse ground, or starts inside it, is not blocked; a wall or a hill blocks both. Main thread; allocates
    /// nothing.
    /// </summary>
    public bool LineBlocked(Vector3 originMetres, Vector3 direction, float maxMetres)
    {
        if (!Raycast(originMetres, direction, maxMetres, out var hit, coarse: true))
        {
            return false;
        }

        if (hit.Level == 0)
        {
            return true;
        }

        var lift = (hit.BoxCentimetres + 0.5f) / VoxelConstants.CentimetresPerMetre;
        return Raycast(originMetres + new Vector3(0, lift, 0), direction, maxMetres, out _, coarse: true);
    }

    /// <summary>
    /// The size (128, 32, 8, 4, 2, or 1) of the largest part of the tree that holds the voxel and is all air or all one
    /// material, and that material (air for air, and for columns that are not resident).
    /// </summary>
    private int Probe(VoxelMemory memory, int x, int y, int z, out byte material, out int level)
    {
        level = 0;
        material = VoxelConstants.Air;
        if (
            !_columns.TryGetValue(ColumnKey.Containing(new VoxelCoord(x, y, z), 0), out var column)
            || column.State != ColumnState.Resident
        )
        {
            return VoxelConstants.ChunkSize;
        }

        var layer = (y >> VoxelConstants.ChunkShift) - Grid.MinChunkY;
        if ((uint)layer >= (uint)column.Layers.Length)
        {
            return VoxelConstants.ChunkSize;
        }

        return ChunkTree.Probe(
            memory,
            column.Layers[layer],
            x & (VoxelConstants.ChunkSize - 1),
            y & (VoxelConstants.ChunkSize - 1),
            z & (VoxelConstants.ChunkSize - 1),
            out material
        );
    }

    /// <summary>
    /// As <see cref="Probe"/>, reading the finest resident level that holds the voxel: the size, in level-0 voxels, of
    /// the largest part of that level's tree that holds it and is all air or all one material, that material, and the
    /// level. Air and level 0 where no level is resident.
    /// </summary>
    private int ProbeCoarse(VoxelMemory memory, int x, int y, int z, out byte material, out int level)
    {
        var voxel = new VoxelCoord(x, y, z);
        for (level = 0; level < _levels; level++)
        {
            if (
                !_columns.TryGetValue(ColumnKey.Containing(voxel, level), out var column)
                || column.State != ColumnState.Resident
            )
            {
                continue;
            }

            var layer = (y >> (VoxelConstants.ChunkShift + level)) - Grid.MinChunkYAt(level);
            if ((uint)layer >= (uint)column.Layers.Length)
            {
                material = VoxelConstants.Air;
                return VoxelConstants.ChunkSize;
            }

            const int mask = VoxelConstants.ChunkSize - 1;
            var size = ChunkTree.Probe(
                memory,
                column.Layers[layer],
                (x >> level) & mask,
                (y >> level) & mask,
                (z >> level) & mask,
                out material
            );
            return size << level;
        }

        level = 0;
        material = VoxelConstants.Air;
        return VoxelConstants.ChunkSize;
    }
}
