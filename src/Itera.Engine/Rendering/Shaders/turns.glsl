// Turns by unit quaternions, as System.Numerics turns them, and the corners of a box as triangles, for the models
// (mesh-data.glsl) and the particle cubes (particle-data.glsl).

// v turned by the unit quaternion q.
vec3 turnBy(vec4 q, vec3 v)
{
    return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v);
}

// v turned back by the unit quaternion q.
vec3 turnBack(vec4 q, vec3 v)
{
    return turnBy(vec4(-q.xyz, q.w), v);
}

// The box's corners by triangle, 36 vertices, two triangles for each face, wound counter-clockwise seen from outside:
// bit 0 is x, bit 1 y, bit 2 z. Faces -x, +x, -y, +y, -z, +z.
const uint BoxCorners[36] = uint[](
    0u, 4u, 6u, 0u, 6u, 2u,
    5u, 1u, 3u, 5u, 3u, 7u,
    0u, 1u, 5u, 0u, 5u, 4u,
    6u, 7u, 3u, 6u, 3u, 2u,
    1u, 0u, 2u, 1u, 2u, 3u,
    4u, 5u, 7u, 4u, 7u, 6u);
