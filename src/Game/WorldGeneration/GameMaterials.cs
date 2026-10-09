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
}
