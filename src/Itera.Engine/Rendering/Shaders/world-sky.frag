#version 460

// The sky (MeshPass): a pixel the world's meshes leave empty shows the sky colour of its ray (sky.glsl), with the look's
// clouds over it (clouds.glsl) and the haze glows' light along it, then the tone curve, the grade, and the dither.
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"
#include "mesh-data.glsl"
#include "clouds.glsl"

layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 ray = viewRay(inNdc);
    vec3 colour = gradeColour(toneMap((skyClouds(ray, skyColour(ray)) + hazeGlows(ray, 1e7)) * LOOK.exposure));

    outColor = vec4(ditherColour(colour, gl_FragCoord.xy), 1.0);
}
