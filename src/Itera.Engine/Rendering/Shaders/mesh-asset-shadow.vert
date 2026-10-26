#version 460

// A shadow cascade's depth of the mesh assets (MeshAssetPass): the primitives the cascade's list names, placed as
// mesh-asset.vert places them and projected by the cascade's view-projection (lighting[slot], as mesh-shadow.vert).
// Compiled with MESH_ASSET_MASK for masked materials: the base colour's texture coordinates and the material go to
// mesh-asset-shadow.frag, which leaves out the pixels below the alpha cutoff.
#extension GL_EXT_scalar_block_layout : require
#include "mesh-data.glsl"
#include "mesh-asset-data.glsl"

layout(push_constant, scalar) uniform ShadowPush
{
    uint slot;
    uint cascade;
} push;

#ifdef MESH_ASSET_MASK
layout(location = 0) out vec4 outUv;
layout(location = 1) out float outAlpha;
layout(location = 2) flat out uint outMaterial;
#endif

void main()
{
    MeshAssetDraw draw = assetDraws[gl_InstanceIndex];
    // The position alone, a posed draw's from its skinned stream (12 bytes a vertex).
    vec3 world = (draw.transform * vec4(assetPositionOf(draw, uint(gl_VertexIndex)), 1.0)).xyz;
    MeshLighting cascades = lighting[push.slot];
    mat4 cascade = push.cascade == 0u ? cascades.nearCascade : cascades.farCascade;
    gl_Position = cascade * vec4(world, 1.0);
#ifdef MESH_ASSET_MASK
    MeshAssetVertex vertex = assetVertices[gl_VertexIndex];
    outUv = vec4(vertex.uv, unpackHalf2x16(vertex.uv1));
    outAlpha = unpackUnorm4x8(vertex.colour).a * (draw.tint == MESH_ASSET_NO_TINT ? 1.0 : unpackUnorm4x8(draw.tint).a);
    outMaterial = draw.material;
#endif
}
