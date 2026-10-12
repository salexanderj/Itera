// The sun's shadow and the sky's visibility at a point, from MeshPass's cascades and sky field (mesh-data.glsl, in
// set MESH_SET): mesh.frag for the world and the models, and particle-simulate.comp for each lit particle. Needs
// mesh-data.glsl.

// The point is moved this many of its cascade's texels along its normal before the lookup, so a surface does not shadow
// itself (acne). Its depth is not moved towards the sun: the maps hold only the faces turned away from it, so a lit
// surface never meets its own depth, and a depth bias lit the ground within it of a caster's shaded side (a bright strip
// where a voxel object's foot meets the ground).
const float ShadowNormalTexels = 1.5;

// Contact-hardening shadows in the near cascade the sun is a disc 2 degrees across (as the raymarcher's soft sun; this is its
// tangent), so a shadow's edge widens with the metres between the
// caster and the surface it falls on, sharp where they touch. The blocker search (8 taps: as good as 16
// in the forest for half the cost) looks as far as a caster this many metres above could blur, and no edge is wider than
// ShadowMostTexels texels.
const float SunSpreadTangent = 0.0349;
const float ShadowSearchMetres = 12.0;
const float ShadowMostTexels = 16.0;

// The blocker search's eight points: every 45 degrees round the disc, on its rim and at 0.4 of it in turn, so no side
// is left out. (Every other point of ShadowDisc leaned to one side, a gap of 140 degrees with none: a small caster on
// that side was missed, and a sharp lit notch cut into its soft shadow.)
const vec2 ShadowSearch[8] = vec2[](
    vec2(0.92388, 0.38268), vec2(0.15307, 0.36955), vec2(-0.38268, 0.92388), vec2(-0.36955, 0.15307),
    vec2(-0.92388, -0.38268), vec2(-0.15307, -0.36955), vec2(0.38268, -0.92388), vec2(0.36955, -0.15307));

// Sixteen points in the unit disc, spread evenly (a Poisson disc): the filter's taps.
const vec2 ShadowDisc[16] = vec2[](
    vec2(-0.94201624, -0.39906216), vec2(0.94558609, -0.76890725), vec2(-0.09418410, -0.92938870),
    vec2(0.34495938, 0.29387760), vec2(-0.91588581, 0.45771432), vec2(-0.81544232, -0.87912464),
    vec2(-0.38277543, 0.27676845), vec2(0.97484398, 0.75648379), vec2(0.44323325, -0.97511554),
    vec2(0.53742981, -0.47373420), vec2(-0.26496911, -0.41893023), vec2(0.79197514, 0.19090188),
    vec2(-0.24188840, 0.99706507), vec2(-0.81409955, 0.91437590), vec2(0.19984126, 0.78641367),
    vec2(0.14383161, -0.14100790));

// How much of the sunlight cascade cascade (0 near, 1 far, 2 distant) lets reach world point world on a surface facing
// normal: 0 to 1. The far and distant cascades: 3x3 lookups, each a 2x2 comparison the sampler blends. The near one:
// the casters' mean depth at 8 points of the search disc, then 16 comparisons over a disc as wide as the sun's blur from
// that gap, at least a texel; fully lit where the search finds no caster. 0.10 to 0.22 ms more than 3x3 at 2560x1440.
float cascadeLight(MeshLighting cascades, uint cascade, vec3 world, vec3 normal)
{
    bool near = cascade == 0u;
    float texel = near
        ? cascades.nearTexelMetres
        : (cascade == 1u ? cascades.farTexelMetres : cascades.distantTexelMetres);
    mat4 projection = near
        ? cascades.nearCascade
        : (cascade == 1u ? cascades.farCascade : cascades.distantCascade);
    vec4 clip = projection * vec4(world + normal * (ShadowNormalTexels * texel), 1.0);
    vec2 uv = clip.xy * 0.5 + 0.5;
    float reference = clip.z;
    float layer = float(cascade);
    vec2 step = 1.0 / vec2(textureSize(shadowMaps, 0).xy);
    if (!near)
    {
        float lit = 0.0;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                lit += texture(shadowMaps, vec4(uv + vec2(x, y) * step, layer, reference));
            }
        }

        return lit / 9.0;
    }

    // Metres along the sun per unit of the map's depth: the projection's depth grows by its z row's length a metre.
    float depthMetres = 1.0 / length(vec3(projection[0][2], projection[1][2], projection[2][2]));
    float search = clamp(0.5 * ShadowSearchMetres * SunSpreadTangent / texel, 1.0, ShadowMostTexels);
    float blockers = 0.0;
    float count = 0.0;
    for (int i = 0; i < 8; i++)
    {
        float depth = texture(shadowDepths, vec3(uv + ShadowSearch[i] * search * step, layer)).r;
        if (depth < reference)
        {
            blockers += depth;
            count += 1.0;
        }
    }

    if (count == 0.0)
    {
        return 1.0;
    }

    float gapMetres = (reference - blockers / count) * depthMetres;
    float radius = clamp(0.5 * gapMetres * SunSpreadTangent / texel, 1.0, ShadowMostTexels);
    float lit = 0.0;
    for (int i = 0; i < 16; i++)
    {
        lit += texture(shadowMaps, vec4(uv + ShadowDisc[i] * radius * step, layer, reference));
    }

    return lit / 16.0;
}

