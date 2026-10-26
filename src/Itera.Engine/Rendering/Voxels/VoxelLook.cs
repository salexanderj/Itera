using System.Numerics;

namespace Itera.Engine.Rendering.Voxels;

/// <summary>
/// The recipe a material's surface is drawn with (<c>mesh-surface.glsl</c>, ported from the raymarcher's
/// <c>voxel-surface.glsl</c> at <c>raymarcher-final</c>; docs/rendering-redesign.md, phase 3, and the art direction's
/// simpler recipes). The console's <c>materials bake</c> works it out into a texture set the world's meshes draw; the
/// models show their colours alone. Each shapes noise into features of <see cref="VoxelLook.SizeCentimetres"/>, colours them from the material's colour, its accent, and darker
/// shades, and gives them relief. Every recipe also takes the large variation (<see cref="VoxelLook.Patches"/>).
/// </summary>
public enum VoxelPattern : byte
{
    /// <summary>One colour, varied only by the large patches.</summary>
    Plain = 0,

    /// <summary>Fine streaks, taller than wide by the aspect: cut wood.</summary>
    Grain = 1,

    /// <summary>
    /// Soft light and dark patches of the feature size over a fine grain, in faint bands by height that wander over a
    /// few metres and darken with depth (brown, grey, slate, near-black basalt below about -25 m): the roads'
    /// flagstones (stone and rock now take <see cref="Mottle"/>).
    /// </summary>
    Strata = 2,

    /// <summary>
    /// A speckled ground with domed pebbles in the accent colour in the accent's share of the features, laid out over x
    /// and z: clover (soils and paths now take <see cref="Soil"/>).
    /// </summary>
    Pebbles = 3,

    /// <summary>
    /// Clumps with dark gaps and bright blade tips, over x and z, in hues between the colour and the accent that drift
    /// over 0.3 m, 2.5 m, and 20 m; the clumps fade out early with distance: grass.
    /// </summary>
    Clumps = 4,

    /// <summary>
    /// Soft clusters with shaded hollows between them, some lighter towards the accent: leaves, needles, bushes.
    /// </summary>
    Clusters = 5,

    /// <summary>
    /// Plates taller than wide by the aspect, split by thin dark cracks that show the accent by the accent's share,
    /// each plate its own shade: bark. Laid out on the planes the surface faces, so it wraps a trunk.
    /// </summary>
    Plates = 6,

    /// <summary>
    /// Dry flakes split by a network of thin dark cracks, over x and z: dried mud. Unused so far: on ash it read as
    /// paving stones.
    /// </summary>
    Cracks = 7,

    /// <summary>
    /// Dressed blocks in courses the feature size high and up to the aspect times as long, each its own shade, with
    /// rounded, chipped edges and thin sunken joints: built walls. Laid out on the plane the surface faces most.
    /// </summary>
    Blocks = 8,

    /// <summary>
    /// Soil, the art direction's first look: the colour in soft patches with sparse specks, and pebbles in the accent
    /// colour in the accent's share of cells of the feature size (none at 0; gravel at most), over x and z.
    /// </summary>
    Soil = 9,

    /// <summary>
    /// Soft streaks the feature size wide and the aspect times as long, up the plane the surface faces, in two tones,
    /// with soft furrows darker and towards the accent by the accent's share: mushroom stems (the barks took it for a
    /// while).
    /// </summary>
    Streaks = 10,

    /// <summary>
    /// Soft round patches about the feature size, lighter and warmer or darker and cooler by the contrast, over a faint
    /// grain, with strata's bands by height: the art direction's simple stone.
    /// </summary>
    Mottle = 11,
}

