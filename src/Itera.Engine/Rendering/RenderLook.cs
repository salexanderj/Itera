using System.Numerics;

namespace Itera.Engine.Rendering;

/// <summary>
/// The look a frame is drawn with, as data a game sets: how the world's surfaces are coloured and shaded, the light's
/// tints and strengths, the fog, the sky, the exposure, the grade, the split tone, and the glow. The world's surfaces,
/// the models, the mesh assets, the sky, and the particles read it each frame (<c>look.glsl</c>,
/// <see cref="GpuData.LookConstants"/>). A new one (<see cref="Default"/>) is the engine's look, a stylised dark-fantasy
/// art direction (docs/rendering-redesign.md): less saturated and darker than the materials, warm sunlight and cool
/// shade, dark tones towards teal and light ones towards orange. A game gives its own per frame through its atmosphere
/// source (<see cref="Systems.IAtmosphereSource.LookAt"/>), changed with <c>with</c> (a darker grade at dusk, say); the
/// console's <c>look</c> tunes one for a run. Colours are linear RGB factors. <c>default(RenderLook)</c> is all zeros, a
/// black picture: start from <see cref="Default"/>.
/// </summary>
public readonly record struct RenderLook
{
    public RenderLook() { }

    /// <summary>The engine's look (also <c>new RenderLook()</c>).</summary>
    public static RenderLook Default { get; } = new();

    /// <summary>
    /// The look <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>, every value
    /// blended (0 gives <paramref name="from"/>, 1 <paramref name="to"/>): for a look that changes with the time of day
    /// or the place. Allocates nothing.
    /// </summary>
    public static RenderLook Lerp(in RenderLook from, in RenderLook to, float t)
    {
        var result = from;
        var fields = LookTuning.Fields;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            result = field.Set(result, Vector3.Lerp(field.Get(from), field.Get(to), t));
        }

        return result;
    }

    /// <summary>How much colour the world's own surfaces keep: 1 their material's, 0 grey. Not models, mesh assets, or glow.</summary>
    public float WorldSaturation { get; init; } = 0.75f;

    /// <summary>How light the world's own surfaces are against their material's colour.</summary>
    public float WorldValue { get; init; } = 0.85f;

    /// <summary>The world's surfaces' brightness where they face down at <see cref="SideShadeBelow"/> or more.</summary>
    public float WorldSideShade { get; init; } = 0.7f;

    /// <summary>The world's surfaces' brightness where they face up at <see cref="TopShadeAbove"/> or more.</summary>
    public float WorldTopShade { get; init; } = 1.1f;

    /// <summary>The normal's height (-1 down to 1 up) at and below which a world surface takes <see cref="WorldSideShade"/>.</summary>
    public float SideShadeBelow { get; init; } = -0.3f;

    /// <summary>The normal's height at and above which a world surface takes <see cref="WorldTopShade"/>.</summary>
    public float TopShadeAbove { get; init; } = 0.9f;

    /// <summary>The tint of direct sunlight on everything drawn, times the atmosphere's sun colour.</summary>
    public Vector3 SunWarmth { get; init; } = new(1.08f, 1.0f, 0.88f);

    /// <summary>The tint of the sky's and the ground's light on every surface of the world's pass.</summary>
    public Vector3 ShadeCool { get; init; } = new(0.85f, 0.95f, 1.15f);

    /// <summary>The sky's light from above, as a share of the atmosphere's horizon colour (doubled under full overcast).</summary>
    public Vector3 SkyLightShare { get; init; } = new(0.6875f, 0.75f, 0.875f);

    /// <summary>The ground's own light from below.</summary>
    public Vector3 GroundLight { get; init; } = new(0.2f, 0.18f, 0.14f);

    /// <summary>The share of the sunlight the sunlit ground throws back up, times the sine of the sun's height.</summary>
    public float GroundBounce { get; init; } = 0.2f;

    /// <summary>The least share of the sky's and the ground's light that a deep corner (a vertex's openness) keeps.</summary>
    public float OcclusionFloor { get; init; } = 0.35f;

    /// <summary>The least share of the sky's and the ground's light a surface keeps however little sky it sees.</summary>
    public float SkyFloor { get; init; } = 0.02f;

    /// <summary>The light a material gives off at full glow, as a multiple of its colour.</summary>
    public float GlowStrength { get; init; } = 4f;

    /// <summary>Where the fog that hides the view's end starts, as a share of the view distance (all of it at the end).</summary>
    public float FogStart { get; init; } = 0.6f;

    /// <summary>The distance in metres at which full haze (the atmosphere's) hides half of a surface.</summary>
    public float HazeHalfMetres { get; init; } = 60f;

    /// <summary>The sky's colour straight up under a clear sky, as a share of its horizon colour.</summary>
    public Vector3 ZenithClear { get; init; } = new(0.35f, 0.5f, 0.85f);

    /// <summary>The sky's colour straight up under full overcast, as a share of its horizon colour.</summary>
    public Vector3 ZenithOvercast { get; init; } = new(0.8f, 0.8f, 0.8f);

    /// <summary>The sky's colour straight down (below the horizon), as a share of its horizon colour.</summary>
    public float BelowHorizon { get; init; } = 0.45f;

    /// <summary>How sharp the sun's disc is: the power of the cosine to the sun; higher is smaller.</summary>
    public float SunDiscSharpness { get; init; } = 400f;

    /// <summary>How bright the sun's disc is, as a multiple of the sun's colour.</summary>
    public float SunDiscStrength { get; init; } = 4f;

    /// <summary>How much of the sun's disc full overcast hides.</summary>
    public float SunDiscOvercast { get; init; } = 0.9f;

    /// <summary>The light's multiple before the tone curve: higher is brighter.</summary>
    public float Exposure { get; init; } = 0.8f;

    /// <summary>How far the atmosphere's full grade moves colours towards grey in the sky's hue.</summary>
    public float GradeDesaturate { get; init; } = 0.6f;

    /// <summary>How much the atmosphere's full grade darkens the picture.</summary>
    public float GradeDarken { get; init; } = 0.3f;

    /// <summary>The split tone's tint of the picture's dark tones (after the grade; not the sky or glow).</summary>
    public Vector3 GradeShadows { get; init; } = new(0.9f, 1.0f, 1.08f);

    /// <summary>The split tone's tint of the picture's light tones.</summary>
    public Vector3 GradeLights { get; init; } = new(1.06f, 1.0f, 0.9f);

    /// <summary>The luma at and below which a pixel takes all of <see cref="GradeShadows"/>.</summary>
    public float ShadowsBelow { get; init; } = 0.15f;

    /// <summary>The luma at and above which a pixel takes all of <see cref="GradeLights"/>.</summary>
    public float LightsAbove { get; init; } = 0.6f;
}
