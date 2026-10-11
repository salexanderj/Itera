using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Maths;

/// <summary>
/// A move of the match's origin (<see cref="WorldOrigin"/>) from <paramref name="From"/> to <paramref name="To"/>,
/// absolute centimetres on whole metres. Nothing in the world moves: a local position kept before it is local to the new
/// origin once it is less <see cref="Metres"/> (<see cref="Apply"/>).
/// </summary>
/// <param name="From">Where the origin was.</param>
/// <param name="To">Where it is now.</param>
public readonly record struct OriginShift(VoxelCoord From, VoxelCoord To)
{
    /// <summary>How far the origin moved, in metres: whole, so exact in a float.</summary>
    public Vector3 Metres =>
        new(
            (float)(((double)To.X - From.X) / VoxelConstants.CentimetresPerMetre),
            (float)(((double)To.Y - From.Y) / VoxelConstants.CentimetresPerMetre),
            (float)(((double)To.Z - From.Z) / VoxelConstants.CentimetresPerMetre)
        );

    /// <summary>
    /// The local position <paramref name="local"/>, kept before the move, local to the new origin. It rounds to the new
    /// origin's floats: a point far from it moves by its float's step there.
    /// </summary>
    public Vector3 Apply(Vector3 local) => local - Metres;
}

/// <summary>
/// Where a game's match starts, for its first origin: the match's origin starts on the whole metres nearest
/// <see cref="Start"/>, so what turns the game's start into local positions as it is made (systems, their cameras) gets
/// small floats from the first step. Registered in the match's scope by a game whose start may lie far from the map's
/// middle (the starter's <c>StartPose</c>); without one the origin starts at the middle.
/// </summary>
public interface IOriginStart
{
    /// <summary>Where the match starts, absolute.</summary>
    WorldPoint Start { get; }
}

/// <summary>
/// Something of the game's that keeps local positions outside the engine's components (a component of its own, a
/// service's list) and moves them when the origin moves (<c>OriginShifts</c>). Registered in the match's scope; called on
/// the main thread after the engine has moved its own, between steps or in the first fixed system; it makes no
/// structural changes to the ECS world.
/// </summary>
public interface IOriginShiftHandler
{
    /// <summary>Moves what this keeps by <paramref name="shift"/> (each local position less its metres).</summary>
    void OnOriginShifted(in OriginShift shift);
}
