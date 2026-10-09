namespace Itera.Engine.Voxels.Generation;

/// <summary>One band of ground below the surface layer: <see cref="Material"/> while the depth is less than <see cref="Depth"/>.</summary>
/// <param name="Depth">The depth in centimetres below the ground, where the band ends. Positive.</param>
/// <param name="Material">The band's material. Not air.</param>
public readonly record struct GroundLayer(int Depth, byte Material);

/// <summary>
/// The materials of the ground by depth, for a <see cref="HeightfieldGenerator"/>. A solid voxel's depth is how far its
/// sample point lies below the lowest ground among its own point and its four neighbours at the level's spacing. A
/// voxel less than <see cref="SurfaceDepthAt"/> deep takes the surface material of its point; deeper voxels take the
/// first band whose depth is greater than theirs, and below the last band the deep material. The surface layer is at
/// least one voxel thick, so at coarse levels the top voxel and the exposed side of every step show the surface
/// material. Immutable and thread safe.
/// </summary>
public sealed class GroundLayers
{
    private readonly int[] _depths;
    private readonly byte[] _materials;

    /// <summary>
    /// Makes the layers. <paramref name="below"/> lists the bands under the surface layer from the shallowest, each
    /// deeper than the one before.
    /// </summary>
    /// <param name="surfaceDepth">The surface layer's depth at level 0, in centimetres. Positive.</param>
    /// <param name="below">The bands under the surface layer. May be empty.</param>
    /// <param name="deepMaterial">The material below the last band. Not air.</param>
    public GroundLayers(int surfaceDepth, ReadOnlySpan<GroundLayer> below, byte deepMaterial)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(surfaceDepth);
        if (deepMaterial == VoxelConstants.Air)
        {
            throw new ArgumentException("The deep material cannot be air.", nameof(deepMaterial));
        }

        _depths = new int[below.Length];
        _materials = new byte[below.Length];
        for (var i = 0; i < below.Length; i++)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(below[i].Depth);
            if (i > 0 && below[i].Depth <= below[i - 1].Depth)
            {
                throw new ArgumentException("Each band must end deeper than the one before.", nameof(below));
            }

            if (below[i].Material == VoxelConstants.Air)
            {
                throw new ArgumentException("A band's material cannot be air.", nameof(below));
            }

            _depths[i] = below[i].Depth;
            _materials[i] = below[i].Material;
        }

        SurfaceDepth = surfaceDepth;
        DeepMaterial = deepMaterial;
    }

    /// <summary>The surface layer's depth at level 0, in centimetres.</summary>
    public int SurfaceDepth { get; }

    /// <summary>The material below the last band.</summary>
    public byte DeepMaterial { get; }

    /// <summary>The surface layer's depth at <paramref name="level"/>: <see cref="SurfaceDepth"/>, or one voxel where voxels are larger.</summary>
    public int SurfaceDepthAt(GridLevel level) => Math.Max(SurfaceDepth, level.Edge);

    /// <summary>The depth at <paramref name="level"/> from which every voxel is <see cref="DeepMaterial"/>.</summary>
    public int DeepDepthAt(GridLevel level) => Math.Max(_depths.Length == 0 ? 0 : _depths[^1], SurfaceDepthAt(level));

    /// <summary>
    /// The material of a solid voxel <paramref name="depth"/> centimetres deep (0 or more) under the surface material
    /// <paramref name="surface"/>, when the surface layer is <paramref name="surfaceDepth"/> thick
    /// (<see cref="SurfaceDepthAt"/>).
    /// </summary>
    public byte MaterialAt(int depth, byte surface, int surfaceDepth)
    {
        if (depth < surfaceDepth)
        {
            return surface;
        }

        for (var i = 0; i < _depths.Length; i++)
        {
            if (depth < _depths[i])
            {
                return _materials[i];
            }
        }

        return DeepMaterial;
    }
}
