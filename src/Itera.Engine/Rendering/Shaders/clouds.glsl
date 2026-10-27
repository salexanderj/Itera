// The look's clouds, drawn by the sky's own pass alone (world-sky.frag; the fog takes the sky without them): clouds on a
// plane at the look's cloud height over the land, so they stay over it as the eye moves and drift along their heading,
// drawn out along it and bent across it by the look's warp. Their shape is the clouds' noise (mesh-data.glsl's
// cloudNoise): its gradient noise, carved into puffs by its cellular noise as the look's billow says, its edges eaten by
// a finer read as the look's erosion says. They take the look's cloud light where little of them lies towards the sun,
// their thick middles are darker, and a second layer twice as high may show behind. They thin to none at the horizon,
// so the fogged far ground still meets the sky. Include after view.glsl, sky.glsl, lighting.glsl, and mesh-data.glsl.

// Lattice cells a tile of the clouds' noise spans: its coarsest octaves have about one feature a cell.
const float CloudCellsPerTile = 4.0;

// The clouds' noise at the lattice point p, scale times finer, read over the pixel's footprint (dx and dy, the point's
// change across the pixel, worked out before any pixel left the sky's shader, so every read picks its level aright).
// It repeats every tile, and the lattice point wraps at a whole number of tiles.
vec4 cloudRead(vec2 p, vec2 dx, vec2 dy, float scale)
{
    float tiles = scale / CloudCellsPerTile;
    return textureGrad(cloudNoise, p * tiles, dx * tiles, dy * tiles);
}

// The clouds' shape at one read of their noise, 0 to 1: the gradient noise (drawn as ridges, thin wisps, as the look's
// ridge share says), carved by the cellular noise as the look's billow says: lowered between the cells' points, so it
// breaks into rounded puffs with creases between.
float cloudShape(vec4 noise)
{
    float gradient = mix(noise.r, 1.0 - abs(2.0 * noise.r - 1.0), LOOK.cloudRidge);
    float cells = dot(noise.gba, vec3(0.625, 0.25, 0.125));
    float carve = (1.0 - cells) * LOOK.cloudBillow;
    return clamp((gradient - carve) / max(1.0 - carve, 0.05), 0.0, 1.0);
}

// The lattice point p bent across the clouds' heading by the look's warp: a slow read, so a cloud's light samples a
// little way off can take the same bend.
vec2 cloudWarp(vec2 p, vec2 dx, vec2 dy)
{
    if (LOOK.cloudWarp > 0.0)
    {
        p.y += LOOK.cloudWarp * (cloudRead(p + vec2(5.2, 1.3), dx, dy, 0.25).r * 2.0 - 1.0);
    }

    return p;
}

// How thick the clouds are at the bent lattice point p (along their heading, drawn out, and across it), 0 to 1: their
// shape, eaten at its thin edges by a five times finer read of the cellular noise as the look's erosion says (when
// eroded), covering the look's share of the sky, with the look's soft edges.
float cloudDensity(vec2 p, vec2 dx, vec2 dy, bool eroded)
{
    float shape = cloudShape(cloudRead(p, dx, dy, 1.0));
    if (eroded && LOOK.cloudErosion > 0.0)
    {
        vec4 detail = cloudRead(p + vec2(3.1, 7.7), dx, dy, 5.0);
        shape -= LOOK.cloudErosion * dot(detail.gba, vec3(0.625, 0.25, 0.125)) * (1.0 - shape);
    }

    float edge = 1.0 - LOOK.cloudCover;
    return smoothstep(edge, edge + LOOK.cloudSoftness, shape);
}

// A point of the plane, local, in the clouds' frame in metres: along their heading over their stretch, and across it.
vec2 cloudFrame(vec2 place)
{
    vec2 along = vec2(LOOK.cloudAlongX, LOOK.cloudAlongZ);
    return vec2(dot(place, along) / LOOK.cloudStretch, dot(place, vec2(-along.y, along.x)));
}

// The sky colour of direction with the look's clouds over it. None while the cover is 0.
vec3 skyClouds(vec3 direction, vec3 colour)
{
    // Where the ray meets each plane: softened near the horizon, as the plane would reach the view's end. Worked out for
    // every pixel, with the reads' footprints, before any pixel leaves.
    float t = max(LOOK.cloudLocalHeight - view.position.y, 10.0) / (max(direction.y, 0.0) + 0.1);
    vec2 low = LOOK.cloudNear + cloudFrame(view.position.xz + direction.xz * t) * LOOK.cloudNearScale;
    vec2 high = LOOK.cloudFar + cloudFrame(view.position.xz + direction.xz * (2.0 * t)) * LOOK.cloudFarScale;
    vec2 lowX = dFdx(low);
    vec2 lowY = dFdy(low);
    vec2 highX = dFdx(high);
    vec2 highY = dFdy(high);
    if (LOOK.cloudCover <= 0.0 || direction.y <= 0.0)
    {
        return colour;
    }

    float fade = smoothstep(0.0, 0.3, direction.y) * LOOK.cloudOpacity;
    float sun = max(dot(direction, view.sunDirection), 0.0);
    vec3 glow = 0.5 * LOOK.sunGlow * pow(sun, LOOK.sunGlowSharpness);

    if (LOOK.cloudUpper > 0.0)
    {
        float thin = cloudDensity(cloudWarp(high, highX, highY), highX, highY, false) * fade * LOOK.cloudUpper;
        colour = mix(colour, LOOK.cloudColour * 1.2 + glow, thin);
    }

    low = cloudWarp(low, lowX, lowY);
    float density = cloudDensity(low, lowX, lowY, true);
    vec3 cloud = LOOK.cloudColour * mix(1.0, 0.55, density) + glow;
    vec2 sunward = cloudFrame(view.sunDirection.xz);
    if (dot(LOOK.cloudLight, vec3(1.0)) > 0.0 && dot(sunward, sunward) > 1e-6)
    {
        vec2 toward = normalize(sunward);
        if (LOOK.cloudShadow > 0.0)
        {
            // The cloud's thickness on the way towards the sun, in three steps of its shape: lit where little lies
            // sunwards (its sunward side, thin rims), dark where much does (its core and far side).
            float depth = 0.0;
            for (int step = 1; step <= 3; step++)
            {
                depth += cloudDensity(low + toward * (0.15 * float(step)), lowX, lowY, false);
            }

            cloud += LOOK.cloudLight * exp2(-depth * LOOK.cloudShadow);
        }
        else
        {
            // An edge whose cloud thins towards the sun faces it: the drop in thickness a quarter of a cell sunwards.
            float towards = cloudDensity(low + toward * 0.25, lowX, lowY, false);
            cloud += LOOK.cloudLight * (clamp((density - towards) * 3.0, 0.0, 1.0) * (1.0 - 0.5 * density));
        }
    }

    return mix(colour, cloud, density * fade);
}
