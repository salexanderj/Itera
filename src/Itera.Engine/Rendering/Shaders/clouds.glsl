// The look's clouds, drawn by the sky's own pass alone (world-sky.frag; the fog takes the sky without them): streaks of
// value noise on a plane at the look's cloud height over the land, so they stay over it as the eye moves and drift along
// their heading, stretched along it and bent across it by the look's warp. Their edges facing the sun take the look's
// cloud light, their thick middles are darker, and a second layer twice as high may show behind. They thin to none at
// the horizon, so the fogged far ground still meets the sky. The noise is read from the noise tables (mesh-data.glsl),
// one gather a cell. Include after view.glsl, sky.glsl, lighting.glsl, and mesh-data.glsl.

// Value noise from 0 to 1 at the lattice point p of the noise tables' layer layer: one gather of the cell's four corners.
// It repeats every NoiseTableCells cells.
float cloudValue(vec2 p, float layer)
{
    vec2 cell = floor(p);
    vec2 f = p - cell;
    vec2 u = f * f * (3.0 - 2.0 * f);
    // The point between the cell's texel and the next on each axis: gather's w the cell, z the next along x, x the next
    // along z, y the next along both.
    vec2 between = (vec2(ivec2(cell) & (NoiseTableCells - 1)) + 1.0) / float(NoiseTableCells);
    vec4 corners = textureGather(noiseTables, vec3(between, layer));
    return mix(mix(corners.w, corners.z, u.x), mix(corners.x, corners.y, u.x), u.y);
}

// octaves of value noise from 0 to 1, each twice as fine as the last and from the next layer of the tables, so the sum
// repeats where the tables do and no octave lines up with another. Each octave is drawn the look's ridge share as a
// ridge (high where the noise crosses its middle), which gives thin wisps.
float cloudNoise(vec2 p, int octaves)
{
    float sum = 0.0;
    float weight = 0.5;
    float total = 0.0;
    for (int octave = 0; octave < octaves; octave++)
    {
        float value = cloudValue(p + vec2(17.3, -9.1) * float(octave), float(octave % 3));
        sum += weight * mix(value, 1.0 - abs(2.0 * value - 1.0), LOOK.cloudRidge);
        total += weight;
        p *= 2.0;
        weight *= 0.5;
    }

    return sum / total;
}

// How thick the clouds are at the lattice point p (along their heading, stretched, and across it), 0 to 1: octaves of
// noise bent across the heading by the look's warp, covering the look's share of the sky, with the look's soft edges.
float cloudDensity(vec2 p, int octaves)
{
    if (LOOK.cloudWarp > 0.0)
    {
        p.y += LOOK.cloudWarp * (cloudValue(p * vec2(0.5, 0.25) + vec2(5.2, 1.3), 2.0) * 2.0 - 1.0);
    }

    float edge = 1.0 - LOOK.cloudCover;
    return smoothstep(edge, edge + LOOK.cloudSoftness, cloudNoise(p, octaves));
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
    if (LOOK.cloudCover <= 0.0 || direction.y <= 0.0)
    {
        return colour;
    }

    // Where the ray meets the lower plane: softened near the horizon, as the plane would reach the view's end.
    float t = max(LOOK.cloudLocalHeight - view.position.y, 10.0) / (direction.y + 0.1);
    float fade = smoothstep(0.0, 0.3, direction.y) * LOOK.cloudOpacity;
    float sun = max(dot(direction, view.sunDirection), 0.0);
    vec3 glow = 0.5 * LOOK.sunGlow * pow(sun, LOOK.sunGlowSharpness);

    if (LOOK.cloudUpper > 0.0)
    {
        vec2 high = LOOK.cloudFar + cloudFrame(view.position.xz + direction.xz * (2.0 * t)) * LOOK.cloudFarScale;
        float thin = cloudDensity(high, 2) * fade * LOOK.cloudUpper;
        colour = mix(colour, LOOK.cloudColour * 1.2 + glow, thin);
    }

    vec2 low = LOOK.cloudNear + cloudFrame(view.position.xz + direction.xz * t) * LOOK.cloudNearScale;
    float density = cloudDensity(low, 3);
    vec3 cloud = LOOK.cloudColour * mix(1.0, 0.6, density) + glow;
    vec2 sunward = cloudFrame(view.sunDirection.xz);
    if (dot(LOOK.cloudLight, vec3(1.0)) > 0.0 && dot(sunward, sunward) > 1e-6)
    {
        // An edge whose cloud thins towards the sun faces it: the drop in thickness a quarter of a cell sunwards.
        float towards = cloudDensity(low + normalize(sunward) * 0.25, 3);
        cloud += LOOK.cloudLight * (clamp((density - towards) * 3.0, 0.0, 1.0) * (1.0 - 0.5 * density));
    }

    return mix(colour, cloud, density * fade);
}
