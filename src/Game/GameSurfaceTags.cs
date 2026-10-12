using Game.WorldGeneration;
using Itera.Engine.Voxels;

namespace Game;

/// <summary>
/// The starter's surface tags, bits the engine passes on and the game reads (<see cref="IVoxelSurfaceTags"/> for the
/// voxels' materials): the boulders, and stone where the ground is dug down to it, are <see cref="Climbable"/> (the
/// engine's <c>ClimbingMode</c>, which meets the boulders as objects).
/// </summary>
public sealed class GameSurfaceTags : IVoxelSurfaceTags
{
    /// <summary>A surface the player can climb.</summary>
    public const uint Climbable = 1;

    public uint TagsOf(byte material) => material is GameMaterials.Stone or GameMaterials.Boulder ? Climbable : 0;
}
