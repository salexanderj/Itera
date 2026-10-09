using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// Ground of dirt on a grid of any base, rising <see cref="Rise"/> per centimetre along x and <see cref="RiseZ"/> along z
/// from <see cref="BaseCentimetres"/> at the origin, with each voxel's density the share of it below the ground at its
/// sample point, as the game's generator gives its ground's: for the mover's and the mesher's tests at 10 cm.
/// </summary>
internal sealed class SlopedGroundGenerator(VoxelGrid grid, double rise, double riseZ = 0) : IChunkGenerator
{
    public const byte Dirt = 2;

    /// <summary>The ground's height at the origin, in centimetres.</summary>
    public const double BaseCentimetres = 150;

    public VoxelGrid Grid { get; } = grid;

    public double Rise { get; } = rise;

    public double RiseZ { get; } = riseZ;

    public string Id => "test.sloped-ground";

    public int Version => 1;

    /// <summary>The ground's height at (x, z), in centimetres.</summary>
    public double Height(double x, double z) => BaseCentimetres + Rise * x + RiseZ * z;

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
                var height = Height(Grid.SampleAt(origin.X + x, level), sampleZ);
                for (var y = 0; y < size; y++)
                {
                    var index = x + size * (y + size * z);
                    var low = (double)(origin.Y + y) * edge;
                    var material = Grid.SampleAt(origin.Y + y, level) < height ? Dirt : VoxelConstants.Air;
                    voxels[index] = material;
                    if (!densities.IsEmpty)
                    {
                        var share = Math.Clamp((height - low) / edge, 0, 1);
                        densities[index] = VoxelDensity.Fit(material, (byte)Math.Floor(share * 255 + 0.5));
                    }
                }
            }
        }
    }
}
