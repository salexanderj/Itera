#version 460

// The sky (MeshPass): a pixel the world's meshes leave empty shows the sky colour of its ray (sky.glsl) with the tone
// curve and the grade.
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"

layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 colour = gradeColour(toneMap(skyColour(viewRay(inNdc)) * Exposure));

    outColor = vec4(colour, 1.0);
}
