#version 460

// The world's meshes (MeshPass): one indexed draw per block, whose MeshDraw is draws[gl_InstanceIndex] (the indirect
// command's first instance). The vertex is read from the mesh heap (meshWorld, meshNormal), placed in the world by the
// block's origin and voxel size, and projected with the view (viewClip: reverse depth, view.glsl).
// Compiled with MESH_MODEL for the models: one indirect command per block of a model level, each instance a
// MeshModelDraw (modelDraws[gl_InstanceIndex]) that places and turns the model and gives its tint; the world's blocks
// take their tint from the tint map instead (tint -1), and so do the models whose draw's tint is below 0 (the voxel
// objects' parts), shaded as the world's surfaces are, with their meshes' weights and palettes. Three outputs: the offset from the camera with the openness, the
// normal, and the material (bits 0-7) and the draw's voxel size as a level (log2 of its centimetres, bits 8-15) with the
// tint's bits; and for the world's blocks the vertex's weights of its mesh's palette's eight materials (two vec4s) with
// the palette (flat), which mesh.frag blends the borders between materials by. Each output is one slot a vertex exports,
// which the forest's many vertices pay for.
#include "view.glsl"
#include "mesh-data.glsl"

layout(location = 0) out vec4 outOffsetOpenness;
layout(location = 1) out vec3 outNormal;
layout(location = 2) flat out uvec2 outMaterialTint;
layout(location = 3) out vec4 outWeightsLow;
layout(location = 4) out vec4 outWeightsHigh;
layout(location = 5) flat out uvec2 outPalette;

void main()
{
    uint index = uint(gl_VertexIndex);
#ifdef MESH_MODEL
    MeshModelDraw draw = modelDraws[gl_InstanceIndex];
    vec3 offset = meshModelWorld(draw, index) - view.position;
    outNormal = meshTurn(draw.turn, meshNormalAt(draw.vertexBase, index));
    float tint = draw.tint;
#else
    MeshDraw draw = draws[gl_InstanceIndex];
    vec3 offset = meshWorld(draw, index) - view.position;
    outNormal = meshNormal(draw, index);
    float tint = -1.0;
#endif
#ifdef MESH_MODEL
    // A model shaded as the world (a voxel object's part, tint below 0) takes its mesh's weights and palette as the
    // world's blocks do; any other its own flat colour.
    if (tint < 0.0)
    {
        meshWeights(draw.vertexBase, index, outWeightsLow, outWeightsHigh);
        outPalette = meshPalette(draw.vertexBase);
    }
    else
    {
        outWeightsLow = vec4(0.0);
        outWeightsHigh = vec4(0.0);
        outPalette = uvec2(0u);
    }
#else
    meshWeights(draw.vertexBase, index, outWeightsLow, outWeightsHigh);
    outPalette = meshPalette(draw.vertexBase);
#endif

    gl_Position = viewClip(offset);

    uint zm = meshMaterialWord(draw.vertexBase, index);
    uint level = uint(clamp(round(log2(draw.voxelMetres * CentimetresPerMetre)), 0.0, 15.0));
    outOffsetOpenness = vec4(offset, float(zm >> 24) / 255.0);
    outMaterialTint = uvec2(((zm >> 16) & 0xFFu) | (level << 8), floatBitsToUint(tint));
}
