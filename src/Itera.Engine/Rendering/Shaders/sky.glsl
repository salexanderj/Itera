// Sky colour for a view direction: a gradient from the horizon colour to a deeper zenith, a darker colour below the
// horizon, the sun's disc in the sunlight's colour, and the glow round it, as the frame's look and atmosphere make them
// (look.glsl, worked out once a frame: an overcast sky flattens the gradient towards the horizon colour and hides the
// disc). The fog takes this colour behind a surface (viewRayTo), so the haze glows towards the sun too. Needs view.glsl.
#include "look.glsl"

vec3 skyColour(vec3 direction)
{
    vec3 colour = direction.y >= 0.0
        ? mix(view.skyHorizon, LOOK.zenith, sqrt(direction.y))
        : mix(view.skyHorizon, LOOK.below, sqrt(-direction.y));
    float sun = max(dot(direction, view.sunDirection), 0.0);
    return colour + LOOK.sunDisc * pow(sun, LOOK.sunDiscSharpness) + LOOK.sunGlow * pow(sun, LOOK.sunGlowSharpness);
}

// A hash of a lattice point of the clouds' plane, 0 to 1.
float skyHash(ivec2 p)
{
    uint h = uint(p.x) * 0x8DA6B343u ^ uint(p.y) * 0xD8163841u;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 12;
    return float(h >> 8) * (1.0 / 16777216.0);
}

// Value noise over the clouds' plane, 0 to 1.
float skyValueNoise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 f = p - cell;
    vec2 u = f * f * (3.0 - 2.0 * f);
    ivec2 c = ivec2(cell);
    float near = mix(skyHash(c), skyHash(c + ivec2(1, 0)), u.x);
    float far = mix(skyHash(c + ivec2(0, 1)), skyHash(c + ivec2(1, 1)), u.x);
    return mix(near, far, u.y);
}

// Three octaves of value noise, each turned and moved from the last so no lattice lines up, 0 to 1. A fourth looked the
// same on soft streaks and cost about a fifth more.
float skyCloudNoise(vec2 p)
{
    float sum = 0.0;
    float weight = 0.5;
    for (int octave = 0; octave < 3; octave++)
    {
        sum += weight * skyValueNoise(p);
        p = mat2(1.6, 1.2, -1.2, 1.6) * p + vec2(17.3, -9.1);
        weight *= 0.5;
    }

    return sum / 0.875;
}

// The sky colour of direction with the look's clouds over it (the sky's own pass, world-sky.frag; the fog takes the sky
// without them): streaks of noise on a plane over the eye, stretched along the look's heading, covering the look's
// share of the sky, in the clouds' colour lit by the sun's glow, thinning to none at the horizon so the fogged far ground
// still meets the sky. None while the cover is 0.
vec3 skyClouds(vec3 direction, vec3 colour)
{
    if (LOOK.cloudCover <= 0.0 || direction.y <= 0.0)
    {
        return colour;
    }

    vec2 plane = direction.xz / (direction.y + 0.1);
    vec2 along = vec2(LOOK.cloudAlongX, LOOK.cloudAlongZ);
    vec2 p = vec2(dot(plane, along) / LOOK.cloudStretch, dot(plane, vec2(-along.y, along.x))) * LOOK.cloudScale;
    float edge = 1.0 - LOOK.cloudCover;
    float density = smoothstep(edge, edge + 0.3, skyCloudNoise(p)) * smoothstep(0.0, 0.3, direction.y)
        * LOOK.cloudOpacity;
    float sun = max(dot(direction, view.sunDirection), 0.0);
    vec3 cloud = LOOK.cloudColour + 0.5 * LOOK.sunGlow * pow(sun, LOOK.sunGlowSharpness);
    return mix(colour, cloud, density);
}
