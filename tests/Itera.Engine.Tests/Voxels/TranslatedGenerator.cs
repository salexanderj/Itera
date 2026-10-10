using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// The world of the inner generator moved by an offset in centimetres on a grid reaching ±160 km on x
/// and z (the most a grid may), its floor and top moved up with it: the voxel at <c>c + offset</c> is the inner world's
/// at <c>c</c>, so a test runs the same ground far from the map's middle. The offset is whole level-4 columns, so every
/// level's chunks line up with the inner world's.
/// </summary>
internal sealed class TranslatedGenerator : IChunkGenerator
{
    private const int MostLevel = 4;

    private readonly IChunkGenerator _inner;
    private readonly int _chunksX;
    private readonly int _chunksY;
    private readonly int _chunksZ;

    public TranslatedGenerator(IChunkGenerator inner, VoxelCoord offset)
    {
        _inner = inner;
        var grid = inner.Grid;
        var column = (int)grid.ColumnCentimetres(MostLevel);
        if (offset.X % column != 0 || offset.Y % column != 0 || offset.Z % column != 0)
        {
            throw new ArgumentException($"{offset} is not whole level-{MostLevel} columns of {column} cm.");
        }

        var chunk = (int)grid.ColumnCentimetres(0);
        _chunksX = offset.X / chunk;
        _chunksY = offset.Y / chunk;
        _chunksZ = offset.Z / chunk;
        Grid = new VoxelGrid(
            grid.BaseCentimetres,
            (int)(VoxelGrid.MostReachCentimetres / chunk),
            grid.MinChunkY + _chunksY,
            grid.MaxChunkY + _chunksY
        );
    }

    public VoxelGrid Grid { get; }

    public string Id => _inner.Id;

    public int Version => _inner.Version;

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) =>
        _inner.DescribeColumn(
            new ColumnKey(column.Level, column.X - (_chunksX >> column.Level), column.Z - (_chunksZ >> column.Level)),
            seed,
            layers
        );

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) => _inner.Fill(Inner(chunk), seed, voxels);

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels, Span<byte> densities) =>
        _inner.Fill(Inner(chunk), seed, voxels, densities);

    private ChunkKey Inner(ChunkKey chunk) =>
        new(
            chunk.Level,
            chunk.X - (_chunksX >> chunk.Level),
            chunk.Y - (_chunksY >> chunk.Level),
            chunk.Z - (_chunksZ >> chunk.Level)
        );
}
