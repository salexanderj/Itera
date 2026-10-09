namespace Itera.Engine.Voxels;

/// <summary>What a part of the tree holds: nothing (all air), one solid material everywhere, or a node or brick.</summary>
internal enum ChildKind : byte
{
    Empty = 0,
    Uniform = 1,
    Handle = 2,
}

/// <summary>
/// The content of one child slot, one chunk, or one column layer: all air, all one material, or a handle to a node or
/// brick in <see cref="VoxelMemory"/> that holds mixed content.
/// </summary>
internal readonly record struct ChildState(ChildKind Kind, int Value)
{
    /// <summary>All air.</summary>
    public static ChildState Empty => default;

    /// <summary>All <paramref name="material"/>; air gives <see cref="Empty"/>.</summary>
    public static ChildState Uniform(byte material) =>
        material == VoxelConstants.Air ? default : new ChildState(ChildKind.Uniform, material);

    /// <summary>Mixed content held by a node or brick.</summary>
    public static ChildState Of(int handle) => new(ChildKind.Handle, handle);

    /// <summary>The material of a uniform state, or air for an empty one.</summary>
    public byte Material => Kind == ChildKind.Uniform ? (byte)Value : VoxelConstants.Air;

    /// <summary>The node or brick handle of a mixed state.</summary>
    public int Handle => Value;

    /// <summary>True for all air.</summary>
    public bool IsEmpty => Kind == ChildKind.Empty;

    /// <summary>True for all air or all one material.</summary>
    public bool IsUniformOrEmpty => Kind != ChildKind.Handle;
}
