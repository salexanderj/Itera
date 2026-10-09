namespace Itera.Engine.Voxels;

/// <summary>
/// How far each level of detail of the view goes, in centimetres: its reach, up to which rays draw it, and its
/// radius, within which its columns load, <see cref="VoxelConstants.ReachMarginColumns"/> of its own columns past its
/// reach. Level 0 is drawn to <see cref="Level0ReachCentimetres"/> and each level after it to <see cref="Growth"/> times as
/// far as the one before. The coarsest level loads to the view distance, <see cref="BaseRadiusCentimetres"/> times
/// 2^(<see cref="Levels"/> - 1), and rays use it to the end of the view. The defaults (level 0 drawn to the base radius
/// less two columns, growth 2) make every level's radius the base radius times 2^k.
/// </summary>
/// <param name="BaseRadiusCentimetres"><c>Engine:Voxels:StreamingRadiusMetres</c>, in centimetres.</param>
/// <param name="Levels">Levels of detail.</param>
/// <param name="Level0ReachCentimetres">
/// How far level 0 is drawn; below zero (a base radius under two columns) it is drawn nowhere but still loads.
/// </param>
/// <param name="Growth">How many times farther each level is drawn than the one before, 2 or less.</param>
public readonly record struct LevelDistances(
    long BaseRadiusCentimetres,
    int Levels,
    long Level0ReachCentimetres,
    double Growth
)
{
    /// <summary>The size of the voxels whose columns these distances count margins in (<see cref="VoxelGrid.ReachMarginCentimetres"/>).</summary>
    public VoxelGrid Grid { get; init; } = VoxelGrid.Unit;

    /// <summary>The distances <paramref name="settings"/> give a world of <paramref name="grid"/>.</summary>
    public static LevelDistances From(VoxelSettings settings, VoxelGrid grid)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var radius = (long)(settings.StreamingRadiusMetres * VoxelConstants.CentimetresPerMetre);
        var reach =
            settings.Level0ReachMetres > 0
                ? (long)Math.Round(settings.Level0ReachMetres * VoxelConstants.CentimetresPerMetre)
                : radius - grid.ReachMarginCentimetres(0);
        return new LevelDistances(radius, VoxelWorld.LevelsFor(settings), reach, settings.ReachGrowth) { Grid = grid };
    }

    /// <summary>
    /// The distances of a view whose level-0 radius is <paramref name="radiusMetres"/>, each level twice as far, in a
    /// world of <paramref name="grid"/>.
    /// </summary>
    public static LevelDistances Doubling(double radiusMetres, int levels, VoxelGrid grid)
    {
        var radius = (long)(radiusMetres * VoxelConstants.CentimetresPerMetre);
        return new LevelDistances(radius, levels, radius - grid.ReachMarginCentimetres(0), 2) { Grid = grid };
    }

    /// <summary>The view distance: the coarsest level's radius.</summary>
    public long ViewRadiusCentimetres => BaseRadiusCentimetres << (Levels - 1);

    /// <summary>
    /// How far rays draw <paramref name="level"/> before they take the next; the coarsest level's is its radius less the
    /// margin (characters are drawn to there). Past the coarsest level each is twice the one before.
    /// </summary>
    public long ReachCentimetres(int level) =>
        level >= Levels - 1
            ? Math.Max(0, RadiusCentimetres(level) - Grid.ReachMarginCentimetres(level))
            : Math.Max(0, Grown(level));

    /// <summary>How far from the view <paramref name="level"/>'s columns load; past the coarsest level, twice the one before.</summary>
    public long RadiusCentimetres(int level) =>
        level >= Levels - 1
            ? ViewRadiusCentimetres << (level - (Levels - 1))
            : Grown(level) + Grid.ReachMarginCentimetres(level);

    /// <summary><see cref="RadiusCentimetres"/> in metres.</summary>
    public double RadiusMetres(int level) => RadiusCentimetres(level) / (double)VoxelConstants.CentimetresPerMetre;

    /// <summary><see cref="ReachCentimetres"/> in metres.</summary>
    public double ReachMetres(int level) => ReachCentimetres(level) / (double)VoxelConstants.CentimetresPerMetre;

    /// <summary>
    /// Level 0's reach times the growth to the power <paramref name="level"/>; exact at growth 2, so the defaults give
    /// what doubling gives.
    /// </summary>
    private long Grown(int level) =>
        Growth == 2
            ? Level0ReachCentimetres << level
            : (long)Math.Round(Level0ReachCentimetres * Math.Pow(Growth, level));
}
