using Itera.Engine.Maths;
using Itera.Engine.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary>
/// A heightfield generator for tests: rolling hills of <see cref="FractalNoise"/> with sand on the low ground and grass
/// above it, a surface layer 5 cm thick, dirt to 1 m, then stone, boulders standing in the ground (rock, half buried),
/// and caves (air balls) cut up through it. A boulder or cave is a <see cref="Ball"/> placed on a
/// <see cref="JitteredGrid"/>. Features can be left out, to see what they changed.
/// </summary>
internal sealed class HillsGenerator(VoxelGrid grid, bool features = true)
    : HeightfieldGenerator(grid, new GroundLayers(5, [new GroundLayer(100, Dirt)], Stone))
{
    public const byte Grass = 1;
    public const byte Dirt = 2;
    public const byte Stone = 3;
    public const byte Sand = 4;
    public const byte Rock = 5;

    /// <summary>Surface height below which the ground is sand, in centimetres.</summary>
    public const double SandBelow = 450;

    public const int MaxRadius = 350;

    private static readonly FractalNoise s_hills = new([6400, 1600, 400, 90], [1, 0.5, 0.25, 0.1], 0x68696C6C73);
    private static readonly JitteredGrid s_boulders = new(1600, MaxRadius, 0x626F756C64, Margin: 100);
    private static readonly JitteredGrid s_caves = new(2000, 300, 0x63617665, Margin: 100);

    public override string Id => "test.hills";

    public override int Version => 1;

    /// <summary>The ground's height at (x, z) in centimetres: 6 m, up and down by up to 9 m.</summary>
    public static double Height(int x, int z, ulong seed) =>
        600 + 900 * FractalNoise.SoftClip(s_hills.Value(x, z, seed) * 1.6);

    /// <summary>The balls that can touch the box of sample points, in the order they are written: boulders, then caves.</summary>
    public static List<Ball> BallsNear(ulong seed, int minX, int maxX, int minZ, int maxZ)
    {
        var balls = new List<Ball>();
        foreach (var (grid, cave) in new[] { (s_boulders, false), (s_caves, true) })
        {
            DeterministicMath.CellRange(grid.Cell, grid.Reach, minX, maxX, out var firstX, out var lastX);
            DeterministicMath.CellRange(grid.Cell, grid.Reach, minZ, maxZ, out var firstZ, out var lastZ);
            for (var cellZ = firstZ; cellZ <= lastZ; cellZ++)
            {
                for (var cellX = firstX; cellX <= lastX; cellX++)
                {
                    if (grid.TryGet(cellX, cellZ, seed, 0.7, out var point))
                    {
                        balls.Add(BallAt(point, seed, cave));
                    }
                }
            }
        }

        return balls;
    }

    protected override double HeightAt(int x, int z, ulong seed) => Height(x, z, seed);

    protected override byte SurfaceAt(int x, int z, ulong seed, double height) => height < SandBelow ? Sand : Grass;

    protected override GroundFeatures? CreateFeatures() => features ? new HillsFeatures() : null;

    private static Ball BallAt(JitteredPoint point, ulong seed, bool cave)
    {
        var state = point.State;
        var height = (int)Math.Floor(Height(point.X, point.Z, seed));
        return cave
            ? new Ball(point.X, height - 150, point.Z, DeterministicMath.NextRange(ref state, 200, 300), 0)
            : new Ball(point.X, height + 60, point.Z, DeterministicMath.NextRange(ref state, 150, MaxRadius), Rock);
    }

    /// <summary>A ball of one material, in centimetres: a boulder, or air for a cave.</summary>
    internal readonly record struct Ball(int X, int Y, int Z, int Radius, byte Material) : IGroundFeature
    {
        public bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
            X + Radius >= minX
            && X - Radius <= maxX
            && Y + Radius >= minY
            && Y - Radius <= maxY
            && Z + Radius >= minZ
            && Z - Radius <= maxZ;

        public void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels) =>
            Ellipsoid.Fill(X, Y, Z, Radius, Radius, Radius, Material, first, level, voxels);

        /// <summary>True when the point (centimetres) lies in the ball.</summary>
        public bool Contains(int x, int y, int z)
        {
            long dx = x - X,
                dy = y - Y,
                dz = z - Z;
            return dx * dx + dy * dy + dz * dz <= (long)Radius * Radius;
        }
    }

    private sealed class HillsFeatures : GroundFeatures
    {
        private readonly GroundFeatureList<Ball> _balls = new(256);
        private JitteredPoint[] _found = new JitteredPoint[256];

        public override void Find(ulong seed, GridLevel level, int minX, int maxX, int minZ, int maxZ)
        {
            _balls.Clear();
            AddAll(s_boulders, false, seed, minX, maxX, minZ, maxZ);
            AddAll(s_caves, true, seed, minX, maxX, minZ, maxZ);
        }

        public override bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ) =>
            _balls.Touches(minX, maxX, minY, maxY, minZ, maxZ);

        public override void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels) =>
            _balls.Fill(first, level, voxels);

        private void AddAll(JitteredGrid grid, bool cave, ulong seed, int minX, int maxX, int minZ, int maxZ)
        {
            var capacity = grid.Capacity(minX, maxX, minZ, maxZ);
            if (_found.Length < capacity)
            {
                _found = new JitteredPoint[capacity];
            }

            var count = grid.Find(seed, 0.7, minX, maxX, minZ, maxZ, _found);
            for (var i = 0; i < count; i++)
            {
                _balls.Add(BallAt(_found[i], seed, cave));
            }
        }
    }
}
