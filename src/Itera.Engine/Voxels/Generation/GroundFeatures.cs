namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// One thing a <see cref="HeightfieldGenerator"/> writes over its ground, such as a rock, a tree, a wall, or a cave:
/// usually a struct holding a shape from the toolkit and a material. Coordinates are centimetres.
/// </summary>
public interface IGroundFeature
{
    /// <summary>
    /// True when the feature can touch a voxel whose sample point lies in the box [minX, maxX] x [minY, maxY] x
    /// [minZ, maxZ]. It may say yes when unsure, and it must not say no when it could change a voxel there: a layer it
    /// touches is described as mixed and filled.
    /// </summary>
    bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ);

    /// <summary>
    /// Writes the feature's materials over the voxels of a dense chunk (index <c>x + 128y + 16384z</c>) whose sample
    /// points it holds, and leaves the rest. <paramref name="first"/> is the chunk's first voxel as an index at
    /// <paramref name="level"/> (<see cref="VoxelRows"/> and the toolkit's shapes take it as it is).
    /// </summary>
    void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels);
}

/// <summary>
/// The features a <see cref="HeightfieldGenerator"/> has found for the column a thread last built, and how they are
/// described and written. A generator makes one for each worker thread (<see cref="HeightfieldGenerator.CreateFeatures"/>),
/// so a set needs no lock; it keeps its lists between columns, so a column that needs no more room than an earlier one
/// allocates nothing. Use a <see cref="GroundFeatureList{T}"/> for each kind of feature.
/// </summary>
public abstract class GroundFeatures
{
    /// <summary>
    /// Finds the features that can touch the column whose sample points lie in [minX, maxX] x [minZ, maxZ], all in
    /// centimetres, and drops those of the column before. Called once for each column a thread builds, after the
    /// ground's heights; the same seed and column must always find the same features, in the same order.
    /// </summary>
    public abstract void Find(ulong seed, GridLevel level, int minX, int maxX, int minZ, int maxZ);

    /// <summary>True when any found feature can touch the box of sample points (see <see cref="IGroundFeature.Touches"/>).</summary>
    public abstract bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ);

    /// <summary>
    /// Writes the found features over a chunk's materials, in the order the generator's version defines: a later
    /// feature overwrites an earlier one. See <see cref="IGroundFeature.Fill"/>.
    /// </summary>
    public abstract void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels);
}

/// <summary>
/// A reusable list of features of one kind, for a <see cref="GroundFeatures"/> set. It grows when a column needs more
/// room and keeps what it grew to.
/// </summary>
/// <typeparam name="T">The kind of feature.</typeparam>
public sealed class GroundFeatureList<T>
    where T : struct, IGroundFeature
{
    private T[] _items;
    private int _count;

    /// <summary>Makes an empty list with room for <paramref name="capacity"/> features.</summary>
    public GroundFeatureList(int capacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new T[capacity];
    }

    /// <summary>How many features the list holds.</summary>
    public int Count => _count;

    /// <summary>The features, in the order added.</summary>
    public ReadOnlySpan<T> Items => _items.AsSpan(0, _count);

    /// <summary>Empties the list.</summary>
    public void Clear() => _count = 0;

    /// <summary>Adds a feature at the end.</summary>
    public void Add(in T feature)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[_count++] = feature;
    }

    /// <summary>True when any feature can touch the box (see <see cref="IGroundFeature.Touches"/>).</summary>
    public bool Touches(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        foreach (ref readonly var feature in Items)
        {
            if (feature.Touches(minX, maxX, minY, maxY, minZ, maxZ))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes every feature, in order, over the chunk (see <see cref="IGroundFeature.Fill"/>).</summary>
    public void Fill(VoxelCoord first, GridLevel level, Span<byte> voxels)
    {
        foreach (ref readonly var feature in Items)
        {
            feature.Fill(first, level, voxels);
        }
    }
}
