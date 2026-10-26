using System.Numerics;
using System.Runtime.InteropServices;
using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The voxel objects a match's world places (<see cref="ObjectWorld"/>), drawn as their kinds' models where the world is
/// drawn finer than the hand-over level K: each pass (the camera's, whose columns the near cascade and the image views
/// share, and the far cascade's) records the level-K columns it split below K (<see cref="RecordSplit"/>), where the
/// world's own voxels hold no copy of the objects, and an object's models go into a pass's lists when a level-K column
/// under its reach is one of them; past them, the world's coarser levels draw its copy. Its models' level follows its
/// anchor's distance as the world's levels do (its own voxels within level 0's reach, twice them within level 1's, else
/// four times), each level fading into the next over <see cref="LevelFadeMetres"/> each side of the distance between
/// them, and the coarsest fading out over its copy, which stays drawn whole, from the farthest an object of a split
/// level-K column can lie (<see cref="HandOverFade"/>) over <see cref="HandOverFadeMetres"/>: the camera draws the models
/// of every known object nearer than that band's end, so the hand-over follows the distance alone, not the columns. A
/// fade gives each draw a share of a screen-space dither's thresholds (<see cref="MeshModelDraw.Shown"/>), the two levels
/// fading into each other taking the two sides of one share. The fades are the camera's alone; the cascades cast each
/// object's one level by its distance, and the image views draw the coarsest, whole. Gathered after the game's models and the retired
/// ones, so a full frame drops objects first.
/// </summary>
public sealed unsafe partial class WorldMeshes
{
    // A model draw's tint that shades it as the world's surfaces (MeshModelDraw.Tint).
    private const float WorldShaded = -1;

    /// <summary>How far each side of the distance between two model levels the two fade into each other, in metres.</summary>
    public const float LevelFadeMetres = 2;

    /// <summary>How far past where the fine models must end the coarsest fades out over the far copy, in metres.</summary>
    public const float HandOverFadeMetres = 30;

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
        _objects = objects is { IsEnabled: true } && objectModels is { Kinds.Count: > 0 } ? objects : null;
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
    /// The distances from the camera, in metres, over which an object's coarsest model fades out over its far copy: from
    /// the farthest an object of a split level-K column can lie (the reach that splits it, its width's diagonal, and the
    /// widest kind's reach), over <see cref="HandOverFadeMetres"/>. None (the largest half) where nothing is copied.
    /// </summary>
    internal (float Start, float End) HandOverFade()
    {
        if (_objects is null || _objectModels is null || _world?.Copies is null)
        {
            return (65504, 65504);
        }

        var split = _distances.ReachCentimetres(_handOver - 1) * _reachShare / VoxelConstants.CentimetresPerMetre;
        var width = _grid.ColumnCentimetres(_handOver) / (double)VoxelConstants.CentimetresPerMetre;
        var reach = _objectModels.Kinds.MostReachCentimetres / (double)VoxelConstants.CentimetresPerMetre;
        var start = (float)(split + width * Math.Sqrt(2) + reach);
        return (start, start + HandOverFadeMetres);
    }

