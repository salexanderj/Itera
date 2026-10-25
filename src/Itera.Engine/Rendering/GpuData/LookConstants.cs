using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The frame's look (<see cref="RenderLook"/>) as the shaders read it (<c>look.glsl</c>'s <c>Look</c>, one per frame
/// slot in <c>MeshPass</c>'s set at binding 17 and in the particles' draw set): the look's values, and the light and
/// colours it gives with the frame's atmosphere (the view's sun and sky colours, haze, overcast, and grade), worked out
/// once a frame here rather than at every pixel (<see cref="From"/>). Written by the main thread each frame. Field order
/// and types match the GLSL struct; <c>GpuLayout</c> tests check them.
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
    public float CloudScale;

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

    private static readonly Vector3 s_luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>
    /// The shaders' record of <paramref name="look"/> with the frame's atmosphere as <paramref name="view"/> carries it
    /// (its haze, overcast, and grade as the shaders unpack them, a byte each). Allocates nothing.
    /// </summary>
    public static LookConstants From(in RenderLook look, in ViewConstants view)
    {
        var haze = (view.Air & 0xFF) / 255f;
        var overcast = ((view.Air >> 8) & 0xFF) / 255f;
        var grade = ((view.Air >> 16) & 0xFF) / 255f;
        // The sun fades out as it sets and gives no light, and shows no disc, at night.
        var daylight = SunPath.Daylight(view.SunDirection);
        // A compass heading: from north (-z) round towards east (+x).
        var heading = look.CloudHeadingDegrees * MathF.PI / 180;
        return new LookConstants
        {
            SunLight = view.SunColour * look.SunWarmth * daylight,
            WorldSaturation = look.WorldSaturation,
            ShadeCool = look.ShadeCool,
            WorldValue = look.WorldValue,
            SkyLight = view.SkyHorizon * look.SkyLightShare * (1 + overcast),
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
            CloudScale = look.CloudScale,
            CloudStretch = MathF.Max(look.CloudStretch, 0.01f),
            CloudAlongX = MathF.Sin(heading),
            CloudAlongZ = -MathF.Cos(heading),
            GroundHazePerMetre = MathF.Max(look.GroundHaze, 0) / MathF.Max(look.GroundHazeHalfMetres, 0.01f),
            GroundHazeFloor = look.GroundHazeFloor - (float)(view.OriginY / (double)VoxelConstants.CentimetresPerMetre),
            GroundHazeFalloff = 1 / MathF.Max(look.GroundHazeFalloffMetres, 0.01f),
        };
    }
}
