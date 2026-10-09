#version 460

// The world's meshes and the models' (MeshPass): the world's material's recipe (mesh-surface.glsl: its colour blended
// towards its tint colour by the tint map, its pattern, relief, and hollows), a model's flat colour by its own tint, the glowing
// surfaces' point lights, sky and ground light scaled by how much of the sky the air in front sees (the sky field), the
// sun (Lambert) through the two shadow cascades (sunShadow),
// sky and ground light dimmed by the vertex's openness, fog into the sky, the tone curve, the grade, and the glow after
// it. The look's light and grade (below) are the art direction's (docs/rendering-redesign.md). The aim dot is the HUD's
// (AimDotLayer).
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"
#include "mesh-data.glsl"
#include "mesh-light.glsl"
#include "mesh-surface.glsl"

// mesh.vert's outputs: the offset from the camera with the openness, the normal, and the material with the tint's bits
// (-1: the tint map's).
layout(location = 0) in vec4 inOffsetOpenness;
layout(location = 1) in vec3 inNormal;
layout(location = 2) flat in uvec2 inMaterialTint;
layout(location = 3) in vec4 inWeightsLow;
layout(location = 4) in vec4 inWeightsHigh;
layout(location = 5) flat in uvec2 inPalette;
layout(location = 0) out vec4 outColor;

// Openness (the share of air round a vertex: each of its cell's corners' mean over the 3x3x3 voxels round it, blended
// to the vertex's place; SurfaceNets) is a half on flat ground, a quarter along an inner edge, and an eighth in an inner
// corner: flat ground keeps all its sky and ground light, an inner corner OcclusionFloor of it.
const float OpennessFlat = 0.5;
const float OpennessCorner = 0.125;

// The light of the point lights that reach this pixel's cluster of the view at world on a surface facing normal (view
// depth depth): Lambert, falling off with the square of the distance, and to nothing at each light's reach.
vec3 pointLight(vec3 world, vec3 normal, float depth)
{
    uint base = view.slot * MESH_CLUSTER_SLOT_WORDS;
    uint cluster = meshCluster(gl_FragCoord.xy, vec2(view.width, view.height), depth);
    uint count = min(clusterWords[base + cluster], MESH_CLUSTER_LIGHTS);
    uint indices = base + MESH_CLUSTER_COUNT + cluster * MESH_CLUSTER_LIGHTS;
    uint first = view.slot * MESH_MOST_LIGHTS;
    vec3 sum = vec3(0.0);
    for (uint i = 0u; i < count; i++)
    {
        MeshLight light = lights[first + clusterWords[indices + i]];
        vec3 to = light.position - world;
        float d2 = dot(to, to);
        float reach2 = light.reach * light.reach;
        if (d2 >= reach2)
        {
            continue;
        }

        float window = 1.0 - d2 / reach2;
        float lambert = max(dot(normal, to * inversesqrt(max(d2, 1e-4))), 0.0);
        sum += light.colour * (light.power * lambert * window * window / max(d2, 0.25));
    }

    return sum;
}

// The least share of the sky's and the ground's light a surface keeps however deep it is.
const float SkyFloor = 0.02;

// The look's light and grade, for a dark-fantasy art style
// (docs/rendering-redesign.md, the art direction):
// - The world's surfaces are WorldSaturation as saturated and WorldValue as light as their materials, so the
//   characters stand out against them; glowing ones keep their colour (and are left out of the grade, below). Their sides are darker than their tops: WorldSideShade where the surface
//   faces down at SideShadeBelow or more, rising to WorldTopShade where it faces up at TopShadeAbove.
// - Sunlight is warmer (SunWarmth) and the sky and ground light cooler (ShadeCool), on the models too.
// - After the grade, the picture's dark tones turn towards teal (GradeShadows) and its light tones towards orange
//   (GradeLights), blending between luma ShadowsBelow and LightsAbove; less as the fog thickens, to none in the sky.
const float WorldSaturation = 0.75;
const float WorldValue = 0.85;
const float WorldSideShade = 0.7;
const float WorldTopShade = 1.1;
const float SideShadeBelow = -0.3;
const float TopShadeAbove = 0.9;
const vec3 SunWarmth = vec3(1.08, 1.0, 0.88);
const vec3 ShadeCool = vec3(0.85, 0.95, 1.15);
const vec3 GradeShadows = vec3(0.9, 1.0, 1.08);
const vec3 GradeLights = vec3(1.06, 1.0, 0.9);
const float ShadowsBelow = 0.15;
const float LightsAbove = 0.6;
const vec3 LumaWeights = vec3(0.2126, 0.7152, 0.0722);

