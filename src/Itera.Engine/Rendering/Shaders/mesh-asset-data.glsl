// The mesh assets' data (MeshAssetPass), the asset pipelines' set 1 (set 0 is MeshPass's, mesh-data.glsl): the
// samplers, the texture slots (MESH_TEXTURE_COUNT of them: the white stand-in first, every asset's textures, then one
// for each image view, RenderViews), every asset's vertices (MeshAssetVertex), every asset's materials
// (MeshAssetMaterial), and the frame's draws of every slot and list (MeshAssetDraw, read at gl_InstanceIndex: a
// command's first instance names its first draw). The vertex buffer also
// holds each frame slot's skinned vertices, which mesh-skin.comp writes (compiled with MESH_ASSET_SKINNING, where the
// buffer is writable): a draw adds its vertexDelta to gl_VertexIndex to read them. Mirrors GpuData's MeshAssetVertex,
// MeshAssetMaterial, and MeshAssetDraw member for member; GpuLayout tests check them.
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
    int vertexDelta;
    uint sides;
};

// MeshAssetDraw.sides of a blended draw: the faces it shows (MeshAssetDraw.FrontOnly, BackOnly).
#define MESH_ASSET_FRONT_ONLY 1u
#define MESH_ASSET_BACK_ONLY 2u

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

#ifdef MESH_ASSET_SKINNING
layout(scalar, set = 1, binding = 2) restrict buffer MeshAssetVertices
#else
layout(scalar, set = 1, binding = 2) restrict readonly buffer MeshAssetVertices
#endif
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

// The coordinates on the octahedron of a direction, which need not be of unit length (Octahedral.Encode); (0, 0) for
// none.
vec2 assetEncodeOctahedral(vec3 d)
{
    float l1 = abs(d.x) + abs(d.y) + abs(d.z);
    if (!(l1 > 0.0))
    {
        return vec2(0.0);
    }

    vec2 p = d.xy / l1;
    if (d.z < 0.0)
    {
        p = (1.0 - abs(p.yx)) * assetSignNotZero(p);
    }

    return p;
}

// value * scale rounded half away from zero, as Octahedral's Quantize.
int assetQuantize(float value, float scale)
{
    float scaled = clamp(value, -1.0, 1.0) * scale;
    return int(sign(scaled) * floor(abs(scaled) + 0.5));
}

// A normal in Octahedral.PackSnorm16's encoding (its rounding of a value a float step from a half may differ).
uint assetPackNormal(vec3 normal)
{
    vec2 p = assetEncodeOctahedral(normal);
    return (uint(assetQuantize(p.x, 32767.0)) & 0xFFFFu) | (uint(assetQuantize(p.y, 32767.0)) << 16u);
}

// A tangent and its bitangent's sign in Octahedral.PackTangent's encoding.
uint assetPackTangent(vec3 tangent, float handedness)
{
    vec2 p = assetEncodeOctahedral(tangent);
    return (uint(assetQuantize(p.x, 32767.0)) & 0xFFFFu)
        | ((uint(assetQuantize(p.y, 16383.0)) & 0x7FFFu) << 16u)
        | (handedness < 0.0 ? 0x80000000u : 0u);
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
