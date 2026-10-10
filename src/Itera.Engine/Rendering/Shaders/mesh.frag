#version 460

// The world's meshes and the models' (MeshPass): the world's material's recipe (mesh-surface.glsl: its colour blended
// towards its tint colour by the tint map, its pattern, relief, and hollows) or a model's flat colour by its own tint,
// the world's own look (below), then the light and the grade every surface of the pass takes (mesh-shade.glsl). The aim
// dot is the HUD's (AimDotLayer).
#include "view.glsl"
#include "sky.glsl"
#include "lighting.glsl"
#include "mesh-data.glsl"
#include "mesh-light.glsl"
#include "mesh-surface.glsl"
#include "mesh-shade.glsl"

// mesh.vert's outputs: the offset from the camera with the openness, the normal, and the material with the tint's bits
// (-1: the tint map's).
layout(location = 0) in vec4 inOffsetOpenness;
layout(location = 1) in vec3 inNormal;
layout(location = 2) flat in uvec2 inMaterialTint;
layout(location = 3) in vec4 inWeightsLow;
layout(location = 4) in vec4 inWeightsHigh;
layout(location = 5) flat in uvec2 inPalette;
layout(location = 0) out vec4 outColor;

// The world's own look (the frame's look's world values, look.glsl; the starter's for a dark-fantasy art style,
// docs/rendering-redesign.md; the light and the grade every surface takes are mesh-shade.glsl's): the world's surfaces
// are worldSaturation as saturated and worldValue as light as their materials, so the characters stand out against
// them; glowing ones keep their colour (and are left out of the grade). Their sides are darker than their tops:
// worldSideShade where the surface faces down at sideShadeBelow or more, rising to worldTopShade where it faces up at
// topShadeAbove.

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
        surface.glow = materials[index].glow;
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
        colour = mix(vec3(dot(colour, LumaWeights)), colour, LOOK.worldSaturation) * LOOK.worldValue;
        colour *= mix(
            LOOK.worldSideShade,
            LOOK.worldTopShade,
            smoothstep(LOOK.sideShadeBelow, LOOK.topShadeAbove, normal.y)
        );
    }

    if (viewSwitch(ViewLevels))
    {
        colour = mix(colour, meshLevelColour(inMaterialTint.x >> 8), 0.75);
    }

    // The recipe's relief tilts the normal the light meets (lit); its hollows hide it from the sky. The shadow maps and
    // the sky field are read along the mesh's own normal.
    vec3 lit = normalize(normal - surface.slope);
    // A material's glow is its share of full glow; the look says how bright full glow is.
    float glow = surface.glow * LOOK.glowStrength;
    vec3 shaded = shadeSurface(colour, normal, lit, offset, openness, surface.cavity, surface.colour, glow);
    outColor = vec4(shaded, 1.0);
}
