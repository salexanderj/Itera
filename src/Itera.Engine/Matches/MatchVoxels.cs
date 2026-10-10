using Itera.Engine.Navigation;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Matches;

/// <summary>
/// A match's voxel terrain (<c>AddIteraVoxels</c>), in the match's scope: its world, the voxel models kept in the
/// world's memory, the colliders loose bodies meet the world through, the loose bodies as voxels for the character
/// mover, and navigation over the world. A match without terrain has none (<see cref="MatchServices.Terrain"/>).
/// </summary>
public sealed class MatchVoxels(
    VoxelWorld voxels,
    VoxelModels models,
    VoxelColliders colliders,
    LooseBodyVoxels bodyVoxels,
    NavigationService navigation
)
{
    public VoxelWorld Voxels { get; } = voxels;

    public VoxelModels Models { get; } = models;

    public VoxelColliders Colliders { get; } = colliders;

    public LooseBodyVoxels BodyVoxels { get; } = bodyVoxels;

    public NavigationService Navigation { get; } = navigation;
}
