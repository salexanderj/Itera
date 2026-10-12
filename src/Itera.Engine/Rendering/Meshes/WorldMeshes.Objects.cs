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
    private ObjectWorld? _objects;
    private VoxelObjects? _objectModels;
    private int _handOver;
    private readonly HashSet<(int X, int Z)> _cameraSplit = [];
    private readonly HashSet<(int X, int Z)> _farSplit = [];

    /// <summary>The level-K columns the camera's pass split below K in the last frame. For tests.</summary>
    internal IReadOnlyCollection<(int X, int Z)> CameraSplit => _cameraSplit;

    /// <summary>The level-K columns the far cascade's pass split below K in the last frame. For tests.</summary>
    internal IReadOnlyCollection<(int X, int Z)> FarSplit => _farSplit;

    /// <summary>Follows the match's objects (none without terrain).</summary>
    private void BeginObjects(ObjectWorld? objects, VoxelObjects? objectModels)
    {
        _objects = objectModels is null ? null : objects;
        _objectModels = objectModels;
        _handOver = objects?.HandOverLevel ?? 0;
        _cameraSplit.Clear();
        _farSplit.Clear();
    }

    private void EndObjects()
    {
        _objects = null;
        _objectModels = null;
        _cameraSplit.Clear();
        _farSplit.Clear();
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
        var origin = _origin;
        var levelZero = _distances.ReachCentimetres(0) * _reachShare / VoxelConstants.CentimetresPerMetre;
        var levelOne = _distances.ReachCentimetres(1) * _reachShare / VoxelConstants.CentimetresPerMetre;
        var views = _viewCount == 0 ? 0 : ((1 << _viewCount) - 1) * FirstViewList;
        foreach (var area in _objects.Areas)
        {
            if (!area.Filled || !AnySplitRound(area.X, area.Z))
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
                foreach (ref readonly var part in objectModels.PartsOf(placement.Kind))
                {
                    var corner = local + Vector3.Transform(part.Corner, turn);
                    GatherModel(models, part.Model, level, corner, turn, 0, ModelShadow.Casts, lists & ~views);
                    if ((lists & views) != 0)
                    {
                        GatherModel(models, part.Model, 2, corner, turn, 0, ModelShadow.Casts, lists & views);
                    }
                }
            }
        }
    }

    /// <summary>True when either pass split the level-K column (x, z) or one of the eight round it.</summary>
    private bool AnySplitRound(int x, int z)
    {
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
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
