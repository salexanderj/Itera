using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// Ground for the mover at 10 cm: a floor at <see cref="FloorCentimetres"/> (off the 10 cm lines) with strips along x,
/// each 3 m wide in z, rising from x = 2 m: slopes of 30, 40, and 60 degrees in the ground (a voxel's density the share of
/// it below the ground at its sample point, as the game's generator gives its ground's), then whole-voxel features on the
/// floor: stairs of 20 cm risers and 30 cm treads, a 50 cm step, and a wall; and a last 30-degree slope under a flat
/// stone ceiling 3.8 to 4.2 m up from x = 3 m. The slopes end in flats at 5 m.
/// </summary>
internal sealed class DensityTerrainGenerator(VoxelGrid grid) : IChunkGenerator
{
    public const byte Dirt = 2;
    public const byte Stone = 3;

    /// <summary>The floor's height, in centimetres.</summary>
    public const double FloorCentimetres = 153;

    /// <summary>Where the slopes, stairs, and steps start along x, in centimetres.</summary>
    public const int Start = 200;

    /// <summary>
    /// Each strip's lowest z, in centimetres: 30, 40, 60 degrees, the stairs, the step, the wall, and 30 degrees under a
    /// ceiling.
    /// </summary>
    public static readonly int[] Strips = [0, 400, 800, 1200, 1600, 2000, 2400];

    /// <summary>A strip's width in z, in centimetres.</summary>
    public const int StripWidth = 300;

    private static readonly double[] s_rises = [0.57735026918962576, 0.83909963117728, 1.7320508075688772];

    public VoxelGrid Grid { get; } = grid;

    public string Id => "test.density-terrain";

    public int Version => 1;

    /// <summary>The middle z of strip <paramref name="strip"/>, in centimetres.</summary>
    public static int Middle(int strip) => Strips[strip] + StripWidth / 2;

    /// <summary>The ground's height at (x, z), in centimetres: the floor, or a slope's.</summary>
    public static double Height(double x, double z)
    {
        for (var strip = 0; strip < s_rises.Length; strip++)
        {
            if (z >= Strips[strip] && z < Strips[strip] + StripWidth && x > Start)
            {
                return FloorCentimetres + s_rises[strip] * (Math.Min(x, 500) - Start);
            }
        }

        if (z >= Strips[6] && z < Strips[6] + StripWidth && x > Start)
        {
            return FloorCentimetres + s_rises[0] * (Math.Min(x, 500) - Start);
        }

        return FloorCentimetres;
    }

    /// <summary>The height of a whole-voxel feature at (x, z), in centimetres, or the floor where none stands.</summary>
    public static int FeatureTop(int x, int z)
    {
        if (z >= Strips[3] && z < Strips[3] + StripWidth && x >= Start)
        {
            // Stairs of 30 cm treads, each 20 cm higher, from the 10 cm line under the floor's top.
            var stair = Math.Min((x - Start) / 30, 3);
            return 150 + 20 * (stair + 1);
        }

        if (z >= Strips[4] && z < Strips[4] + StripWidth && x >= 300 && x < 400)
        {
            return 150 + 50;
        }

        if (z >= Strips[5] && z < Strips[5] + StripWidth && x >= 300 && x < 330)
        {
            return 450;
        }

        return int.MinValue;
    }

    /// <summary>True where the ceiling over the last slope stands, in centimetres.</summary>
    public static bool Ceiling(int x, int y, int z) =>
        z >= Strips[6] && z < Strips[6] + StripWidth && x >= 300 && x < 600 && y >= 380 && y < 420;

    /// <summary>
    /// True when the 10 cm voxel (x, y, z) is not air: its sample point lies under the ground, in a feature, or in the
    /// ceiling (what the generator fills at level 0 on a 10 cm grid).
    /// </summary>
    public static bool Solid(int x, int y, int z)
    {
        int sampleX = x * 10 + 5,
            sampleY = y * 10 + 5,
            sampleZ = z * 10 + 5;
        return sampleY < FeatureTop(sampleX, sampleZ)
            || Ceiling(sampleX, sampleY, sampleZ)
            || sampleY < Height(sampleX, sampleZ);
    }

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) => layers.Fill(ChunkFill.Mixed);

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) => Fill(chunk, seed, voxels, default);

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels, Span<byte> densities)
    {
        const int size = VoxelConstants.ChunkSize;
        var level = chunk.Level;
        var edge = Grid.Edge(level);
        var origin = chunk.LevelOrigin;
        for (var z = 0; z < size; z++)
        {
            var sampleZ = Grid.SampleAt(origin.Z + z, level);
            for (var x = 0; x < size; x++)
            {
                var sampleX = Grid.SampleAt(origin.X + x, level);
                var height = Height(sampleX, sampleZ);
                var feature = FeatureTop(sampleX, sampleZ);
                for (var y = 0; y < size; y++)
                {
                    var index = x + size * (y + size * z);
                    var low = (double)(origin.Y + y) * edge;
                    var sampleY = Grid.SampleAt(origin.Y + y, level);
                    var material =
                        sampleY < feature || Ceiling(sampleX, sampleY, sampleZ) ? Stone
                        : sampleY < height ? Dirt
                        : VoxelConstants.Air;
                    voxels[index] = material;
                    if (!densities.IsEmpty)
                    {
                        var share = material == Stone ? 1 : Math.Clamp((height - low) / edge, 0, 1);
                        densities[index] = VoxelDensity.Fit(material, (byte)Math.Floor(share * 255 + 0.5));
                    }
                }
            }
        }
    }
}