    /// <summary>
    /// Takes each placed object whose reach meets a split level-K column into the lists of the passes that split it, and
    /// each known one nearer than the hand-over's fade's end into the camera's: its parts' models turned and placed from
    /// its anchor, at each level its distances reach, faded.
    /// </summary>
    private void GatherObjects(VoxelModels models)
    {
        if (_objects is null || _objectModels is not { IsInitialized: true } objectModels)
        {
            return;
        }

        var kinds = objectModels.Kinds;
        var width = _objects.AreaCentimetres;
        // The areas whose objects may reach a split column: as far round it as the widest kind reaches.
        var round = (int)((kinds.MostReachCentimetres + width - 1) / width);
        var origin = _origin;
        var (fadeStart, fadeEnd) = HandOverFade();
        var copies = _world?.Copies;
        // A kind with no copy has nothing to hand over to: its models are drawn, whole, as far as the view reaches.
        var anyUncopied = false;
        for (var k = 0; k < kinds.Count && copies is not null; k++)
        {
            anyUncopied |= !copies.IsCopied(new VoxelObjectKindId((ushort)k));
        }

        // An area holds its objects' anchors, so it may hold one nearer than the fade's end when its middle (across) is
        // within that and its half diagonal.
        var areaReach = width * Math.Sqrt(2) / 2 / VoxelConstants.CentimetresPerMetre;
        var views = _viewCount == 0 ? 0 : ((1 << _viewCount) - 1) * FirstViewList;
        // Nearest the camera first, so a list too full for every draw keeps the nearest.
        var areas = SortedAreas(width);
        for (var a = 0; a < areas.Length; a++)
        {
            var area = areas[a].Area;
            var near = anyUncopied || Math.Sqrt(areas[a].Squared) - areaReach < fadeEnd;
            if (!near && !AnySplitRound(area.X, area.Z, round))
            {
                continue;
            }

            foreach (ref readonly var placement in CollectionsMarshal.AsSpan(area.Objects))
            {
                var kind = kinds[placement.Kind];
                var reach = kind.ReachCentimetres;
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

                double dx = anchor.X / (double)VoxelConstants.CentimetresPerMetre - _viewAt.X,
                    dy = anchor.Y / (double)VoxelConstants.CentimetresPerMetre - _viewAt.Y,
                    dz = anchor.Z / (double)VoxelConstants.CentimetresPerMetre - _viewAt.Z;
                var distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var copied = copies is null || copies.IsCopied(placement.Kind);
                // An object under no split column has its copy drawn: the camera draws its model over it, fading out over
                // the hand-over's band. One under a split column has none, nor has a kind not copied: its model stays
                // whole, and casts into the cascades its bounds meet.
                var split = (lists & CameraList) != 0;
                lists |= distance < fadeEnd ? CameraList : 0;
                lists |= copied ? 0 : CameraList | NearList | FarList | views;
                if (lists == 0)
                {
                    continue;
                }

                // Its level follows its voxels' size on the drawn image at its nearest point (ObjectVoxelPixels).
                var unit = kind.UnitCentimetres / (float)VoxelConstants.CentimetresPerMetre;
                var nearest = Math.Max(distance - KindRadiusMetres(kind), 0);
                var (levelZero, levelOne) = ObjectLevelDistances(unit);
                var level =
                    nearest < levelZero ? 0
                    : nearest < levelOne ? 1
                    : 2;
                // The cascades cast its level, or a coarser one whose voxels are no narrower than the map's texels: the
                // far cascade's 22 cm or so take a 10 cm object's 40 cm level.
                var nearLevel = Math.Max(level, LevelForTexel(unit, _view.NearTexelMetres));
                var farLevel = Math.Max(level, LevelForTexel(unit, _view.FarTexelMetres));
                // The shares of the dither's thresholds each level shows at its distance: level 0 from the band to level 1
                // up, level 1 between that and the band to level 2, level 2 below that, each over the hand-over's fade
                // (which, with a hand-over level of 1, may begin before level 2's band).
                var toOne = Ramp(nearest, levelZero - LevelFadeMetres, levelZero + LevelFadeMetres);
                var toTwo = Ramp(nearest, levelOne - LevelFadeMetres, levelOne + LevelFadeMetres);
                var toCopy = split || !copied ? 0 : Ramp(distance, fadeStart, fadeEnd);
                var local =
                    new Vector3(anchor.X - origin.X, anchor.Y - origin.Y, anchor.Z - origin.Z)
                    / VoxelConstants.CentimetresPerMetre;
                var turn = ObjectTurns.Rotation(placement.Heading);
                // A damaged object's own part models once on the GPU, its kind's for the rest.
                var parts = _damage is { } damage ? damage.PartsOf(in placement) : objectModels.PartsOf(placement.Kind);
                foreach (ref readonly var part in parts)
                {
                    var corner = local + Vector3.Transform(part.Corner, turn);
                    for (var drawn = 0; drawn < 3; drawn++)
                    {
                        var (from, to) = drawn switch
                        {
                            0 => (Math.Max(toCopy, toOne), 1f),
                            1 => (Math.Max(toCopy, toTwo), toOne),
                            _ => (toCopy, toTwo),
                        };
                        // The camera draws every level with a share; each cascade casts its one level.
                        var shown = MeshModelDraw.Shows(from, to);
                        var drawnLists =
                            (shown != MeshModelDraw.ShowsNone ? lists & CameraList : 0)
                            | (drawn == nearLevel ? lists & NearList : 0)
                            | (drawn == farLevel ? lists & FarList : 0);
                        if (drawnLists != 0)
                        {
                            GatherModel(
                                models,
                                part.Model,
                                drawn,
                                corner,
                                turn,
                                WorldShaded,
                                ModelShadow.Casts,
                                drawnLists,
                                shown
                            );
                        }
                    }

                    // The views draw the coarsest level, whole.
                    if ((lists & views) != 0)
                    {
                        GatherModel(models, part.Model, 2, corner, turn, WorldShaded, ModelShadow.Casts, lists & views);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The distances from the eye, in metres, past which an object of <paramref name="unitMetres"/> voxels draws its
    /// level 1 and its level 2: where one voxel of the finer level is <see cref="ObjectVoxelPixels"/> wide on the drawn
    /// image. An orthographic view is the same size at every distance: its levels are all or nothing.
    /// </summary>
    private (float LevelZero, float LevelOne) ObjectLevelDistances(float unitMetres)
    {
        var pixels = _view.PixelsPerMetre * unitMetres / ObjectVoxelPixels;
        if (!_view.Orthographic)
        {
            return (pixels, 2 * pixels);
        }

        // Past every distance (the finer level drawn) or before every one (the coarser).
        const float Always = 65504;
        const float Never = -65504;
        return (pixels >= 1 ? Always : Never, 2 * pixels >= 1 ? Always : Never);
    }

    /// <summary>
    /// The finest of an object's three levels whose voxels (<paramref name="unitMetres"/> at level 0, doubling) are at
    /// least <paramref name="texelMetres"/> wide; 0 without a map; the coarsest when none is.
    /// </summary>
    private static int LevelForTexel(float unitMetres, float texelMetres)
    {
        var level = 0;
        while (level < VoxelModels.MaxLevels - 1 && unitMetres * (1 << level) < texelMetres)
        {
            level++;
        }

        return level;
    }

    /// <summary>How far from its anchor a kind's voxels reach in any direction, in metres: across, up, and down.</summary>
    internal static float KindRadiusMetres(VoxelObjectKind kind)
    {
        double across = kind.ReachCentimetres;
        double upDown = Math.Max(Math.Abs(kind.TopCentimetres), Math.Abs(kind.BottomCentimetres));
        return (float)(Math.Sqrt(across * across + upDown * upDown) / VoxelConstants.CentimetresPerMetre);
    }

    /// <summary>
    /// How far <paramref name="value"/> lies from <paramref name="from"/> to <paramref name="to"/>, 0 to 1 (a step at
    /// <paramref name="to"/> where the two meet).
    /// </summary>
    private static float Ramp(float value, float from, float to) =>
        to <= from ? (value >= to ? 1 : 0) : Math.Clamp((value - from) / (to - from), 0, 1);

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
