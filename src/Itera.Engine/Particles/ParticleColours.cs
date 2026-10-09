using System.Numerics;
using Itera.Engine.Rendering.Voxels;

namespace Itera.Engine.Particles;

/// <summary>
/// Colours for particles from what the world shows: a material's colour where it lies, blended towards its tint
/// colour by the match's tint map as the mesh shader blends it (<see cref="VoxelTintSampler"/>; without a match, the
/// material's own colour). Main thread; allocates nothing.
/// </summary>
public sealed class ParticleColours
{
    private readonly IVoxelPalette _palette;

    public ParticleColours(IVoxelPalette palette)
    {
        _palette = palette;
    }

    /// <param name="palette">The materials' looks.</param>
    /// <param name="tint">The tint map's sampler (<see cref="Tint"/>).</param>
    public ParticleColours(IVoxelPalette palette, VoxelTintSampler tint)
    {
        _palette = palette;
        Tint = tint;
    }

    /// <summary>The match's tint map, set while a match exists; null without one.</summary>
    public VoxelTintSampler? Tint { get; set; }

    /// <summary>The colour of <paramref name="material"/> at <paramref name="at"/> (world metres), with alpha 1.</summary>
    public Vector4 OfMaterial(byte material, Vector3 at)
    {
        var look = _palette.Look(material);
        var share = Tint?.At(at) ?? 0;
        return new Vector4(look.Colour + (look.TintColour - look.Colour) * share, 1);
    }
}
