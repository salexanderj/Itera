// The look's clouds, drawn by the sky's own pass alone (world-sky.frag; the fog takes the sky without them): clouds on a
// plane at the look's cloud height over the land, so they stay over it as the eye moves and drift along their heading,
// drawn out along it and bent across it by the look's warp. Their shape is cloud-shape.glsl's, its edges eaten into
// rounded lobes by a finer read as the look's erosion says; scraps narrower than a lobe are thin. Each takes the sky's
// light, less where it is thicker, and the look's cloud light by how little of the clouds lies towards the sun, read
// blurred over about a cloud's width: a mass lights on its sunward side and darkens softly in its middle and on its far
// side, and each billow is lit on its sunward side and shaded on its far side. With the look's phase that light depends
// on the view against the sun. No cloud is brighter than the sun. A second layer twice as high may show
// behind, and thin wisps lower down in front. With the look's thickness, each cloud's column rises over the plane, and
// the clouds low over the horizon stand as a bank of their sides. They take the look's share of the haze by their
// distance, and thin to none at the horizon, or past the look's reach, so the fogged far ground still meets the sky.
// Include after view.glsl, sky.glsl, lighting.glsl, mesh-data.glsl, and cloud-shape.glsl.

// How many times finer than the shape the erosion's read is: a whole number of its tiles fits the lattice's wrap.
const float CloudErosionScale = 3.0;

// The light's blurred reads: this many mip levels over the read's own, and at least this level (16 texels of a
// 512-texel tile, about 190 m at the clouds' usual scale), about the width of a cloud's lobe.
const float CloudLightLevels = 2.0;
const float CloudLightLeastLevel = 4.0;

// Lattice cells from a point to its three blurred reads towards the sun, past a lobe's width to a mass's.
const float CloudLightSteps[3] = float[](0.1, 0.25, 0.45);

// The billows' light: their relief is the shape plus this share of the erosion's lobes, read this many mip levels over
// the read's own and at least this level (so each billow rounds and no edge rims), at a step this many lattice cells
// towards the sun; this many times the rise over the step lights a billow's sunward side fully or shades its far side;
// the far side keeps this share less of the sky's light, and the sunward side takes this share of the cloud light.
const float CloudBillowRelief = 0.5;
const float CloudBillowLevels = 1.0;
const float CloudBillowLeastLevel = 1.0;
const float CloudBillowStep = 0.04;
const float CloudBillowGain = 4.0;
const float CloudBillowShade = 0.5;
const float CloudBillowLight = 0.8;

// How much of the sky a strip or scrap of cloud with no mass round it hides, and over how much of the shape's range
// its mass's blurred shape makes it whole.
const float CloudScrapOpacity = 0.35;
const float CloudScrapSoftness = 0.3;

// The share of the sky's light the deepest cloud keeps, and how much of its depth is its own shape rather than its mass
// blurred: a mass is lighter where it is thinner, so it shows its shape inside, but takes no sharp rims.
const float CloudBaseDark = 0.3;
const float CloudDetailShare = 0.6;

// With the look's phase: the forward lean of the light a thin cloud and a thick one scatter (a Henyey-Greenstein lobe's
// g); the backward lean of the light a thick cloud's many scatterings send back towards the sun, and its share at full
// thickness, so masses seen with the sun behind the eye are lit; and the most times the light the same from every side
// the phase may give.
const float CloudForwardThin = 0.75;
const float CloudForwardThick = 0.3;
const float CloudBackward = -0.35;
const float CloudBackShare = 0.5;
const float CloudPhaseMost = 3.0;

// With the look's reach, how much is added to the ray's rise a unit when finding where it meets the plane: the far
// clouds shrink in rows, but less flat than a plane seen edge-on.
const float CloudHorizonLift = 0.04;

// The most times one side of a read's footprint may be the other's: the cloud sampler's anisotropy, so a read at a
// slant picks its level by the footprint's short side and stays sharp across the screen.
const float CloudMostSlant = 8.0;

// With the look's reach, the rises a unit of a ray (the sines of its angle over the horizon) over which the clouds
// turn to their far look: seen at a slant, their ragged and soft edges are only smudges, and their lit edges lines that
// read as scratches.
const float CloudFarFrom = 0.2;
const float CloudFarTo = 0.06;