/// <summary>
/// How a material looks: its colour, the colour the tint map blends it towards (<see cref="IVoxelTintMap"/>), its
/// recipe, its accent, and its relief and glow. Linear RGB, each component 0 to 1.
/// </summary>
/// <param name="Colour">The colour where the tint map is 0.</param>
/// <param name="TintColour">The colour where the tint map is 1.</param>
/// <param name="Pattern">The recipe.</param>
/// <param name="SizeCentimetres">Width of the recipe's features (cells, plates, clusters; the height of a course of blocks) in level-0 voxels: a power of two from 1 to 32768.</param>
/// <param name="Aspect">How many times taller than wide the features are (for blocks, longer than high): a power of two from 1 to 128.</param>
/// <param name="Accent">The recipe's second colour: pebbles, dry grass, light leaves, inner bark.</param>
/// <param name="AccentShare">0 to 1: how much of the surface takes the accent.</param>
/// <param name="LivingAccent">True when the accent is alive (fresh leaves, moss) and dies away where the tint map is 1.</param>
/// <param name="Contrast">0 to 1: how strongly the recipe's shades differ.</param>
/// <param name="Relief">0 to 1: how far the recipe's relief tilts the normal sunlight and sky light use, and how dark its hollows get.</param>
/// <param name="Patches">0 to 1: strength of the variation over about 0.6 m and 10 m, which never fades.</param>
/// <param name="Glow">0 to 1: light the material gives off, as a share of four times its colour; 0 for most.</param>
/// <param name="Texture">
/// The name of the texture set the world draws the material from instead of its recipe (a folder of
/// <c>MaterialTextureSets.Folder</c>; docs/rendering-redesign.md, the materials plan), or null for its recipe.
/// </param>
/// <param name="TextureColourMatch">
/// 0 to 1: how far the texture's colour is moved to <paramref name="Colour"/> (its mean scaled to it); 0 keeps the
/// set's own colours (a bake's are the material's already). The tint map moves either as it moves the colour.
/// </param>
/// <param name="TextureDetail">0 to 1: how much of the texture's colour variation round its mean is kept (1 all of it).</param>
/// <param name="TextureRelief">0 to 1: how strongly its normal map and occlusion shade it (1 fully).</param>
/// <param name="TextureBlur">0 to 4: how many mip levels softer it is read (0 as sharp as the screen allows).</param>
/// <param name="StrataBands">
/// Whether a textured material takes strata's bands by height over its set, which a 2D tile cannot hold; null for its
/// pattern's choice (<see cref="VoxelPattern.Strata"/> and <see cref="VoxelPattern.Mottle"/> take them).
/// </param>
public readonly record struct VoxelLook(
    Vector3 Colour,
    Vector3 TintColour,
    VoxelPattern Pattern = VoxelPattern.Plain,
    int SizeCentimetres = 4,
    int Aspect = 1,
    Vector3 Accent = default,
    float AccentShare = 0,
    bool LivingAccent = false,
    float Contrast = 0.5f,
    float Relief = 0,
    float Patches = 0.3f,
    float Glow = 0,
    string? Texture = null,
    float TextureColourMatch = 0,
    float TextureDetail = 1,
    float TextureRelief = 1,
    float TextureBlur = 0,
    bool? StrataBands = null
)
{
    /// <summary>The <c>look</c> word's bit for <see cref="LivingAccent"/>.</summary>
    public const uint LivingAccentBit = 1 << 15;

    /// <summary>One colour everywhere, no pattern, no glow.</summary>
    public static VoxelLook Plain(Vector3 colour) => new(colour, colour, Patches: 0);

    /// <summary>
    /// The <c>look</c> word the recipes read (<c>Material</c> in the raymarcher's <c>voxel-data.glsl</c>): <see cref="Pattern"/> in bits 0 to 7, the
    /// feature size's log2 in bits 8 to 11, the aspect's log2 in bits 12 to 14, <see cref="LivingAccentBit"/>, and
    /// <see cref="AccentShare"/> in bits 16 to 23.
    /// </summary>
    public uint PackLook()
    {
        var size = (uint)BitOperations.Log2((uint)Math.Clamp(SizeCentimetres, 1, 1 << 15));
        var aspect = (uint)BitOperations.Log2((uint)Math.Clamp(Aspect, 1, 128));
        return (uint)Pattern
            | size << 8
            | aspect << 12
            | (LivingAccent ? LivingAccentBit : 0)
            | Byte(AccentShare) << 16;
    }

    /// <summary>
    /// The <c>amounts</c> word: <see cref="Contrast"/>, <see cref="Relief"/>, <see cref="Patches"/>, and
    /// <see cref="Glow"/> a byte each from the lowest (GLSL <c>unpackUnorm4x8</c>).
    /// </summary>
    public uint PackAmounts() => Byte(Contrast) | Byte(Relief) << 8 | Byte(Patches) << 16 | Byte(Glow) << 24;

    private static uint Byte(float value) => (uint)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
