#version 460
#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_control_flow_attributes : require

// FSR 1's upscale (EASU, ffx_fsr1.glsl; UpscalePass): the picture drawn at the render scale, in the top-left part of the
// source image, into an image the size of the screen. The picture is read through a UNORM view of its sRGB image, so its
// values stay sRGB encoded, as FSR 1 expects; the output keeps that encoding, for fsr-rcas.frag.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColour;

layout(set = 0, binding = 0) uniform sampler2D source;

layout(push_constant, scalar) uniform EasuConstants
{
    // FsrEasuCon's four constants, worked out on the CPU (EasuConstants.Create); the shader takes their bits.
    vec4 con0;
    vec4 con1;
    vec4 con2;
    vec4 con3;
    // The source image's size, and the picture's in its top-left part, in texels.
    vec2 sourceSize;
    vec2 pictureSize;
}
easu;

#define A_GPU 1
#define A_GLSL 1
#include "ffx_a.glsl"

// True when the pixel's taps reach past the picture's right or bottom edge, where the source image holds no picture.
bool nearPictureEdge;

// The four texels textureGather(source, p, channel) reads, each held inside the picture as the sampler's clamp holds
// them inside the image: the picture's edge texels stand in past its edges.
AF4 gatherInPicture(AF2 p, int channel)
{
    ivec2 t = ivec2(floor(p * easu.sourceSize - 0.5));
    ivec2 last = ivec2(easu.pictureSize) - 1;
    return AF4(
        texelFetch(source, clamp(t + ivec2(0, 1), ivec2(0), last), 0)[channel],
        texelFetch(source, clamp(t + ivec2(1, 1), ivec2(0), last), 0)[channel],
        texelFetch(source, clamp(t + ivec2(1, 0), ivec2(0), last), 0)[channel],
        texelFetch(source, clamp(t, ivec2(0), last), 0)[channel]);
}

#define FSR_EASU_F 1
AF4 FsrEasuRF(AF2 p)
{
    [[dont_flatten]] if (nearPictureEdge)
    {
        return gatherInPicture(p, 0);
    }

    return textureGather(source, p, 0);
}

AF4 FsrEasuGF(AF2 p)
{
    [[dont_flatten]] if (nearPictureEdge)
    {
        return gatherInPicture(p, 1);
    }

    return textureGather(source, p, 1);
}

AF4 FsrEasuBF(AF2 p)
{
    [[dont_flatten]] if (nearPictureEdge)
    {
        return gatherInPicture(p, 2);
    }

    return textureGather(source, p, 2);
}

#include "ffx_fsr1.glsl"

void main()
{
    uvec4 con0 = floatBitsToUint(easu.con0);
    uvec4 con1 = floatBitsToUint(easu.con1);
    uvec4 con2 = floatBitsToUint(easu.con2);
    uvec4 con3 = floatBitsToUint(easu.con3);
    // gl_FragCoord counts from the top-left corner, as the picture's rows do.
    uvec2 ip = uvec2(gl_FragCoord.xy);
    // FsrEasuF's 'f' texel. Its taps reach two texels right of it and below it; one more keeps the test safe from
    // rounding, since inside the picture the clamped reads give the same texels as the gathers.
    vec2 f = floor(vec2(ip) * easu.con0.xy + easu.con0.zw);
    nearPictureEdge = any(greaterThanEqual(f + 3.0, easu.pictureSize));
    vec3 colour;
    FsrEasuF(colour, ip, con0, con1, con2, con3);
    outColour = vec4(colour, 1.0);
}