// The far look: how much of the clouds' light it takes away, of their edges' softness, and of the upper layer.
const float CloudFarLightCut = 0.5;
const float CloudFarSharpen = 0.45;
const float CloudFarUpperCut = 0.7;

// The far look: how much of the look's erosion it keeps, so the far clouds keep some ragged detail and do not turn to
// blobs.
const float CloudFarErosion = 0.4;

// The share of the look's reach where the clouds start to thin.
const float CloudReachFadeShare = 0.3;

// With the look's thickness: the steps through the slab, each at the middle of its share. Each side grows over more
// than three steps' heights (CloudSideSoftness), so the steps show as no lines.
const int CloudSlabSteps = 12;

// With the look's thickness: the rises a unit of a ray over which the sides come in, none overhead, so the clouds
// overhead keep their flat look and those low over the horizon stand as a bank.
const float CloudSideFrom = 0.45;
const float CloudSideTo = 0.25;

// With the look's thickness: the share of the slab's height over which a column's side grows opaque, how much of what
// lies behind a step's side hides, how much less dark a side is at the slab's top than at its base, and how fast the
// cloud above a side darkens it (halving its light each this share of the slab).
const float CloudSideSoftness = 0.3;
const float CloudSideOpacity = 0.8;
const float CloudTopBrighten = 0.3;
const float CloudSideShadow = 6.0;

// With the look's thickness and reach: the rises a unit of a ray over which the bank of sides fades into the haze, by
// its angle, as a fade by distance would end in an edge a degree high where the bank is dense.
const float CloudBankFadeFrom = 0.14;
const float CloudBankFadeTo = 0.035;

// With the look's thickness: the share of the cloud light a side takes when it faces the sun, and when it faces away.
const float CloudSideLitFacing = 0.6;
const float CloudSideLitAway = 0.15;

