#version 460

// One triangle that covers the whole image: corners (-1, -1), (3, -1), (-1, 3). The viewport has a negative
// height, so +y in clip space is up on screen, and outNdc is the point's position in [-1, 1] with +y up.
layout(location = 0) out vec2 outNdc;

void main()
{
    vec2 position = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0;
    outNdc = position;
    gl_Position = vec4(position, 0.0, 1.0);
}
