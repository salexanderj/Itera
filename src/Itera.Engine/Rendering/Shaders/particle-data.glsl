// The particles (ParticlePass): the pool, the frame's lists of cubes and sprites to draw, the counters with the two
// indirect draws, and each frame slot's recipes, bursts, and attractors, in set 0. The structs mirror
// Itera.Engine.Rendering.GpuData.GpuParticle, GpuParticleRecipe, GpuParticleBurst, and ParticleCounters member for
// member; ParticlePassTests checks them. The compute passes define PARTICLE_WRITES before including this file; the
// vertex shaders do not, so every buffer is read only there (a vertex shader may not write storage buffers without
// vertexPipelineStoresAndAtomics).
#extension GL_EXT_scalar_block_layout : require
#include "turns.glsl"

#ifdef PARTICLE_WRITES
#define PARTICLE_ACCESS
#else
#define PARTICLE_ACCESS readonly
#endif

// Recipes and attractors each frame slot holds (ParticleRecipes.MaxRecipes, ParticleEmitter.MaxAttractors).
#define PARTICLE_MAX_RECIPES 128u
#define PARTICLE_MAX_ATTRACTORS 64u

// GpuParticle.Packed: the recipe, the attractor, the resting bit, the new bit, the sky light, and the sunlight (bytes,
// 0 to 255).
#define PARTICLE_RECIPE_MASK 255u
#define PARTICLE_ATTRACTOR_SHIFT 8u
#define PARTICLE_ATTRACTOR_MASK 63u
#define PARTICLE_RESTING 0x4000u
#define PARTICLE_SKY_SHIFT 16u
#define PARTICLE_SUN_SHIFT 24u
// Made this frame, in the frame's origin already: the simulate pass moves every other particle by push.shift, then
// clears the bit.
#define PARTICLE_NEW 0x8000u

// ParticleDraw, ParticleCollision, ParticleStretch, ParticleTraits, ParticleShape.
#define PARTICLE_DRAW_CUBE 0u
#define PARTICLE_COLLISION_NONE 0u
#define PARTICLE_COLLISION_BOUNCE 1u
#define PARTICLE_COLLISION_STICK 2u
#define PARTICLE_COLLISION_DIE 3u
#define PARTICLE_STRETCH_VELOCITY 1u
#define PARTICLE_STRETCH_SEGMENT 2u
#define PARTICLE_FLAG_LIT 1u
#define PARTICLE_FLAG_FADE_OUT 2u
#define PARTICLE_SHAPE_SPHERE 1u
#define PARTICLE_SHAPE_SEGMENT 2u
#define PARTICLE_SHAPE_DISC 3u

struct Particle
{
    vec3 position;
    float age;
    vec3 velocity;
    float lifetime;
    uint packed;
    uint colour;
    uint seed;
    float size;
    vec3 end;
    float spin;
};

struct ParticleRecipe
{
    uint draw;
    uint collision;
    uint stretch;
    uint flags;
    uint colourStart;
    uint colourEnd;
    float glow;
    float cover;
    float speedMin;
    float speedMax;
    float spreadCosine;
    float lifetimeMin;
    float lifetimeMax;
    float sizeStart;
    float sizeEnd;
    float sizeJitter;
    float colourJitter;
    vec3 gravity;
    vec3 up;
    float drag;
    float restitution;
    float friction;
    float spin;
    float stretchSeconds;
    float attraction;
    float fadeIn;
    float fadeOutShare;
};

struct ParticleBurst
{
    vec3 origin;
    uint prefix;
    vec3 direction;
    uint count;
    vec3 end;
    float radius;
    vec3 velocity;
    uint first;
    uint packed;
    uint colour;
    uint seed;
};

struct ParticleCounters
{
    uint cubeVertexCount;
    uint cubeInstanceCount;
    uint cubeFirstVertex;
    uint cubeFirstInstance;
    uint spriteVertexCount;
    uint spriteInstanceCount;
    uint spriteFirstVertex;
    uint spriteFirstInstance;
    uint living;
    uint overwritten;
    uint resting;
    uint spare;
};

layout(scalar, set = 0, binding = 0) PARTICLE_ACCESS buffer Particles
{
    Particle particles[];
};

layout(scalar, set = 0, binding = 1) PARTICLE_ACCESS buffer ParticleCubes
{
    uint cubeList[];
};

// One element: an array, so the layout test can compare it with the C# struct.
layout(scalar, set = 0, binding = 2) PARTICLE_ACCESS buffer ParticleCounterBlock
{
    ParticleCounters counters[];
};

layout(scalar, set = 0, binding = 3) readonly buffer ParticleRecipes
{
    ParticleRecipe recipes[];
};

layout(scalar, set = 0, binding = 4) readonly buffer ParticleBursts
{
    ParticleBurst bursts[];
};

layout(scalar, set = 0, binding = 5) readonly buffer ParticleAttractors
{
    vec4 attractors[];
};

// The frame's sprites to draw, back to front (particle-sort.glsl).
layout(scalar, set = 0, binding = 6) PARTICLE_ACCESS buffer ParticleSprites
{
    uint spriteList[];
};

// The recipe id of a particle's packed word in frame slot slot's table.
ParticleRecipe recipeOf(uint slot, uint packed)
{
    return recipes[slot * PARTICLE_MAX_RECIPES + (packed & PARTICLE_RECIPE_MASK)];
}

// A 32-bit hash (PCG's output function over one LCG step).
uint particleHash(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// The next random value of seed, 0 to 1 (below 1).
float particleRandom(inout uint seed)
{
    seed = particleHash(seed);
    return float(seed >> 8u) * (1.0 / 16777216.0);
}

// A unit vector at right angles to the unit vector n.
vec3 particlePerpendicular(vec3 n)
{
    vec3 other = abs(n.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    return normalize(cross(n, other));
}
