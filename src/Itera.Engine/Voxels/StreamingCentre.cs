using System.Numerics;

namespace Itera.Engine.Voxels;

/// <summary>
/// A place voxel columns are loaded around (<see cref="VoxelWorld.SetStreamingCentres"/>): level
/// k loads columns within <paramref name="Level0RadiusMetres"/> times 2^k, for the first <paramref name="Levels"/>
/// levels, or, for the view, within each level's radius (<see cref="VoxelWorld.Distances"/>). The player's own view
/// uses every level; a session host streams a small level-0 disc around a remote player or a loose body.
/// </summary>
/// <param name="Position">The centre in metres.</param>
/// <param name="Level0RadiusMetres">The radius at level 0, in metres.</param>
/// <param name="Levels">How many levels, from level 0, this centre streams.</param>
/// <param name="View">
/// True for the view (the player's eye or the camera): its levels load to the world's level distances, not to
/// <paramref name="Level0RadiusMetres"/> times 2^k.
/// </param>
public readonly record struct StreamingCentre(
    Vector3 Position,
    double Level0RadiusMetres,
    int Levels,
    bool View = false
);
