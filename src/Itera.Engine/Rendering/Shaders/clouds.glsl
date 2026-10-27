// The look's clouds, drawn by the sky's own pass alone (world-sky.frag; the fog takes the sky without them): streaks of
// value noise on a plane at the look's cloud height over the land, so they stay over it as the eye moves and drift along
// their heading, stretched along it and bent across it by the look's warp, as lobes or wisps, their edges eaten. They
// take the look's cloud light where little of them lies towards the sun, their thick middles are darker, and a second
// layer twice as high may show behind. They thin to none at
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

// octaves of value noise from 0 to 1, each turned 45 degrees from the last and the square root of two times as fine,
// from the next layer of the tables, so no octave lines up with another or with the lattice. The turn takes whole
// lattice points to whole lattice points, so the sum repeats where the tables do. Each octave is drawn the look's billow
// share as a lobe (low where the noise crosses its middle: creases between puffs) and its ridge share as a ridge (high
// there: thin wisps).
float cloudNoise(vec2 p, int octaves)
{
    float sum = 0.0;
    float weight = 1.0;
    float total = 0.0;
    for (int octave = 0; octave < octaves; octave++)
    {
        float value = cloudValue(p + vec2(17.3, -9.1) * float(octave), float(octave % 3));
        float crease = abs(2.0 * value - 1.0);
        value = mix(mix(value, crease, LOOK.cloudBillow), 1.0 - crease, LOOK.cloudRidge);
        sum += weight * value;
        total += weight;
        p = mat2(1.0, 1.0, -1.0, 1.0) * p;
        weight *= 0.7071;
    }

    return sum / total;
}

// The lattice point p bent across the clouds' heading by the look's warp: a slow noise, so a cloud's light samples a
// little way off can take the same bend.
vec2 cloudWarp(vec2 p)
{
    if (LOOK.cloudWarp > 0.0)
    {
        p.y += LOOK.cloudWarp * (cloudValue(p * vec2(0.5, 0.25) + vec2(5.2, 1.3), 2.0) * 2.0 - 1.0);
    }

    return p;
}

// How thick the clouds are at the bent lattice point p (along their heading, stretched, and across it), 0 to 1:
// octaves of noise, eaten at their thin edges by a finer noise as the look's erosion says (when eroded), covering the
// look's share of the sky, with the look's soft edges.
float cloudDensity(vec2 p, int octaves, bool eroded)
{
    float noise = cloudNoise(p, octaves);
    if (eroded && LOOK.cloudErosion > 0.0)
    {
        noise -= LOOK.cloudErosion * cloudNoise(p * 6.0 + vec2(3.1, 7.7), 2) * (1.0 - noise);
    }

    float edge = 1.0 - LOOK.cloudCover;
    return smoothstep(edge, edge + LOOK.cloudSoftness, noise);
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
        float thin = cloudDensity(cloudWarp(high), 2, false) * fade * LOOK.cloudUpper;
        colour = mix(colour, LOOK.cloudColour * 1.2 + glow, thin);
    }

    vec2 low = cloudWarp(LOOK.cloudNear + cloudFrame(view.position.xz + direction.xz * t) * LOOK.cloudNearScale);
    float density = cloudDensity(low, 4, true);
    vec3 cloud = LOOK.cloudColour * mix(1.0, 0.55, density) + glow;
    vec2 sunward = cloudFrame(view.sunDirection.xz);
    if (dot(LOOK.cloudLight, vec3(1.0)) > 0.0 && dot(sunward, sunward) > 1e-6)
    {
        vec2 toward = normalize(sunward);
        if (LOOK.cloudShadow > 0.0)
        {
            // The cloud's thickness on the way towards the sun, in two steps of its coarse shape: lit where little lies
            // sunwards (its sunward side, thin rims), dark where much does (its core and far side).
            float depth = cloudDensity(low + toward * 0.2, 2, false) + cloudDensity(low + toward * 0.4, 2, false);
            cloud += LOOK.cloudLight * exp2(-depth * 1.5 * LOOK.cloudShadow);
        }
        else
        {
            // An edge whose cloud thins towards the sun faces it: the drop in thickness a quarter of a cell sunwards.
            float towards = cloudDensity(low + toward * 0.25, 3, false);
            cloud += LOOK.cloudLight * (clamp((density - towards) * 3.0, 0.0, 1.0) * (1.0 - 0.5 * density));
        }
    }

    return mix(colour, cloud, density * fade);
}
