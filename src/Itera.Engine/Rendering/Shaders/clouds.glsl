// The look's clouds, drawn by the sky's own pass alone (world-sky.frag; the fog takes the sky without them): clouds on a
// plane at the look's cloud height over the land, so they stay over it as the eye moves and drift along their heading,
// drawn out along it and bent across it by the look's warp. Their shape is the clouds' noise (mesh-data.glsl's
// cloudNoise): its gradient noise, carved into puffs by its cellular noise as the look's billow says, its edges eaten by
// a finer read as the look's erosion says. They take the look's cloud light where little of them lies towards the sun,
// their thick middles are darker, each puff may be lit on its sunward side and its creases darkened, a second layer
// twice as high may show behind, and thin wisps lower down in front. They thin to none at the horizon, or past the
// look's reach, so the fogged far ground still meets the sky. Include after view.glsl, sky.glsl, lighting.glsl, and
// mesh-data.glsl.

// Lattice cells a tile of the clouds' noise spans: its coarsest octaves have about one feature a cell.
const float CloudCellsPerTile = 4.0;

// Lattice cells from a point to the read sunwards that lights its puff: under half the eroded puffs' width.
const float CloudPuffStep = 0.08;

// How many times the fall in shape over that step lights a puff fully (or its rise shades it).
const float CloudPuffGain = 4.0;

// The creases' blurred read: this many mip levels over the read's own, and at least this level, about a puff's width.
const float CloudCreaseLevels = 3.0;
const float CloudCreaseLeastLevel = 4.0;

// How many times the shape's dip under its blurred read darkens a crease fully (or its rise over it lightens a crown).
const float CloudCreaseGain = 4.0;

// With the look's reach, how much is added to the ray's rise a unit when finding where it meets the plane: the far
// clouds shrink in rows, but less flat than a plane seen edge-on.
const float CloudHorizonLift = 0.04;

// The most times one side of a read's footprint may be the other's: the cloud sampler's anisotropy, so a read at a
// slant picks its level by the footprint's short side and stays sharp across the screen.
const float CloudMostSlant = 8.0;

// With the look's reach, the shares of it over which the clouds' light fades to a fifth: far edges seen at a slant are
// thin lines, and a line of light reads as a scratch.
const float CloudFarLightStart = 0.25;
const float CloudFarLightEnd = 0.5;

// The share of the look's reach where the clouds start to thin.
const float CloudReachFadeShare = 0.5;

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

// The clouds' shape at the bent lattice point p, eaten at its thin edges by a five times finer read of the cellular
// noise as the look's erosion says.
float cloudEroded(float shape, vec2 p, vec2 dx, vec2 dy)
{
    if (LOOK.cloudErosion > 0.0)
    {
        vec4 detail = cloudRead(p + vec2(3.1, 7.7), dx, dy, 5.0);
        shape -= LOOK.cloudErosion * dot(detail.gba, vec3(0.625, 0.25, 0.125)) * (1.0 - shape);
    }

    return shape;
}

// The clouds' shape at the bent lattice point p (along their heading, drawn out, and across it), 0 to 1, eroded when
// asked.
float cloudShapeAt(vec2 p, vec2 dx, vec2 dy, bool eroded)
{
    float shape = cloudShape(cloudRead(p, dx, dy, 1.0));
    return eroded ? cloudEroded(shape, p, dx, dy) : shape;
}

// How thick the clouds are where their shape is shape, 0 to 1: covering the look's share of the sky, with the look's
// soft edges.
float cloudCovering(float shape)
{
    float edge = 1.0 - LOOK.cloudCover;
    return smoothstep(edge, edge + LOOK.cloudSoftness, shape);
}

// How thick the clouds are at the bent lattice point p, 0 to 1.
float cloudDensity(vec2 p, vec2 dx, vec2 dy, bool eroded)
{
    return cloudCovering(cloudShapeAt(p, dx, dy, eroded));
}

