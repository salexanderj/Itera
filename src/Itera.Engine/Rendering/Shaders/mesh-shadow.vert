#version 460

// A shadow cascade's depth (MeshPass): the blocks the cascade's command list names, placed as mesh.vert places them and
// projected by the cascade's view-projection (lighting[slot]: z 0 towards the sun, 1 away from it). Compiled with
// MESH_MODEL for the models' blocks (modelDraws, as mesh.vert's model variant reads them).
#extension GL_EXT_scalar_block_layout : require
#include "mesh-data.glsl"

layout(push_constant, scalar) uniform ShadowPush
{
    uint slot;
    uint cascade;
} push;

void main()
{
#ifdef MESH_MODEL
    vec3 world = meshModelWorld(modelDraws[gl_InstanceIndex], uint(gl_VertexIndex));
#else
    vec3 world = meshWorld(draws[gl_InstanceIndex], uint(gl_VertexIndex));
#endif
    MeshLighting cascades = lighting[push.slot];
    mat4 cascade = push.cascade == 0u ? cascades.nearCascade : cascades.farCascade;
    gl_Position = cascade * vec4(world, 1.0);
}
