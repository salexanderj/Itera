using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The frame's look (<see cref="RenderLook"/>) as the shaders read it (<c>look.glsl</c>'s <c>Look</c>, one per frame
/// slot in <c>MeshPass</c>'s set at binding 17 and in the particles' draw set): the look's values, and the light and
/// colours it gives with the frame's atmosphere (the view's sun and sky colours, haze, overcast, and grade), and the
/// frame's haze glows and clouds' drift, worked out once a frame here rather than at every pixel
/// (<see cref="From(in RenderLook, in ViewConstants, ReadOnlySpan{HazeGlow}, double)"/>). Written by the main thread
/// each frame. Field order and types match the GLSL struct; <c>GpuLayout</c> tests check them.
/// </summary>
public struct LookConstants
{
    /// <summary>The sunlight: the sun's colour times the look's warmth, faded out as the sun sets (<see cref="SunPath.Daylight"/>).</summary>
    public Vector3 SunLight;
    public float WorldSaturation;
    public Vector3 ShadeCool;
    public float WorldValue;

    /// <summary>The sky's light from above: the horizon colour times the look's share, times 1 plus the overcast.</summary>
    public Vector3 SkyLight;
    public float WorldSideShade;

    /// <summary>The light from below: the look's ground light plus the sun's colour times its bounce and the sun's height.</summary>
    public Vector3 GroundLight;
    public float WorldTopShade;

    /// <summary>The sky's colour straight up: the horizon colour times the look's zenith, clear to overcast by the overcast.</summary>
    public Vector3 Zenith;
    public float SideShadeBelow;

    /// <summary>The sky's colour straight down: the horizon colour times the look's below-horizon share.</summary>
    public Vector3 Below;
    public float TopShadeAbove;
    public Vector3 GradeShadows;
    public float ShadowsBelow;
    public Vector3 GradeLights;
    public float LightsAbove;

    /// <summary>The sun's disc: the sun's colour times the look's disc strength, less what the overcast hides, faded out as it sets.</summary>
    public Vector3 SunDisc;
    public float SunDiscSharpness;

    /// <summary>The hue the grade shifts towards: the horizon colour over its luma.</summary>
    public Vector3 GradeHue;

    /// <summary>How far the grade desaturates: the look's desaturation times the grade.</summary>
    public float GradeMix;

    /// <summary>What the grade leaves of the light: 1 less the look's darkening times the grade.</summary>
    public float GradeScale;
    public float OcclusionFloor;
    public float SkyFloor;
    public float GlowStrength;
    public float FogStart;

    /// <summary>The haze's density: the haze over the look's haze half distance.</summary>
    public float HazePerMetre;
    public float Exposure;

    /// <summary>How much of its light the sun gives (<see cref="SunPath.Daylight"/>): the particles' sunlight, which takes no warmth.</summary>
    public float Daylight;

    /// <summary>The glow round the sun: the sun's colour times the look's glow strength, faded out as it sets.</summary>
    public Vector3 SunGlow;

    /// <summary>The glow's power of the cosine to the sun, at least 1.</summary>
    public float SunGlowSharpness;

    /// <summary>The clouds' colour: the horizon colour times the look's cloud colour.</summary>
    public Vector3 CloudColour;

    /// <summary>The look's cloud cover, 0 to 1: 0 draws no clouds.</summary>
    public float CloudCover;
    public float CloudOpacity;

    /// <summary>The lower clouds' lattice cells a metre across their plane: the look's cloud scale over their height.</summary>
    public float CloudNearScale;

    /// <summary>The look's cloud stretch, at least 0.01.</summary>
    public float CloudStretch;

    /// <summary>The direction the clouds' streaks run, a unit vector over x and z (the look's heading).</summary>
    public float CloudAlongX;
    public float CloudAlongZ;

    /// <summary>The ground haze's density at its floor: the look's ground haze over its half distance.</summary>
    public float GroundHazePerMetre;

    /// <summary>The ground haze's floor, local: the look's absolute height less the origin's.</summary>
    public float GroundHazeFloor;

    /// <summary>How many times the ground haze halves a metre higher: 1 over the look's falloff metres.</summary>
    public float GroundHazeFalloff;

    /// <summary>The mesh assets' edge light: <see cref="SkyLight"/> times the look's rim strength.</summary>
    public Vector3 RimLight;

    /// <summary>The edge light's power, at least 1.</summary>
    public float RimSharpness;

    /// <summary>How far the world's colour wanders over about 20 m, each channel (<see cref="RenderLook.WorldHueDrift"/>).</summary>
    public Vector3 WorldHueDrift;

