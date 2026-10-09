namespace Itera.Engine.Voxels;

/// <summary>
/// The size of a voxel store's voxels: the edge of its level-0 voxel in centimetres (<see cref="BaseCentimetres"/>), each
/// level twice the one below. Coordinates (<see cref="VoxelCoord"/>, edits, rays, positions) are centimetres; a store
/// indexes its voxels by level-0 voxel, an index of <see cref="BaseCentimetres"/> centimetres, and its levels, chunks,
/// and bricks by shifts of that index, so a grid converts only where centimetres meet indices: <see cref="ToVoxel(int)"/>
/// in, <see cref="ToCentimetres(int)"/> out, and <see cref="SampleAt"/> for the point a voxel's content is decided at.
/// The world's grid is its generator's (<see cref="IChunkGenerator.Grid"/>, <see cref="VoxelWorld.Grid"/>); models use
/// <see cref="Unit"/> (1 cm).
/// <para>
/// The grid also bounds the world in whole level-0 chunks: across, the chunks that cover ±500 m
/// (<see cref="MapHalfWidthCentimetres"/>); down, the first whole chunk at or below -40.96 m
/// (<see cref="FloorCentimetres"/>); up, whole chunks to at least 81.92 m (<see cref="TopCentimetres"/>), rounded up to
/// a multiple of 8 so levels 0 to 3 share the top. At 1 cm that is ±500.48 m, -40.96 m, and 81.92 m; at 10 cm ±512 m,
/// -51.2 m, and 102.4 m. A coarser level's chunks start at <c>MinChunkY &gt;&gt; level</c>, so a level whose chunk does
/// not divide the floor reaches deeper.
/// </para>
/// </summary>
public readonly record struct VoxelGrid
{
    /// <summary>A grid of 1 cm level-0 voxels, every level's index the same number as a coordinate's centimetres at level 0.</summary>
    public static VoxelGrid Unit { get; } = new(1);

    public VoxelGrid(int baseCentimetres)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(baseCentimetres, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseCentimetres, MostBaseCentimetres);
        BaseCentimetres = baseCentimetres;
        var chunk = baseCentimetres * VoxelConstants.ChunkSize;
        MaxChunkXZ = CeilDiv(MapHalfWidthCentimetres, chunk) - 1;
        MinChunkXZ = -(MaxChunkXZ + 1);
        MinChunkY = FloorDiv(FloorCentimetres, chunk);
        MaxChunkY = (CeilDiv(TopCentimetres, chunk) + TopChunkMultiple - 1) / TopChunkMultiple * TopChunkMultiple - 1;
    }

    /// <summary>The largest base: a level-9 voxel of it is 51.2 m, a chunk at level 0 12.8 m.</summary>
    public const int MostBaseCentimetres = 10;

    /// <summary>How far the world's columns reach from the map's middle at least, in centimetres, on x and z.</summary>
    public const int MapHalfWidthCentimetres = 50_000;

    /// <summary>The height the world's floor lies at or below, in centimetres.</summary>
    public const int FloorCentimetres = -4096;

    /// <summary>The height the world's top lies at or above, in centimetres.</summary>
    public const int TopCentimetres = 8192;

    /// <summary>The level-0 chunks from height 0 to the top are rounded up to a multiple of this.</summary>
    private const int TopChunkMultiple = 8;

    /// <summary>The edge of a level-0 voxel, in centimetres.</summary>
    public int BaseCentimetres { get; }

    /// <summary>Lowest level-0 chunk x and z.</summary>
    public int MinChunkXZ { get; }

    /// <summary>Highest level-0 chunk x and z.</summary>
    public int MaxChunkXZ { get; }

    /// <summary>Lowest level-0 chunk y.</summary>
    public int MinChunkY { get; }

    /// <summary>Highest level-0 chunk y.</summary>
    public int MaxChunkY { get; }

    /// <summary>Layers in a level-0 column. Columns at higher levels have no more.</summary>
    public int MaxLayers => MaxChunkY - MinChunkY + 1;

    /// <summary>The most layers a column of any grid has: a level-0 column of <see cref="Unit"/>'s.</summary>
    public static int MostLayers => Unit.MaxLayers;

    /// <summary>Lowest level-0 voxel x and z inside the world (an index).</summary>
    public int MinVoxelXZ => MinChunkXZ << VoxelConstants.ChunkShift;

    /// <summary>First level-0 voxel x and z past the world (an index).</summary>
    public int EndVoxelXZ => (MaxChunkXZ + 1) << VoxelConstants.ChunkShift;

    /// <summary>Lowest level-0 voxel y inside the world (an index).</summary>
    public int MinVoxelY => MinChunkY << VoxelConstants.ChunkShift;

    /// <summary>First level-0 voxel y past the world (an index).</summary>
    public int EndVoxelY => (MaxChunkY + 1) << VoxelConstants.ChunkShift;

    /// <summary>Lowest chunk x and z at <paramref name="level"/>.</summary>
    public int MinChunkXZAt(int level) => MinChunkXZ >> level;

    /// <summary>Highest chunk x and z at <paramref name="level"/>.</summary>
    public int MaxChunkXZAt(int level) => MaxChunkXZ >> level;

    /// <summary>Lowest chunk y at <paramref name="level"/>; layer 0 of a column.</summary>
    public int MinChunkYAt(int level) => MinChunkY >> level;

    /// <summary>Highest chunk y at <paramref name="level"/>.</summary>
    public int MaxChunkYAt(int level) => MaxChunkY >> level;

    /// <summary>Layers in a column at <paramref name="level"/>.</summary>
    public int LayerCount(int level) => MaxChunkYAt(level) - MinChunkYAt(level) + 1;

    /// <summary>The edge of a voxel at <paramref name="level"/>, in centimetres.</summary>
    public int Edge(int level) => BaseCentimetres << level;

    /// <summary>The edge of a voxel at <paramref name="level"/>, in metres.</summary>
    public float EdgeMetres(int level) => Edge(level) / (float)VoxelConstants.CentimetresPerMetre;

    /// <summary>The edge of a chunk, and a column's width, at <paramref name="level"/>, in centimetres.</summary>
    public long ColumnCentimetres(int level) => (long)Edge(level) * VoxelConstants.ChunkSize;

    /// <summary>
    /// <see cref="VoxelConstants.ReachMarginColumns"/> of <paramref name="level"/>'s columns, in centimetres: how far a
    /// level's reach stays inside its streaming radius.
    /// </summary>
    public long ReachMarginCentimetres(int level) => VoxelConstants.ReachMarginColumns * ColumnCentimetres(level);

    /// <summary>
    /// The distance from a streaming centre, in centimetres, up to which <paramref name="level"/> is drawn when each
    /// level's radius is twice the one below's: its streaming radius less <see cref="ReachMarginCentimetres"/>.
    /// </summary>
    /// <param name="radiusCentimetres">The level-0 streaming radius in centimetres.</param>
    /// <param name="level">The level.</param>
    public long ReachCentimetres(long radiusCentimetres, int level) =>
        Math.Max(0, (radiusCentimetres << level) - ReachMarginCentimetres(level));

    /// <summary>
    /// The level-0 voxels an edit's bounds (<see cref="VoxelEdit.GetBounds"/>, centimetres) reach, as indices
    /// [<paramref name="min"/>, <paramref name="end"/>): every voxel whose sample point the edit can hold, and the few
    /// beside them whose centimetres the bounds touch.
    /// </summary>
    public void GetVoxelBounds(in VoxelEdit edit, out VoxelCoord min, out VoxelCoord end)
    {
        edit.GetBounds(out var minCentimetres, out var endCentimetres);
        min = ToVoxel(minCentimetres);
        end = new VoxelCoord(
            ToVoxel(endCentimetres.X - 1) + 1,
            ToVoxel(endCentimetres.Y - 1) + 1,
            ToVoxel(endCentimetres.Z - 1) + 1
        );
    }

    /// <summary>The column at <paramref name="level"/> that holds the centimetre <paramref name="centimetres"/> on x or z.</summary>
    public int ColumnAt(long centimetres, int level) =>
        ToVoxel((int)centimetres) >> (VoxelConstants.ChunkShift + level);

    /// <summary>The column at <paramref name="level"/> that holds the point <paramref name="centimetres"/>.</summary>
    public ColumnKey ColumnContaining(VoxelCoord centimetres, int level) =>
        ColumnKey.Containing(ToVoxel(centimetres), level);

    /// <summary>The chunk at <paramref name="level"/> that holds the point <paramref name="centimetres"/>.</summary>
    public ChunkKey ChunkContaining(VoxelCoord centimetres, int level) =>
        ChunkKey.Containing(ToVoxel(centimetres), level);

    /// <summary>
    /// The index in <paramref name="chunk"/>'s dense 128^3 array (x + 128 y + 16384 z) of the voxel that holds the
    /// point <paramref name="centimetres"/>, which must lie in the chunk.
    /// </summary>
    public int IndexInChunk(ChunkKey chunk, VoxelCoord centimetres)
    {
        var voxel = ToVoxel(centimetres);
        var origin = chunk.LevelOrigin;
        int x = (voxel.X >> chunk.Level) - origin.X,
            y = (voxel.Y >> chunk.Level) - origin.Y,
            z = (voxel.Z >> chunk.Level) - origin.Z;
        return x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z);
    }

    /// <summary>The level-0 voxel that holds the centimetre <paramref name="centimetres"/>: its index, rounded down.</summary>
    public int ToVoxel(int centimetres) => FloorDiv(centimetres, BaseCentimetres);

    /// <summary>The level-0 voxel that holds the point at <paramref name="centimetres"/>.</summary>
    public VoxelCoord ToVoxel(VoxelCoord centimetres) =>
        new(ToVoxel(centimetres.X), ToVoxel(centimetres.Y), ToVoxel(centimetres.Z));

    /// <summary>The low corner, in centimetres, of level-0 voxel <paramref name="voxel"/>.</summary>
    public int ToCentimetres(int voxel) => voxel * BaseCentimetres;

    /// <summary>The low corner, in centimetres, of level-0 voxel <paramref name="voxel"/>.</summary>
    public VoxelCoord ToCentimetres(VoxelCoord voxel) =>
        new(ToCentimetres(voxel.X), ToCentimetres(voxel.Y), ToCentimetres(voxel.Z));

    /// <summary>
    /// The centimetre at which the voxel of index <paramref name="coordinate"/> at <paramref name="level"/> is sampled:
    /// its centre, rounded down. Generators and edits test this point, so a voxel's content depends only on integers. At
    /// a base of 1 cm it is <see cref="VoxelConstants.SampleAt"/>.
    /// </summary>
    public int SampleAt(int coordinate, int level)
    {
        var edge = Edge(level);
        return coordinate * edge + (edge >> 1);
    }

    /// <summary>
    /// The level whose voxels are nearest <paramref name="centimetres"/> across, by ratio (0 to
    /// <see cref="VoxelConstants.MaxLevel"/>): for a size that a level number stood for, such as the sky field's 64 cm
    /// cells.
    /// </summary>
    public int LevelNearest(double centimetres)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(centimetres);
        var best = 0;
        var bestRatio = double.MaxValue;
        for (var level = 0; level <= VoxelConstants.MaxLevel; level++)
        {
            var edge = (double)Edge(level);
            var ratio = Math.Max(edge / centimetres, centimetres / edge);
            if (ratio < bestRatio)
            {
                best = level;
                bestRatio = ratio;
            }
        }

        return best;
    }

    /// <summary><paramref name="value"/> over <paramref name="divisor"/> (positive), rounded down, for negatives too.</summary>
    public static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static int CeilDiv(int value, int divisor) => -FloorDiv(-value, divisor);
}

/// <summary>
/// A grid at one level: where that level's voxels are sampled, for code that fills or edits them a row at a time.
/// </summary>
public readonly record struct GridLevel(VoxelGrid Grid, int Level)
{
    /// <summary>The edge of the level's voxels, in centimetres.</summary>
    public int Edge => Grid.Edge(Level);

    /// <summary>The centimetre the voxel of index <paramref name="coordinate"/> is sampled at (<see cref="VoxelGrid.SampleAt"/>).</summary>
    public int SampleAt(int coordinate) => Grid.SampleAt(coordinate, Level);

    /// <summary>The lowest voxel index whose sample point is at or after <paramref name="centimetres"/>.</summary>
    public int FirstSampleAtOrAfter(int centimetres) => (int)FirstSampleAtOrAfter((long)centimetres);

    /// <inheritdoc cref="FirstSampleAtOrAfter(int)"/>
    public long FirstSampleAtOrAfter(long centimetres)
    {
        // Voxel i is sampled at i * edge + half: the first i with that at or after the centimetre, rounded up.
        long edge = Edge;
        var value = centimetres - (edge >> 1) + edge - 1;
        return value >= 0 ? value / edge : -((-value + edge - 1) / edge);
    }
}
