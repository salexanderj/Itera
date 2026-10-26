// Lighting, fog, and the final colour for the world (mesh.frag, world-sky.frag) and the particles: sky and ground light
// with occlusion, fog and haze, the tone curve, and the grade, by the frame's look (look.glsl; RenderLook explains each
// value). Needs view.glsl.
#include "look.glsl"

// Light colours, linear, worked out once a frame (LookConstants): the sky light is the look's share of the sky's horizon
// colour, so it follows the atmosphere, and it grows with the overcast, up to twice as much under a fully overcast sky:
// cloud spreads the light over the whole sky, so shade is lighter. The light from below is the look's ground light plus
// the sunlight the sunlit ground around throws back (its ground bounce of the sun, times the sine of its height), which
// lights walls, trunks, and undersides in shade.

// How much of a surface at distance t metres from the eye is lost in the fog, 0 to 1, when the view ends at maxT metres:
// the fog that hides the view distance (from the look's fog start), and the haze (the atmosphere's, over the look's
// haze half distance), which at full haze hides half of a surface that far away.
float fogAt(float t, float maxT)
{
    return max(smoothstep(LOOK.fogStart * maxT, maxT, t), 1.0 - exp2(-t * LOOK.hazePerMetre));
}

// The same for a surface at offset from the eye, with the look's ground haze too: haze that lies in low ground, thickest
// at and below its floor and half as thick every falloff higher. Along the ray its density is averaged between the eye's
// height and the surface's (an exponential in height has an exact mean), so a view across a hollow from a hill sees
// it, and a view down into it from above sees less.
float fogAt(vec3 offset, float maxT)
{
    float t = length(offset);
    float density = LOOK.hazePerMetre;
    if (LOOK.groundHazePerMetre > 0.0)
    {
        float eye = max(view.position.y - LOOK.groundHazeFloor, 0.0) * LOOK.groundHazeFalloff;
        float point = max(view.position.y + offset.y - LOOK.groundHazeFloor, 0.0) * LOOK.groundHazeFalloff;
        float rise = point - eye;
        float mean = abs(rise) < 1e-3 ? exp2(-eye) : (exp2(-eye) - exp2(-point)) / (rise * 0.6931472);
        density += LOOK.groundHazePerMetre * mean;
    }

    return max(smoothstep(LOOK.fogStart * maxT, maxT, t), 1.0 - exp2(-t * density));
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

