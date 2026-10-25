#version 460

// The particle sprites (ParticlePass): six vertices, two triangles, for the particle the frame's sprite list names at
// gl_InstanceIndex. The quad lies across the view: round sprites face the camera, and stretched ones run along an axis
// (their velocity on screen, trailing behind them, or the segment from their start to their end) with round ends, as
// long as the axis plus a radius at each end and a radius wide. The colour is the recipe's blended over its life times
// the burst's tint, lit by the sun and sky or its own light with glow, faded in and out, faded where the fog is thick
// and right in front of the camera, and put through the tone curve and the grade here, once per vertex;
// particle-sprites.frag only shapes it.
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"
#include "particle-data.glsl"

layout(location = 0) out vec2 outLocal;
layout(location = 1) flat out float outHalfLength;
layout(location = 2) flat out vec4 outColour;
layout(location = 3) flat out float outCover;

const vec2 QuadCorners[6] = vec2[](
    vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(1.0, 1.0),
    vec2(-1.0, -1.0), vec2(1.0, 1.0), vec2(-1.0, 1.0));

void main()
{
    Particle particle = particles[spriteList[gl_InstanceIndex]];
    ParticleRecipe recipe = recipeOf(view.slot, particle.packed);
    float life = clamp(particle.age / particle.lifetime, 0.0, 1.0);
    float radius = max(mix(recipe.sizeStart, recipe.sizeEnd, life) * particle.size, 1e-4);

    vec3 centre = particle.position;
    vec3 axis = normalize(view.right);
    float halfLength = 0.0;
    if (recipe.stretch == PARTICLE_STRETCH_SEGMENT)
    {
        vec3 span = particle.end - particle.position;
        float length2 = dot(span, span);
        centre += 0.5 * span;
        axis = length2 > 1e-8 ? span * inversesqrt(length2) : axis;
        halfLength = 0.5 * sqrt(length2);
    }
    else if (recipe.stretch == PARTICLE_STRETCH_VELOCITY)
    {
        vec3 toward = viewRayTo(particle.position - view.position);
        vec3 across = particle.velocity - dot(particle.velocity, toward) * toward;
        float length2 = dot(across, across);
        if (length2 > 1e-6)
        {
            vec3 trail = across * recipe.stretchSeconds;
            centre -= 0.5 * trail;
            axis = across * inversesqrt(length2);
            halfLength = 0.5 * length(trail);
        }
    }

    vec3 side = cross(axis, viewOrthographic() ? view.forward : centre - view.position);
    float sideLength = length(side);
    side = sideLength > 1e-6 ? side / sideLength : normalize(view.up);

    vec2 corner = QuadCorners[gl_VertexIndex];
    vec3 point = centre + axis * (corner.x * (halfLength + radius)) + side * (corner.y * radius);
    // Moved towards the camera by its radius (never past half the way, or half the way to an orthographic view's near
    // end), so a round sprite that sits on a surface or in a hole is not cut off by it along a hard line.
    if (viewOrthographic())
    {
        point -= view.forward * clamp(0.5 * (dot(point - view.position, view.forward) - view.near), 0.0, radius);
    }
    else
    {
        vec3 toward = view.position - point;
        float away = length(toward);
        point += toward / max(away, 1e-4) * min(radius, 0.5 * away);
    }

    vec3 offset = point - view.position;
    // How far in front of the camera, or of an orthographic view's near end.
    float depth = dot(offset, view.forward) - (viewOrthographic() ? view.near : 0.0);
    gl_Position = viewClip(offset);
    outLocal = vec2(corner.x * (halfLength + radius) / radius, corner.y);
    outHalfLength = halfLength / radius;

    vec4 colour = mix(unpackUnorm4x8(recipe.colourStart), unpackUnorm4x8(recipe.colourEnd), life)
        * unpackUnorm4x8(particle.colour);
    vec3 light = vec3(1.0 + recipe.glow);
    if ((recipe.flags & PARTICLE_FLAG_LIT) != 0u)
    {
        float sunlight = float(particle.packed >> PARTICLE_SUN_SHIFT) / 255.0;
        float sky = float((particle.packed >> PARTICLE_SKY_SHIFT) & 255u) / 255.0;
        light = view.sunColour * (0.6 * sunlight * LOOK.daylight) + ambientLight(vec3(0.0, 1.0, 0.0), 1.0) * sky
            + recipe.glow;
    }

    float opacity = colour.a;
    if (recipe.fadeIn > 0.0)
    {
        opacity *= smoothstep(0.0, recipe.fadeIn, life);
    }

    if ((recipe.flags & PARTICLE_FLAG_FADE_OUT) != 0u)
    {
        opacity *= 1.0 - smoothstep(1.0 - recipe.fadeOutShare, 1.0, life);
    }

    float fog = fogAt(centre - view.position, view.maxDistance);
    // Faded right in front of the camera, where a sprite would fill the screen.
    opacity *= (1.0 - fog) * smoothstep(0.1, 0.5, depth + radius);
    outColour = vec4(gradeColour(toneMap(colour.rgb * light * LOOK.exposure)), opacity);
    outCover = recipe.cover;
}
