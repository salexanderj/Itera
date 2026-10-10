#version 460

// The aimed block's highlight: a glow along the box's edges, falling to a third edgeMetres in from them, and a faint one
// over its faces where they turn from the eye, pulsing slowly; added to the picture (the pass blends additively).
#include "../view.glsl"
#include "aim-highlight-data.glsl"

layout(location = 0) in vec3 inOffset;
layout(location = 1) in vec3 inLocal;
layout(location = 2) flat in vec3 inNormal;
layout(location = 3) flat in vec3 inAcross;
layout(location = 0) out vec4 outColor;

void main()
{
    AimHighlightData data = highlights[view.slot];
    // How far the point lies from its face's nearest edge, in metres: the two coordinates along the face count, the one
    // across it (0 or 1 there) is put out of reach.
    float scale = length(data.transform[0].xyz);
    vec3 toEdges = min(inLocal, 1.0 - inLocal) * scale + inAcross * 1e3;
    float edge = min(toEdges.x, min(toEdges.y, toEdges.z));
    float glow = exp(-edge / max(data.edgeMetres, 1e-4));
    float facing = abs(dot(normalize(-inOffset), inNormal));
    float sheen = 0.15 * (1.0 - facing) * (1.0 - facing);
    float pulse = 0.8 + 0.2 * sin(data.seconds * 4.0);
    outColor = vec4(data.colour.rgb * (data.colour.a * (glow + sheen) * pulse), 0.0);
}
