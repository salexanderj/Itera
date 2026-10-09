#version 450

// Compiled once at startup to prove the shader compiler, its native library, and the cache work.
layout(location = 0) out vec3 fragColor;

vec2 positions[3] = vec2[](vec2(0.0, -0.5), vec2(0.5, 0.5), vec2(-0.5, 0.5));

void main()
{
    gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
    fragColor = vec3(1.0);
}
