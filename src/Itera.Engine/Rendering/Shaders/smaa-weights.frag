#version 460
#extension GL_EXT_scalar_block_layout : require

// SMAA's second pass (SmaaPass): for each edge pixel, how much of its neighbours across the edge to blend in, from the
// shape of the edge around it (the searches along it, the search texture, and the area texture), into the weights
// image. Pixels without an edge keep the clear's 0.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outWeights;

layout(set = 0, binding = 0) uniform sampler2D edges;
layout(set = 0, binding = 1) uniform sampler2D area;
layout(set = 0, binding = 2) uniform sampler2D search;

#include "smaa-common.glsl"

void main()
{
    vec2 texcoord = smaaTexcoord();
    vec2 pixcoord;
    vec4 offset[3];
    SMAABlendingWeightCalculationVS(texcoord, pixcoord, offset);
    // SMAA 1x: no subsamples.
    outWeights = SMAABlendingWeightCalculationPS(texcoord, pixcoord, offset, edges, area, search, vec4(0.0));
}
