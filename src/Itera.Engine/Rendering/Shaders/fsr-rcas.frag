#version 460
#extension GL_EXT_scalar_block_layout : require

// FSR 1's sharpening (RCAS, ffx_fsr1.glsl; UpscalePass): the upscaled picture from fsr-easu.frag, sRGB encoded, into
// the swapchain image. An sRGB swapchain image encodes what it is
// given, so the colour is made linear first.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColour;

layout(set = 0, binding = 0) uniform sampler2D source;

layout(push_constant, scalar) uniform RcasConstants
{
    uint width;
    uint height;
    float sharpness;
    uint linearOutput;
}
rcas;

#define A_GPU 1
#define A_GLSL 1
#include "ffx_a.glsl"

#define FSR_RCAS_F 1
AF4 FsrRcasLoadF(ASU2 p)
{
    // The 3x3 neighbourhood reaches one pixel past the edges; the edge pixel stands in there.
    return texelFetch(source, clamp(p, ivec2(0), ivec2(rcas.width, rcas.height) - 1), 0);
}

void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b)
{
}

#include "ffx_fsr1.glsl"

vec3 srgbToLinear(vec3 c)
{
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), greaterThan(c, vec3(0.04045)));
}

void main()
{
    uvec4 con;
    FsrRcasCon(con, rcas.sharpness);
    vec3 colour;
    FsrRcasF(colour.r, colour.g, colour.b, uvec2(gl_FragCoord.xy), con);
    colour = clamp(colour, 0.0, 1.0);

    outColour = vec4(rcas.linearOutput != 0u ? srgbToLinear(colour) : colour, 1.0);
}
