#version 460

// The wireframe debug view (MeshPass; the console's view wire): the meshes' triangles drawn again as lines over the
// world, near black, pulled a little towards the camera by a depth bias so they are not hidden by their own faces. It
// takes mesh.vert's outputs and uses none of them.
layout(location = 0) in vec4 inOffsetOpenness;
layout(location = 1) in vec3 inNormal;
layout(location = 2) flat in uvec2 inMaterialTint;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(vec3(0.02), 1.0);
}
