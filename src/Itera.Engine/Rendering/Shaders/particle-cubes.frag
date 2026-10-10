#version 460

// The particle cubes (ParticlePass; particle-cubes.vert): flat faces lit as the world is lit, by the sun (the
// particle's sunlight through the shadow cascades) and by the sky and ground light (scaled by the sky field), plus its
// glow; a particle that gives off its own light
// shows its colour times 1 + glow. Then fog, the tone curve, and the grade. The
// cube writes its depth, so later cubes, sprites, and lines behind it are hidden.
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"

layout(location = 0) flat in vec3 inColour;
layout(location = 1) flat in vec3 inNormal;
layout(location = 2) in vec3 inOffset;
layout(location = 3) flat in vec3 inLight;
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 shaded;
    if (inLight.x < 0.0)
    {
        shaded = inColour * (1.0 + inLight.y);
    }
    else
    {
        float sunlight = max(dot(inNormal, view.sunDirection), 0.0) * inLight.x;
        vec3 light = view.sunColour * sunlight + ambientLight(inNormal, 1.0) * inLight.z;
        shaded = inColour * (light + inLight.y);
    }

    float fog = fogAt(length(inOffset), view.maxDistance);
    shaded = mix(shaded, skyColour(normalize(inOffset)), fog);
    shaded = gradeColour(toneMap(shaded * LOOK.exposure));

    outColor = vec4(shaded, 1.0);
}
