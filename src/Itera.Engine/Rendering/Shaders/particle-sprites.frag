#version 460

// The particle sprites (ParticlePass; particle-sprites.vert): a soft round blob, or a capsule for a stretched sprite,
// whose opacity falls from its axis to its edge; or a flake, a torn outline with a sharp edge, pale at its edge and
// dark and blotched in its middle. Premultiplied: the colour times the opacity is added, and the picture behind is
// dimmed by the opacity times the recipe's cover (0 adds light, 1 covers).
layout(location = 0) in vec2 inLocal;
layout(location = 1) flat in float inHalfLength;
layout(location = 2) flat in vec4 inColour;
layout(location = 3) flat in float inCover;
layout(location = 4) flat in uint inFlake;
layout(location = 0) out vec4 outColor;

// A random value of a lattice point for this flake, 0 to 1 (below 1).
float flakeHash(ivec2 point)
{
    uint h = (uint(point.x) * 1597334677u) ^ (uint(point.y) * 3812015801u) ^ inFlake;
    h = (h ^ (h >> 16u)) * 2246822519u;
    h = (h ^ (h >> 13u)) * 3266489917u;
    h ^= h >> 16u;
    return float(h >> 8u) * (1.0 / 16777216.0);
}

// Value noise for this flake, 0 to 1, smooth between the whole-number lattice points.
float flakeNoise(vec2 x)
{
    vec2 cell = floor(x);
    vec2 f = x - cell;
    vec2 u = f * f * (3.0 - 2.0 * f);
    ivec2 corner = ivec2(cell);
    return mix(
        mix(flakeHash(corner), flakeHash(corner + ivec2(1, 0)), u.x),
        mix(flakeHash(corner + ivec2(0, 1)), flakeHash(corner + ivec2(1, 1)), u.x),
        u.y);
}

// A flake's shade at its quad's point p (-1 to 1 on each axis) and its opacity there, 0 to 1.
vec2 flake(vec2 p)
{
    // Inside where the field is above 0: a radius of 0.62 (particle-sprites.vert sizes the quad by it) moved in and out
    // by three sizes of noise over the quad (not by angle, so the outline is torn, with no symmetry), up to 1 at most,
    // so it stays inside the quad. Each flake is up to 1.7 times as long as it is wide, from four bits of its seed (its
    // seed is a hash, so they are random). A few burnt holes and bites where a fourth noise is lowest.
    float stretch = 1.0 + 0.7 * float((inFlake >> 24u) & 15u) / 15.0;
    float r = length(p * vec2(1.0, stretch));
    float noise = 0.5 * flakeNoise(p * 1.8 + 7.0) + 0.3 * flakeNoise(p * 4.1 + 13.0)
        + 0.2 * flakeNoise(p * 11.0 + 29.0);
    float field = min(0.62 + 0.75 * (noise - 0.5) - r, 0.5 * (flakeNoise(p * 4.5 + 41.0) - 0.12));
    // Blended over the width of a pixel, so the edge stays sharp but does not crawl.
    float opacity = clamp(field / max(fwidth(field), 1e-4) + 0.5, 0.0, 1.0);

    // Burnt from the edge in: pale along its edge and dark in its middle, with dark blotches that fade out as a pixel
    // grows from 0.1 to 0.3 of the quad's half width, so a far flake is one even shade.
    float detail = 1.0 - smoothstep(0.1, 0.3, length(fwidth(p)));
    float blotched = 1.0 - 0.3 * detail * smoothstep(0.45, 0.8, flakeNoise(p * 5.0 + 71.0));
    return vec2(mix(1.15, 0.5, smoothstep(0.0, 0.2, field)) * blotched, opacity);
}

void main()
{
    if (inFlake != 0u)
    {
        vec2 shaded = flake(inLocal);
        if (shaded.y <= 0.0)
        {
            discard;
        }

        float flakeOpacity = inColour.a * shaded.y;
        outColor = vec4(inColour.rgb * shaded.x * flakeOpacity, flakeOpacity * inCover);
        return;
    }

    vec2 fromAxis = vec2(max(abs(inLocal.x) - inHalfLength, 0.0), inLocal.y);
    float r2 = dot(fromAxis, fromAxis);
    if (r2 >= 1.0)
    {
        discard;
    }

    float soft = 1.0 - r2;
    float opacity = inColour.a * soft * soft;
    outColor = vec4(inColour.rgb * opacity, opacity * inCover);
}
