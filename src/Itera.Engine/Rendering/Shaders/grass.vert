#version 460

// The grass's blades (GrassDrawPass): two indexed draws over a pattern of GRASS_BLADES_AN_INSTANCE blades an
// instance, the near draw's instances from 0 and the far draw's from the far blades' first (grass-count.comp). A near
// blade has seven corners (three pairs up its length and its tip), a far one three; the corner and the blade come from
// gl_VertexIndex (blade * corners + corner, the pattern's index) and the instance. A blade past those placed this frame
// collapses to a point. The blade is a curve from its root to its tip, leaned along the way it faces and swayed by the
// wind (a gust that drifts over the ground plus a sway of the blade's own phase), as wide as its width tapering to the
// tip, and never narrower than leastPixels pixels at its root, so a far blade stays a pixel wide and does not shimmer. A
// blade seen edge on turns its width towards the eye. Its colour is the ground's under it, darker at its root and
// lighter at its tip; grass.frag lights it. A far blade, a few pixels, has its sunlight's shadow and its sky read here,
// once a corner, in place of once a pixel.
#include "view.glsl"
#include "grass-data.glsl"
#include "mesh-data.glsl"
#include "mesh-light.glsl"

layout(location = 0) out vec3 outOffset;
layout(location = 1) out vec3 outColour;
layout(location = 2) out vec3 outNormal;
layout(location = 3) out vec3 outGround;
// A far blade's shadow and sky (sunShadowOnce, skyVisibility) at its corner; below 0 for a near one, lit by its pixels.
layout(location = 4) flat out vec2 outLight;

// Value noise from 0 to 1 over the plane, on a lattice of whole numbers.
float gustNoise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 f = p - cell;
    vec2 u = f * f * (3.0 - 2.0 * f);
    uvec2 c = uvec2(ivec2(cell));
    float a = grassUnit(grassHash3(uvec3(c, 7u)).x);
    float b = grassUnit(grassHash3(uvec3(c + uvec2(1u, 0u), 7u)).x);
    float d = grassUnit(grassHash3(uvec3(c + uvec2(0u, 1u), 7u)).x);
    float e = grassUnit(grassHash3(uvec3(c + uvec2(1u, 1u), 7u)).x);
    return mix(mix(a, b, u.x), mix(d, e, u.x), u.y);
}

// How far the wind's gusts reach across the ground, in metres.
const float GustMetres = 7.0;

