namespace Game.WorldGeneration;

/// <summary>
/// The world's material ids. 0 is air (<c>VoxelConstants.Air</c>). <see cref="GamePalette"/> says how each looks. A new
/// material takes the next id, a look in the palette, and, for a textured one, a texture set in <c>Textures/</c>.
/// </summary>
public static class GameMaterials
{
    /// <summary>Grass: the ground's surface.</summary>
    public const byte Grass = 1;

    /// <summary>Dirt: under the grass, down to the stone.</summary>
    public const byte Dirt = 2;

    /// <summary>Stone: the ground's body, and boulders.</summary>
    public const byte Stone = 3;

    /// <summary>Gravel: the surface where the ground lies low (below 2 m).</summary>
    public const byte Gravel = 4;

    /// <summary>Bark: the trees' trunks and branches, and the bushes' stems (<see cref="GameObjects"/>).</summary>
    public const byte Bark = 5;

    /// <summary>Leaves: the trees' crowns and the bushes; characters pass through them (<see cref="GamePassability"/>).</summary>
    public const byte Leaves = 6;

    /// <summary>
    /// Rock: the rocks among the trees, drawn as stone is, but a material of its own, apart from the ground's (a far copy's
    /// voxels are told apart by their material).
    /// </summary>
    public const byte Rock = 7;

    /// <summary>
    /// Boulder: the boulders' stone, drawn as stone is and climbable, but a material of its own, apart from the ground's.
    /// </summary>
    public const byte Boulder = 8;
}
