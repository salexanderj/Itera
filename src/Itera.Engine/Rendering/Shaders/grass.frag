#version 460

// The grass's blades (GrassDrawPass), lit as the ground is but cheaply: the sun through one filtered lookup
// of the shadow cascade that holds the pixel (the ground takes 24 in the near one), the sky and ground light scaled by
// how much of the sky the air there sees (the sky field), no point lights, then the fog, the tone curve, the grade, and
// the split tone as mesh-shade.glsl's shadeSurface gives every surface of the world, so the blades sit in the ground's
// light. Opaque: it writes depth and discards nothing, so the depth test runs before it shades.
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"
#include "mesh-data.glsl"
#include "mesh-light.glsl"

layout(location = 0) in vec3 inOffset;
layout(location = 1) in vec3 inColour;
layout(location = 2) in vec3 inNormal;
layout(location = 3) in vec3 inGround;
// A far blade's shadow and sky from its corner (grass.vert); below 0 for a near blade, read here.
layout(location = 4) flat in vec2 inLight;
layout(location = 0) out vec4 outColor;

const vec3 Luma = vec3(0.2126, 0.7152, 0.0722);

void main()
{
    vec3 world = view.position + inOffset;
    vec3 normal = normalize(inNormal);
    vec3 ground = normalize(inGround);
    float depth = dot(inOffset, view.forward);
    float shadow = inLight.x >= 0.0 ? inLight.x : sunShadowOnce(view.slot, world, ground, viewCascadeDepth(depth));
    float sunlight = max(dot(normal, view.sunDirection), 0.0) * shadow;
    float sky = max(inLight.y >= 0.0 ? inLight.y : skyVisibility(view.slot, world, ground), LOOK.skyFloor);
    vec3 light = LOOK.sunLight * sunlight + ambientLight(normal, 1.0) * LOOK.shadeCool * sky;
    vec3 shaded = inColour * light;

    // As shadeSurface finishes a surface.
    float fog = fogAt(inOffset, view.maxDistance);
    shaded = mix(shaded, skyColour(viewRayTo(inOffset)), fog);
    vec3 graded = gradeColour(toneMap(shaded * LOOK.exposure));
    vec3 split = mix(
        LOOK.gradeShadows,
        LOOK.gradeLights,
        smoothstep(LOOK.shadowsBelow, LOOK.lightsAbove, dot(graded, Luma)));
    graded *= mix(split, vec3(1.0), fog);
    outColor = vec4(min(graded, vec3(1.0)), 1.0);
}
