#version 460

// The particle sprites (ParticlePass; particle-sprites.vert): a soft round blob, or a capsule for a stretched sprite,
// whose opacity falls from its axis to its edge, premultiplied: the colour times the opacity is added, and the
// picture behind is dimmed by the opacity times the recipe's cover (0 adds light, 1 covers).
layout(location = 0) in vec2 inLocal;
layout(location = 1) flat in float inHalfLength;
layout(location = 2) flat in vec4 inColour;
layout(location = 3) flat in float inCover;
layout(location = 0) out vec4 outColor;

void main()
{
    vec2 fromAxis = vec2(max(abs(inLocal.x) - inHalfLength, 0.0), inLocal.y);
    float r2 = dot(fromAxis, fromAxis);
    if (r2 >= 1.0)
    {
        discard;
    }

    float soft = 1.0 - r2;
    float opacity = inColour.a * soft * soft;
    outColor = vec4(inColour.rgb * opacity, opacity * inCover);
}
