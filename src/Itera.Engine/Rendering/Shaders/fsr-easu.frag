#version 460
#extension GL_EXT_scalar_block_layout : require

// FSR 1's upscale (EASU, ffx_fsr1.glsl; UpscalePass): the picture drawn at the render scale, whole, into an image the
// size of the screen. The picture is read through a UNORM view of its sRGB image, so its values stay sRGB encoded, as
// FSR 1 expects; the output keeps that encoding, for fsr-rcas.frag.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColour;

layout(set = 0, binding = 0) uniform sampler2D source;

layout(push_constant, scalar) uniform EasuConstants
{
    vec2 inputSize;
    vec2 outputSize;
}
easu;

#define A_GPU 1
#define A_GLSL 1
#include "ffx_a.glsl"

#define FSR_EASU_F 1
AF4 FsrEasuRF(AF2 p)
{
    return textureGather(source, p, 0);
}

AF4 FsrEasuGF(AF2 p)
{
    return textureGather(source, p, 1);
}

AF4 FsrEasuBF(AF2 p)
{
    return textureGather(source, p, 2);
}

#include "ffx_fsr1.glsl"

void main()
{
    // The constants depend only on the sizes; working them out here keeps the push constants two sizes.
    uvec4 con0;
    uvec4 con1;
    uvec4 con2;
    uvec4 con3;
    FsrEasuCon(con0, con1, con2, con3, easu.inputSize.x, easu.inputSize.y, easu.inputSize.x, easu.inputSize.y, easu.outputSize.x, easu.outputSize.y);
    vec3 colour;
    // gl_FragCoord counts from the top-left corner, as the picture's rows do.
    FsrEasuF(colour, uvec2(gl_FragCoord.xy), con0, con1, con2, con3);
    outColour = vec4(colour, 1.0);
}
