// The light a surface drawn in MeshPass's rendering takes and the colour it ends as (shadeSurface), shared by the
// world's meshes and the models' (mesh.frag): the sun (Lambert) through the two shadow cascades (sunShadow) and the
// look's clouds' shadow (cloudGroundShadow), sky and
// ground light dimmed by the vertex's openness and scaled by how much of the sky the air in front sees (the sky field),
// the glowing surfaces' point lights, the air into the sky (air.glsl) and the lights' halos in it (light-halos.glsl), the
// tone curve, the grade, the glow after it, and a
// dither against bands. The light and the grade take the frame's look (look.glsl; the art direction's,
// docs/rendering-redesign.md). Include after view.glsl, sky.glsl, lighting.glsl, air.glsl, mesh-data.glsl,
// mesh-light.glsl, and cloud-shape.glsl.

// Openness (the share of air round a vertex: each of its cell's corners' mean over the 3x3x3 voxels round it, blended
// to the vertex's place; SurfaceNets) is a half on flat ground, a quarter along an inner edge, and an eighth in an inner
// corner: flat ground keeps all its sky and ground light, an inner corner the look's occlusion floor of it.
const float OpennessFlat = 0.5;
const float OpennessCorner = 0.125;

// The light of the point lights that reach this pixel's cluster of the view at world on a surface facing normal (view
// depth depth): Lambert, falling off with the square of the distance, and to nothing at each light's reach.
vec3 pointLight(vec3 world, vec3 normal, float depth)
{
    // With no light the frame sorts no clusters (MeshPass.Record): theirs are an earlier frame's.
    if (lighting[view.slot].lightCount == 0u)
    {
        return vec3(0.0);
    }

    uint base = view.slot * MESH_CLUSTER_SLOT_WORDS;
    uint cluster = meshCluster(gl_FragCoord.xy, vec2(view.width, view.height), depth, viewOrthographic());
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

// The look's light and grade (RenderLook; the starter's are a dark-fantasy art style, docs/rendering-redesign.md):
// - Sunlight is tinted by its sun warmth and the sky and ground light by its shade coolness, on everything drawn; a
//   surface keeps at least its sky floor of the sky's and the ground's light however deep it is.
// - After the grade, the picture's dark tones turn towards its shadow tint and its light tones towards its light tint,
//   blending between luma shadowsBelow and lightsAbove (lighting.glsl's LumaWeights); less as the fog thickens, to none
//   in the sky.

// The colour a surface of colour ends as at offset from the camera: lit along lit (its normal as its relief tilts it)
// with the shadow maps and the sky field read along its own normal, its sky and ground light kept by its openness and
// cavity (its hollows hide it from the sky), and glowColour glowing by glow (0 for none) after the grade. rim scales
// the look's edge light (1 for the mesh assets, 0 for the world's surfaces and the voxel models): the sky's light round
// the surface's outline, where it turns from the eye, as much as the sky reaches it.
vec3 shadeSurface(vec3 colour, vec3 normal, vec3 lit, vec3 offset, float openness, float cavity, vec3 glowColour,
    float glow, float rim)
{
    vec3 world = view.position + offset;
    float depth = dot(offset, view.forward);
    // A surface turned from the sun stays dark whatever its relief (as the raymarcher's did).
    float sunlight = 0.0;
    if (dot(normal, view.sunDirection) > 0.0)
    {
        sunlight = max(dot(lit, view.sunDirection), 0.0)
            * sunShadow(view.slot, world, normal, viewCascadeDepth(depth))
            * cloudGroundShadow(world);
    }

    float open = clamp((openness - OpennessCorner) / (OpennessFlat - OpennessCorner), 0.0, 1.0);
    float sky = max(skyVisibility(view.slot, world, normal), LOOK.skyFloor);
    vec3 light = LOOK.sunLight * sunlight
        + ambientLight(lit, mix(LOOK.occlusionFloor, 1.0, open)) * LOOK.shadeCool * sky * cavity
        + pointLight(world, lit, depth);
    if (rim > 0.0)
    {
        float edge = 1.0 - clamp(dot(normal, -viewRayTo(offset)), 0.0, 1.0);
        light += LOOK.rimLight * (rim * sky * pow(edge, LOOK.rimSharpness));
    }

    vec3 shaded = colour * light;

    float fog;
    shaded = throughAir(shaded, offset, view.maxDistance, fog) + lightHalos(viewRayTo(offset), length(offset));
    vec3 toned = toneMap(shaded * LOOK.exposure);
    vec3 graded = gradeColour(toned);
    // Faded out with the fog: the sky (world-sky.frag) takes no split tone, so the fogged far ground must not either, or
    // the two would not meet at the horizon.
    vec3 split = mix(
        LOOK.gradeShadows,
        LOOK.gradeLights,
        smoothstep(LOOK.shadowsBelow, LOOK.lightsAbove, dot(graded, LumaWeights))
    );
    graded *= mix(split, vec3(1.0), fog);
    if (glow > 0.0)
    {
        // A glowing surface (embers, lava, glow caps) keeps its own colour: no grade or split tone where the fog leaves
        // it, and its glow's own brightness, what the glow adds through the tone curve, added after them, so embers and
        // lava stay a vivid red where the grade greys the picture (in a dark place). The glow fades with the fog.
        vec3 glowing = glowColour * glow * (1.0 - fog);
        graded = mix(graded, toned, 1.0 - fog) + toneMap((shaded + glowing) * LOOK.exposure) - toned;
    }

    return ditherColour(min(graded, vec3(1.0)), gl_FragCoord.xy);
}
