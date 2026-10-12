using Itera.Engine.Voxels;

namespace Game.WorldGeneration;

/// <summary>
/// What characters and navigation pass through: the leaves of the trees' crowns and of the bushes. Bark, rock, and the
/// ground block; rays and tools still meet leaves, so they can be cut.
/// </summary>
public sealed class GamePassability : IVoxelPassability
{
    public bool IsPassable(byte material) => material == GameMaterials.Leaves;
}
