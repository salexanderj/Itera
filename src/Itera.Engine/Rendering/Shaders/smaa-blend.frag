#version 460
#extension GL_EXT_scalar_block_layout : require

// SMAA's last pass (SmaaPass): each pixel blended with its neighbours by the weights, into the swapchain image (render
// scale 1) or FSR 1's source image (below 1). The picture is read through its own view, so an
// sRGB image gives linear colour and the blend happens in linear light, as SMAA asks; the target has the same format,
// so it encodes the result as the picture was. The console's `aa edges` and `aa weights` show the edges or the weights
// over a dimmed picture instead.
layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColour;

layout(set = 0, binding = 0) uniform sampler2D scene;
layout(set = 0, binding = 1) uniform sampler2D weights;
layout(set = 0, binding = 2) uniform sampler2D edges;

#include "smaa-common.glsl"

void main()
{
    vec2 texcoord = smaaTexcoord();
    vec4 offset;
    SMAANeighborhoodBlendingVS(texcoord, offset);
    vec3 colour = SMAANeighborhoodBlendingPS(texcoord, offset, scene, weights).rgb;

    if (smaa.picture == 1u)
    {
        // Left edges red, top edges green.
        vec2 e = texelFetch(edges, ivec2(gl_FragCoord.xy), 0).rg;
        colour = any(greaterThan(e, vec2(0.0))) ? vec3(e, 0.0) : colour * 0.25;
    }
    else if (smaa.picture == 2u)
    {
        // Blending with the pixels above and below red, left and right green.
        vec4 w = texelFetch(weights, ivec2(gl_FragCoord.xy), 0);
        vec2 shown = vec2(w.r + w.g, w.b + w.a);
        colour = any(greaterThan(shown, vec2(0.0))) ? vec3(min(shown * 2.0, 1.0), 0.0) : colour * 0.25;
    }

    outColour = vec4(colour, 1.0);
}
