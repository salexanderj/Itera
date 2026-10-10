namespace Itera.Engine.Voxels;

/// <summary>
/// A game's surface tags of its voxel materials: up to 32 bits a game defines (for example climbable), which a hit on a
/// voxel of the material carries (<c>CollisionSources.Raycast</c>, the character mover's contacts). Mesh colliders take
/// theirs by material name when a game registers them (<c>MeshColliders.Add</c>). The engine defines no tag.
/// </summary>
public interface IVoxelSurfaceTags
{
    /// <summary>The tags of <paramref name="material"/>; 0 for none.</summary>
    uint TagsOf(byte material);
}

/// <summary>No material has a tag.</summary>
public sealed class NoVoxelSurfaceTags : IVoxelSurfaceTags
{
    public uint TagsOf(byte material) => 0;
}
