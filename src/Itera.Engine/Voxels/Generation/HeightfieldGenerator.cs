namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// A base for generators of ground: a height at every point of x and z, a surface material, layers of material by depth,
/// and features written over it. A subclass says what the ground is (<see cref="HeightAt"/>, <see cref="SurfaceAt"/>,
/// the <see cref="GroundLayers"/> given to the constructor, and optionally <see cref="CreateFeatures"/>); the base
/// makes it fast and exact.
/// <para>
/// The rules. A voxel is sampled at its sample point (<see cref="VoxelGrid.SampleAt"/>). It is solid when its sample y
/// is below the ground's <c>top</c> at its point, the height rounded down. A solid voxel takes the surface material of
/// its point while it lies less than <c>max(surface depth, its edge)</c> below the lowest top among its point and the
/// four neighbouring points at the level's spacing, then the materials of the layers by depth
/// (<see cref="GroundLayers"/>). Its density is the share of its height below the ground's exact height
/// (<see cref="GroundDensity"/>), fitted to its material. The features are written over the ground in the order the
/// set defines, and then every voxel a feature made solid or air takes its material's default density
/// (<see cref="VoxelDensity.Agree"/>), so a feature's voxels are whole.
/// </para>
/// <para>
/// Speed. Each worker thread keeps the heights of the column it last worked on (its 128 x 128 sample points and a ring
/// of one point around them, the surface materials, and the least and most each row and the whole column reach), and
/// builds them again only when asked for another column, level, or seed. The chunks of one column share a build, and
/// a row that lies wholly in air or wholly in the deep material is filled in one call.
/// <see cref="Fill(ChunkKey, ulong, Span{byte}, Span{byte})"/> and <see cref="DescribeColumn"/> allocate nothing after
/// a thread's first column (a feature set that grows its lists for a larger column allocates then).
/// </para>
/// <para>
/// Descriptions. A layer is described as air only where the lowest voxel's bottom lies at or above the ground
/// everywhere, and as the deep material only where every voxel is wholly in it (its top at or below the ground
/// everywhere, and deeper than every layer above). Both test the voxels' edges, not their sample points, so the density
/// agrees. A layer any feature can touch is described as mixed.
/// </para>
/// <para>
/// Determinism. The base uses integers and + - * / and floor only. A subclass must do the same (see
/// <see cref="IChunkGenerator"/>), and must raise <see cref="Version"/> whenever the same seed would give other
/// voxels. Heights are finite and within the range of an <see cref="int"/> in centimetres.
/// </para>
/// </summary>
public abstract class HeightfieldGenerator : IChunkGenerator
{
    // The one column a thread last built. Shared by every generator on the thread; it rebuilds when the generator changes.
    [ThreadStatic]
    private static ColumnGround? t_column;

    /// <summary>Makes a generator on <paramref name="grid"/> whose ground has these layers.</summary>
    protected HeightfieldGenerator(VoxelGrid grid, GroundLayers layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        Grid = grid;
        Layers = layers;
    }

    /// <summary>The size of the voxels this generator makes.</summary>
    public VoxelGrid Grid { get; }

    /// <summary>The materials of the ground by depth.</summary>
    public GroundLayers Layers { get; }

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract int Version { get; }