    /// <summary>The sky's light towards the sun: the sun's colour times the look's lobe strength, faded out as it sets.</summary>
    public Vector3 SkyLobe;

    /// <summary>The lobe's power of half of one plus the cosine to the sun, at least 0.1.</summary>
    public float SkyLobeSharpness;

    /// <summary>The look's darkening of the sky away from the sun, 0 to 1.</summary>
    public float SkyAwayDarken;

    /// <summary>The power of the view's height that turns the horizon colour to the zenith's, at least 0.05.</summary>
    public float ZenithPower;

    /// <summary>How many times the horizon band halves over a unit of the view's height: 1 over the sine of its degrees.</summary>
    public float HorizonBandFalloff;

    /// <summary>The horizon band's light: the horizon colour times the look's band.</summary>
    public Vector3 HorizonBand;

    /// <summary>The look's haze start in metres, at least 0.</summary>
    public float HazeStart;

    /// <summary>The look's haze cap, 0 to 1.</summary>
    public float HazeMost;

    /// <summary>The look's fade of each colour in the haze, each at least 0.</summary>
    public Vector3 HazeFade;

    /// <summary>The fade's luma-weighted mean: the haze's thickness for what fades as one (fogAt).</summary>
    public float HazeFadeMean;

    /// <summary>How many of the glows below the frame draws, up to <see cref="HazeGlow.Most"/>.</summary>
    public uint GlowCount;

    public HazeGlowConstants Glow0;
    public HazeGlowConstants Glow1;
    public HazeGlowConstants Glow2;
    public HazeGlowConstants Glow3;

    /// <summary>The light on the clouds' edges facing the sun: the sun's colour times the look's cloud light, faded out as it sets.</summary>
    public Vector3 CloudLight;

    /// <summary>The lower clouds' height, local: the look's absolute height, at least 10 m, less the origin's.</summary>
    public float CloudLocalHeight;

    /// <summary>
    /// The lower clouds' lattice point at the origin, moved back by their drift so far: along their heading (stretched)
    /// and across it, each wrapped to 4,096 cells, where the clouds' noise repeats.
    /// </summary>
    public Vector2 CloudNear;

    /// <summary>The same for the upper clouds, twice as high.</summary>
    public Vector2 CloudFar;

    /// <summary>The upper clouds' lattice cells a metre across their plane.</summary>
    public float CloudFarScale;

    /// <summary>The look's bend of the streaks across their heading, at least 0.</summary>
    public float CloudWarp;

    /// <summary>How much the upper clouds show, 0 to 1.</summary>
    public float CloudUpper;

    /// <summary>The look's share of the clouds drawn as ridges, 0 to 1.</summary>
    public float CloudRidge;

    /// <summary>The look's softness of the clouds' edges, at least 0.01.</summary>
    public float CloudSoftness;

    /// <summary>The look's halo strength round the point lights, at least 0.</summary>
    public float Halo;

    /// <summary>How many of the frame's brightest lights have a halo, 0 to <see cref="MostHaloLights"/>.</summary>
    public uint HaloLights;

    /// <summary>The look's halo radius in metres, at least 0.1.</summary>
    public float HaloRadius;

    /// <summary>The look's share of the clouds drawn as lobes, 0 to 1.</summary>
    public float CloudBillow;

    /// <summary>The look's erosion of the clouds' edges, 0 to 1.</summary>
    public float CloudErosion;

    /// <summary>The look's shading of the clouds by their thickness towards the sun, at least 0.</summary>
    public float CloudShadow;

    private static readonly Vector3 s_luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>The most of the frame's brightest point lights that have a halo in the air.</summary>
    public const int MostHaloLights = 8;

    /// <summary>The upper clouds' lattice scale against the lower's, so their streaks look larger.</summary>
    private const float UpperCloudScale = 0.7f;

    /// <summary>
    /// The shaders' record of <paramref name="look"/> with the frame's atmosphere as <paramref name="view"/> carries it
    /// (its haze, overcast, and grade as the shaders unpack them, a byte each). Allocates nothing.
    /// </summary>
    public static LookConstants From(in RenderLook look, in ViewConstants view) => From(in look, in view, default, 0);

