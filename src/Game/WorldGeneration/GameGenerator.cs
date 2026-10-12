using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Game.WorldGeneration;

/// <summary>
/// The world, 8 km across (<see cref="WorldGrid"/>): rolling hills (<see cref="FractalNoise"/>) of grass, with gravel on
/// the hills' low ground, 5 cm of surface over dirt down to 1 m, then stone; past 1 km from the middle, broad relief
/// (2 km across, up to 30 m up or down) rises over the hills, whole from 1.5 km. On it stand its voxel objects (boulders,
/// trees, bushes, and rocks: <see cref="GameForests"/>). On the engine's <see cref="HeightfieldGenerator"/>, which keeps
/// it fast and exact; a game grows its world from here. Thread safe. Raise <see cref="Version"/> whenever the same seed
/// would give other voxels or objects, or the grid changes.
/// </summary>
public sealed class GameGenerator()
    : HeightfieldGenerator(
        WorldGrid,
        new GroundLayers(5, [new GroundLayer(100, GameMaterials.Dirt)], GameMaterials.Stone)
    )
{
    /// <summary>The hills' height (the ground less the broad relief) below which the ground is gravel, in centimetres.</summary>
    public const double GravelBelow = 200;

    /// <summary>The distance from the middle where the broad relief starts to rise, in centimetres.</summary>
    public const double ReliefFrom = 100_000;

    /// <summary>The distance from the middle where the broad relief is whole, in centimetres.</summary>
    public const double ReliefTo = 150_000;

    /// <summary>The most the broad relief raises or lowers the ground, in centimetres.</summary>
    public const double ReliefCentimetres = 3_000;

    private static readonly FractalNoise s_hills = new([12800, 3200, 800, 200], [1, 0.35, 0.1, 0.03], 0x68696C6C73);
    private static readonly FractalNoise s_relief = new([200_000], [1], 0x72656C696566);
    private static readonly GameForests s_forests = new();

    /// <summary>
    /// The world's grid: voxels of 10 cm, 8 km across (±4,006.4 m in whole 12.8 m columns), from 51.2 m below the
    /// middle's height to 102.4 m above it; the ground lies from -34 m to 46 m, the boulders reach 50 m.
    /// </summary>
    public static VoxelGrid WorldGrid { get; } = VoxelGrid.Sized(10, 4_000, -51.2, 102.4);

    public override string Id => "game.world";

    public override int Version => 4;

    /// <summary>The boulders, trees, bushes, and rocks (<see cref="GameForests"/>).</summary>
    public override Itera.Engine.Objects.IObjectPlacer Objects => s_forests;

    /// <summary>
    /// The ground's height at (x, z) in centimetres: about 6 m, up and down by up to 10 m in hills, and past 1 km from
    /// the middle up and down by up to 30 m more (<see cref="Relief"/>).
    /// </summary>
    public static double Height(int x, int z, ulong seed)
    {
        var hills = 600 + 1000 * FractalNoise.SoftClip(s_hills.Value(x, z, seed) * 1.3);
        return hills + Relief(x, z, seed);
    }

    /// <summary>
    /// The broad relief at (x, z) in centimetres: none within <see cref="ReliefFrom"/> of the middle, rising smoothly to
    /// whole at <see cref="ReliefTo"/>, where it is up to <see cref="ReliefCentimetres"/> up or down over 2 km.
    /// </summary>
    public static double Relief(int x, int z, ulong seed)
    {
        var squared = (double)x * x + (double)z * z;
        if (squared <= ReliefFrom * ReliefFrom)
        {
            return 0;
        }

        var weight = 1.0;
        if (squared < ReliefTo * ReliefTo)
        {
            var t = (Math.Sqrt(squared) - ReliefFrom) / (ReliefTo - ReliefFrom);
            weight = t * t * (3 - 2 * t);
        }

        return weight * ReliefCentimetres * FractalNoise.SoftClip(s_relief.Value(x, z, seed) * 1.3);
    }

    /// <summary>
    /// The top of what is solid at (x, z) in centimetres: the ground, or a boulder's top where one stands there
    /// (<see cref="GameForests.SurfaceHeight"/>). Where a player or a camera may start. Not for generation.
    /// </summary>
    public static double SurfaceHeight(int x, int z, ulong seed) => GameForests.SurfaceHeight(x, z, seed);

    protected override double HeightAt(int x, int z, ulong seed) => Height(x, z, seed);

    // Gravel by the hills alone, so the broad relief's hollows keep the hills' patches instead of turning to gravel.
    protected override byte SurfaceAt(int x, int z, ulong seed, double height) =>
        height - Relief(x, z, seed) < GravelBelow ? GameMaterials.Gravel : GameMaterials.Grass;
}
