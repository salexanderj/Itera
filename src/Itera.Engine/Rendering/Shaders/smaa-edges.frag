#version 460
#extension GL_EXT_scalar_block_layout : require

// SMAA's first pass (SmaaPass): the edges of the picture the world, models, particles, and lines drew, found from the
// luma of its sRGB-encoded values (read through the UNORM view, as SMAA asks), into the edges image: red where the
// pixel's left edge is one, green where its top edge is. A pixel with neither is discarded and keeps the clear's 0.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec2 outEdges;

layout(set = 0, binding = 0) uniform sampler2D scene;

#include "smaa-common.glsl"

void main()
{
    vec2 texcoord = smaaTexcoord();
    vec4 offset[3];
    SMAAEdgeDetectionVS(texcoord, offset);
    outEdges = SMAALumaEdgeDetectionPS(texcoord, offset, scene);
}