// How much of the sunlight the distant cascade lets reach world point world on a surface facing normal at view depth
// depth: its lookups weakened by its strength (the sun's height), and faded to 1 over its last stretch.
float distantLight(MeshLighting cascades, vec3 world, vec3 normal, float depth)
{
    float fade = clamp(
        (depth - cascades.distantFadeStart) / (cascades.distantEnd - cascades.distantFadeStart),
        0.0,
        1.0);
    return mix(1.0, cascadeLight(cascades, 2u, world, normal), cascades.distantStrength * (1.0 - fade));
}

// How much of the sunlight reaches world point world on a surface facing normal at view depth depth, in frame slot
// slot: 0 to 1, from the cascade that covers it; in the band before the near cascade's end, blended from it to the far
// one, and in the far one's last stretch blended into the distant one where that is drawn (distantStrength above 0),
// else faded to 1, and the distant one faded to 1 over its own last stretch, so no end shows as a line. 1 past the last
// cascade or with shadows off.
float sunShadow(uint slot, vec3 world, vec3 normal, float depth)
{
    MeshLighting cascades = lighting[slot];
    bool distant = cascades.distantStrength > 0.0;
    if (cascades.shadows == 0u || depth >= (distant ? cascades.distantEnd : cascades.farEnd))
    {
        return 1.0;
    }

    if (depth < cascades.nearBlendStart)
    {
        return cascadeLight(cascades, 0u, world, normal);
    }

    if (depth >= cascades.farEnd)
    {
        return distantLight(cascades, world, normal, depth);
    }

    float far = cascadeLight(cascades, 1u, world, normal);
    if (depth < cascades.nearEnd)
    {
        float share = (depth - cascades.nearBlendStart) / (cascades.nearEnd - cascades.nearBlendStart);
        return mix(cascadeLight(cascades, 0u, world, normal), far, share);
    }

    float fade = clamp((depth - cascades.farFadeStart) / (cascades.farEnd - cascades.farFadeStart), 0.0, 1.0);
    if (fade == 0.0)
    {
        return far;
    }

    return mix(far, distant ? distantLight(cascades, world, normal, depth) : 1.0, fade);
}

// How much of the open sky the air in front of a surface at world (facing normal) sees, 0 to 1: the sky field, blended
// between its cells by the sampler; 1 with no field, and past its edges (the border). Half a cell along the normal, so a
// surface reads its air.
float skyVisibility(uint slot, vec3 world, vec3 normal)
{
    float cellMetres = lighting[slot].skyCellMetres;
    if (cellMetres <= 0.0)
    {
        return 1.0;
    }

    vec3 cells = (world + normal * (0.5 * cellMetres) - lighting[slot].skyCorner) / cellMetres;
    return texture(skyField, cells / vec3(textureSize(skyField, 0))).r;
}
