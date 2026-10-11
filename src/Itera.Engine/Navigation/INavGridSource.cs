namespace Itera.Engine.Navigation;

/// <summary>
/// The game's part of navigation: how to fill the grid's cells from the game's world, and the flow fields to keep. The
/// engine places the grid's window (the map, or <c>Engine:Navigation:WindowCells</c> round the players) and asks for its
/// cells a rectangle at a time as it moves. <see cref="NavigationService"/> builds nothing when no source is registered.
/// </summary>
public interface INavGridSource
{
    /// <summary>
    /// The highest a solid may rise over a cell's floor and still be stood on (it raises the floor); higher blocks the
    /// cell. Reading cells from the voxels follows the same rule as <see cref="Fill"/>.
    /// </summary>
    int LowCentimetres { get; }

    /// <summary>The flow fields to keep, their goals absolute places. Called once a match, on the main thread.</summary>
    IReadOnlyList<NavFieldSpec> CreateFields();

    /// <summary>
    /// Fills the cells of <paramref name="grid"/> from columns <paramref name="firstColumn"/> to
    /// <paramref name="lastColumn"/> and rows <paramref name="firstRow"/> to <paramref name="lastRow"/> (inclusive, in
    /// the grid's window: <see cref="NavGrid.CentreX"/> gives a column's absolute x) from the world as generated, before
    /// any edit; it leaves every other cell as it is. Called for the whole window at start and after every reset of the
    /// world, and for the strips a move of the window brings in, on the navigation thread; it may use other threads.
    /// </summary>
    void Fill(NavGrid grid, int firstColumn, int firstRow, int lastColumn, int lastRow);

    /// <summary>
    /// The level-0 y from which <see cref="Fill"/> judges what stands at column (<paramref name="x"/>,
    /// <paramref name="z"/>) (the top of the ground there, or of a step the game lets walkers climb): reading cells from
    /// the voxels judges against it too, so solid stacked more than <see cref="LowCentimetres"/> over it blocks the cell
    /// however it was built. <see cref="int.MinValue"/> when the game has none there; the
    /// cell's present floor stands in for it. Any thread.
    /// </summary>
    int GroundAt(int x, int z) => int.MinValue;

    /// <summary>
    /// True when removing <paramref name="material"/> counts as a dig where the voxels cannot be read (a far
    /// <see cref="Voxels.VoxelEditOperation.ClearMaterials"/>; a pit only where soil goes);
    /// by default every material does. Any thread.
    /// </summary>
    bool IsGround(byte material) => true;
}