    /// <summary>
    /// The shaders' record of <paramref name="look"/> with the frame's atmosphere as <paramref name="view"/> carries it,
    /// the first <see cref="HazeGlow.Most"/> of <paramref name="glows"/>, their places made local to the view's
    /// origin, and the clouds drifted for <paramref name="seconds"/> (the run's clock). Allocates nothing.
    /// </summary>
    public static LookConstants From(
        in RenderLook look,
        in ViewConstants view,
        ReadOnlySpan<HazeGlow> glows,
        double seconds
    )
    {
        var haze = (view.Air & 0xFF) / 255f;
        var overcast = ((view.Air >> 8) & 0xFF) / 255f;
        var grade = ((view.Air >> 16) & 0xFF) / 255f;
        // The sun fades out as it sets and gives no light, and shows no disc, at night.
        var daylight = SunPath.Daylight(view.SunDirection);
        // A compass heading: from north (-z) round towards east (+x).
        var heading = look.CloudHeadingDegrees * MathF.PI / 180;
        var skyLight = view.SkyHorizon * look.SkyLightShare * (1 + overcast);
        var fade = Vector3.Max(look.HazeColourFade, Vector3.Zero);
        var count = Math.Min(glows.Length, HazeGlow.Most);
        var alongX = MathF.Sin(heading);
        var alongZ = -MathF.Cos(heading);
        var stretch = MathF.Max(look.CloudStretch, 0.01f);
        var cloudHeight = MathF.Max(look.CloudHeightMetres, 10);
        var nearScale = look.CloudScale / cloudHeight;
        var farScale = look.CloudScale * UpperCloudScale / (2 * cloudHeight);
        // The origin in the clouds' frame, in metres: along their heading (less the drift, which moves the clouds
        // along it) over their stretch, and across it.
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        var originX = view.OriginX / perMetre;
        var originZ = view.OriginZ / perMetre;
        var along = (originX * alongX + originZ * alongZ - look.CloudDriftMetresPerSecond * seconds) / stretch;
        var across = originZ * alongX - originX * alongZ;
        return new LookConstants
        {
            SunLight = view.SunColour * look.SunWarmth * daylight,
            WorldSaturation = look.WorldSaturation,
            ShadeCool = look.ShadeCool,
            WorldValue = look.WorldValue,
            SkyLight = skyLight,
            WorldSideShade = look.WorldSideShade,
            GroundLight = look.GroundLight + view.SunColour * (look.GroundBounce * MathF.Max(view.SunDirection.Y, 0)),
            WorldTopShade = look.WorldTopShade,
            // GLSL's mix: a times (1 - t) plus b times t.
            Zenith = view.SkyHorizon * (look.ZenithClear * (1 - overcast) + look.ZenithOvercast * overcast),
            SideShadeBelow = look.SideShadeBelow,
            Below = view.SkyHorizon * look.BelowHorizon,
            TopShadeAbove = look.TopShadeAbove,
            GradeShadows = look.GradeShadows,
            ShadowsBelow = look.ShadowsBelow,
            GradeLights = look.GradeLights,
            LightsAbove = look.LightsAbove,
            SunDisc = view.SunColour * look.SunDiscStrength * (1 - look.SunDiscOvercast * overcast) * daylight,
            SunDiscSharpness = look.SunDiscSharpness,
            GradeHue = view.SkyHorizon / MathF.Max(Vector3.Dot(view.SkyHorizon, s_luma), 0.01f),
            GradeMix = look.GradeDesaturate * grade,
            GradeScale = 1 - look.GradeDarken * grade,
            OcclusionFloor = look.OcclusionFloor,
            SkyFloor = look.SkyFloor,
            GlowStrength = look.GlowStrength,
            FogStart = look.FogStart,
            HazePerMetre = haze / look.HazeHalfMetres,
            Exposure = look.Exposure,
            Daylight = daylight,
            SunGlow = view.SunColour * look.SunGlowStrength * daylight,
            SunGlowSharpness = MathF.Max(look.SunGlowSharpness, 1),
            CloudColour = view.SkyHorizon * look.CloudColour,
            CloudCover = Math.Clamp(look.CloudCover, 0, 1),
            CloudOpacity = Math.Clamp(look.CloudOpacity, 0, 1),
            CloudNearScale = nearScale,
            CloudStretch = stretch,
            CloudAlongX = alongX,
            CloudAlongZ = alongZ,
            GroundHazePerMetre = MathF.Max(look.GroundHaze, 0) / MathF.Max(look.GroundHazeHalfMetres, 0.01f),
            GroundHazeFloor = look.GroundHazeFloor - (float)(view.OriginY / (double)VoxelConstants.CentimetresPerMetre),
            GroundHazeFalloff = 1 / MathF.Max(look.GroundHazeFalloffMetres, 0.01f),
            RimLight = skyLight * MathF.Max(look.RimStrength, 0),
            RimSharpness = MathF.Max(look.RimSharpness, 1),
            WorldHueDrift = look.WorldHueDrift,
            SkyLobe = view.SunColour * MathF.Max(look.SkyLobeStrength, 0) * daylight,
            SkyLobeSharpness = MathF.Max(look.SkyLobeSharpness, 0.1f),
            SkyAwayDarken = Math.Clamp(look.SkyAwayDarken, 0, 1),
            ZenithPower = MathF.Max(look.ZenithPower, 0.05f),
            HorizonBandFalloff = 1 / MathF.Sin(Math.Clamp(look.HorizonBandDegrees, 0.1f, 90) * MathF.PI / 180),
            HorizonBand = view.SkyHorizon * MathF.Max(look.HorizonBand, 0),
            HazeStart = MathF.Max(look.HazeStartMetres, 0),
            HazeMost = Math.Clamp(look.HazeMost, 0, 1),
            HazeFade = fade,
            HazeFadeMean = Vector3.Dot(fade, s_luma),
            GlowCount = (uint)count,
            Glow0 = count > 0 ? HazeGlowConstants.From(glows[0], in view) : default,
            Glow1 = count > 1 ? HazeGlowConstants.From(glows[1], in view) : default,
            Glow2 = count > 2 ? HazeGlowConstants.From(glows[2], in view) : default,
            Glow3 = count > 3 ? HazeGlowConstants.From(glows[3], in view) : default,
            CloudLight = view.SunColour * MathF.Max(look.CloudLight, 0) * daylight,
            CloudLocalHeight = cloudHeight - (float)(view.OriginY / perMetre),
            CloudNear = CloudLattice(along, across, nearScale),
            CloudFar = CloudLattice(along, across, farScale),
            CloudFarScale = farScale,
            CloudWarp = MathF.Max(look.CloudWarp, 0),
            CloudUpper = Math.Clamp(look.CloudUpper, 0, 1),
            CloudRidge = Math.Clamp(look.CloudRidge, 0, 1),
            CloudSoftness = MathF.Max(look.CloudSoftness, 0.01f),
            Halo = MathF.Max(look.LightHalo, 0),
            HaloLights = (uint)Math.Clamp(MathF.Round(look.LightHaloLights), 0, MostHaloLights),
            HaloRadius = MathF.Max(look.LightHaloRadiusMetres, 0.1f),
            CloudBillow = Math.Clamp(look.CloudBillow, 0, 1),
            CloudErosion = Math.Clamp(look.CloudErosion, 0, 1),
            CloudShadow = MathF.Max(look.CloudShadow, 0),
        };
    }