// The clouds' noise at the lattice point p, scale times finer, read over the pixel's footprint (dx and dy, the point's
// change across the pixel, worked out before any pixel left the sky's shader, so every read picks its level aright).
// It repeats every tile, and the lattice point wraps at a whole number of tiles.
vec4 cloudRead(vec2 p, vec2 dx, vec2 dy, float scale)
{
    float tiles = scale / CloudCellsPerTile;
    return textureGrad(cloudNoise, p * tiles, dx * tiles, dy * tiles);
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

// The clouds' shape at the bent lattice point p, eaten at its thin edges by erosion (the look's, less with distance): a
// finer read of the cellular noise takes cloud from between its cells' points, so the edges break into rounded lobes,
// puffs on the puffs.
float cloudEroded(float shape, vec2 p, vec2 dx, vec2 dy, float erosion)
{
    if (erosion > 0.0)
    {
        vec4 detail = cloudRead(p + vec2(3.1, 7.7), dx, dy, CloudErosionScale);
        shape -= erosion * (1.0 - dot(detail.gba, vec3(0.625, 0.25, 0.125))) * (1.0 - shape);
    }

    return shape;
}

// The billows' relief at the bent lattice point p: the shape at mip level level, raised by the erosion's lobes, read at
// the same blur of their finer tile.
float cloudRelief(vec2 p, float level)
{
    vec4 detail = cloudReadLod(p + vec2(3.1, 7.7), CloudErosionScale, level + log2(CloudErosionScale));
    return cloudShape(cloudReadLod(p, 1.0, level)) + CloudBillowRelief * dot(detail.gba, vec3(0.625, 0.25, 0.125));
}

// How thick the clouds are at the bent lattice point p, 0 to 1, uneroded, with edges softness wide.
float cloudDensity(vec2 p, vec2 dx, vec2 dy, float softness)
{
    return cloudCovering(cloudShape(cloudRead(p, dx, dy, 1.0)), softness);
}

// The mip level a read at scale 1 of footprint dx and dy picks (a level's texels across the pixel, as a power of 2).
float cloudLevel(vec2 dx, vec2 dy)
{
    float texels = max(length(dx), length(dy)) / CloudCellsPerTile * float(textureSize(cloudNoise, 0).x);
    return log2(max(texels, 1e-6));
}

// A Henyey-Greenstein lobe of lean g at the cosine cosine of the angle from the light's way, times 4 pi: 1 on average
// over every direction.
float cloudLobe(float g, float cosine)
{
    return (1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * cosine, 1.5);
}

// How much of the cloud light a cloud of thickness density (0 to 1) sends towards the eye along direction, by the
// look's phase: a forward lobe, narrow for thin cloud and wide for thick, so thin edges glow towards the sun and dim
// away from it, and for thick cloud a share of a backward lobe, so masses glow with the sun behind the eye. Blended
// from light the same from every side, and at most CloudPhaseMost times it.
float cloudPhase(vec3 direction, float density)
{
    if (LOOK.cloudPhase <= 0.0)
    {
        return 1.0;
    }

    float cosine = dot(direction, view.sunDirection);
    float forward = cloudLobe(mix(CloudForwardThin, CloudForwardThick, density), cosine);
    float lobe = mix(forward, cloudLobe(CloudBackward, cosine), CloudBackShare * density);
    return mix(1.0, min(lobe, CloudPhaseMost), LOOK.cloudPhase);
}

// colour dimmed, where it must be, to the look's brightest cloud: a share of the sky's brightness towards the sun.
vec3 cloudBelowSun(vec3 colour)
{
    float bright = dot(colour, LumaWeights);
    return bright > LOOK.cloudBrightest ? colour * (LOOK.cloudBrightest / bright) : colour;
}

// The share of each colour of a cloud t metres away that the look's share of the haze hides: as the land's haze
// (hazeOver), at the clouds' own density.
vec3 cloudHaze(float t)
{
    float density = LOOK.cloudHazePerMetre;
    if (density <= 0.0)
    {
        return vec3(0.0);
    }

    return vec3(
        hazeOver(t, density * LOOK.hazeFade.r),
        hazeOver(t, density * LOOK.hazeFade.g),
        hazeOver(t, density * LOOK.hazeFade.b)
    );
}

// A point of the lower wisps' plane, local, in their frame in metres: along the clouds' heading over the wisps'
// stretch, and across it.
vec2 cloudLowerFrame(vec2 place)
{
    vec2 along = vec2(LOOK.cloudAlongX, LOOK.cloudAlongZ);
    return vec2(dot(place, along) / LOOK.cloudLowerStretch, dot(place, vec2(-along.y, along.x)));
}

// How far along a ray whose direction rises upward a unit it meets a plane rise metres over the eye: with the look's
// reach, about where it does (CloudHorizonLift); without, softened near the horizon more, as the plane would reach the
// view's end.
float cloudPlane(float rise, float upward)
{
    rise = max(rise, 10.0);
    return LOOK.cloudReach > 0.0 ? rise / max(upward + CloudHorizonLift, 1e-4) : rise / (max(upward, 0.0) + 0.1);
}

// The same, no farther than the look's reach.
float cloudDistance(float rise, float upward)
{
    float t = cloudPlane(rise, upward);
    return LOOK.cloudReach > 0.0 ? min(t, LOOK.cloudReach) : t;
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

// The clouds' sides a ray passes in the slab, from its warped lattice point at the base plane (entry, t0 metres along
// it) to the one at the top plane (exit), each with its footprint: at each step, front to back until little shows
// behind, the column rising over the ray's height hides what lies behind, darker low down and lit where little of the
// column lies over it. Returns the colour (times its share) and the share of the sky it hides.
vec4 cloudSides(
    vec2 entry,
    vec2 entryX,
    vec2 entryY,
    vec2 exit,
    vec2 exitX,
    vec2 exitY,
    float t0,
    float upward,
    vec3 lit,
    vec3 glow
)
{
    vec4 sum = vec4(0.0);
    for (int i = 0; i < CloudSlabSteps; i++)
    {
        float share = (float(i) + 0.5) / float(CloudSlabSteps);
        vec2 dx = mix(entryX, exitX, share);
        vec2 dy = mix(entryY, exitY, share);
        float shape = cloudShape(cloudRead(mix(entry, exit, share), dx, dy, 1.0));
        // How far the column rises over the ray here, as a share of the slab.
        float over = cloudColumn(shape) - share;
        float hides = smoothstep(0.0, CloudSideSoftness, over) * CloudSideOpacity;
        if (hides > 0.0)
        {
            vec3 side = LOOK.cloudColour * mix(1.0, 0.55, 1.0 - CloudTopBrighten * share) + glow;
            side += lit * exp2(-over * CloudSideShadow);
            sum += (1.0 - sum.a) * hides * vec4(side, 1.0);
            if (sum.a > 0.98)
            {
                break;
            }
        }
    }

    // The bank fades by its angle over the horizon with a reach, so its far edge thins smoothly into the haze, and by
    // where the ray enters it without one.
    float fade = LOOK.cloudReach > 0.0 ? smoothstep(CloudBankFadeTo, CloudBankFadeFrom, upward) : cloudFade(t0, upward);
    return sum * fade * LOOK.cloudOpacity;
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
    // The slab's top past the reach too, so a ray low over the horizon finds its heights in the slab aright.
    float topT = cloudPlane(LOOK.cloudLocalHeight + LOOK.cloudThickness - view.position.y, direction.y);
    vec2 low = LOOK.cloudNear + cloudFrame(view.position.xz + direction.xz * t) * LOOK.cloudNearScale;
    vec2 top = LOOK.cloudNear + cloudFrame(view.position.xz + direction.xz * topT) * LOOK.cloudNearScale;
    vec2 high = LOOK.cloudFar + cloudFrame(view.position.xz + direction.xz * (2.0 * t)) * LOOK.cloudFarScale;
    vec2 wisp = LOOK.cloudLowerNear + cloudLowerFrame(view.position.xz + direction.xz * lowerT) * LOOK.cloudLowerScale;
    vec2 lowX = dFdx(low);
    vec2 lowY = dFdy(low);
    vec2 highX = dFdx(high);
    vec2 highY = dFdy(high);
    vec2 wispX = dFdx(wisp);
    vec2 wispY = dFdy(wisp);
    vec2 topX = dFdx(top);
    vec2 topY = dFdy(top);
    cloudFootprint(lowX, lowY);
    cloudFootprint(topX, topY);
    cloudFootprint(highX, highY);
    cloudFootprint(wispX, wispY);
    if (LOOK.cloudCover <= 0.0 || direction.y <= 0.0)
    {
        return colour;
    }

    float fade = cloudFade(t, direction.y) * LOOK.cloudOpacity;
    float sun = max(dot(direction, view.sunDirection), 0.0);
    vec3 glow = 0.5 * LOOK.sunGlow * pow(sun, LOOK.sunGlowSharpness);
    // How much of each colour of the clouds the haze leaves, by where the ray meets their plane (past the reach too).
    float planeT = cloudPlane(LOOK.cloudLocalHeight - view.position.y, direction.y);
    vec3 keep = 1.0 - cloudHaze(planeT);

    // How far into their far look the clouds here are, 0 to 1: none without a reach.
    float far = 0.0;
    if (LOOK.cloudReach > 0.0)
    {
        far = 1.0 - smoothstep(CloudFarTo, CloudFarFrom, direction.y);
    }

    if (LOOK.cloudUpper > 0.0)
    {
        float thin = cloudDensity(cloudWarp(high, highX, highY), highX, highY, LOOK.cloudSoftness) * fade;
        vec3 upper = cloudBelowSun(LOOK.cloudColour * 1.2 + glow);
        vec3 share = thin * LOOK.cloudUpper * (1.0 - CloudFarUpperCut * far) * (1.0 - cloudHaze(2.0 * planeT));
        colour = mix(colour, upper, share);
    }

    float erosion = LOOK.cloudErosion * mix(1.0, CloudFarErosion, far);
    float softness = LOOK.cloudSoftness * (1.0 - CloudFarSharpen * far);
    float lightNear = 1.0 - CloudFarLightCut * far;
    low = cloudWarp(low, lowX, lowY);
    float shape = cloudEroded(cloudShape(cloudRead(low, lowX, lowY, 1.0)), low, lowX, lowY, erosion);
    // The clouds blurred over about a lobe's width: their masses, where the light goes through them and where not.
    float level = max(cloudLevel(lowX, lowY) + CloudLightLevels, CloudLightLeastLevel);
    float mass = cloudShape(cloudReadLod(low, 1.0, level));
    // A strip or scrap narrower than a lobe is thin cloud, which the sky shows through, so it does not stand as a line.
    float massCover = cloudCovering(mass, CloudScrapSoftness);
    float density = cloudCovering(shape, softness) * mix(CloudScrapOpacity, 1.0, massCover);
    // The sky's light from below, less where the cloud is deeper: by its mass and its own shape.
    float thickness = mix(cloudDepth(mass), cloudDepth(shape), CloudDetailShare);
    vec3 cloud = LOOK.cloudColour * mix(1.0, CloudBaseDark, thickness) + glow;
    vec2 sunward = cloudFrame(view.sunDirection.xz);

    if (dot(LOOK.cloudLight, vec3(1.0)) > 0.0 && dot(sunward, sunward) > 1e-6)
    {
        vec2 toward = normalize(sunward);
        float reach;
        if (LOOK.cloudShadow > 0.0)
        {
            // The cloud on the way towards the sun, blurred: little on a mass's sunward side, much in its middle and
            // on its far side, so the light turns softly across a mass and never rims a thin strip.
            float depth = 0.5 * cloudDepth(mass);
            for (int i = 0; i < 3; i++)
            {
                float ahead = cloudShape(cloudReadLod(low + toward * CloudLightSteps[i], 1.0, level));
                depth += cloudDepth(ahead);
            }

            reach = exp2(-depth * LOOK.cloudShadow);
        }
        else
        {
            // An edge whose cloud thins towards the sun faces it: the drop in thickness a quarter of a cell sunwards.
            float towards = cloudDensity(low + toward * 0.25, lowX, lowY, softness);
            reach = clamp((density - towards) * 3.0, 0.0, 1.0) * (1.0 - 0.5 * density);
        }

        // By the mass's thickness, so a thin margin glows towards the sun across its width, not as a line at its edge.
        float phase = cloudPhase(direction, massCover);
        cloud += LOOK.cloudLight * (lightNear * reach * phase);

        // The billows: the shape with the lobes' relief, a little blurred, against the same a short step sunwards. Each
        // billow is lit on its side towards the sun and shaded on its far side, the more where the mass lets light in.
        float billowLevel = max(cloudLevel(lowX, lowY) + CloudBillowLevels, CloudBillowLeastLevel);
        float rise = cloudRelief(low, billowLevel) - cloudRelief(low + toward * CloudBillowStep, billowLevel);
        float facing = clamp(rise * CloudBillowGain, -1.0, 1.0) * lightNear;
        cloud *= 1.0 + CloudBillowShade * min(facing, 0.0);
        cloud += LOOK.cloudLight * (CloudBillowLight * max(facing, 0.0) * mix(0.35, 1.0, reach) * phase);
    }

    cloud = cloudBelowSun(cloud);

    // With the look's thickness, the sides beyond the base lie behind it, where the base does not hide them.
    float sideShare = LOOK.cloudThickness > 0.0 ? 1.0 - smoothstep(CloudSideTo, CloudSideFrom, direction.y) : 0.0;
    if (sideShare > 0.0 && density * fade < 0.99)
    {
        // The sides seen face the eye: lit when the sun is behind it, in their own shade when it looks towards the sun.
        float facing = 0.5 - 0.5 * dot(normalize(direction.xz), normalize(view.sunDirection.xz + vec2(1e-5)));
        vec3 lit = LOOK.cloudLight * lightNear * mix(CloudSideLitAway, CloudSideLitFacing, facing);
        vec2 exit = cloudWarp(top, topX, topY);
        // The haze of the base's distance on the whole bank: by each step's own, its edges would show.
        vec4 sides = cloudSides(low, lowX, lowY, exit, topX, topY, t, direction.y, lit, glow) * sideShare;
        colour = colour * (1.0 - sides.a * keep) + sides.rgb * keep;
    }

    colour = mix(colour, cloud, density * fade * keep);

    if (LOOK.cloudLower > 0.0)
    {
        // Streaks of the gradient noise drawn out along the wind, eaten by a finer read of the cellular noise.
        vec4 noise = cloudRead(wisp, wispX, wispY, 1.0);
        float fray = dot(cloudRead(wisp + vec2(6.3, 2.9), wispX, wispY, 2.0).gba, vec3(0.625, 0.25, 0.125));
        float streak = noise.r * (0.7 + 0.6 * noise.g) - 0.3 * fray;
        float thin = smoothstep(0.45, 0.75, streak) * 0.5 * cloudFade(lowerT, direction.y) * LOOK.cloudLower;
        vec3 lowerKeep = 1.0 - cloudHaze(cloudPlane(LOOK.cloudLowerLocalHeight - view.position.y, direction.y));
        vec3 wisps = cloudBelowSun(LOOK.cloudColour * 0.8 + 0.3 * LOOK.cloudLight + glow);
        colour = mix(colour, wisps, thin * lowerKeep);
    }

    return colour;
}
