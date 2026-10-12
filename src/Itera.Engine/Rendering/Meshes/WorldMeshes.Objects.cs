using System.Numerics;
using System.Runtime.InteropServices;
using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The voxel objects a match's world places (<see cref="ObjectWorld"/>), drawn as their kinds' models where the world is
/// drawn finer than the hand-over level K: each pass (the camera's, whose columns the near cascade and the image views
/// share, and the far cascade's) records the level-K columns it split below K (<see cref="RecordSplit"/>), where the
/// world's own voxels hold no copy of the objects, and an object's models go into a pass's lists when a level-K column
/// under its reach is one of them; past them, the world's coarser levels draw its copy. Its models' level follows its
/// distance as the world's levels do (its own voxels within level 0's reach, twice them within level 1's, else four
/// times), and the image views draw the coarsest. Gathered after the game's models and the retired ones, so a full frame
/// drops objects first.
/// </summary>
public sealed unsafe partial class WorldMeshes
{
    // A model draw's tint that shades it as the world's surfaces (MeshModelDraw.Tint).
    private const float WorldShaded = -1;

    private ObjectWorld? _objects;
    private VoxelObjects? _objectModels;
    private ObjectDamage? _damage;
    private int _handOver;
    private bool _objectModelsReady;
    private readonly HashSet<(int X, int Z)> _cameraSplit = [];
    private readonly HashSet<(int X, int Z)> _farSplit = [];

    /// <summary>The level-K columns the camera's pass split below K in the last frame. For tests.</summary>
    internal IReadOnlyCollection<(int X, int Z)> CameraSplit => _cameraSplit;

    /// <summary>The level-K columns the far cascade's pass split below K in the last frame. For tests.</summary>
    internal IReadOnlyCollection<(int X, int Z)> FarSplit => _farSplit;

    /// <summary>Follows the match's objects (none without terrain), damaged ones drawn as their edits left them.</summary>
    private void BeginObjects(ObjectWorld? objects, VoxelObjects? objectModels, ObjectDamage? damage)
    {
        _damage = damage;
        // Followed only where the world places objects of some kind.
        _objects = objects is { HasPlacer: true } && objectModels is { Kinds.Count: > 0 } ? objects : null;
        _objectModels = objectModels;
        _handOver = objects?.HandOverLevel ?? 0;
        _objectModelsReady = false;
        _cameraSplit.Clear();
        _farSplit.Clear();
    }

    private void EndObjects()
    {
        _objects = null;
        _objectModels = null;
        _damage = null;
        _cameraSplit.Clear();
        _farSplit.Clear();
    }

    /// <summary>
    /// True when the column <paramref name="key"/> may split for its objects: below the hand-over level K its voxels hold no
    /// copy of them, so a level-K column splits only once the areas whose objects reach into it are filled
    /// (<see cref="ObjectWorld.IsKnown"/>) and the kinds' models are on the GPU; until then it draws the copies, and no
    /// object goes undrawn. Always for other levels, and without objects. Allocates nothing.
    /// </summary>
    private bool ObjectsReady(ColumnKey key)
    {
        if (_objects is null || key.Level != _handOver)
        {
            return true;
        }

        if (!_objectModelsReady)
        {
            // The kinds' models stay on the GPU for the match: once they all are, they are.
            if (_objectModels is not { IsInitialized: true } models || !models.AllUploaded())
            {
                return false;
            }

            _objectModelsReady = true;
        }

        long width = _grid.ColumnCentimetres(key.Level);
        long x = key.X * width,
            z = key.Z * width;
        return _objects.IsKnown(x, x + width - 1, z, z + width - 1);
    }

    /// <summary>Notes that the pass drew <paramref name="leaf"/>: below the hand-over level, its level-K column is split.</summary>
    private void RecordSplit(ColumnKey leaf)
    {
        if (_objects is null || leaf.Level >= _handOver)
        {
            return;
        }

        var shift = _handOver - leaf.Level;
        (_farPass ? _farSplit : _cameraSplit).Add((leaf.X >> shift, leaf.Z >> shift));
    }

    /// <summary>Forgets the pass's split columns before it chooses its columns again.</summary>
    private void BeginSplits() => (_farPass ? _farSplit : _cameraSplit).Clear();

