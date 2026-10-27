// Lighting, fog, and the final colour for the world (mesh.frag, world-sky.frag) and the particles: sky and ground light
// with occlusion, fog and haze, the tone curve, and the grade, by the frame's look (look.glsl; RenderLook explains each
// value). Needs view.glsl.
#include "look.glsl"

// Light colours, linear, worked out once a frame (LookConstants): the sky light is the look's share of the sky's horizon
// colour, so it follows the atmosphere, and it grows with the overcast, up to twice as much under a fully overcast sky:
// cloud spreads the light over the whole sky, so shade is lighter. The light from below is the look's ground light plus
// the sunlight the sunlit ground around throws back (its ground bounce of the sun, times the sine of its height), which
// lights walls, trunks, and undersides in shade.

// The haze's share of a surface lost over t metres at density per metre (scaled by each colour's fade): none over the
// look's haze start, and at most its haze cap.
float hazeOver(float t, float density)
{
    return min(1.0 - exp2(-max(t - LOOK.hazeStart, 0.0) * density), LOOK.hazeMost);
}

// How much of a surface at distance t metres from the eye is lost in the fog, 0 to 1, when the view ends at maxT metres:
// the fog that hides the view distance (from the look's fog start), and the haze (the atmosphere's, over the look's
// haze half distance), which at full haze hides half of a surface that far away past the haze start.
float fogAt(float t, float maxT)
{
    return max(smoothstep(LOOK.fogStart * maxT, maxT, t), hazeOver(t, LOOK.hazePerMetre * LOOK.hazeFadeMean));
}

// The haze's density per metre along the ray to a surface at offset from the eye: the atmosphere's, and the look's ground
// haze, haze that lies in low ground, thickest at and below its floor and half as thick every falloff higher. Along the
// ray its density is averaged between the eye's height and the surface's (an exponential in height has an exact mean),
// so a view across a hollow from a hill sees it, and a view down into it from above sees less.
float hazeDensity(vec3 offset)
{
    float density = LOOK.hazePerMetre;
    if (LOOK.groundHazePerMetre > 0.0)
    {
        float eye = max(view.position.y - LOOK.groundHazeFloor, 0.0) * LOOK.groundHazeFalloff;
        float point = max(view.position.y + offset.y - LOOK.groundHazeFloor, 0.0) * LOOK.groundHazeFalloff;
        float rise = point - eye;
        float mean = abs(rise) < 1e-3 ? exp2(-eye) : (exp2(-eye) - exp2(-point)) / (rise * 0.6931472);
        density += LOOK.groundHazePerMetre * mean;
    }

    return density;
}

// The same for a surface at offset from the eye, with the look's ground haze too, for what fades as one colour (the
// particles' sprites, the split tone).
float fogAt(vec3 offset, float maxT)
{
    float t = length(offset);
    return max(smoothstep(LOOK.fogStart * maxT, maxT, t), hazeOver(t, hazeDensity(offset) * LOOK.hazeFadeMean));
}

// The same for each colour of a surface at offset, each faded in the haze by the look's colour fade, for what mixes into
// the sky's colour (air.glsl). Where the look fades every colour alike, each is fogAt's.
vec3 fogAmounts(vec3 offset, float maxT)
{
    float t = length(offset);
    float density = hazeDensity(offset);
    vec3 haze = vec3(
        hazeOver(t, density * LOOK.hazeFade.r),
        hazeOver(t, density * LOOK.hazeFade.g),
        hazeOver(t, density * LOOK.hazeFade.b)
    );
    return max(vec3(smoothstep(LOOK.fogStart * maxT, maxT, t)), haze);
}

// colour (linear, 0 to 1, as written to an 8-bit sRGB image) moved by up to one step of the image either way by a fixed
// triangular noise of the pixel, so smooth gradients of sky and fog show no bands. The step is measured in a square-root
// encoding, close to sRGB's. pixel is the fragment's place in the image (gl_FragCoord.xy).
vec3 ditherColour(vec3 colour, vec2 pixel)
{
    // Two interleaved gradient noises (Jimenez 2014), offset so they are apart, summed into -1 to 1, peaked at 0.
    const vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
    float a = fract(magic.z * fract(dot(pixel, magic.xy)));
    float b = fract(magic.z * fract(dot(pixel + vec2(37.0, 17.0), magic.xy)));
    vec3 encoded = clamp(sqrt(max(colour, 0.0)) + (a + b - 1.0) / 255.0, 0.0, 1.0);
    return encoded * encoded;
}

// A filmic tone curve (Narkowicz's fit of the ACES curve): dark values a little darker, the middle a little brighter,
// and bright light rolled off towards white instead of clipped.
vec3 toneMap(vec3 colour)
{
    return clamp(colour * (2.51 * colour + 0.03) / (colour * (2.43 * colour + 0.59) + 0.14), 0.0, 1.0);
}

// The luma of a linear colour: its dot with these (Rec. 709).
const vec3 LumaWeights = vec3(0.2126, 0.7152, 0.0722);

// The grade (the atmosphere's) desaturates and darkens the picture and shifts it towards the sky's hue, as the look
// says how far; 0 leaves it alone.
vec3 gradeColour(vec3 colour)
{
    vec3 graded = mix(colour, dot(colour, LumaWeights) * LOOK.gradeHue, LOOK.gradeMix);
    return graded * LOOK.gradeScale;
}

// Light from the sky above and the ground below at a surface facing normal: sides get both, and surfaces facing x a
// little more than those facing z (0.9 and 0.8 on flat walls), so shapes still read in shadow; scaled by occlusion.
vec3 ambientLight(vec3 normal, float occlusion)
{
    float side = 1.0 - 0.1 * normal.x * normal.x - 0.2 * normal.z * normal.z;
    return mix(LOOK.groundLight, LOOK.skyLight, 0.5 + 0.5 * normal.y) * side * occlusion;
}

