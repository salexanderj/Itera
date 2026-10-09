// Lighting, fog, and the final colour for the world (mesh.frag, world-sky.frag) and the particles: sky and ground light
// with occlusion, fog and haze, the tone curve, and the grade. Needs view.glsl.

// Light colours, linear. Sunlight comes from the atmosphere (view.sunColour). The sky light is a share of the sky's
// horizon colour, so it follows the atmosphere too, and it grows with the overcast (view.air's y), up to twice as much
// under a fully overcast sky: cloud spreads the light over the whole sky, so shade is lighter. The light from below is the ground's own plus the sunlight the sunlit ground around
// throws back (GroundBounce of the sun, times the sine of its height), which lights walls, trunks, and undersides in
// shade. These raised the sky light by 25 % over an earlier version and added the overcast and the bounce: shade about
// a third brighter.
const vec3 SkyLightShare = vec3(0.6875, 0.75, 0.875);
const vec3 GroundLight = vec3(0.2, 0.18, 0.14);
const float GroundBounce = 0.2;

// The least share of the sky and ground light that occlusion keeps (mesh.frag's openness): 0.6 before the
// dark-fantasy look's deeper crevices.
const float OcclusionFloor = 0.35;

// Haze (view.air's x): fog that thickens with distance on top of the fog that hides the view distance. At full haze,
// half of a surface this far away, in metres, is lost in it.
const float HazeHalfMetres = 60.0;

// Exposure before the tone curve.
const float Exposure = 0.8;

// How much of a surface at distance t metres from the eye is lost in the fog, 0 to 1, when the view ends at maxT metres.
float fogAt(float t, float maxT)
{
    float haze = unpackUnorm4x8(view.air).x;
    return max(smoothstep(0.6 * maxT, maxT, t), 1.0 - exp2(-t * haze / HazeHalfMetres));
}

// A filmic tone curve (Narkowicz's fit of the ACES curve): dark values a little darker, the middle a little brighter,
// and bright light rolled off towards white instead of clipped.
vec3 toneMap(vec3 colour)
{
    return clamp(colour * (2.51 * colour + 0.03) / (colour * (2.43 * colour + 0.59) + 0.14), 0.0, 1.0);
}

// The grade (view.air's z) desaturates and darkens the picture and shifts it towards the sky's hue; 0 leaves it alone.
vec3 gradeColour(vec3 colour)
{
    float grade = unpackUnorm4x8(view.air).z;
    const vec3 luma = vec3(0.2126, 0.7152, 0.0722);
    vec3 hue = view.skyHorizon / max(dot(view.skyHorizon, luma), 0.01);
    vec3 graded = mix(colour, dot(colour, luma) * hue, 0.6 * grade);
    return graded * (1.0 - 0.3 * grade);
}

// How much the overcast raises the sky light: 1 under a clear sky, 2 under a fully overcast one.
float overcastLight()
{
    return 1.0 + unpackUnorm4x8(view.air).y;
}

// The sunlight the ground throws back up: GroundBounce of the sun, times the sine of the sun's height.
float groundBounce()
{
    return GroundBounce * max(view.sunDirection.y, 0.0);
}

// Light from the sky above and the ground below at a surface facing normal: sides get both, and surfaces facing x a
// little more than those facing z (0.9 and 0.8 on flat walls), so shapes still read in shadow; scaled by occlusion.
vec3 ambientLight(vec3 normal, float occlusion)
{
    vec3 skyLight = view.skyHorizon * SkyLightShare * overcastLight();
    vec3 groundLight = GroundLight + view.sunColour * groundBounce();
    float side = 1.0 - 0.1 * normal.x * normal.x - 0.2 * normal.z * normal.z;
    return mix(groundLight, skyLight, 0.5 + 0.5 * normal.y) * side * occlusion;
}

