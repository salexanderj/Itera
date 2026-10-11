using Itera.Engine.Maths;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Game.WorldGeneration;

/// <summary>
/// The world, 8 km across (<see cref="WorldGrid"/>): rolling hills (<see cref="FractalNoise"/>) of grass, with gravel on
/// the hills' low ground, 5 cm of surface over dirt down to 1 m, then stone, and stone boulders (2 to 6 m across) a
/// third of their height buried in the ground, one a 16 m cell at most (<see cref="JitteredGrid"/>); past 1 km from the
/// middle, broad relief (2 km across, up to 30 m up or down) rises over the hills, whole from 1.5 km. On the engine's
/// <see cref="HeightfieldGenerator"/>, which keeps it fast and exact; a game grows its world from here. Thread safe.
/// Raise <see cref="Version"/> whenever the same seed would give other voxels, or the grid changes.
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

    /// <summary>The largest boulder's radius, in centimetres.</summary>
    public const int MaxBoulderRadius = 300;

    /// <summary>The share of the boulders' cells that hold one.</summary>
    public const double BoulderChance = 0.6;

    private static readonly FractalNoise s_hills = new([12800, 3200, 800, 200], [1, 0.35, 0.1, 0.03], 0x68696C6C73);
    private static readonly JitteredGrid s_boulders = new(1600, MaxBoulderRadius, 0x626F756C64, Margin: 100);
    private static readonly FractalNoise s_relief = new([200_000], [1], 0x72656C696566);

    /// <summary>
    /// The world's grid: voxels of 10 cm, 8 km across (±4,006.4 m in whole 12.8 m columns), from 51.2 m below the
    /// middle's height to 102.4 m above it; the ground lies from -34 m to 46 m, the boulders to 50 m.
    /// </summary>
    public static VoxelGrid WorldGrid { get; } = VoxelGrid.Sized(10, 4_000, -51.2, 102.4);

    public override string Id => "game.world";

    public override int Version => 2;

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
    /// The top of what is solid at (x, z) in centimetres: the ground, or a boulder's top where one stands there. Where a
    /// player or a camera may start. Allocates; not for generation.
    /// </summary>
    public static double SurfaceHeight(int x, int z, ulong seed)
    {
        var top = Height(x, z, seed);
        var found = new JitteredPoint[s_boulders.Capacity(x, x, z, z)];
        var count = s_boulders.Find(seed, BoulderChance, x, x, z, z, found);
        for (var i = 0; i < count; i++)
        {
            var boulder = Boulder.At(in found[i], seed);
            double dx = x - boulder.X;
            double dz = z - boulder.Z;
            var room = (double)boulder.Radius * boulder.Radius - dx * dx - dz * dz;
            if (room >= 0)
            {
                top = Math.Max(top, boulder.Y + Math.Sqrt(room));
            }
        }

        return top;
    }

    protected override double HeightAt(int x, int z, ulong seed) => Height(x, z, seed);

    // Gravel by the hills alone, so the broad relief's hollows keep the hills' patches instead of turning to gravel.
    protected override byte SurfaceAt(int x, int z, ulong seed, double height) =>
        height - Relief(x, z, seed) < GravelBelow ? GameMaterials.Gravel : GameMaterials.Grass;

    protected override GroundFeatures CreateFeatures() => new Boulders();

    /// <summary>A boulder: a ball of stone, in centimetres, its middle a third of its radius over the ground there.</summary>
    private readonly record struct Boulder(int X, int Y, int Z, int Radius) : IGroundFeature
    {
        public static Boulder At(in JitteredPoint point, ulong seed)
        {
            var state = point.State;
            var radius = DeterministicMath.NextRange(ref state, 100, MaxBoulderRadius);
            var ground = (int)Math.Floor(Height(point.X, point.Z, seed));
            return new Boulder(point.X, ground + radius / 3, point.Z, radius);
        }

        public bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
            X + Radius >= minX
            && X - Radius <= maxX
            && Y + Radius >= minY
            && Y - Radius <= maxY
            && Z + Radius >= minZ
            && Z - Radius <= maxZ;

        public void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels) =>
            Ellipsoid.Fill(X, Y, Z, Radius, Radius, Radius, GameMaterials.Stone, first, level, voxels);
    }

    /// <summary>The boulders near a column, found once a column on each worker thread.</summary>
    private sealed class Boulders : GroundFeatures
    {
        private readonly GroundFeatureList<Boulder> _boulders = new(64);
        private JitteredPoint[] _found = new JitteredPoint[64];

        public override void Find(ulong seed, GridLevel level, int minX, int maxX, int minZ, int maxZ)
        {
            _boulders.Clear();
            var capacity = s_boulders.Capacity(minX, maxX, minZ, maxZ);
            if (_found.Length < capacity)
            {
                _found = new JitteredPoint[capacity];
            }

            var count = s_boulders.Find(seed, BoulderChance, minX, maxX, minZ, maxZ, _found);
            for (var i = 0; i < count; i++)
            {
                _boulders.Add(Boulder.At(in _found[i], seed));
            }
        }

        public override bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
            _boulders.Touches(minX, maxX, minY, maxY, minZ, maxZ);

        public override void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels) =>
            _boulders.Fill(first, level, voxels);
    }
}
