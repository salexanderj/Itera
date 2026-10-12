using System.Numerics;
using Itera.Engine.Rendering.Voxels;

namespace Game.WorldGeneration;

/// <summary>
/// How <see cref="GameMaterials"/> look, in linear RGB. Each names a texture set in <c>Textures/</c> (made by
/// <c>tools/Textures</c> from a recipe the console's <c>materials bake</c> bakes); the world draws the set, and the
/// colour and pattern stand in where a set is missing. Unknown ids are magenta, so they stand out.
/// </summary>
public sealed class GamePalette : IVoxelPalette
{
    public void Prepare() { }

    public VoxelLook Look(byte material) =>
        material switch
        {
            GameMaterials.Grass => new(
                new Vector3(0.065f, 0.16f, 0.045f),
                new Vector3(0.065f, 0.16f, 0.045f),
                VoxelPattern.Clumps,
                SizeCentimetres: 16,
                Accent: new Vector3(0.16f, 0.17f, 0.06f),
                AccentShare: 0.5f,
                Contrast: 0.6f,
                Relief: 0.5f,
                Patches: 0.7f,
                Texture: "grass"
            ),
            GameMaterials.Dirt => new(
                new Vector3(0.16f, 0.1f, 0.055f),
                new Vector3(0.16f, 0.1f, 0.055f),
                VoxelPattern.Soil,
                SizeCentimetres: 4,
                Contrast: 0.5f,
                Relief: 0.6f,
                Patches: 0.5f,
                Texture: "dirt"
            ),
            GameMaterials.Stone => new(
                new Vector3(0.25f, 0.24f, 0.23f),
                new Vector3(0.25f, 0.24f, 0.23f),
                VoxelPattern.Mottle,
                SizeCentimetres: 32,
                Contrast: 0.5f,
                Relief: 0.4f,
                Patches: 0.4f,
                Texture: "stone"
            ),
            // Square plates split by thin dark cracks (no stretched bark).
            GameMaterials.Bark => new(
                new Vector3(0.1f, 0.075f, 0.055f),
                new Vector3(0.1f, 0.075f, 0.055f),
                VoxelPattern.Plates,
                SizeCentimetres: 8,
                Aspect: 1,
                Accent: new Vector3(0.035f, 0.028f, 0.022f),
                AccentShare: 0.6f,
                Contrast: 0.45f,
                Relief: 0.5f,
                Patches: 0.3f,
                Texture: "bark"
            ),
            // Soft clusters: low contrast and no relief.
            GameMaterials.Leaves => new(
                new Vector3(0.045f, 0.11f, 0.03f),
                new Vector3(0.045f, 0.11f, 0.03f),
                VoxelPattern.Clusters,
                SizeCentimetres: 16,
                Accent: new Vector3(0.09f, 0.15f, 0.04f),
                AccentShare: 0.4f,
                Contrast: 0.3f,
                Relief: 0,
                Patches: 0.4f,
                Texture: "leaves"
            ),
            GameMaterials.Boulder => new(
                new Vector3(0.25f, 0.24f, 0.23f),
                new Vector3(0.25f, 0.24f, 0.23f),
                VoxelPattern.Mottle,
                SizeCentimetres: 32,
                Contrast: 0.5f,
                Relief: 0.4f,
                Patches: 0.4f,
                Texture: "stone"
            ),
            GameMaterials.Rock => new(
                new Vector3(0.23f, 0.22f, 0.21f),
                new Vector3(0.23f, 0.22f, 0.21f),
                VoxelPattern.Mottle,
                SizeCentimetres: 32,
                Contrast: 0.5f,
                Relief: 0.4f,
                Patches: 0.4f,
                Texture: "stone"
            ),
            GameMaterials.Gravel => new(
                new Vector3(0.16f, 0.13f, 0.1f),
                new Vector3(0.16f, 0.13f, 0.1f),
                VoxelPattern.Soil,
                SizeCentimetres: 8,
                Accent: new Vector3(0.24f, 0.21f, 0.18f),
                AccentShare: 0.85f,
                Contrast: 0.6f,
                Relief: 0.6f,
                Patches: 0.4f,
                Texture: "gravel"
            ),
            _ => VoxelLook.Plain(new Vector3(1, 0, 1)),
        };
}
