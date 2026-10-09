#version 460

// The particle cubes (ParticlePass): 36 vertices, two triangles for each face of a cube (turns.glsl), for the particle
// the frame's cube list names at gl_InstanceIndex. The cube is its recipe's size blended over its life, times its own
// size, shrinking over the last part of its life with FadeOut, and turned about an axis from its seed by an angle that
// grows quickly and settles, so a cube tumbles as it flies and lies still once it rests. Placed with the frame's view
// as mesh.vert places a vertex (reverse depth, view.glsl).
#include "view.glsl"
#include "lighting.glsl"
#include "particle-data.glsl"

layout(location = 0) flat out vec3 outColour;
layout(location = 1) flat out vec3 outNormal;
layout(location = 2) out vec3 outOffset;
layout(location = 3) flat out vec3 outLight;

// The faces' normals in BoxCorners's order.
const vec3 FaceNormals[6] = vec3[](
    vec3(-1.0, 0.0, 0.0), vec3(1.0, 0.0, 0.0),
    vec3(0.0, -1.0, 0.0), vec3(0.0, 1.0, 0.0),
    vec3(0.0, 0.0, -1.0), vec3(0.0, 0.0, 1.0));

void main()
{
    Particle particle = particles[cubeList[gl_InstanceIndex]];
    ParticleRecipe recipe = recipeOf(view.slot, particle.packed);
    float life = clamp(particle.age / particle.lifetime, 0.0, 1.0);
    float size = mix(recipe.sizeStart, recipe.sizeEnd, life) * particle.size;
    if ((recipe.flags & PARTICLE_FLAG_FADE_OUT) != 0u)
    {
        size *= 1.0 - smoothstep(1.0 - recipe.fadeOutShare, 1.0, life);
    }

    // The turn: the angle's rate falls from the spin to 0 over about half a second.
    uint seed = particle.seed;
    vec3 axis = normalize(vec3(particleRandom(seed), particleRandom(seed), particleRandom(seed)) - 0.5 + 1e-4);
    float angle = particle.spin * (1.0 - exp(-2.0 * particle.age)) * 0.5 + 6.2831853 * particleRandom(seed);
    vec4 turn = vec4(axis * sin(0.5 * angle), cos(0.5 * angle));

    uint corner = BoxCorners[gl_VertexIndex];
    vec3 unit = vec3(corner & 1u, (corner >> 1) & 1u, (corner >> 2) & 1u) - 0.5;
    vec3 offset = particle.position - view.position + turnBy(turn, unit * size);
    float depth = dot(offset, view.forward);
    float x = dot(offset, view.right) / dot(view.right, view.right);
    float y = dot(offset, view.up) / dot(view.up, view.up);
    gl_Position = vec4(x, y, NearMetres, depth);

    vec3 colour = mix(unpackUnorm4x8(recipe.colourStart).rgb, unpackUnorm4x8(recipe.colourEnd).rgb, life);
    outColour = colour * unpackUnorm4x8(particle.colour).rgb;
    outNormal = turnBy(turn, FaceNormals[gl_VertexIndex / 6]);
    outOffset = offset;
    // x: the sunlight, 0 to 1, or -1 for a particle that gives off its own light; y: its glow; z: its sky light, 0 to 1
    // (particle-simulate.comp).
    bool lit = (recipe.flags & PARTICLE_FLAG_LIT) != 0u;
    outLight = vec3(
        lit ? float(particle.packed >> PARTICLE_SUN_SHIFT) / 255.0 : -1.0,
        recipe.glow,
        float((particle.packed >> PARTICLE_SKY_SHIFT) & 255u) / 255.0);
}