// The levels debug view's colour for voxels of 2^level cm: 1 cm magenta, 2 violet, 4 blue, 8 cyan, 16 green, 32 yellow,
// 64 orange, 128 red, and white past that.
vec3 meshLevelColour(uint level)
{
    const vec3 colours[9] = vec3[](
        vec3(0.9, 0.2, 0.8),
        vec3(0.55, 0.3, 0.95),
        vec3(0.2, 0.35, 1.0),
        vec3(0.1, 0.85, 0.9),
        vec3(0.2, 0.85, 0.25),
        vec3(0.95, 0.9, 0.2),
        vec3(1.0, 0.55, 0.1),
        vec3(0.9, 0.15, 0.1),
        vec3(1.0)
    );
    return colours[min(level, 8u)];
}

// How far a material's height (0 to 1, its texture set's height map, or RecipeHeight) moves a border between two
// materials, in shares of their weights: 1 moves it up to half a vertex's spacing each way, so pebbles stand out of the
// soil round them.
const float HeightBlend = 1.0;

// The texture set the world draws material index from, as the console's materials says (0 for its recipe or flat
// colour), and the set's tile width and colour mean: a material without a set, with all, takes the first small set (every
// set is 1024 texels; the large group holds only its stand-in).
uint drawnTexture(uint index, out float metres, out vec3 mean)
{
    uint texture = materials[index].texture;
    metres = materials[index].textureMetres;
    mean = materials[index].textureMean;
    if (viewSwitch(ViewMaterialsFlat))
    {
        return 0u;
    }

    if (texture == 0u && viewSwitch(ViewMaterialsAll))
    {
        metres = 3.0;
        mean = vec3(1.0);
        return 1u;
    }

    return texture;
}

// The world's material index at the point: its texture set where it is drawn from one, else its recipe at the point in
// centimetres (or its flat colour), tinted by the tint map.
Surface worldSurface(uint index, float mapTint, ivec3 whole, vec3 part, vec3 offset, vec3 normal, vec3 dx, vec3 dy)
{
    float metres;
    vec3 mean;
    uint texture = drawnTexture(index, metres, mean);
    if (texture != 0u)
    {
        return surfaceTextured(index, texture, metres, mean, mapTint, whole, part, offset, normal, dx, dy);
    }

    if (viewSwitch(ViewMaterialsFlat))
    {
        return surfaceFlat(index, mapTint);
    }

    return surfacePlain(index, mapTint, whole, part, normal);
}

// The height material index has at a border: its texture set's, or RecipeHeight.
float materialHeight(uint index, vec3 offset, vec3 normal, vec3 dx, vec3 dy)
{
    float metres;
    vec3 mean;
    uint texture = drawnTexture(index, metres, mean);
    return texture != 0u ? textureHeight(index, texture, metres, offset, normal, dx, dy) : RecipeHeight;
}

// The material of slot slot of a mesh's palette.
uint paletteMaterial(uvec2 palette, uint slot)
{
    return ((slot < 4u ? palette.x : palette.y) >> (8u * (slot & 3u))) & 0xFFu;
}

