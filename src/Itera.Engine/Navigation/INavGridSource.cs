namespace Itera.Engine.Navigation;

/// <summary>
/// The game's part of navigation: the grid's extent, how to fill it from the game's world, and the flow fields to
/// keep. <see cref="NavigationService"/> builds nothing when no source is registered.
/// </summary>
public interface INavGridSource
{
    /// <summary>
    /// The highest a solid may rise over a cell's floor and still be stood on (it raises the floor); higher blocks the
    /// cell. Reading cells from the voxels follows the same rule as <see cref="Fill"/>.
    /// </summary>
    int LowCentimetres { get; }

    /// <summary>A new, empty grid covering the world. Called once, on the main thread.</summary>
    NavGrid CreateGrid();

    /// <summary>
    /// The flow fields to keep, their goals in <paramref name="grid"/>'s cells. Called once, on the main thread, after
    /// <see cref="CreateGrid"/>.
    /// </summary>
    IReadOnlyList<NavFieldSpec> CreateFields(NavGrid grid);

    /// <summary>
    /// Fills every cell of <paramref name="grid"/> from the world as generated, before any edit. Called at start and
    /// again after every reset of the world, on the navigation thread; it may use other threads.
    /// </summary>
    void Fill(NavGrid grid);

    /// <summary>
    /// The level-0 y from which <see cref="Fill"/> judges what stands at column (<paramref name="x"/>,
    /// <paramref name="z"/>) (the top of the ground there, or of a step the game lets walkers climb): reading cells from
    /// the voxels judges against it too, so solid stacked more than <see cref="LowCentimetres"/> over it blocks the cell
    /// however it was built. <see cref="int.MinValue"/> when the game has none there; the
    /// cell's present floor stands in for it. Any thread, after <see cref="CreateFields"/>.
    /// </summary>
    int GroundAt(int x, int z) => int.MinValue;

    /// <summary>
    /// True when removing <paramref name="material"/> counts as a dig where the voxels cannot be read (a far
    /// <see cref="Voxels.VoxelEditOperation.ClearMaterials"/>; a pit only where soil goes);
    /// by default every material does. Any thread.
    /// </summary>
    bool IsGround(byte material) => true;
}
