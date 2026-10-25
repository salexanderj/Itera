#version 460

// The sky (MeshPass): a pixel the world's meshes leave empty shows the sky colour of its ray (sky.glsl), with the look's
// clouds over it, and the tone curve and the grade.
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"

layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 ray = viewRay(inNdc);
    vec3 colour = gradeColour(toneMap(skyClouds(ray, skyColour(ray)) * LOOK.exposure));

    outColor = vec4(colour, 1.0);
}
