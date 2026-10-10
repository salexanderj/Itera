// The light a surface drawn in MeshPass's rendering takes and the colour it ends as (shadeSurface), shared by the
// world's meshes and the models' (mesh.frag): the sun (Lambert) through the two shadow cascades (sunShadow), sky and
// ground light dimmed by the vertex's openness and scaled by how much of the sky the air in front sees (the sky field),
// the glowing surfaces' point lights, fog into the sky, the tone curve, the grade, and the glow after it. The look's light
// and grade (below) are the art direction's (docs/rendering-redesign.md). Include after view.glsl, sky.glsl,
// lighting.glsl, mesh-data.glsl, and mesh-light.glsl.

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

// The look's light and grade, for a dark-fantasy art style (docs/rendering-redesign.md, the art direction):
// - Sunlight is warmer (SunWarmth) and the sky and ground light cooler (ShadeCool), on everything drawn.
// - After the grade, the picture's dark tones turn towards teal (GradeShadows) and its light tones towards orange
//   (GradeLights), blending between luma ShadowsBelow and LightsAbove; less as the fog thickens, to none in the sky.
const vec3 SunWarmth = vec3(1.08, 1.0, 0.88);
const vec3 ShadeCool = vec3(0.85, 0.95, 1.15);
const vec3 GradeShadows = vec3(0.9, 1.0, 1.08);
const vec3 GradeLights = vec3(1.06, 1.0, 0.9);
const float ShadowsBelow = 0.15;
const float LightsAbove = 0.6;
const vec3 LumaWeights = vec3(0.2126, 0.7152, 0.0722);

// The colour a surface of colour ends as at offset from the camera: lit along lit (its normal as its relief tilts it)
// with the shadow maps and the sky field read along its own normal, its sky and ground light kept by its openness and
// cavity (its hollows hide it from the sky), and glowColour glowing by glow (0 for none) after the grade.
vec3 shadeSurface(vec3 colour, vec3 normal, vec3 lit, vec3 offset, float openness, float cavity, vec3 glowColour,
    float glow)
{
    vec3 world = view.position + offset;
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
        + ambientLight(lit, mix(OcclusionFloor, 1.0, open)) * ShadeCool * sky * cavity + pointLight(world, lit, depth);
    vec3 shaded = colour * light;

    float fog = fogAt(length(offset), view.maxDistance);
    shaded = mix(shaded, skyColour(normalize(offset)), fog);
    vec3 toned = toneMap(shaded * Exposure);
    vec3 graded = gradeColour(toned);
    // Faded out with the fog: the sky (world-sky.frag) takes no split tone, so the fogged far ground must not either, or
    // the two would not meet at the horizon.
    vec3 split = mix(GradeShadows, GradeLights, smoothstep(ShadowsBelow, LightsAbove, dot(graded, LumaWeights)));
    graded *= mix(split, vec3(1.0), fog);
    if (glow > 0.0)
    {
        // A glowing surface (embers, lava, glow caps) keeps its own colour: no grade or split tone where the fog leaves
        // it, and its glow's own brightness, what the glow adds through the tone curve, added after them, so embers and
        // lava stay a vivid red where the grade greys the picture (in a dark place). The glow fades with the fog.
        vec3 glowing = glowColour * glow * (1.0 - fog);
        graded = mix(graded, toned, 1.0 - fog) + toneMap((shaded + glowing) * Exposure) - toned;
    }

    return min(graded, vec3(1.0));
}
