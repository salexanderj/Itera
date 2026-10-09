namespace Itera.Engine.Voxels;

/// <summary>How much of a chunk a generator knows without filling it.</summary>
public enum ChunkFillKind : byte
{
    /// <summary>Every voxel is air.</summary>
    Air = 0,

    /// <summary>Every voxel is <see cref="ChunkFill.Material"/>.</summary>
    Uniform = 1,

    /// <summary>The chunk must be filled with <see cref="IChunkGenerator.Fill(ChunkKey, ulong, Span{byte}, Span{byte})"/>.</summary>
    Mixed = 2,
}

/// <summary>What <see cref="IChunkGenerator.DescribeColumn"/> says about one layer of a column.</summary>
public readonly record struct ChunkFill(ChunkFillKind Kind, byte Material)
{
    /// <summary>All air.</summary>
    public static ChunkFill Air => default;

    /// <summary>All <paramref name="material"/>.</summary>
    public static ChunkFill Uniform(byte material) =>
        material == VoxelConstants.Air ? default : new ChunkFill(ChunkFillKind.Uniform, material);

    /// <summary>Needs filling.</summary>
    public static ChunkFill Mixed => new(ChunkFillKind.Mixed, VoxelConstants.Air);
}

/// <summary>
/// Builds the voxels of the world from a seed. Worker threads call it, several at once, so it must be thread safe; in
/// practice a pure function of its arguments. It must be deterministic on every machine: integer hashing and basic
/// floating-point operations (+, -, *, /, sqrt) only, no <see cref="Random"/>, no <c>Math.Sin</c>, <c>Pow</c>, or
/// <c>Exp</c> (their results differ between platform maths libraries), no <c>System.Numerics</c> vector helpers (some
/// use <c>MultiplyAddEstimate</c>, which fuses on some machines), and no hash set or dictionary iteration order. A
/// level-k voxel is sampled at its grid's <see cref="VoxelGrid.SampleAt"/>.
/// </summary>
public interface IChunkGenerator
{
    /// <summary>
    /// The size of the voxels it makes, which the world it fills takes as its own (<see cref="VoxelWorld.Grid"/>): 1 cm
    /// level-0 voxels unless it says otherwise.
    /// </summary>
    VoxelGrid Grid => VoxelGrid.Unit;

    /// <summary>Names the generator in saves, for example "itera.test-terrain".</summary>
    string Id { get; }

    /// <summary>Changes whenever the same seed would give different voxels. Saves record it.</summary>
    int Version { get; }

    /// <summary>
    /// Describes every layer of a column (<c>layers[i]</c> is chunk y <c>Grid.MinChunkYAt(level) + i</c>). Air and uniform
    /// answers must be exact; when unsure, answer mixed. Only mixed layers are filled.
    /// </summary>
    void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers);

    /// <summary>
    /// Writes all 128^3 materials of a chunk into <paramref name="voxels"/>, index <c>x + 128y + 16384z</c>, including
    /// the air.
    /// </summary>
    void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels);

    /// <summary>
    /// Writes all 128^3 materials of a chunk into <paramref name="voxels"/> and how much of each voxel is solid into
    /// <paramref name="densities"/> (<see cref="VoxelDensity"/>), both at index <c>x + 128y + 16384z</c>. A density is
    /// fitted to its voxel's material where stored (<see cref="VoxelDensity.Fit"/>). A generator that does not say how
    /// much of each voxel is solid fills the materials, and every voxel takes its material's default.
    /// </summary>
    void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels, Span<byte> densities)
    {
        Fill(chunk, seed, voxels);
        VoxelDensity.Defaults(voxels[..VoxelConstants.ChunkVoxels], densities);
    }
}
