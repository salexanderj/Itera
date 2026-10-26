// The raster pipeline's world data (MeshPass, WorldMeshes): the mesh heap's words, which hold each block's vertices
// (MeshVertex, four words each: x and y; z, material, and openness; the octahedral normal; the palette's weights), its
// 16-bit indices, the materials, and the tint map; the frame's draws, one per block (MeshDraw); the sun's two shadow
// cascades, one record per frame slot (MeshLighting), and their maps; the frame's model draws, one per block of each
// model drawn (MeshModelDraw).
// Mirrors GpuData.MeshDraw, MeshMaterial, MeshLighting, and MeshModelDraw member for member; GpuLayout tests check them.
#extension GL_EXT_scalar_block_layout : require
#include "turns.glsl"

// The set these bindings are in: MeshPass's own, 0, or 1 where a pass binds its own set first (ParticlePass).
#ifndef MESH_SET
#define MESH_SET 0
#endif

// Centimetres per metre: the tint map is in centimetres.
const float CentimetresPerMetre = 100.0;

// Tint map words before the texels: size, texel shift, corner x, corner z (WorldMeshes.TintMapWords).
#define MESH_TINT_HEADER_WORDS 4u

struct MeshDraw
{
    vec3 origin;
    float voxelMetres;
    uint vertexBase;
};

struct MeshMaterial
{
    vec3 colour;
    uint look;
    vec3 tintColour;
    uint amounts;
    vec3 accentColour;
    float glow;
    vec3 textureMean;
    float textureMetres;
    uint texture;
    uint textureStyle;
    // The match's origin's place within the material's texture tile, metres on each axis (tilePoint).
    vec3 tileOrigin;
    // The lowest and highest height its texture set holds; RecipeHeight twice without one.
    vec2 heights;
};

// MeshMaterial.texture: the set's layer plus 1 in bits 0 to 15 (0: the recipe), the large arrays' bit, and the colour
// match in bits 24 to 31.
#define MESH_TEXTURE_LARGE (1u << 16u)
// MeshMaterial.texture's bit for strata's bands by height (strataBands), which a set's 2D tile cannot hold.
#define MESH_TEXTURE_BANDS (1u << 17u)

layout(scalar, set = MESH_SET, binding = 0) restrict readonly buffer MeshHeap
{
    uint meshWords[];
};

layout(scalar, set = MESH_SET, binding = 1) restrict readonly buffer MeshDraws
{
    MeshDraw draws[];
};

layout(scalar, set = MESH_SET, binding = 2) restrict readonly buffer MeshMaterials
{
    MeshMaterial materials[];
};

layout(scalar, set = MESH_SET, binding = 3) restrict readonly buffer MeshTintMap
{
    uint tintMap[];
};

struct MeshLighting
{
    mat4 nearCascade;
    mat4 farCascade;
    float nearEnd;
    float farEnd;
    float nearTexelMetres;
    float farTexelMetres;
    uint shadows;
    vec3 skyCorner;
    float skyCellMetres;
    uint lightCount;
    float nearBlendStart;
    float farFadeStart;
    mat4 distantCascade;
    float distantEnd;
    float distantTexelMetres;
    float distantFadeStart;
    float distantStrength;
};

struct MeshLight
{
    vec3 position;
    float power;
    vec3 colour;
    float reach;
};

layout(scalar, set = MESH_SET, binding = 4) restrict readonly buffer MeshLightingRecords
{
    MeshLighting lighting[];
};

// The cascades' depth maps, layer 0 near, 1 far, and 2 distant (only with Engine:Renderer:DistantShadowMetres), compared
// as they are read (lit where the point is no farther from the sun than the map holds).
layout(set = MESH_SET, binding = 5) uniform sampler2DArrayShadow shadowMaps;

struct MeshModelDraw
{
    vec3 corner;
    float voxelMetres;
    vec4 turn;
    uint vertexBase;
    uint block;
    float tint;
    uint shown;
};

layout(scalar, set = MESH_SET, binding = 6) restrict readonly buffer MeshModelDraws
{
    MeshModelDraw modelDraws[];
};

// The sky field: how much of the open sky each cell of air round the camera sees (0 to 1), its first texel's corner at
// lighting[slot].skyCorner; past its edges a white border reads as open sky.
layout(set = MESH_SET, binding = 8) uniform sampler3D skyField;

// The cascades' depth maps again, read as depths (0 at the sun's side, 1 the far side and past the edges): the blocker
// search of the contact-hardening shadows.
layout(set = MESH_SET, binding = 9) uniform sampler2DArray shadowDepths;

// The point lights of every slot, MESH_MOST_LIGHTS each (MeshPass.MostLights); lighting[slot].lightCount are used.
#define MESH_MOST_LIGHTS 256u
layout(scalar, set = MESH_SET, binding = 7) restrict readonly buffer MeshLights
{
    MeshLight lights[];
};

