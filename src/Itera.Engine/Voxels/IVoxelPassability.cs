using System.Runtime.CompilerServices;

namespace Itera.Engine.Voxels;

/// <summary>
/// Which solid materials characters walk through (foliage, flowers). The game provides it from its material table; the
/// engine's default, <see cref="NothingPassable"/>, lets every solid voxel block. Rays still hit passable materials, so
/// tools can dig them.
/// </summary>
public interface IVoxelPassability
{
    /// <summary>True when characters pass through <paramref name="material"/>. Air (0) is always passable.</summary>
    bool IsPassable(byte material);
}

/// <summary>Every solid material blocks.</summary>
public sealed class NothingPassable : IVoxelPassability
{
    public bool IsPassable(byte material) => material == VoxelConstants.Air;
}

/// <summary>A set of materials, one bit each, built once from an <see cref="IVoxelPassability"/>.</summary>
internal readonly struct MaterialSet
{
    private readonly Words _words;

    private MaterialSet(in Words words)
    {
        _words = words;
    }

    /// <summary>No material.</summary>
    public static MaterialSet None => default;

    /// <summary>True when the set holds no material.</summary>
    public bool IsEmpty => (_words[0] | _words[1] | _words[2] | _words[3]) == 0;

    /// <summary>The solid materials that <paramref name="passability"/> lets characters through; air is left out.</summary>
    public static MaterialSet Passable(IVoxelPassability passability)
    {
        var words = default(Words);
        for (var material = 1; material < 256; material++)
        {
            if (passability.IsPassable((byte)material))
            {
                words[material >> 6] |= 1UL << (material & 63);
            }
        }

        return new MaterialSet(in words);
    }

    /// <summary>A set of the given materials.</summary>
    public static MaterialSet Of(params ReadOnlySpan<byte> materials)
    {
        var words = default(Words);
        foreach (var material in materials)
        {
            words[material >> 6] |= 1UL << (material & 63);
        }

        return new MaterialSet(in words);
    }

    /// <summary>The materials in this set or <paramref name="other"/>.</summary>
    public MaterialSet Union(in MaterialSet other)
    {
        var words = default(Words);
        for (var i = 0; i < 4; i++)
        {
            words[i] = _words[i] | other._words[i];
        }

        return new MaterialSet(in words);
    }

    /// <summary>True when the set holds <paramref name="material"/>.</summary>
    public bool Contains(byte material) => ((_words[material >> 6] >> (material & 63)) & 1) != 0;

    /// <summary>True when the two sets hold the same materials.</summary>
    public bool SameAs(in MaterialSet other) =>
        _words[0] == other._words[0]
        && _words[1] == other._words[1]
        && _words[2] == other._words[2]
        && _words[3] == other._words[3];

    /// <summary>True when some material is in both sets.</summary>
    public bool Overlaps(in MaterialSet other) =>
        (
            (_words[0] & other._words[0])
            | (_words[1] & other._words[1])
            | (_words[2] & other._words[2])
            | (_words[3] & other._words[3])
        ) != 0;

    [InlineArray(4)]
    private struct Words
    {
        private ulong _word;
    }
}