    /// <inheritdoc />
    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        var at = new GridLevel(Grid, column.Level);
        var ground = ColumnFor(column, at, seed);
        for (var layer = 0; layer < layers.Length; layer++)
        {
            var firstY = column.Chunk(layer, Grid).Y << VoxelConstants.ChunkShift;
            var minY = at.SampleAt(firstY);
            var maxY = at.SampleAt(firstY + VoxelConstants.ChunkSize - 1);
            layers[layer] = ground.Describe(minY, maxY);
        }
    }

    /// <inheritdoc />
    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) => Fill(chunk, seed, voxels, default);

    /// <summary>
    /// Writes the chunk's materials and, unless <paramref name="densities"/> is empty, how much of each voxel is solid:
    /// the ground's from its height (<see cref="GroundDensity"/>), fitted to the voxel's material, and every voxel a
    /// feature made solid or air its material's default.
    /// </summary>
    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels, Span<byte> densities)
    {
        var at = new GridLevel(Grid, chunk.Column.Level);
        ColumnFor(chunk.Column, at, seed).Fill(chunk.Y << VoxelConstants.ChunkShift, voxels, densities);
    }

    /// <summary>
    /// The density of a voxel from <paramref name="low"/> to <paramref name="low"/> + <paramref name="edge"/>
    /// centimetres high under ground at <paramref name="height"/>: the share of it below the height, in 255ths, rounded.
    /// </summary>
    public static byte GroundDensity(double height, double low, int edge)
    {
        var share = (height - low) / edge;
        return share <= 0 ? VoxelDensity.Empty
            : share >= 1 ? VoxelDensity.Full
            : (byte)Math.Floor(share * VoxelDensity.Full + 0.5);
    }

    /// <summary>
    /// The height of the ground at (<paramref name="x"/>, <paramref name="z"/>) in centimetres, as a double: the
    /// densities come from it, and its floor is the highest solid y. Called from worker threads, several at once, for
    /// each sample point of a column and its ring, in no fixed order; it must depend on its arguments alone.
    /// </summary>
    protected abstract double HeightAt(int x, int z, ulong seed);

    /// <summary>
    /// The material of the ground's surface layer at (<paramref name="x"/>, <paramref name="z"/>) in centimetres, where
    /// the ground has <paramref name="height"/> (what <see cref="HeightAt"/> gave). Not air. Called for the 128 x 128
    /// points of a column, not its ring, and under the same rules as <see cref="HeightAt"/>.
    /// </summary>
    protected abstract byte SurfaceAt(int x, int z, ulong seed, double height);

    /// <summary>
    /// Makes the set of features for one worker thread, or null when the ground has none. Called when a thread first
    /// builds a column for this generator, and again if the thread builds columns of another generator and comes back.
    /// </summary>
    protected virtual GroundFeatures? CreateFeatures() => null;

    private ColumnGround ColumnFor(ColumnKey column, GridLevel at, ulong seed)
    {
        var ground = t_column ??= new ColumnGround();
        if (!ground.Holds(this, column, at, seed))
        {
            ground.Build(this, column, at, seed);
        }

        return ground;
    }

    /// <summary>
    /// The ground heights at a column's 128 x 128 sample points plus a ring of one point around them, the surface
    /// materials at the 128 x 128, and what every row and the whole column need for fast fills and exact descriptions.
    /// One per worker thread.
    /// </summary>
    private sealed class ColumnGround
    {
        private const int Size = VoxelConstants.ChunkSize;
        private const int Ringed = Size + 2;

        private readonly int[] _tops = new int[Ringed * Ringed];

        /// <summary>The ground's height at each of the 128 x 128 sample points, before <see cref="_tops"/> rounds it down.</summary>
        private readonly double[] _heights = new double[Size * Size];
        private readonly double[] _rowLowestHeight = new double[Size];
        private readonly double[] _rowHighestHeight = new double[Size];
        private readonly byte[] _surfaces = new byte[Size * Size];
        private readonly int[] _lowest = new int[Size * Size];
        private readonly int[] _rowHighestTop = new int[Size];
        private readonly int[] _rowLowestLowest = new int[Size];

        private HeightfieldGenerator? _owner;
        private GroundFeatures? _features;
        private GroundLayers? _layers;
        private bool _valid;
        private ulong _seed;
        private ColumnKey _column;
        private GridLevel _level;
        private int _surfaceDepth;
        private int _deepDepth;
        private int _minX;
        private int _maxX;
        private int _minZ;
        private int _maxZ;
        private int _lowestLowest;
        private double _highestHeight;
        private double _lowestHeight;

        public bool Holds(HeightfieldGenerator generator, ColumnKey column, GridLevel level, ulong seed) =>
            _valid && ReferenceEquals(_owner, generator) && _column == column && _level == level && _seed == seed;

        public void Build(HeightfieldGenerator generator, ColumnKey column, GridLevel level, ulong seed)
        {
            _valid = false;
            if (!ReferenceEquals(_owner, generator))
            {
                _owner = generator;
                _layers = generator.Layers;
                _features = generator.CreateFeatures();
            }

            _column = column;
            _level = level;
            _seed = seed;
            _surfaceDepth = _layers!.SurfaceDepthAt(level);
            _deepDepth = _layers.DeepDepthAt(level);
            var firstX = (column.X << VoxelConstants.ChunkShift) - 1;
            var firstZ = (column.Z << VoxelConstants.ChunkShift) - 1;

            for (var z = 0; z < Ringed; z++)
            {
                var sampleZ = level.SampleAt(firstZ + z);
                var inner = z > 0 && z <= Size;
                for (var x = 0; x < Ringed; x++)
                {
                    var sampleX = level.SampleAt(firstX + x);
                    var height = generator.HeightAt(sampleX, sampleZ, seed);
                    _tops[z * Ringed + x] = (int)Math.Floor(height);
                    if (inner && x > 0 && x <= Size)
                    {
                        _heights[(z - 1) * Size + x - 1] = height;
                        _surfaces[(z - 1) * Size + x - 1] = generator.SurfaceAt(sampleX, sampleZ, seed, height);
                    }
                }
            }

            _lowestLowest = int.MaxValue;
            _highestHeight = double.NegativeInfinity;
            _lowestHeight = double.PositiveInfinity;
            for (var z = 0; z < Size; z++)
            {
                var rowHighest = int.MinValue;
                var rowLowest = int.MaxValue;
                var rowHighestHeight = double.NegativeInfinity;
                var rowLowestHeight = double.PositiveInfinity;
                for (var x = 0; x < Size; x++)
                {
                    var height = _heights[z * Size + x];
                    rowHighestHeight = Math.Max(rowHighestHeight, height);
                    rowLowestHeight = Math.Min(rowLowestHeight, height);
                    var centre = (z + 1) * Ringed + x + 1;
                    var top = _tops[centre];
                    var lowest = Math.Min(
                        Math.Min(top, Math.Min(_tops[centre - 1], _tops[centre + 1])),
                        Math.Min(_tops[centre - Ringed], _tops[centre + Ringed])
                    );
                    _lowest[z * Size + x] = lowest;
                    rowHighest = Math.Max(rowHighest, top);
                    rowLowest = Math.Min(rowLowest, lowest);
                }

                _rowHighestTop[z] = rowHighest;
                _rowLowestLowest[z] = rowLowest;
                _rowHighestHeight[z] = rowHighestHeight;
                _rowLowestHeight[z] = rowLowestHeight;
                _highestHeight = Math.Max(_highestHeight, rowHighestHeight);
                _lowestHeight = Math.Min(_lowestHeight, rowLowestHeight);
                _lowestLowest = Math.Min(_lowestLowest, rowLowest);
            }

            _minX = level.SampleAt(firstX + 1);
            _maxX = level.SampleAt(firstX + Size);
            _minZ = level.SampleAt(firstZ + 1);
            _maxZ = level.SampleAt(firstZ + Size);
            _features?.Find(seed, level, _minX, _maxX, _minZ, _maxZ);
            _valid = true;
        }

        /// <summary>What a layer whose voxels are sampled from <paramref name="minY"/> to <paramref name="maxY"/> holds.</summary>
        public ChunkFill Describe(int minY, int maxY)
        {
            if (_features is not null && _features.Touches(_minX, _maxX, minY, maxY, _minZ, _maxZ))
            {
                return ChunkFill.Mixed;
            }

            return DescribeGround(minY, maxY);
        }

        /// <summary>
        /// What the ground makes of a layer sampled from <paramref name="minY"/> to <paramref name="maxY"/>: air only when
        /// its lowest voxel's bottom lies at or above the ground everywhere, so no voxel holds part of it, and the deep
        /// material only when its highest voxel's top lies at or below the ground everywhere, so every voxel is full.
        /// </summary>
        private ChunkFill DescribeGround(int minY, int maxY)
        {
            var edge = _level.Edge;
            var bottom = minY - (edge >> 1);
            var top = maxY - (edge >> 1) + edge;
            if (bottom >= _highestHeight)
            {
                return ChunkFill.Air;
            }

            if (maxY <= _lowestLowest - 1 - _deepDepth && top <= _lowestHeight)
            {
                return ChunkFill.Uniform(_layers!.DeepMaterial);
            }

            return ChunkFill.Mixed;
        }

        /// <summary>
        /// Writes the chunk of this column whose first voxel y is <paramref name="firstY"/>, with its densities unless
        /// <paramref name="densities"/> is empty.
        /// </summary>
        public void Fill(int firstY, Span<byte> voxels, Span<byte> densities)
        {
            FillGround(firstY, voxels, densities);
            if (_features is null)
            {
                return;
            }

            var first = new VoxelCoord(
                _column.X << VoxelConstants.ChunkShift,
                firstY,
                _column.Z << VoxelConstants.ChunkShift
            );
            _features.Fill(first, _level, voxels);
            if (!densities.IsEmpty)
            {
                // The features are written by sample points: where one made a voxel solid or air, it is whole.
                VoxelDensity.Agree(voxels[..VoxelConstants.ChunkVoxels], densities[..VoxelConstants.ChunkVoxels]);
            }
        }

        private void FillGround(int firstY, Span<byte> voxels, Span<byte> densities)
        {
            var layers = _layers!;
            var deepMaterial = layers.DeepMaterial;
            var edge = _level.Edge;
            var withDensities = !densities.IsEmpty;
            for (var z = 0; z < Size; z++)
            {
                for (var y = 0; y < Size; y++)
                {
                    var sampleY = _level.SampleAt(firstY + y);
                    var low = (double)(firstY + y) * edge;
                    var index = (z * Size + y) * Size;
                    var row = voxels.Slice(index, Size);
                    var densityRow = withDensities ? densities.Slice(index, Size) : default;
                    if (sampleY >= _rowHighestTop[z])
                    {
                        row.Clear();
                    }
                    else if (sampleY <= _rowLowestLowest[z] - 1 - _deepDepth)
                    {
                        // Deep under every top of the row (each point's lowest is at or under its own top): the deep
                        // material, as MaterialAt finds it.
                        row.Fill(deepMaterial);
                    }
                    else
                    {
                        var ringed = (z + 1) * Ringed + 1;
                        for (var x = 0; x < Size; x++)
                        {
                            row[x] =
                                sampleY >= _tops[ringed + x]
                                    ? VoxelConstants.Air
                                    : layers.MaterialAt(
                                        _lowest[z * Size + x] - 1 - sampleY,
                                        _surfaces[z * Size + x],
                                        _surfaceDepth
                                    );
                        }
                    }

                    if (withDensities)
                    {
                        GroundDensities(z, low, edge, row, densityRow);
                    }
                }
            }
        }

        /// <summary>
        /// Writes the ground's density of each voxel of row <paramref name="z"/> from <paramref name="low"/> centimetres
        /// up, fitted to the material the row holds: empty above every height of the row, full under every one, and
        /// otherwise each voxel's share below its point's height.
        /// </summary>
        private void GroundDensities(int z, double low, int edge, ReadOnlySpan<byte> row, Span<byte> densities)
        {
            if (low >= _rowHighestHeight[z])
            {
                densities.Clear();
                return;
            }

            if (low + edge <= _rowLowestHeight[z])
            {
                densities.Fill(VoxelDensity.Full);
                return;
            }

            var heights = _heights.AsSpan(z * Size, Size);
            for (var x = 0; x < Size; x++)
            {
                densities[x] = VoxelDensity.Fit(row[x], GroundDensity(heights[x], low, edge));
            }
        }
    }
}