// The clouds' shape at the bent lattice point p, blurred over about a puff's width: read at a coarser mip level than
// its footprint (dx and dy) picks.
float cloudBlurredShape(vec2 p, vec2 dx, vec2 dy)
{
    float tiles = 1.0 / CloudCellsPerTile;
    float footprint = max(length(dx), length(dy)) * tiles * float(textureSize(cloudNoise, 0).x);
    float level = max(log2(max(footprint, 1e-6)) + CloudCreaseLevels, CloudCreaseLeastLevel);
    return cloudShape(textureLod(cloudNoise, p * tiles, level));
}

// A point of the plane, local, in the clouds' frame in metres: along their heading over their stretch, and across it.
vec2 cloudFrame(vec2 place)
{
    vec2 along = vec2(LOOK.cloudAlongX, LOOK.cloudAlongZ);
    return vec2(dot(place, along) / LOOK.cloudStretch, dot(place, vec2(-along.y, along.x)));
}

// A point of the lower wisps' plane, local, in their frame in metres: along the clouds' heading over the wisps' stretch,
// and across it.
vec2 cloudLowerFrame(vec2 place)
{
    vec2 along = vec2(LOOK.cloudAlongX, LOOK.cloudAlongZ);
    return vec2(dot(place, along) / LOOK.cloudLowerStretch, dot(place, vec2(-along.y, along.x)));
}

// How far along a ray whose direction rises upward a unit it meets a plane rise metres over the eye: with the look's
// reach, about where it does (CloudHorizonLift), no farther than the reach; without, softened near the horizon more, as
// the plane would reach the view's end.
float cloudDistance(float rise, float upward)
{
    rise = max(rise, 10.0);
    if (LOOK.cloudReach > 0.0)
    {
        return min(rise / max(upward + CloudHorizonLift, 1e-4), LOOK.cloudReach);
    }

    return rise / (max(upward, 0.0) + 0.1);
}

// How much of a cloud t metres away along a ray whose direction rises upward a unit shows, before the look's opacity:
// past half the look's reach it thins to none at the reach; without a reach, it thins by its angle over the horizon.
float cloudFade(float t, float upward)
{
    if (LOOK.cloudReach > 0.0)
    {
        float far = 1.0 - smoothstep(CloudReachFadeShare * LOOK.cloudReach, LOOK.cloudReach, t);
        return far * smoothstep(0.0, 0.02, upward);
    }

    return smoothstep(0.0, 0.3, upward);
}

// A read's footprint (dx and dy) with its longer side drawn in to CloudMostSlant times its shorter.
void cloudFootprint(inout vec2 dx, inout vec2 dy)
{
    float lx = length(dx);
    float ly = length(dy);
    float most = max(min(lx, ly) * CloudMostSlant, 1e-8);
    dx *= min(1.0, most / max(lx, 1e-8));
    dy *= min(1.0, most / max(ly, 1e-8));
}