    /// <summary>
    /// Takes each placed object whose reach meets a split level-K column into the lists of the passes that split it, its
    /// parts' models turned and placed from its anchor.
    /// </summary>
    private void GatherObjects(VoxelModels models)
    {
        if (
            _objects is null
            || _objectModels is not { IsInitialized: true } objectModels
            || (_cameraSplit.Count == 0 && _farSplit.Count == 0)
        )
        {
            return;
        }

        var kinds = objectModels.Kinds;
        var width = _objects.AreaCentimetres;
        // The areas whose objects may reach a split column: as far round it as the widest kind reaches.
        var round = (int)((kinds.MostReachCentimetres + width - 1) / width);
        var origin = _origin;
        var levelZero = _distances.ReachCentimetres(0) * _reachShare / VoxelConstants.CentimetresPerMetre;
        var levelOne = _distances.ReachCentimetres(1) * _reachShare / VoxelConstants.CentimetresPerMetre;
        var views = _viewCount == 0 ? 0 : ((1 << _viewCount) - 1) * FirstViewList;
        // Nearest the camera first, so a list too full for every draw keeps the nearest.
        var areas = SortedAreas(width);
        for (var a = 0; a < areas.Length; a++)
        {
            var area = areas[a].Area;
            if (!AnySplitRound(area.X, area.Z, round))
            {
                continue;
            }

            foreach (ref readonly var placement in CollectionsMarshal.AsSpan(area.Objects))
            {
                var reach = kinds[placement.Kind].ReachCentimetres;
                var anchor = placement.Anchor;
                var lists = 0;
                var x0 = DeterministicMath.FloorDivide((long)anchor.X - reach, width);
                var x1 = DeterministicMath.FloorDivide((long)anchor.X + reach, width);
                var z0 = DeterministicMath.FloorDivide((long)anchor.Z - reach, width);
                var z1 = DeterministicMath.FloorDivide((long)anchor.Z + reach, width);
                for (var z = z0; z <= z1; z++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        lists |= _cameraSplit.Contains(((int)x, (int)z)) ? CameraList | NearList | views : 0;
                        lists |= _farSplit.Contains(((int)x, (int)z)) ? FarList : 0;
                    }
                }

                if (lists == 0)
                {
                    continue;
                }

                var local =
                    new Vector3(anchor.X - origin.X, anchor.Y - origin.Y, anchor.Z - origin.Z)
                    / VoxelConstants.CentimetresPerMetre;
                double dx = anchor.X / (double)VoxelConstants.CentimetresPerMetre - _viewAt.X,
                    dy = anchor.Y / (double)VoxelConstants.CentimetresPerMetre - _viewAt.Y,
                    dz = anchor.Z / (double)VoxelConstants.CentimetresPerMetre - _viewAt.Z;
                var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var level =
                    distance < levelZero ? 0
                    : distance < levelOne ? 1
                    : 2;
                var turn = ObjectTurns.Rotation(placement.Heading);
                // The views draw the coarsest level: one draw for all when it is the level drawn already.
                var apart = level == 2 ? 0 : lists & views;
                // A damaged object's own part models once on the GPU, its kind's for the rest.
                var parts = _damage is { } damage ? damage.PartsOf(in placement) : objectModels.PartsOf(placement.Kind);
                foreach (ref readonly var part in parts)
                {
                    var corner = local + Vector3.Transform(part.Corner, turn);
                    GatherModel(
                        models,
                        part.Model,
                        level,
                        corner,
                        turn,
                        WorldShaded,
                        ModelShadow.Casts,
                        lists & ~apart
                    );
                    if (apart != 0)
                    {
                        GatherModel(models, part.Model, 2, corner, turn, WorldShaded, ModelShadow.Casts, apart);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Makes air of the objects' copies in a coarser block read to stitch a block of <paramref name="fineLevel"/> to it,
    /// where the finer level holds no copies (below the hand-over level) and the coarser does: the seam's coarser
    /// vertices are then the ground's, as the finer block's own, and no copy shows in the finer level's meshes. The copies'
    /// materials are the objects' alone.
    /// </summary>
    private static void MaskCopies(VoxelWorld world, int fineLevel, Span<byte> samples, Span<byte> densities)
    {
        if (world.Copies is not { } copies || fineLevel + 1 < copies.FirstLevel || fineLevel >= copies.FirstLevel)
        {
            return;
        }

        var materials = copies.Materials;
        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] != VoxelConstants.Air && materials.Contains(samples[i]))
            {
                samples[i] = VoxelConstants.Air;
                densities[i] = VoxelDensity.Empty;
            }
        }
    }

    /// <summary>
    /// The filled areas, nearest the camera first (by their middles), in a table kept from frame to frame.
    /// </summary>
    private ReadOnlySpan<AreaByDistance> SortedAreas(long width)
    {
        if (_sortedAreas.Length < _objects!.AreaCount)
        {
            _sortedAreas = new AreaByDistance[Math.Max(_objects.AreaCount, 2 * _sortedAreas.Length)];
        }

        var count = 0;
        foreach (var area in _objects.Areas)
        {
            if (!area.Filled)
            {
                continue;
            }

            double dx = (area.X + 0.5) * width / VoxelConstants.CentimetresPerMetre - _viewAt.X,
                dz = (area.Z + 0.5) * width / VoxelConstants.CentimetresPerMetre - _viewAt.Z;
            _sortedAreas[count++] = new AreaByDistance(dx * dx + dz * dz, area);
        }

        // An insertion sort: a few dozen areas, and Array.Sort with a comparer makes a delegate each call.
        for (var i = 1; i < count; i++)
        {
            var area = _sortedAreas[i];
            var j = i - 1;
            while (j >= 0 && _sortedAreas[j].Squared > area.Squared)
            {
                _sortedAreas[j + 1] = _sortedAreas[j];
                j--;
            }

            _sortedAreas[j + 1] = area;
        }

        return _sortedAreas.AsSpan(0, count);
    }

    private AreaByDistance[] _sortedAreas = new AreaByDistance[64];

    /// <summary>A filled area and its middle's squared distance from the camera, in metres.</summary>
    private readonly record struct AreaByDistance(double Squared, ObjectWorld.Area Area);

    /// <summary>True when either pass split the level-K column (x, z) or one within <paramref name="round"/> columns of it.</summary>
    private bool AnySplitRound(int x, int z, int round)
    {
        for (var dz = -round; dz <= round; dz++)
        {
            for (var dx = -round; dx <= round; dx++)
            {
                if (_cameraSplit.Contains((x + dx, z + dz)) || _farSplit.Contains((x + dx, z + dz)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