void main()
{
    GrassFrame frame = grassFrames[view.slot];
    uint farBase = frame.nearMost / GRASS_BLADES_AN_INSTANCE;
    uint instance = uint(gl_InstanceIndex);
    bool near = instance < farBase;
    uint corners = near ? GRASS_NEAR_CORNERS : GRASS_FAR_CORNERS;
    uint vertex = uint(gl_VertexIndex);
    uint corner = vertex % corners;
    uint number = (near ? instance : instance - farBase) * GRASS_BLADES_AN_INSTANCE + vertex / corners;
    uint placed = near ? min(grassDraws.nearPlaced, frame.nearMost) : min(grassDraws.farPlaced, frame.farMost);
    if (number >= placed)
    {
        gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
        outOffset = vec3(0.0);
        outColour = vec3(0.0);
        outNormal = vec3(0.0, 1.0, 0.0);
        outGround = vec3(0.0, 1.0, 0.0);
        outLight = vec2(-1.0);
        return;
    }

    GrassBlade blade = blades[near ? number : frame.nearMost + number];
    vec2 facing = unpackHalf2x16(blade.facing);
    vec2 size = unpackHalf2x16(blade.size);
    vec4 ground = unpackSnorm4x8(blade.normal);
    vec4 motion = unpackUnorm4x8(blade.motion);
    vec3 normal = normalize(ground.xyz);
    float lean = (ground.w * 0.5 + 0.5) * GrassMostLean;

    // Up the blade (0 at the root, 1 at the tip) and which side of it (-1, 0 at the tip, 1).
    float along;
    float side;
    if (near)
    {
        along = float(corner >> 1u) / 3.0;
        side = corner == 6u ? 0.0 : ((corner & 1u) == 0u ? -1.0 : 1.0);
    }
    else
    {
        along = corner == 2u ? 1.0 : 0.0;
        side = corner == 2u ? 0.0 : (corner == 0u ? -1.0 : 1.0);
    }

    // The wind: a gust drifting with it over the ground (at the root's place in the world), and the blade's own sway.
    vec3 root = blade.root;
    vec2 place = (root.xz + vec2(viewOrigin().xz) / 100.0) / GustMetres;
    float gust = gustNoise(place - frame.windWay * (frame.windSpeed * frame.seconds / GustMetres));
    float sway = sin(frame.seconds * (1.6 + 1.2 * motion.y) + 6.2831853 * motion.x);
    float push = frame.wind * (0.25 + gust) * (0.75 + 0.25 * sway);
    vec3 way = vec3(facing.x, 0.0, facing.y);
    vec3 windWay = vec3(frame.windWay.x, 0.0, frame.windWay.y);

    // The curve: from the root, up, to the tip leaned along its way and pushed by the wind, about the blade's height
    // long; the middle point keeps the lower part upright, so the blade bends more towards its tip.
    float height = size.x;
    vec3 tipWay = normalize(vec3(0.0, cos(lean), 0.0) + way * sin(lean) + windWay * push);
    vec3 tip = root + tipWay * height;
    vec3 middle = root + vec3(0.0, 0.6 * height, 0.0) + (tip - root - vec3(0.0, height, 0.0)) * (0.25 + 0.3 * motion.z);
    vec3 position = mix(mix(root, middle, along), mix(middle, tip, along), along);
    vec3 tangent = normalize(mix(middle - root, tip - middle, along) + vec3(0.0, 1e-4, 0.0));

    // Across the blade, turned towards the eye as the blade turns edge on.
    vec3 toEye = view.position - root;
    vec2 flatEye = normalize(toEye.xz + vec2(1e-5, 0.0));
    vec3 across = vec3(-way.z, 0.0, way.x);
    vec3 facingEye = vec3(-flatEye.y, 0.0, flatEye.x);
    facingEye *= dot(facingEye, across) < 0.0 ? -1.0 : 1.0;
    float edgeOn = abs(dot(across.xz, flatEye));
    across = normalize(mix(across, facingEye, edgeOn * edgeOn));

    // At least leastPixels pixels wide at its root, tapering to the tip.
    float depth = max(dot(root - view.position, view.forward), view.near);
    float pixel = viewOrthographic() ? 2.0 * length(view.up) / float(max(view.height, 1u))
        : 2.0 * length(view.up) * depth / float(max(view.height, 1u));
    float width = max(size.y, frame.leastPixels * pixel) * (1.0 - along);
    position += across * (0.5 * side * width);

    // Its normal faces the eye, and leans towards the ground's, wholly at its root. Through the band where the field
    // thins, its shading and its light and dark fade into the ground's, so a far blade that comes and goes between
    // pixels changes the picture little.
    float contrast = 1.0 - smoothstep(frame.fullMetres, frame.radius, length(toEye));
    vec3 face = normalize(cross(across, tangent));
    face *= dot(face, toEye) < 0.0 ? -1.0 : 1.0;
    outNormal = normalize(mix(normal, face, 0.5 * along * contrast));
    outGround = normal;
    outLight = near ? vec2(-1.0)
        : vec2(
            sunShadowOnce(view.slot, position, normal, viewCascadeDepth(dot(position - view.position, view.forward))),
            skyVisibility(view.slot, position, normal));

    vec4 colour = unpackUnorm4x8(blade.colour);
    outColour = colour.rgb * colour.rgb * mix(1.0, mix(frame.rootShade, frame.tipLight, along), contrast);
    outOffset = position - view.position;
    gl_Position = viewClip(outOffset);
}
