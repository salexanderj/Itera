#version 460

// The mesh assets (MeshAssetPass), drawn in MeshPass's rendering after the world and the models: one indexed draw per
// primitive drawn, each instance a MeshAssetDraw (assetDraws[gl_InstanceIndex]: its transform, material, and tint). The
// vertex is read from every asset's vertices at gl_VertexIndex (the command's vertex offset is its asset's first
// vertex) plus the draw's vertexDelta (a posed instance's skinned copy, mesh-skin.comp), placed by the draw's transform (M * v), its normal turned by the transform's cofactor (right under any
// scale), and projected as the world is (viewClip). Outputs: the offset from the camera, the normal, the tangent with
// its bitangent's sign, both texture coordinates, the vertex colour, and the material and tint.
#include "view.glsl"
#include "mesh-asset-data.glsl"

layout(location = 0) out vec3 outOffset;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec4 outTangent;
layout(location = 3) out vec4 outUv;
layout(location = 4) out vec4 outColour;
layout(location = 5) flat out uvec2 outMaterialTint;

void main()
{
    MeshAssetDraw draw = assetDraws[gl_InstanceIndex];
    MeshAssetVertex vertex = assetVertices[gl_VertexIndex + draw.vertexDelta];
    vec3 world = (draw.transform * vec4(vertex.position, 1.0)).xyz;
    vec3 offset = world - view.position;
    mat3 turn = mat3(draw.transform);
    mat3 cofactor = mat3(cross(turn[1], turn[2]), cross(turn[2], turn[0]), cross(turn[0], turn[1]));
    vec4 tangent = assetTangent(vertex.tangent);
    outOffset = offset;
    outNormal = cofactor * assetNormal(vertex.normal);
    outTangent = vec4(turn * tangent.xyz, tangent.w);
    outUv = vec4(vertex.uv, unpackHalf2x16(vertex.uv1));
    outColour = unpackUnorm4x8(vertex.colour);
    outMaterialTint = uvec2(draw.material, draw.tint);
    gl_Position = viewClip(offset);
}
