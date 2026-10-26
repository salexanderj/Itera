// The sprites' sort, back to front (ParticlePass), in set 0 beside particle-data.glsl. particle-simulate.comp lists each
// sprite it draws as a key, its depth bin over its pool index, and counts the sprites of each bin;
// particle-sort-scan.comp turns the counts into each bin's first place, the farthest bin's first; and
// particle-sort-scatter.comp puts each sprite's index at its bin's next place in the sprites' list (spriteList), which
// the draw reads in order. Within a bin the order is the atomics', which may change from frame to frame: a bin is under
// 1 % of the distance deep. Only the compute passes include this file.
#extension GL_EXT_scalar_block_layout : require

// Bins of depth (ParticlePass.SortBins), 0 the farthest; the scan gives each of its 64 invocations as many.
#define PARTICLE_SORT_BINS 1024u
#define PARTICLE_SORT_BINS_EACH 16u

// A key's pool index: the pool holds at most 2^20 particles (Engine:Particles:Capacity), the bin is above it.
#define PARTICLE_INDEX_BITS 20u
#define PARTICLE_INDEX_MASK 0xFFFFFu

layout(scalar, set = 0, binding = 9) buffer ParticleSpriteKeys
{
    uint spriteKeys[];
};

// Each bin's sprite count, cleared each frame; the scan makes it the bin's first place, and the scatter takes places
// from it.
layout(scalar, set = 0, binding = 10) buffer ParticleSortBins
{
    uint spriteBins[];
};