// The view's clusters (MeshPass's cluster pass): 16 x 9 tiles of the drawn image, top-left first, by 24 slices of view
// depth (MeshClusterSlice), each with up to MESH_CLUSTER_LIGHTS of the slot's lights that reach it, those that matter
// most to it (power over the squared distance). Per slot MESH_CLUSTER_SLOT_WORDS words: every cluster's count, then each cluster's indices.
#define MESH_CLUSTERS_X 16u
#define MESH_CLUSTERS_Y 9u
#define MESH_CLUSTERS_Z 24u
#define MESH_CLUSTER_COUNT (MESH_CLUSTERS_X * MESH_CLUSTERS_Y * MESH_CLUSTERS_Z)
#define MESH_CLUSTER_LIGHTS 32u
#define MESH_CLUSTER_SLOT_WORDS (MESH_CLUSTER_COUNT * (1u + MESH_CLUSTER_LIGHTS))
#ifdef MESH_CLUSTERS_WRITE
layout(scalar, set = MESH_SET, binding = 10) restrict writeonly buffer MeshClusters
#else
layout(scalar, set = MESH_SET, binding = 10) restrict readonly buffer MeshClusters
#endif
{
    uint clusterWords[];
};

// The materials' texture sets (MeshPass.Textures.cs), each set a layer of three arrays: colour with occlusion in alpha
// (sRGB, read as linear), the normal map's x and y, and the height; the large sets (2048 texels) at bindings 11 to 13,
// the small (1024) at 14 to 16. Every mip level, repeating, filtered anisotropically where the device can.
layout(set = MESH_SET, binding = 11) uniform sampler2DArray largeColours;
layout(set = MESH_SET, binding = 12) uniform sampler2DArray largeNormals;
layout(set = MESH_SET, binding = 13) uniform sampler2DArray largeHeights;
layout(set = MESH_SET, binding = 14) uniform sampler2DArray smallColours;
layout(set = MESH_SET, binding = 15) uniform sampler2DArray smallNormals;
layout(set = MESH_SET, binding = 16) uniform sampler2DArray smallHeights;

// The plane value noise's lattices as tables (MeshPass.Textures.cs, NoiseTables): a layer each, 1024 cells a side, cell
// c at texel c & 1023, holding the hash valueNoise2 takes for the cells from -512 to 511. Read with textureGather only.
layout(set = MESH_SET, binding = 18) uniform sampler2DArray noiseTables;
const int NoiseTableCells = 1024;

// The layers: the surfaces' patches (2^10 cm cells, salt 1), the strata's wander (2^11, salt 4), the hue drift (2^11,
// salt 41).
const float NoisePatches = 0.0;
const float NoiseStrata = 1.0;
const float NoiseHueDrift = 2.0;

// The slices: the first to 1 m, the last from 48 m on (the lights are gathered within 48 m), and between them 22 slices
// each the same ratio deeper. An orthographic view's are even, across the 96 m round its middle (where its lights are
// gathered), the first and last reaching on to its box's ends.
const float MeshClusterNearMetres = 1.0;
const float MeshClusterFarMetres = 48.0;

// The slice a point at view depth depth lies in.
uint meshClusterSlice(float depth, bool orthographic)
{
    if (orthographic)
    {
        float even = (depth + MeshClusterFarMetres) / (2.0 * MeshClusterFarMetres) * float(MESH_CLUSTERS_Z);
        return uint(clamp(even, 0.0, float(MESH_CLUSTERS_Z - 1u)));
    }

    if (depth < MeshClusterNearMetres)
    {
        return 0u;
    }

    if (depth >= MeshClusterFarMetres)
    {
        return MESH_CLUSTERS_Z - 1u;
    }

    float share = log(depth / MeshClusterNearMetres) / log(MeshClusterFarMetres / MeshClusterNearMetres);
    return min(1u + uint(share * float(MESH_CLUSTERS_Z - 2u)), MESH_CLUSTERS_Z - 2u);
}

// The view depth where slice slice starts (its end is the next one's start; the last one's has none, and an orthographic
// view's first one reaches back past it).
float meshClusterSliceStart(uint slice, bool orthographic)
{
    if (orthographic)
    {
        return MeshClusterFarMetres * (2.0 * float(slice) / float(MESH_CLUSTERS_Z) - 1.0);
    }

    if (slice == 0u)
    {
        return 0.0;
    }

    float share = float(slice - 1u) / float(MESH_CLUSTERS_Z - 2u);
    return MeshClusterNearMetres * pow(MeshClusterFarMetres / MeshClusterNearMetres, share);
}

// The cluster of the drawn image's pixel pixel (in pixels from its top-left, of an image width x height) at view depth
// depth.
uint meshCluster(vec2 pixel, vec2 size, float depth, bool orthographic)
{
    uvec2 tile = min(uvec2(pixel / size * vec2(MESH_CLUSTERS_X, MESH_CLUSTERS_Y)), uvec2(MESH_CLUSTERS_X - 1u, MESH_CLUSTERS_Y - 1u));
    return tile.x + MESH_CLUSTERS_X * (tile.y + MESH_CLUSTERS_Y * meshClusterSlice(depth, orthographic));
}

