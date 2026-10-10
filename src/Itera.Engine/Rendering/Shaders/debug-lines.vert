#version 460

// Debug lines (DebugLinePass): two vertices a line, read from a storage buffer by gl_VertexIndex (the draw's first
// vertex picks the frame slot's region). A point is placed with the frame's view as every pass places it (view.glsl's
// viewClip), so it gets the reverse depth the world pass writes and points nearer than the view's near plane are left
// out.
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
    gl_Position = viewClip(offset);
    outColour = unpackUnorm4x8(vertex.colour);
}
