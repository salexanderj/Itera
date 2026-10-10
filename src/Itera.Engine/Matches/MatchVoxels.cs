using Itera.Engine.Navigation;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Matches;

/// <summary>
/// A match's voxel terrain (<c>AddIteraVoxels</c>), in the match's scope: its world, the colliders loose bodies meet the
/// world through, and navigation over the world. A match without terrain has none (<see cref="MatchServices.Terrain"/>);
/// the voxel models and the loose bodies are every match's (<see cref="MatchServices.Models"/>).
/// </summary>
public sealed class MatchVoxels(VoxelWorld voxels, VoxelColliders colliders, NavigationService navigation)
{
    public VoxelWorld Voxels { get; } = voxels;

    public VoxelColliders Colliders { get; } = colliders;

    public NavigationService Navigation { get; } = navigation;
}
