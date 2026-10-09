// What SMAA's three passes share (SmaaPass): the push constants (SmaaConstants) and SMAA itself (smaa.glsl), as SMAA
// 1x with the HIGH preset and luma edges. SMAA was written for Direct3D, whose texture origin is the top-left corner, as
// Vulkan's is, so its offsets need no flip: each pass takes its texture coordinate from gl_FragCoord (the pixel's centre,
// counted from the top-left corner) and works out the offsets SMAA's vertex functions would have passed it. Every sampler
// filters linearly and clamps, as SMAA asks. Needs GL_EXT_scalar_block_layout.

layout(push_constant, scalar) uniform SmaaConstants
{
    // 1 / width, 1 / height, width, height of the picture drawn at the render scale.
    vec4 rtMetrics;
    // What the blend pass shows: 0 the anti-aliased picture, 1 its edges, 2 its blend weights.
    uint picture;
}
smaa;

#define SMAA_RT_METRICS smaa.rtMetrics
#define SMAA_GLSL_4
#define SMAA_PRESET_HIGH
#include "smaa.glsl"

// The pixel's texture coordinate, as SMAA's vertex functions take it.
vec2 smaaTexcoord()
{
    return gl_FragCoord.xy * smaa.rtMetrics.xy;
}
