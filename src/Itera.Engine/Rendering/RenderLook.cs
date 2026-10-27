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

    /// <summary>
    /// How far the world's own surfaces' colour wanders over about 20 m: each channel is multiplied by 1 plus this times a
    /// value noise from -1 to 1, so (0.04, 0, -0.04) wanders warm to cool by 4 %. Zero, the default, leaves it.
    /// </summary>
    public Vector3 WorldHueDrift { get; init; }

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

    /// <summary>
    /// How bright the edge light on the mesh assets (figures, creatures, guns) is, as a share of the sky's light: light
    /// round their outlines from the sky behind them, so they stand out from the ground. 0, the default, none. Not the
    /// world's own surfaces or the voxel models.
    /// </summary>
    public float RimStrength { get; init; }

    /// <summary>How thin the edge light is: the power of one less the cosine to the eye; higher is thinner.</summary>
    public float RimSharpness { get; init; } = 3f;

    /// <summary>Where the fog that hides the view's end starts, as a share of the view distance (all of it at the end).</summary>
    public float FogStart { get; init; } = 0.6f;

    /// <summary>The distance in metres at which full haze (the atmosphere's) hides half of a surface.</summary>
    public float HazeHalfMetres { get; init; } = 60f;

    /// <summary>
    /// How many metres from the eye the haze (the atmosphere's and the ground haze) begins, so the near ground stays
    /// clear; 0, the default, from the eye. The fog that hides the view's end is not moved.
    /// </summary>
    public float HazeStartMetres { get; init; }

    /// <summary>
    /// 0 to 1: the most of a surface the haze hides before the view's end, so far shapes stay faintly seen; 1, the
    /// default, all of it. The fog that hides the view's end still hides all.
    /// </summary>
    public float HazeMost { get; init; } = 1f;

    /// <summary>
    /// How fast each colour of a surface fades in the haze, as a multiple of the haze's thickness: (0.8, 1, 1.2) loses blue
    /// first, so far ground turns towards red before it meets the sky. (1, 1, 1), the default, fades every colour alike.
    /// </summary>
    public Vector3 HazeColourFade { get; init; } = Vector3.One;

    /// <summary>
    /// 0 to 1: how thick the haze that lies in low ground is, on top of the atmosphere's haze; 0, the default, none. At 1,
    /// half of a surface <see cref="GroundHazeHalfMetres"/> away is lost in it at <see cref="GroundHazeFloor"/>.
    /// </summary>
    public float GroundHaze { get; init; }

    /// <summary>The distance in metres at which full ground haze at its floor hides half of a surface.</summary>
    public float GroundHazeHalfMetres { get; init; } = 100f;

    /// <summary>The height in metres in the world (absolute) at and below which the ground haze is thickest.</summary>
    public float GroundHazeFloor { get; init; }

    /// <summary>How many metres higher the ground haze is half as thick: smaller keeps it lower.</summary>
    public float GroundHazeFalloffMetres { get; init; } = 10f;

    /// <summary>The sky's colour straight up under a clear sky, as a share of its horizon colour.</summary>
    public Vector3 ZenithClear { get; init; } = new(0.35f, 0.5f, 0.85f);

    /// <summary>The sky's colour straight up under full overcast, as a share of its horizon colour.</summary>
    public Vector3 ZenithOvercast { get; init; } = new(0.8f, 0.8f, 0.8f);

    /// <summary>
    /// How the sky turns from its horizon colour to its zenith colour with the height of the view: the power of the
    /// direction's height. 0.5, the default, reaches most of the zenith colour soon over the horizon; 1 turns evenly.
    /// </summary>
    public float ZenithPower { get; init; } = 0.5f;

    /// <summary>The sky's colour straight down (below the horizon), as a share of its horizon colour.</summary>
    public float BelowHorizon { get; init; } = 0.45f;

    /// <summary>
    /// How strongly the haze lights up round the frame's brightest point lights (lamps, fires, flashes), as a share of a
    /// light's power scattered a metre of air: the halos' strength. 0, the default, none.
    /// </summary>
    public float LightHalo { get; init; }

    /// <summary>How many of the frame's brightest point lights have a halo, 0 to 8.</summary>
    public float LightHaloLights { get; init; } = 4f;

    /// <summary>How wide a halo's bright middle is round its light, in metres.</summary>
    public float LightHaloRadiusMetres { get; init; } = 1f;

    /// <summary>How sharp the sun's disc is: the power of the cosine to the sun; higher is smaller.</summary>
    public float SunDiscSharpness { get; init; } = 400f;

    /// <summary>How bright the sun's disc is, as a multiple of the sun's colour.</summary>
    public float SunDiscStrength { get; init; } = 4f;

    /// <summary>How much of the sun's disc full overcast hides.</summary>
    public float SunDiscOvercast { get; init; } = 0.9f;

    /// <summary>
    /// How bright the glow round the sun is, as a multiple of the sun's colour: the light the air scatters towards the
    /// eye near the sun. 0, the default, none. The fog takes the sky's colour behind a surface, so the haze glows too.
    /// </summary>
    public float SunGlowStrength { get; init; }

    /// <summary>How tight the glow round the sun is: the power of the cosine to the sun; higher is smaller.</summary>
    public float SunGlowSharpness { get; init; } = 8f;

    /// <summary>
    /// How bright the sky and the haze are towards the sun over half the sky, as a multiple of the sun's colour: the
    /// light the air scatters broadly, wider than the glow, so the light's direction shows whether or not the sun is in
    /// view. 0, the default, none. Faded out as the sun sets.
    /// </summary>
    public float SkyLobeStrength { get; init; }

    /// <summary>How wide that light is: the power of half of one plus the cosine to the sun; higher is narrower.</summary>
    public float SkyLobeSharpness { get; init; } = 4f;

    /// <summary>0 to 1: how much darker the sky and the haze are straight away from the sun; 0, the default, none.</summary>
    public float SkyAwayDarken { get; init; }

    /// <summary>
    /// How bright a band of light along the horizon is, as a share of the horizon colour, so the far land stands against
    /// a lighter sky; 0, the default, none. The haze takes it too.
    /// </summary>
    public float HorizonBand { get; init; }

    /// <summary>How many degrees over (and under) the horizon the band is half as bright.</summary>
    public float HorizonBandDegrees { get; init; } = 6f;

    /// <summary>
    /// 0 to 1: how much of the sky streaks of ash cloud cover, drawn on the sky alone and thinning to none at the horizon;
    /// 0, the default, none.
    /// </summary>
    public float CloudCover { get; init; }

    /// <summary>The clouds' colour, as a share of the sky's horizon colour.</summary>
    public Vector3 CloudColour { get; init; } = new(0.5f, 0.5f, 0.5f);

    /// <summary>0 to 1: how much of the sky behind the thickest cloud it hides.</summary>
    public float CloudOpacity { get; init; } = 1f;

    /// <summary>How many clouds lie across the sky: higher gives more, smaller ones.</summary>
    public float CloudScale { get; init; } = 2f;

    /// <summary>How many times longer than wide the clouds are, along <see cref="CloudHeadingDegrees"/>: streaks above 1.</summary>
    public float CloudStretch { get; init; } = 1f;

    /// <summary>The compass direction the clouds' streaks run, in degrees from north (-z) round towards east (+x).</summary>
    public float CloudHeadingDegrees { get; init; }

    /// <summary>
    /// How high the clouds lie over the world's height 0, in metres: they stay over the land as the eye moves, so they
    /// shift against it.
    /// </summary>
    public float CloudHeightMetres { get; init; } = 1500f;

    /// <summary>How fast the clouds drift along their heading, in metres a second; 0, the default, they stand still.</summary>
    public float CloudDriftMetresPerSecond { get; init; }

    /// <summary>
    /// How far the clouds' streaks bend and braid across their heading, as a share of a streak's width; 0, the default,
    /// straight.
    /// </summary>
    public float CloudWarp { get; init; }

    /// <summary>
    /// How bright the clouds' edges facing the sun are, as a multiple of the sun's colour; 0, the default, unlit. Faded
    /// out as the sun sets.
    /// </summary>
    public float CloudLight { get; init; }

    /// <summary>
    /// 0 to 1: how much a second layer of cloud twice as high shows behind the first, its streaks larger and slower to
    /// the eye; 0, the default, none.
    /// </summary>
    public float CloudUpper { get; init; }

    /// <summary>
    /// 0 to 1: how much the clouds are drawn as thin ridges (wisps and filaments) rather than soft blobs; 0, the default,
    /// soft.
    /// </summary>
    public float CloudRidge { get; init; }

    /// <summary>How soft the clouds' edges are, as a share of their noise's range: smaller is crisper.</summary>
    public float CloudSoftness { get; init; } = 0.3f;

    /// <summary>
    /// 0 to 1: how much the clouds are drawn as rounded lobes, puffs with creases between, rather than soft blobs; 0, the
    /// default, soft.
    /// </summary>
    public float CloudBillow { get; init; }

    /// <summary>
    /// 0 to 1: how much a finer noise eats into the clouds' thin edges, so they end ragged and wispy rather than smooth;
    /// 0, the default, none.
    /// </summary>
    public float CloudErosion { get; init; }

    /// <summary>
    /// How much a cloud's own thickness towards the sun shades it: lit on its sunward side and thin rims, dark in its
    /// core and on its far side, as a cloud with volume is. 0, the default, none: the cloud light then lights the edges
    /// facing the sun alone.
    /// </summary>
    public float CloudShadow { get; init; }

    /// <summary>
    /// How much each puff of a cloud is lit on its side towards the sun and shaded on its far side, by the cloud's own
    /// shape a short way sunwards, so a mass shows its lumps; 0, the default, none.
    /// </summary>
    public float CloudPuffLight { get; init; }

    /// <summary>
    /// How much the creases between a cloud's puffs darken and the puffs' crowns lighten, by the shape against a blurred
    /// read of it, so a mass is not one even shade; 0, the default, none.
    /// </summary>
    public float CloudCreases { get; init; }

    /// <summary>
    /// How far away the clouds still show, in metres: they shrink towards the horizon as a real layer does and thin out
    /// over the farther half of this distance. 0, the default: their size changes more slowly towards the horizon, and
    /// they thin by their angle over it, gone below about 17 degrees.
    /// </summary>
    public float CloudReachMetres { get; init; }

    /// <summary>
    /// 0 to 1: how much a layer of thin wisps shows in front of the clouds, at 0.6 of their height and drifting half as
    /// fast again; 0, the default, none.
    /// </summary>
    public float CloudLower { get; init; }

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