// The sky colour of direction with the look's clouds over it. None while the cover is 0.
vec3 skyClouds(vec3 direction, vec3 colour)
{
    // Where the ray meets each plane. Worked out for every pixel, with the reads' footprints, before any pixel leaves.
    float t = cloudDistance(LOOK.cloudLocalHeight - view.position.y, direction.y);
    float lowerT = cloudDistance(LOOK.cloudLowerLocalHeight - view.position.y, direction.y);
    vec2 low = LOOK.cloudNear + cloudFrame(view.position.xz + direction.xz * t) * LOOK.cloudNearScale;
    vec2 high = LOOK.cloudFar + cloudFrame(view.position.xz + direction.xz * (2.0 * t)) * LOOK.cloudFarScale;
    vec2 wisp = LOOK.cloudLowerNear + cloudLowerFrame(view.position.xz + direction.xz * lowerT) * LOOK.cloudLowerScale;
    vec2 lowX = dFdx(low);
    vec2 lowY = dFdy(low);
    vec2 highX = dFdx(high);
    vec2 highY = dFdy(high);
    vec2 wispX = dFdx(wisp);
    vec2 wispY = dFdy(wisp);
    cloudFootprint(lowX, lowY);
    cloudFootprint(highX, highY);
    cloudFootprint(wispX, wispY);
    if (LOOK.cloudCover <= 0.0 || direction.y <= 0.0)
    {
        return colour;
    }

    float fade = cloudFade(t, direction.y) * LOOK.cloudOpacity;
    float sun = max(dot(direction, view.sunDirection), 0.0);
    vec3 glow = 0.5 * LOOK.sunGlow * pow(sun, LOOK.sunGlowSharpness);

    if (LOOK.cloudUpper > 0.0)
    {
        float thin = cloudDensity(cloudWarp(high, highX, highY), highX, highY, false) * fade * LOOK.cloudUpper;
        colour = mix(colour, LOOK.cloudColour * 1.2 + glow, thin);
    }

    low = cloudWarp(low, lowX, lowY);
    float body = cloudShape(cloudRead(low, lowX, lowY, 1.0));
    float shape = cloudEroded(body, low, lowX, lowY);
    float density = cloudCovering(shape);
    float dark = density;
    if (LOOK.cloudCreases > 0.0)
    {
        // A crease lies under its surroundings' shape, a puff's crown over it (the shape before its erosion, so the
        // eaten edges do not darken).
        float crease = clamp((cloudBlurredShape(low, lowX, lowY) - body) * CloudCreaseGain, -1.0, 1.0);
        dark = clamp(density * (1.0 + LOOK.cloudCreases * crease), 0.0, 1.5);
    }

    vec3 cloud = LOOK.cloudColour * mix(1.0, 0.55, dark) + glow;
    vec2 sunward = cloudFrame(view.sunDirection.xz);
    float lightNear = 1.0;
    if (LOOK.cloudReach > 0.0)
    {
        lightNear -= 0.8 * smoothstep(CloudFarLightStart * LOOK.cloudReach, CloudFarLightEnd * LOOK.cloudReach, t);
    }

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

            cloud += LOOK.cloudLight * lightNear * exp2(-depth * LOOK.cloudShadow);
        }
        else
        {
            // An edge whose cloud thins towards the sun faces it: the drop in thickness a quarter of a cell sunwards.
            float towards = cloudDensity(low + toward * 0.25, lowX, lowY, false);
            cloud += LOOK.cloudLight * (lightNear * clamp((density - towards) * 3.0, 0.0, 1.0) * (1.0 - 0.5 * density));
        }

        if (LOOK.cloudPuffLight > 0.0)
        {
            // A puff faces the sun where its shape falls sunwards, and turns from it where its shape rises.
            float ahead = cloudShapeAt(low + toward * CloudPuffStep, lowX, lowY, true);
            float facing = clamp((shape - ahead) * CloudPuffGain, -1.0, 1.0);
            cloud *= 1.0 + 0.5 * LOOK.cloudPuffLight * lightNear * facing;
            cloud += LOOK.cloudLight * (0.4 * LOOK.cloudPuffLight * lightNear * max(facing, 0.0));
        }
    }

    colour = mix(colour, cloud, density * fade);

    if (LOOK.cloudLower > 0.0)
    {
        // Streaks of the gradient noise drawn out along the wind, eaten by a finer read of the cellular noise.
        vec4 noise = cloudRead(wisp, wispX, wispY, 1.0);
        float fray = dot(cloudRead(wisp + vec2(6.3, 2.9), wispX, wispY, 2.0).gba, vec3(0.625, 0.25, 0.125));
        float streak = noise.r * (0.7 + 0.6 * noise.g) - 0.3 * fray;
        float thin = smoothstep(0.45, 0.75, streak) * 0.5 * cloudFade(lowerT, direction.y) * LOOK.cloudLower;
        colour = mix(colour, LOOK.cloudColour * 0.8 + 0.3 * LOOK.cloudLight + glow, thin);
    }

    return colour;
}