void main()
{
    vec3 offset = inOffsetOpenness.xyz;
    float openness = inOffsetOpenness.w;
    float tint = uintBitsToFloat(inMaterialTint.y);
    vec3 normal = normalize(inNormal);
    vec3 world = view.position + offset;
    uint index = inMaterialTint.x & 0xFFu;
    // Taken here, where every pixel of a 2x2 group runs, for the texture reads of the branches below.
    vec3 dx = dFdx(offset);
    vec3 dy = dFdy(offset);
    Surface surface;
    if (tint >= 0.0)
    {
        // A model, its own tint: its flat colour for now (its recipes need a point in the model's own space, or they would
        // slide over it as it moves).
        surface.colour = baseColour(index, tint);
        surface.slope = vec3(0.0);
        surface.cavity = 1.0;
        surface.glow = materials[index].glow * GlowStrength;
    }
    else
    {
        // The world: of the two materials of the mesh's palette that weigh most here, the one whose share and height are
        // greater, so the border between them wanders round the stones of their height maps (a hard edge: a soft band,
        // two surfaces worked out, cost up to 0.26 ms more and looked almost the same).
        vec3 centimetres = world * CentimetresPerMetre;
        ivec3 whole = ivec3(floor(centimetres));
        vec3 part = centimetres - vec3(whole);
        float mapTint = meshTintAt(world);
        float weights[8] = float[](inWeightsLow.x, inWeightsLow.y, inWeightsLow.z, inWeightsLow.w, inWeightsHigh.x,
            inWeightsHigh.y, inWeightsHigh.z, inWeightsHigh.w);
        uint first = 0u;
        uint second = 0u;
        float firstWeight = -1.0;
        float secondWeight = -1.0;
        float total = 0.0;
        for (uint i = 0u; i < 8u; i++)
        {
            float w = weights[i];
            total += w;
            if (w > firstWeight)
            {
                second = first;
                secondWeight = firstWeight;
                first = i;
                firstWeight = w;
            }
            else if (w > secondWeight)
            {
                second = i;
                secondWeight = w;
            }
        }

        uint a = paletteMaterial(inPalette, first);
        uint b = paletteMaterial(inPalette, second);
        if (a == 0u)
        {
            a = index;
        }

        if (b != 0u && secondWeight > 0.0)
        {
            float shareA = firstWeight / total + HeightBlend * (materialHeight(a, offset, normal, dx, dy) - 0.5);
            float shareB = secondWeight / total + HeightBlend * (materialHeight(b, offset, normal, dx, dy) - 0.5);
            a = shareB > shareA ? b : a;
        }

        surface = worldSurface(a, mapTint, whole, part, offset, normal, dx, dy);
    }

    vec3 colour = surface.colour;
    if (tint < 0.0 && surface.glow <= 0.0)
    {
        colour = mix(vec3(dot(colour, LumaWeights)), colour, WorldSaturation) * WorldValue;
        colour *= mix(WorldSideShade, WorldTopShade, smoothstep(SideShadeBelow, TopShadeAbove, normal.y));
    }

    if (viewSwitch(ViewLevels))
    {
        colour = mix(colour, meshLevelColour(inMaterialTint.x >> 8), 0.75);
    }

    // The recipe's relief tilts the normal the light meets (lit); its hollows hide it from the sky. The shadow maps and
    // the sky field are read along the mesh's own normal.
    vec3 lit = normalize(normal - surface.slope);

    float depth = dot(offset, view.forward);
    // A surface turned from the sun stays dark whatever its relief (as the raymarcher's did).
    float sunlight = 0.0;
    if (dot(normal, view.sunDirection) > 0.0)
    {
        sunlight = max(dot(lit, view.sunDirection), 0.0) * sunShadow(view.slot, world, normal, depth);
    }

    float open = clamp((openness - OpennessCorner) / (OpennessFlat - OpennessCorner), 0.0, 1.0);
    float sky = max(skyVisibility(view.slot, world, normal), SkyFloor);
    vec3 light = view.sunColour * SunWarmth * sunlight
        + ambientLight(lit, mix(OcclusionFloor, 1.0, open)) * ShadeCool * sky * surface.cavity + pointLight(world, lit, depth);
    vec3 shaded = colour * light;

    float fog = fogAt(length(offset), view.maxDistance);
    shaded = mix(shaded, skyColour(normalize(offset)), fog);
    vec3 toned = toneMap(shaded * Exposure);
    vec3 graded = gradeColour(toned);
    // Faded out with the fog: the sky (world-sky.frag) takes no split tone, so the fogged far ground must not either, or
    // the two would not meet at the horizon.
    vec3 split = mix(GradeShadows, GradeLights, smoothstep(ShadowsBelow, LightsAbove, dot(graded, LumaWeights)));
    graded *= mix(split, vec3(1.0), fog);
    if (surface.glow > 0.0)
    {
        // A glowing surface (embers, lava, glow caps) keeps its own colour: no grade or split tone where the fog leaves
        // it, and its glow's own brightness, what the glow adds through the tone curve, added after them, so embers and
        // lava stay a vivid red where the grade greys the picture (in a dark place). The glow fades with the fog.
        vec3 glow = surface.colour * surface.glow * (1.0 - fog);
        graded = mix(graded, toned, 1.0 - fog) + toneMap((shaded + glow) * Exposure) - toned;
    }

    shaded = min(graded, vec3(1.0));

    outColor = vec4(shaded, 1.0);
}
