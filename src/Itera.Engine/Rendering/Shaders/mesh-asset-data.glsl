// The mesh assets' data (MeshAssetPass), the asset pipelines' set 1 (set 0 is MeshPass's, mesh-data.glsl): the
// samplers, the textures of every asset (MESH_TEXTURE_COUNT of them, the white stand-in first), every asset's vertices
// (MeshAssetVertex), every asset's materials (MeshAssetMaterial), and the frame's draws of every slot and list
// (MeshAssetDraw, read at gl_InstanceIndex: a command's first instance names its first draw). Mirrors GpuData's
// MeshAssetVertex, MeshAssetMaterial, and MeshAssetDraw member for member; GpuLayout tests check them.
#extension GL_EXT_scalar_block_layout : require
#extension GL_EXT_nonuniform_qualifier : require

#ifndef MESH_TEXTURE_COUNT
#define MESH_TEXTURE_COUNT 1
#endif

struct MeshAssetVertex
{
    vec3 position;
    uint normal;
    uint tangent;
    vec2 uv;
    uint uv1;
    uint colour;
};

struct MeshAssetMaterial
{
    vec4 baseColour;
    vec3 emission;
    float alphaCutoff;
    uint baseColourTexture;
    uint normalTexture;
    uint occlusionTexture;
    uint emissionTexture;
    uint metalRoughTexture;
    float normalScale;
    float occlusionStrength;
    float metallic;
    float roughness;
    uint flags;
};

struct MeshAssetDraw
{
    mat4 transform;
    uint material;
    uint tint;
    uint flags;
};

// A texture reference's index for none (MeshAssetMaterial.NoTexture), and its fields: the index in bits 0 to 15, the
// sampler in bits 16 to 18, the texture coordinates in bit 19.
#define MESH_ASSET_NO_TEXTURE 0xFFFFu
// MeshAssetMaterial.flags.
#define MESH_ASSET_MASKED 1u
#define MESH_ASSET_TWO_SIDED 2u
// MeshAssetDraw.tint that leaves the colours as they are.
#define MESH_ASSET_NO_TINT 0xFFFFFFFFu

layout(set = 1, binding = 0) uniform sampler assetSamplers[6];
layout(set = 1, binding = 1) uniform texture2D assetTextures[MESH_TEXTURE_COUNT];

layout(scalar, set = 1, binding = 2) restrict readonly buffer MeshAssetVertices
{
    MeshAssetVertex assetVertices[];
};

layout(scalar, set = 1, binding = 3) restrict readonly buffer MeshAssetMaterials
{
    MeshAssetMaterial assetMaterials[];
};

layout(scalar, set = 1, binding = 4) restrict readonly buffer MeshAssetDraws
{
    MeshAssetDraw assetDraws[];
};

vec2 assetSignNotZero(vec2 value)
{
    return vec2(value.x >= 0.0 ? 1.0 : -1.0, value.y >= 0.0 ? 1.0 : -1.0);
}

// The unit direction at octahedral coordinates p (Octahedral.Decode).
vec3 assetOctahedral(vec2 p)
{
    vec3 n = vec3(p, 1.0 - abs(p.x) - abs(p.y));
    if (n.z < 0.0)
    {
        n.xy = (1.0 - abs(n.yx)) * assetSignNotZero(n.xy);
    }

    return normalize(n);
}

// A vertex's normal (Octahedral.PackSnorm16).
vec3 assetNormal(uint packed)
{
    return assetOctahedral(vec2(float(int(packed << 16) >> 16), float(int(packed) >> 16)) / 32767.0);
}

// A vertex's tangent and its bitangent's sign (Octahedral.PackTangent).
vec4 assetTangent(uint packed)
{
    vec2 p = vec2(float(int(packed << 16) >> 16) / 32767.0, float(int(packed << 1) >> 17) / 16383.0);
    return vec4(assetOctahedral(p), (packed >> 31) != 0u ? -1.0 : 1.0);
}

// True when the texture reference names a texture.
bool assetHasTexture(uint reference)
{
    return (reference & 0xFFFFu) != MESH_ASSET_NO_TEXTURE;
}

// The texture a reference names at the vertex's first (uv.xy) or second (uv.zw) texture coordinates, through its
// sampler; white for none. The index and sampler may differ between the draws of one call: nonuniformEXT.
vec4 assetTexture(uint reference, vec4 uv)
{
    uint index = reference & 0xFFFFu;
    if (index == MESH_ASSET_NO_TEXTURE)
    {
        return vec4(1.0);
    }

    uint samplerIndex = (reference >> 16u) & 7u;
    vec2 at = (reference & (1u << 19u)) != 0u ? uv.zw : uv.xy;
    return texture(sampler2D(assetTextures[nonuniformEXT(index)], assetSamplers[nonuniformEXT(samplerIndex)]), at);
}
