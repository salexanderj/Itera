#version 460

// Debug lines (DebugLinePass): two vertices a line, read from a storage buffer by gl_VertexIndex (the draw's first
// vertex picks the frame slot's region). A point is placed with the frame's view (view.glsl): the camera's right and
// up are scaled so a point along forward + x * right + y * up shows at (x, y) in [-1, 1], so clip-space w is the depth
// along forward, and z is the near distance, which gives the reverse depth the world pass writes and keeps points
// nearer than 1 cm out.
#include "view.glsl"

struct DebugLineVertex
{
    vec3 position;
    uint colour;
};

layout(scalar, set = 0, binding = 0) readonly buffer Lines
{
    DebugLineVertex vertices[];
};

layout(location = 0) out vec4 outColour;

void main()
{
    DebugLineVertex vertex = vertices[gl_VertexIndex];
    vec3 offset = vertex.position - view.position;
    float depth = dot(offset, view.forward);
    float x = dot(offset, view.right) / dot(view.right, view.right);
    float y = dot(offset, view.up) / dot(view.up, view.up);
    gl_Position = vec4(x, y, NearMetres, depth);
    outColour = unpackUnorm4x8(vertex.colour);
}
