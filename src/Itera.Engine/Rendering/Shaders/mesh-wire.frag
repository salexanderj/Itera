#version 460

// The wireframe debug view (MeshPass; the console's view wire): the meshes' triangles drawn again as lines over the
// world, near black, pulled a little towards the camera by a depth bias so they are not hidden by their own faces. It
// takes mesh.vert's outputs and uses none of them but, compiled with MESH_FADE for the models', the share they show.
layout(location = 0) in vec4 inOffsetOpenness;
layout(location = 1) in vec3 inNormal;
layout(location = 2) flat in uvec4 inMaterialTint;
#ifdef MESH_FADE
// The models' share of their pixels (MeshModelDraw.shown; mesh-fade.glsl): the edges of the pixels it shows.
#include "mesh-fade.glsl"
layout(location = 6) flat in uint inShown;
#endif

layout(location = 0) out vec4 outColor;

void main()
{
#ifdef MESH_FADE
    if (!meshShows(inShown))
    {
        discard;
    }
#endif

    outColor = vec4(vec3(0.02), 1.0);
}
