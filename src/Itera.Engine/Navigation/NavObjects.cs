using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Navigation;

/// <summary>
/// The voxel objects a world's generator places, as generated (before any edit), for the navigation thread to stamp onto
/// the cells it fills (<see cref="NavGridScanner.StampObjects"/>): the placer's objects whose anchors lie in the areas the
/// match's objects are kept in (<see cref="ObjectWorld.KeptBox"/>), each read in the mover's cells as the mover reads it
/// (<see cref="ObjectColumnReader"/>), so a cell stamped here reads the same once read from the placed objects. Made on
/// the main thread with every kind's tables made; used by the navigation thread alone.
/// </summary>
internal sealed class NavObjects
{
    private readonly IObjectPlacer _placer;
    private readonly ulong _seed;
    private readonly VoxelObjectKinds _kinds;
    private readonly (long MinX, long EndX, long MinZ, long EndZ) _kept;
    private readonly ObjectColumnReader _reader;
    private readonly List<ObjectPlacement> _found = [];
    private readonly List<ObjectSpan> _spans = [];

    /// <param name="placer">The world's placer.</param>
    /// <param name="seed">The world's seed.</param>
    /// <param name="kinds">The run's kinds, sealed, with their tables made (<see cref="VoxelObjectKind.MakeTables"/>).</param>
    /// <param name="passability">The materials characters pass through.</param>
    /// <param name="cellCentimetres">The mover's cells: the world's level-0 voxels.</param>
    /// <param name="kept">The box whose objects are placed (<see cref="ObjectWorld.KeptBox"/>).</param>
    public NavObjects(
        IObjectPlacer placer,
        ulong seed,
        VoxelObjectKinds kinds,
        IVoxelPassability passability,
        int cellCentimetres,
        (long MinX, long EndX, long MinZ, long EndZ) kept
    )
    {
        _placer = placer;
        _seed = seed;
        _kinds = kinds;
        _kept = kept;
        _reader = new ObjectColumnReader(MaterialSet.Passable(passability), cellCentimetres);
    }

    /// <summary>The edge of the mover's cells, in centimetres.</summary>
    public int CellCentimetres => _reader.CellCentimetres;

    /// <summary>The run's kinds.</summary>
    public VoxelObjectKinds Kinds => _kinds;

    /// <summary>
    /// The objects whose kind's reach meets the box from (<paramref name="minX"/>, <paramref name="minZ"/>) to
    /// (<paramref name="maxX"/>, <paramref name="maxZ"/>), absolute centimetres, inclusive; the list is reused by the next
    /// call. Throws, as the objects' world does, for a kind or heading the run does not have.
    /// </summary>
    public List<ObjectPlacement> Near(int minX, int maxX, int minZ, int maxZ)
    {
        _found.Clear();
        // Each kind's reach, and half a voxel (a cell reads the voxel column holding its centre).
        var slack = CellCentimetres / 2 + 1;
        var reach = _kinds.MostReachCentimetres + slack;
        _placer.Find(
            _seed,
            (int)Math.Max(_kept.MinX, (long)minX - reach),
            (int)Math.Min(_kept.EndX, (long)maxX + reach + 1),
            (int)Math.Max(_kept.MinZ, (long)minZ - reach),
            (int)Math.Min(_kept.EndZ, (long)maxZ + reach + 1),
            _found
        );
        var kept = 0;
        for (var i = 0; i < _found.Count; i++)
        {
            var placement = _found[i];
            if (placement.Kind.Value >= _kinds.Count || (uint)placement.Heading >= ObjectPlacement.HeadingSteps)
            {
                throw new InvalidOperationException(
                    $"The world's placer gave object {placement.Id.Value} kind {placement.Kind.Value} (of {_kinds.Count}) and heading {placement.Heading}."
                );
            }

            var anchor = placement.Anchor;
            var kindReach = _kinds[placement.Kind].ReachCentimetres + slack;
            if (
                anchor.X >= _kept.MinX
                && anchor.X < _kept.EndX
                && anchor.Z >= _kept.MinZ
                && anchor.Z < _kept.EndZ
                && (long)anchor.X + kindReach >= minX
                && (long)anchor.X - kindReach <= maxX
                && (long)anchor.Z + kindReach >= minZ
                && (long)anchor.Z - kindReach <= maxZ
            )
            {
                _found[kept++] = placement;
            }
        }

        _found.RemoveRange(kept, _found.Count - kept);
        return _found;
    }

    /// <summary>
    /// The runs of cells the object blocks, as generated, in column (<paramref name="x"/>, <paramref name="z"/>) of the
    /// mover's cells, lowest first; the list is reused by the next call.
    /// </summary>
    public List<ObjectSpan> Spans(in ObjectPlacement placement, int x, int z)
    {
        _spans.Clear();
        var kind = _kinds[placement.Kind];
        _reader.AddColumn(in placement, kind, kind, false, x, z, _spans);
        return _spans;
    }
}
