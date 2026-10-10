#version 460

// The aimed block's highlight (the mesh sample's AimHighlightPass): a unit cube, its 36 corners found from
// gl_VertexIndex, placed by the frame's transform (M * v) and projected as every pass is (viewClip).
#include "../view.glsl"
#include "aim-highlight-data.glsl"

layout(location = 0) out vec3 outOffset;
layout(location = 1) out vec3 outLocal;
layout(location = 2) flat out vec3 outNormal;
layout(location = 3) flat out vec3 outAcross;

const vec3 Corners[8] = vec3[](
    vec3(0, 0, 0), vec3(1, 0, 0), vec3(1, 0, 1), vec3(0, 0, 1),
    vec3(0, 1, 0), vec3(1, 1, 0), vec3(1, 1, 1), vec3(0, 1, 1));

// Each face's two triangles, counter-clockwise seen from outside: -y, +y, -z, +z, -x, +x.
const int Faces[36] = int[](
    0, 1, 2, 0, 2, 3,
    4, 7, 6, 4, 6, 5,
    0, 4, 5, 0, 5, 1,
    3, 2, 6, 3, 6, 7,
    0, 3, 7, 0, 7, 4,
    1, 5, 6, 1, 6, 2);

const vec3 Normals[6] = vec3[](
    vec3(0, -1, 0), vec3(0, 1, 0), vec3(0, 0, -1), vec3(0, 0, 1), vec3(-1, 0, 0), vec3(1, 0, 0));

void main()
{
    AimHighlightData data = highlights[view.slot];
    vec3 local = Corners[Faces[gl_VertexIndex]];
    vec3 normal = Normals[gl_VertexIndex / 6];
    vec3 offset = (data.transform * vec4(local, 1.0)).xyz - view.position;
    outOffset = offset;
    outLocal = local;
    outNormal = normalize(mat3(data.transform) * normal);
    outAcross = abs(normal);
    gl_Position = viewClip(offset);
}
