using Game.WorldGeneration;
using Itera.Engine.Voxels;

namespace Game;

/// <summary>
/// The starter's surface tags, bits the engine passes on and the game reads (<see cref="IVoxelSurfaceTags"/> for the
/// voxels' materials): stone, the boulders, is <see cref="Climbable"/> (<see cref="ClimbMode"/>).
/// </summary>
public sealed class GameSurfaceTags : IVoxelSurfaceTags
{
    /// <summary>A surface the player can climb.</summary>
    public const uint Climbable = 1;

    public uint TagsOf(byte material) => material == GameMaterials.Stone ? Climbable : 0;
}
