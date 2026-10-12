using Itera.Engine.Objects;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// A test's world of objects: flat stone below y = 0 on a grid of 10 cm (<see cref="FlatTestGenerator"/>), placing the
/// given objects wherever a box asks for them.
/// </summary>
internal sealed class PlacingGenerator(List<ObjectPlacement> placements) : IChunkGenerator, IObjectPlacer
{
    /// <summary>The grid: 10 cm voxels, the default bounds (±512 m).</summary>
    public static readonly VoxelGrid TenCentimetres = new(10);

    private readonly FlatTestGenerator _ground = new(TenCentimetres);

    public VoxelGrid Grid => TenCentimetres;

    public string Id => "test.placing";

    public int Version => 1;

    public IObjectPlacer Objects => this;

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) =>
        _ground.DescribeColumn(column, seed, layers);

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) => _ground.Fill(chunk, seed, voxels);

    public void Find(ulong seed, int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found)
    {
        foreach (var placement in placements)
        {
            if (
                placement.Anchor.X >= minX
                && placement.Anchor.X < maxX
                && placement.Anchor.Z >= minZ
                && placement.Anchor.Z < maxZ
            )
            {
                found.Add(placement);
            }
        }
    }
}
