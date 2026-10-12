namespace Itera.Engine.Voxels;

/// <summary>
/// Hears what each clear a player made takes out of the world (<see cref="VoxelWorld.Removals"/>): a game that gives players what they dig out hears it here.
/// The world counts it on its own voxels, and on the loaded voxel objects' (<c>ObjectDamage</c>; not an object whose copy
/// the damage cap dropped), just before the edit changes them, so a session's every game hears every player's clears, the
/// host's and, on a client, as the host's edits arrive, each counted from that game's own copy.
/// </summary>
public interface IVoxelRemovals
{
    /// <summary>
    /// Called once for each <see cref="VoxelEditOperation.Clear"/> or <see cref="VoxelEditOperation.ClearMaterials"/>
    /// edit with a maker (a session slot), before it is
    /// logged or changes a voxel, with what it removes by material (256 entries, in cubic centimetres; see
    /// <see cref="VoxelWorld.CountRemoved"/>). Entry 0 is not a material's: it holds what the clear takes from air
    /// voxels' densities, the solid of a neighbour's surface that reaches into them, which no material owns. Never for
    /// an edit without a maker, a replay, or a column loaded again.
    /// Main thread; the span is valid only during the call. It must not throw, nor edit the world.
    /// </summary>
    void Removed(in VoxelEdit edit, int maker, ReadOnlySpan<long> byMaterial);
}
