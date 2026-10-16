using Itera.Engine.Maths;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Objects;

/// <summary>
/// An object's number: the same on every peer and every time its world is made, so edits, saves, and messages can name
/// it. Its placer gives it from what placed it, such as a jittered grid's cell (<see cref="Of"/>), and one placed during
/// play takes it from its place, kind, heading, and edit (<see cref="Placed"/>); no two objects of a world share one.
/// </summary>
public readonly record struct ObjectId(ulong Value)
{
    /// <summary>The number of the object a jittered grid placed at <paramref name="point"/>, for a grid of <paramref name="salt"/>.</summary>
    public static ObjectId Of(in JitteredPoint point, ulong salt) =>
        new(DeterministicMath.Hash(point.CellX, point.CellZ, salt, 0x6F626A656374));

    /// <summary>
    /// The number of an object placed during play: a hash of its anchor, kind, heading, and the sequence its edit takes,
    /// and <paramref name="attempt"/> (raised past a number a live object has).
    /// </summary>
    public static ObjectId Placed(
        VoxelCoord anchor,
        VoxelObjectKindId kind,
        int heading,
        long sequence,
        int attempt = 0
    )
    {
        var hash = DeterministicMath.Hash(anchor.X, anchor.Z, (ulong)(uint)anchor.Y, 0x706C61636564);
        hash = DeterministicMath.Hash(hash, ((ulong)kind.Value << 32) | (uint)heading);
        hash = DeterministicMath.Hash(hash, (ulong)sequence);
        return new(DeterministicMath.Hash(hash, (ulong)attempt));
    }
}

/// <summary>
/// Where a world places one object: its <see cref="Id"/>, its kind, its anchor (the kind's
/// <see cref="VoxelObjectKind.AnchorCentimetres"/>) at absolute centimetres, and its heading about the vertical in
/// steps of a whole turn over <see cref="HeadingSteps"/>, from the kind's own axes (0) towards +x from -z; and the
/// sequence of the edit that placed it during play (<see cref="Since"/>, from which the log's edits change it; 0 for
/// one the world's generator places, which every edit since the last reset changes).
/// </summary>
public readonly record struct ObjectPlacement(
    ObjectId Id,
    VoxelObjectKindId Kind,
    VoxelCoord Anchor,
    int Heading,
    long Since = 0
)
{
    /// <summary>The steps of a whole turn a heading counts in.</summary>
    public const int HeadingSteps = 4096;
}

/// <summary>
/// A world's objects (trees, bushes, rocks), as its generator places them (<see cref="IChunkGenerator.Objects"/>). Worker
/// threads and the main thread call it, several at once, so it must be thread safe and a pure function of its arguments,
/// deterministic on every machine as the generator is: the same seed and box give the same placements in the same order.
/// </summary>
public interface IObjectPlacer
{
    /// <summary>
    /// Adds to <paramref name="found"/> every object whose anchor lies in the box from (<paramref name="minX"/>,
    /// <paramref name="minZ"/>) up to but not including (<paramref name="maxX"/>, <paramref name="maxZ"/>), in absolute
    /// centimetres, in a fixed order, so boxes that tile an area find each object once. Each kind is one of the run's
    /// (<see cref="VoxelObjectKinds"/>) and each heading 0 to <see cref="ObjectPlacement.HeadingSteps"/> - 1.
    /// </summary>
    void Find(ulong seed, int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found);
}