    // The lattice point of metres along and across the clouds' frame at scale cells a metre, each wrapped to the period
    // the clouds' noise repeats at, so the point keeps its precision over any time or place.
    private static Vector2 CloudLattice(double along, double across, float scale)
    {
        // A tile of the clouds' noise spans 4 cells, its slowest read (the warp) 16, its finest (the erosion) 0.8: 4,096
        // cells is a whole number of each, so the clouds repeat there.
        const double Period = 4096;
        var x = along * scale;
        var y = across * scale;
        return new Vector2((float)(x - Period * Math.Floor(x / Period)), (float)(y - Period * Math.Floor(y / Period)));
    }
}

/// <summary>
/// One haze glow as the shaders read it (<c>look.glsl</c>'s <c>HazeGlow</c>, in <see cref="LookConstants"/>): its place
/// local to the view's origin, its radius, and its colour.
/// </summary>
public struct HazeGlowConstants
{
    /// <summary>The glow's middle, local: its absolute place less the origin's.</summary>
    public Vector3 Place;

    /// <summary>The glow's radius in metres, at least 1.</summary>
    public float Radius;

    /// <summary>The glow's linear colour, each channel at least 0.</summary>
    public Vector3 Colour;

    /// <summary>The shaders' record of <paramref name="glow"/> in <paramref name="view"/>'s origin. Allocates nothing.</summary>
    public static HazeGlowConstants From(in HazeGlow glow, in ViewConstants view)
    {
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        return new HazeGlowConstants
        {
            Place = new Vector3(
                (float)(glow.Place.X - view.OriginX / perMetre),
                (float)(glow.Place.Y - view.OriginY / perMetre),
                (float)(glow.Place.Z - view.OriginZ / perMetre)
            ),
            Radius = MathF.Max(glow.RadiusMetres, 1),
            Colour = Vector3.Max(glow.Colour, Vector3.Zero),
        };
    }
}
