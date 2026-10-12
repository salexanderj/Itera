using Itera.Engine.Navigation;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Matches;

/// <summary>
/// A match's voxel terrain (<c>AddIteraVoxels</c>), in the match's scope: its world, the colliders loose bodies meet the
/// world through, navigation over the world, the object kinds' models, and the objects the world places. A match
/// without terrain has none
/// (<see cref="MatchServices.Terrain"/>); the voxel models and the loose bodies are every match's
/// (<see cref="MatchServices.Models"/>).
/// </summary>
public sealed class MatchVoxels(
    VoxelWorld voxels,
    VoxelColliders colliders,
    NavigationService navigation,
    VoxelObjects objects,
    ObjectWorld placed
)
{
    public VoxelWorld Voxels { get; } = voxels;

    public VoxelColliders Colliders { get; } = colliders;

    public NavigationService Navigation { get; } = navigation;

    public VoxelObjects Objects { get; } = objects;

    public ObjectWorld Placed { get; } = placed;

    /// <summary>
    /// Streams the world (<see cref="VoxelWorld.Update"/>) and then follows its columns with the placed objects
    /// (<see cref="ObjectWorld.Update"/>). Each loop iteration, before the fixed steps.
    /// </summary>
    public void Update(double nowSeconds)
    {
        Voxels.Update(nowSeconds);
        Placed.Update();
    }
}