// signNotZero of the octahedral encoding: +1 for 0.
vec2 meshSignNotZero(vec2 v)
{
    return vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

// Words of a vertex in the heap (MeshVertex.Words), and of a mesh's palette before its first vertex
// (MeshBuffers.PaletteWords).
#define MESH_VERTEX_WORDS 4u
#define MESH_PALETTE_WORDS 2u

// The place of a vertex in its block, in the block's voxels (MeshVertex: (p + 2) * 256 in 16 bits).
vec3 meshLocal(uint vertexBase, uint index)
{
    uint base = vertexBase + MESH_VERTEX_WORDS * index;
    uint xy = meshWords[base];
    uint zm = meshWords[base + 1u];
    return vec3(float(xy & 0xFFFFu), float(xy >> 16), float(zm & 0xFFFFu)) / 256.0 - 2.0;
}

// The world position, in metres, of vertex index (gl_VertexIndex) of draw.
vec3 meshWorld(MeshDraw draw, uint index)
{
    return draw.origin + meshLocal(draw.vertexBase, index) * draw.voxelMetres;
}

// The world position, in metres, of vertex index of a model's draw: its place in the model's box, turned and moved. A
// model's blocks start one voxel before its box (WorldMeshes.ModelBlockShift).
vec3 meshModelWorld(MeshModelDraw draw, uint index)
{
    vec3 block = vec3(float(draw.block & 0xFFu), float((draw.block >> 8) & 0xFFu), float((draw.block >> 16) & 0xFFu));
    vec3 local = (meshLocal(draw.vertexBase, index) + 32.0 * block - 1.0) * draw.voxelMetres;
    return draw.corner + turnBy(draw.turn, local);
}

// The unit normal of vertex index of the block whose vertices start at vertexBase (MeshVertex's octahedral encoding).
vec3 meshNormalAt(uint vertexBase, uint index)
{
    uint packed = meshWords[vertexBase + MESH_VERTEX_WORDS * index + 2u];
    vec2 p = vec2(float(int(packed << 16) >> 16), float(int(packed) >> 16)) / 32767.0;
    vec3 n = vec3(p, 1.0 - abs(p.x) - abs(p.y));
    if (n.z < 0.0)
    {
        n.xy = (1.0 - abs(n.yx)) * meshSignNotZero(n.xy);
    }

    return normalize(n);
}

// The unit normal of vertex index of draw.
vec3 meshNormal(MeshDraw draw, uint index)
{
    return meshNormalAt(draw.vertexBase, index);
}

// The word of vertex index's material, openness, and z (MeshVertex.PositionZMaterial).
uint meshMaterialWord(uint vertexBase, uint index)
{
    return meshWords[vertexBase + MESH_VERTEX_WORDS * index + 1u];
}

// Vertex index's weights: how many of its cell's surface corners are each of its mesh's palette's eight materials, 0 to
// 8, four bits each (MeshVertex.Weights), as two vec4s, slots 0 to 3 and 4 to 7.
void meshWeights(uint vertexBase, uint index, out vec4 low, out vec4 high)
{
    uint w = meshWords[vertexBase + MESH_VERTEX_WORDS * index + 3u];
    low = vec4(uvec4(w, w >> 4u, w >> 8u, w >> 12u) & 15u);
    high = vec4(uvec4(w >> 16u, w >> 20u, w >> 24u, w >> 28u) & 15u);
}

// The palette of the mesh whose vertices start at vertexBase: eight material ids, a byte each from the lowest.
uvec2 meshPalette(uint vertexBase)
{
    return uvec2(meshWords[vertexBase - MESH_PALETTE_WORDS], meshWords[vertexBase - MESH_PALETTE_WORDS + 1u]);
}

// One texel of the tint map, 0 to 1. Texels past the edge repeat the edge.
float meshTintTexel(ivec2 texel, int size)
{
    texel = clamp(texel, ivec2(0), ivec2(size - 1));
    uint index = uint(texel.y * size + texel.x);
    return float((tintMap[MESH_TINT_HEADER_WORDS + index / 4u] >> (8u * (index & 3u))) & 0xFFu) / 255.0;
}

// The tint map at the point whole + part (whole absolute centimetres and the fraction within one), blended between the
// centres of the four texels around it; 0 without a map. The map's corner is taken off in integers first, so the point
// keeps its fraction far from the map's middle. The same as VoxelTintSampler.At on the CPU.
float meshTintAt(ivec3 whole, vec3 part)
{
    int size = int(tintMap[0]);
    if (size == 0)
    {
        return 0.0;
    }

    ivec2 corner = ivec2(int(tintMap[2]), int(tintMap[3]));
    vec2 p = (vec2(whole.xz - corner) + part.xz) / float(1 << int(tintMap[1])) - 0.5;
    ivec2 texel = ivec2(floor(p));
    vec2 f = p - vec2(texel);
    float near = mix(meshTintTexel(texel, size), meshTintTexel(texel + ivec2(1, 0), size), f.x);
    float far = mix(meshTintTexel(texel + ivec2(0, 1), size), meshTintTexel(texel + ivec2(1, 1), size), f.x);
    return mix(near, far, f.y);
}
