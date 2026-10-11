// The world's materials as mesh.frag draws them (docs/rendering-redesign.md, the materials plan): from texture sets,
// photographs or recipes baked into images (surfaceTextured, read from the sides of x, y, and z the surface faces), or
// plain; and the recipes themselves (surfacePattern), which material-bake.comp works out at every detail into a tile,
// ported from the shelved raymarcher's voxel-surface.glsl (tag raymarcher-final). Each material names a recipe
// (Itera.Engine.Rendering.Voxels.VoxelPattern) built from a few pieces: value noise with its gradient, cellular noise
// (distances to the nearest random points of a lattice, whose borders give cracks and gaps), a grid of blocks in
// courses, cut-offs that turn noise into sparse features, and a warp that bends straight features. Colours come from the
// material's colour blended towards its tint colour, its accent colour, and darker shades of both, not from brightness
// alone. Everything is keyed on the absolute point in centimetres as an integer plus a fraction. A pattern's features
// fade by a footprint (0 in the bake, every detail: the mip levels fade them now) to their means (MeanPlateShare and the
// rest). Needs mesh-data.glsl; with MESH_SURFACE_BAKE, the bake's part alone (no view, no texture arrays).

#define VOXEL_PATTERN_GRAIN 1u
#define VOXEL_PATTERN_STRATA 2u
#define VOXEL_PATTERN_PEBBLES 3u
#define VOXEL_PATTERN_CLUMPS 4u
#define VOXEL_PATTERN_CLUSTERS 5u
#define VOXEL_PATTERN_PLATES 6u
#define VOXEL_PATTERN_CRACKS 7u
#define VOXEL_PATTERN_BLOCKS 8u
#define VOXEL_PATTERN_SOIL 9u
#define VOXEL_PATTERN_STREAKS 10u
#define VOXEL_PATTERN_MOTTLE 11u
#define VOXEL_LOOK_LIVING_ACCENT (1u << 15u)

// How far the large variation moves a colour at full strength.
const float PatchContrast = 0.35;

// How far a pattern's relief tilts the normal at full relief, per unit of height change across one of its cells.
const float ReliefScale = 0.35;

struct Surface
{
    vec3 colour;
    // The relief's slope in the surface's plane: the lit normal is normalize(normal - slope).
    vec3 slope;
    // The share of sky and ground light that reaches the pattern's hollows, 1 on its crests.
    float cavity;
    // The material's share of full glow, 0 to 1; mesh.frag scales it by the look's glow strength.
    float glow;
};

#include "surface-noise.glsl"

// The means that features fade to past their size, so a surface keeps its mean colour at any distance (worked out over
// many cells of each recipe's layout): the share of bark that is plate rather than crack, a plate's mean shade, the
// share of the ground a pebble covers for each part of the accent share that has pebbles (and its hollow's), and a
// grass clump's mean darkening towards its edges.
const float MeanPlateShare = 0.72;
const float MeanPlateShade = 1.016;
const float PebbleCover = 0.31;
const float PebbleHollow = 0.055;
const float ClumpHollow = 0.33;

// How much of a feature size voxels across is kept at a pixel footprint: 1 while a pixel covers under a quarter of it,
// half where a pixel covers half of it (the sampling limit, past which it would alias), 0 once a pixel covers it. The
// step runs over the logarithm of the ratio, so on ground seen along the view, whose footprint grows with the square of
// the distance, it still spreads over twice the distance instead of showing as a line (2026-10-05; it ran from 0.35 to
// 1.5 of the size before, half at 0.93).
float fadeFor(float footprint, float size)
{
    return 1.0 - smoothstep(-2.0, 0.0, log2(max(footprint / size, 1e-6)));
}

// Bark cells on one plane (whole and part are the point's two coordinates on it, the second along the trunk): cells
// 2^shift wide and 2^(shift + aspect) tall. plate is 0 in the cracks between cells and 1 inside them, from a border
// crackWidth of a cell wide, drawn at least footprint (a pixel, in cell widths) wide; gradient is its gradient per cell
// width; id is the cell's hash.
void barkCells(ivec2 whole, vec2 part, int shift, int aspect, float crackWidth, uint salt, float footprint,
    out float plate, out vec2 gradient, out float id)
{
    // A sideways wave of up to half a cell along the trunk bends the cells, so they are uneven in shape and size.
    float warp = valueNoise1(whole.y, part.y, shift + aspect + 1, salt + 100u);
    vec2 bent = part + vec2(0.5 * float(1 << shift) * warp, 0.0);
    Cells cells = cells2(whole + ivec2(floor(bent)), fract(bent), ivec2(shift, shift + aspect), salt);
    float border = cells.f2 - cells.f1 - 0.02;
    // A crack narrower than a pixel (footprint, in cell widths) is drawn a pixel wide and as much fainter, so the network
    // keeps its mean darkness instead of breaking into specks. The fainting goes by the cracks' mean width, the same on
    // both sides of a crack, whose two cells have their own widths: by a cell's own it would step along its middle.
    float strength = crackWidth / max(crackWidth, footprint);
    crackWidth *= 0.6 + 0.8 * fract(cells.id * 17.3);
    float drawn = max(crackWidth, footprint);
    plate = 1.0 - strength * (1.0 - smoothRamp(border, drawn));
    gradient = strength * smoothDerivative(border, drawn) * borderGradient(cells) * vec2(1.0, 1.0 / float(1 << aspect));
    id = cells.id;
}

// Blocks laid in courses on one plane (whole and part are the point's two coordinates on it, the second up): courses
// 2^shift high, blocks up to 2^(shift + aspect) long, each course shifted along by a random amount and some blocks
// split in two. edge is the distance from the point to the nearest joint in centimetres, away the direction from
// that joint on the plane, and id the block's hash, 0 to 1.
void blockJoints(ivec2 whole, vec2 part, int shift, int aspect, uint salt, out float edge, out vec2 away, out float id)
{
    int course = whole.y >> shift;
    int lengthShift = shift + aspect;
    float length = float(1 << lengthShift);
    int along = whole.x + int(latticeHash(ivec3(course, 0, 0), salt) * length);
    int block = along >> lengthShift;
    float u = float(along - (block << lengthShift)) + part.x;
    float v = float(whole.y - (course << shift)) + part.y;
    vec3 r = latticeHash3(ivec3(block, course, 0), salt);
    // Half the blocks are split somewhere in their middle half.
    float split = r.y < 0.5 ? length * (0.25 + 0.5 * r.z) : 0.0;
    bool first = u < split;
    float start = first ? 0.0 : split;
    float end = first ? split : length;
    id = first ? fract(r.x * 7.3) : r.x;
    float height = float(1 << shift);
    float du = min(u - start, end - u);
    float dv = min(v, height - v);
    edge = min(du, dv);
    away = du < dv ? vec2(u - start < end - u ? 1.0 : -1.0, 0.0) : vec2(0.0, v < 0.5 * height ? 1.0 : -1.0);
}

// The strata's colour by height in centimetres: warm brown near the surface, grey below, dark slate from about
// -10 m, and near-black basalt below -25 m.
vec3 strataTint(float height)
{
    float depth = clamp(-height / 2500.0, 0.0, 1.0);
    vec3 tint = mix(vec3(1.05, 0.95, 0.82), vec3(0.95, 0.97, 1.02), smoothstep(0.02, 0.2, depth));
    tint = mix(tint, vec3(0.62, 0.63, 0.7), smoothstep(0.3, 0.6, depth));
    return mix(tint, vec3(0.34, 0.33, 0.35), smoothstep(0.75, 1.0, depth));
}

// A surface of colour, relief slope (the lit normal is normalize(normal - slope), taken here into the surface's plane),
// and cavity, with the variation over about 10 m and 0.6 m that never fades, and the glow of amount (the material's
// contrast, relief, patches, and glow). The 10 m octave is taken at the turned point, so its square cells do not line up
// with those of the other. A third octave over 40 m cost 0.6 % in the benchmark view looking down at grass, which took it
// past 8 % over the build before the surface look; grass has a 20 m drift of its own.
Surface surfaceFinish(vec3 colour, vec3 slope, float cavity, ivec3 whole, vec3 part, vec3 normal, vec4 amount)
{
    ivec3 turnedWhole;
    vec3 turnedPart;
    turnPoint(whole, part, turnedWhole, turnedPart);
    float patches = 0.6 * valueNoise2(turnedWhole.xz, turnedPart.xz, 10, 1u) + 0.4 * valueNoise(whole, part, ivec3(6), 2u).x;
    Surface surface;
    surface.colour = max(colour * (1.0 + PatchContrast * amount.z * patches), vec3(0.0));
    surface.slope = slope - dot(slope, normal) * normal;
    surface.cavity = cavity;
    surface.glow = amount.w;
    return surface;
}

// The colour of material index where the tint map is tint.
vec3 baseColour(uint index, float tint)
{
    return mix(materials[index].colour, materials[index].tintColour, tint);
}

// Strata's faint bands by height, wandering by up to 1.5 m over about 20 m, and its colour by depth (strataTint): a
// colour factor at the point whole + part, which the world's pixels take (a 2D tile cannot hold them).
vec3 strataBands(ivec3 whole, vec3 part)
{
    float height = float(whole.y) + part.y + 150.0 * valueNoise2(whole.xz, part.xz, 11, 4u);
    float band = floor(height);
    float bands = valueNoise1(int(band), height - band, 6, 5u) + 0.5 * valueNoise1(int(band), height - band, 4, 6u);
    return strataTint(height) * (1.0 + 0.08 * bands);
}

// The point whole + part moved by offset centimetres on a plane, as a whole centimetre and a fraction again.
void movePoint(ivec2 whole, vec2 part, vec2 offset, out ivec2 movedWhole, out vec2 movedPart)
{
    vec2 moved = part + offset;
    vec2 steps = floor(moved);
    movedWhole = whole + ivec2(steps);
    movedPart = moved - steps;
}

// A small step of hue for a feature with the hash id, 0 to 1: from a little warmer to a little cooler.
vec3 hueStep(float id)
{
    return mix(vec3(1.07, 1.0, 0.88), vec3(0.9, 0.97, 1.1), id);
}

// Pebbles of the soil recipe in the accent's share of cells 2^shift cm wide (more and larger as the share nears 1): each
// a flat fill in the accent colour, its own shade and hue, lighter in its middle, with a light rim inside its edge;
// stone is 1 on a pebble, dome its height there, and shadow the ring of shadow it throws on the ground round it.
void soilPebbles(uint index, ivec2 whole, vec2 part, int shift, float accent, float contrast, uint salt, out float stone,
    out float dome, out float shadow, out vec3 pebble)
{
    float size = float(1 << shift);
    // The cells' points moved by up to a twelfth of a cell, so the pebbles' outlines are uneven.
    vec2 warp = 0.08 * size * vec2(valueNoise2(whole, part, shift - 2, salt + 10u), valueNoise2(whole, part, shift - 2, salt + 11u));
    ivec2 movedWhole;
    vec2 movedPart;
    movePoint(whole, part, warp, movedWhole, movedPart);
    Cells cells = cells2(movedWhole, movedPart, ivec2(shift), salt);
    float radius = mix(0.2, 0.4, smoothstep(0.5, 0.9, accent)) + 0.17 * fract(cells.id * 7.31);
    float d = cells.f1 / radius;
    float has = step(fract(cells.id * 11.7), accent);
    stone = has * (1.0 - smoothstep(0.92, 1.0, d));
    dome = sqrt(max(0.0, 1.0 - d * d));
    pebble = materials[index].accentColour * (1.0 + contrast * (0.9 * fract(cells.id * 13.7) - 0.45))
        * hueStep(fract(cells.id * 5.3));
    pebble *= (0.68 + 0.3 * dome) * (1.0 + 0.2 * smoothstep(0.62, 0.8, d) * (1.0 - smoothstep(0.82, 0.95, d)));
    shadow = has * (1.0 - stone) * (1.0 - smoothstep(1.0, 1.35, d));
}

// The soil recipe at the point whole + part on the ground's plane: the material's colour in soft round patches of
// 0.15 to 0.6 m, a little lighter and warmer or darker and cooler; sparse light and dark specks a few centimetres wide; and
// pebbles (soilPebbles) in the accent's share of cells 2^shift cm wide, with a sparser layer twice their size, kept
// within reach of the ground's value. The art direction's first look (docs/rendering-redesign.md): a few steps up from
// a flat colour, and pebbles only where the material is made of them (gravel). height is also the hollows' light.
void soilAt(uint index, ivec2 whole, vec2 part, int shift, float accent, float contrast, out vec3 colour,
    out float height)
{
    vec3 base = baseColour(index, 0.0);
    // Gradient noise, whose blobs are round where value noise's are square.
    ivec3 point = ivec3(whole.x, 0, whole.y);
    vec3 fraction = vec3(part.x, 0.0, part.y);
    float patches = gradientNoise(point, fraction, ivec3(6, 0, 6), 71u) + 0.5 * gradientNoise(point, fraction, ivec3(5, 0, 5), 72u)
        + 0.25 * gradientNoise(point, fraction, ivec3(4, 0, 4), 78u);
    float tone = smoothstep(-0.7, 0.7, patches);
    vec3 ground = base * mix(vec3(0.93, 0.95, 0.99), vec3(1.05, 1.03, 0.96), tone);
    Cells specks = cells2(whole, part, ivec2(2), 73u);
    float speck = (1.0 - smoothstep(0.14, 0.24, specks.f1)) * step(0.72, fract(specks.id * 3.1));
    ground *= 1.0 + 0.4 * contrast * speck * (fract(specks.id * 7.3) > 0.5 ? 1.0 : -1.0);
    height = 0.9 + 0.1 * tone;
    colour = ground;
    if (accent <= 0.0)
    {
        return;
    }

    float stone;
    float dome;
    float shadow;
    vec3 pebble;
    soilPebbles(index, whole, part, shift, accent, contrast, 76u, stone, dome, shadow, pebble);
    float bigStone;
    float bigDome;
    float bigShadow;
    vec3 big;
    soilPebbles(index, whole, part, shift + 1, 0.3 * accent, contrast, 77u, bigStone, bigDome, bigShadow, big);
    shadow = max(shadow, bigShadow);
    ground *= 1.0 - 0.45 * shadow;
    pebble = mix(mix(ground, pebble, 0.7), mix(ground, big, 0.7), bigStone);
    dome = mix(dome, bigDome, bigStone);
    stone = max(stone, bigStone);
    colour = mix(ground, pebble, stone);
    height = mix(height - 0.35 * shadow, 0.9 + 0.1 * dome, stone);
}

// The streaks recipe at the point whole + part on a plane: soft streaks along the plane's second axis (up a trunk),
// 2^shift cm wide and 2^aspect times as long, in two tones a little lighter and warmer or darker and cooler, with soft
// furrows where the streaks' noise crosses its middle, darker and towards the accent by the accent's share. The art
// direction's simple bark (docs/rendering-redesign.md). height is also the hollows' light.
void streaksAt(uint index, ivec2 whole, vec2 part, int shift, int aspect, float accent, float contrast, out vec3 colour,
    out float height)
{
    vec3 base = baseColour(index, 0.0);
    ivec3 point = ivec3(whole, 0);
    vec3 fraction = vec3(part, 0.0);
    // The furrows bent sideways by up to a quarter of their width, so they wander.
    float sway = 0.25 * float(1 << shift) * gradientNoise(point, fraction, ivec3(shift + 1, shift + aspect + 1, 0), 83u);
    ivec2 movedWhole;
    vec2 movedPart;
    movePoint(whole, part, vec2(sway, 0.0), movedWhole, movedPart);
    ivec3 moved = ivec3(movedWhole, 0);
    vec3 movedFraction = vec3(movedPart, 0.0);
    float ridge = gradientNoise(moved, movedFraction, ivec3(shift, shift + aspect, 0), 81u)
        + 0.4 * gradientNoise(moved, movedFraction, ivec3(shift - 1, shift + aspect - 1, 0), 82u);
    float furrow = 1.0 - smoothstep(0.0, 0.3, abs(ridge));
    float tone = smoothstep(-0.8, 0.8, gradientNoise(point, fraction, ivec3(shift + 1, shift + aspect, 0), 84u));
    vec3 bark = base * mix(vec3(0.92, 0.94, 0.98), vec3(1.06, 1.03, 0.95), tone);
    vec3 inner = mix(base * 0.5, materials[index].accentColour * 0.7, accent);
    colour = mix(bark, inner, furrow * contrast);
    height = 1.0 - 0.5 * furrow * contrast;
}

// The mottle recipe at the point whole + part on a plane: the colour in soft round patches of 2^(shift - 1) to
// 2^(shift + 1) cm, lighter and warmer or darker and cooler by the contrast, over a faint grain of 4 cm. The art
// direction's simple stone (docs/rendering-redesign.md); its bands by height go on per pixel, as strata's do. height is
// also the hollows' light.
void mottleAt(uint index, ivec2 whole, vec2 part, int shift, float contrast, out vec3 colour, out float height)
{
    ivec3 point = ivec3(whole.x, 0, whole.y);
    vec3 fraction = vec3(part.x, 0.0, part.y);
    float patches = gradientNoise(point, fraction, ivec3(shift + 1, 0, shift + 1), 91u)
        + 0.5 * gradientNoise(point, fraction, ivec3(shift, 0, shift), 92u)
        + 0.25 * gradientNoise(point, fraction, ivec3(shift - 1, 0, shift - 1), 93u);
    float tone = smoothstep(-0.8, 0.8, patches);
    float grain = gradientNoise(point, fraction, ivec3(2, 0, 2), 94u);
    vec3 shade = mix(vec3(0.9, 0.92, 0.97), vec3(1.07, 1.04, 0.96), tone);
    colour = baseColour(index, 0.0) * mix(vec3(1.0), shade, 2.0 * contrast) * (1.0 + 0.06 * contrast * grain);
    height = 0.85 + 0.15 * tone;
}

// The recipes worked out on a plane (soil, streaks, mottle): colour and height at the point whole + part on it.
void planeRecipeAt(uint pattern, uint index, ivec2 whole, vec2 part, int shift, int aspect, float accent, float contrast,
    out vec3 colour, out float height)
{
    if (pattern == VOXEL_PATTERN_SOIL)
    {
        soilAt(index, whole, part, shift, accent, contrast, colour, height);
    }
    else if (pattern == VOXEL_PATTERN_MOTTLE)
    {
        mottleAt(index, whole, part, shift, contrast, colour, height);
    }
    else
    {
        streaksAt(index, whole, part, shift, aspect, accent, contrast, colour, height);
    }
}

// How many centimetres of relief a plane recipe's height of 1 stands for, before the material's relief and ReliefScale.
const float PlaneDepth = 12.0;

// The pattern of material index at the point whole + part, blended tint of the way from its base to its tint colour,
// where the smooth normal is normal and a pixel covers footprint centimetres (0 for every detail: the bake,
// material-bake.comp): its colour, its relief's slope (by the material's relief), and how much its hollows hide it from
// the sky. The material's fields are read where they are used, to keep few values live.
void surfacePattern(uint index, float tint, ivec3 whole, vec3 part, vec3 normal, float footprint, out vec3 colour,
    out vec3 gradient, out float cavity)
{
    uint look = materials[index].look;
    uint pattern = look & 255u;
    int shift = int((look >> 8u) & 15u);
    int aspect = int((look >> 12u) & 7u);
    // An accent that lives (fresh leaves, moss) dies away where the tint is full.
    float accent = float((look >> 16u) & 255u) / 255.0 * ((look & VOXEL_LOOK_LIVING_ACCENT) != 0u ? 1.0 - tint : 1.0);
    // x contrast, y relief, z patches, w glow.
    vec4 amount = unpackUnorm4x8(materials[index].amounts);

    gradient = vec3(0.0);
    cavity = 1.0;
    if (pattern == VOXEL_PATTERN_GRAIN)
    {
        float size = float(1 << shift);
        float fade = fadeFor(footprint, size);
        // Cells 2^aspect times taller than wide; the gradient is taken per cell width.
        ivec3 lattice = ivec3(shift, shift + aspect, shift);
        vec3 perWidth = vec3(1.0, 1.0 / float(1 << aspect), 1.0);
        vec4 n = fade > 0.0 ? valueNoise(whole, part, lattice, 3u) : vec4(0.0);
        vec3 base = baseColour(index, tint);
        colour = base * (1.0 + 0.5 * amount.x * fade * n.x);
        gradient = 0.5 * n.yzw * perWidth * fade;
    }
    else if (pattern == VOXEL_PATTERN_STRATA)
    {
        // Each part is reduced to a few values before the next starts, which keeps few values live.
        // Soft light and dark patches of the feature size and half of it, the larger one raised a little.
        ivec3 lattice = ivec3(shift, shift + aspect, shift);
        float fade = fadeFor(footprint, float(1 << shift));
        vec4 n = fade > 0.0 ? valueNoise(whole, part, lattice, 7u) : vec4(0.0);
        float halfFade = fadeFor(footprint, 0.5 * float(1 << shift));
        float mottle = fade * n.x + 0.5 * halfFade * (halfFade > 0.0 ? valueNoise(whole, part, lattice - 1, 8u).x : 0.0);
        gradient = 0.5 * n.yzw * fade;
        cavity = 1.0 - 0.2 * fade * (1.0 - smoothRamp(n.x + 0.6, 1.2));
        // A fine grain of light and dark specks. (The bands by height go on per pixel: strataBands.)
        float fine = fadeFor(footprint, 2.0);
        float grain = speckle(fineNoise(whole, part, ivec3(1), 9u, fine));
        colour = baseColour(index, tint) * (1.0 + amount.x * (0.3 * mottle + 0.2 * fine * grain));
    }
    else if (pattern == VOXEL_PATTERN_PEBBLES)
    {
        float size = float(1 << shift);
        float fade = fadeFor(footprint, size);
        float fine = fadeFor(footprint, 2.0);
        // Laid out over x and z: holds on ground that faces up, and fades out on steep banks.
        float up = smoothstep(0.45, 0.8, normal.y);
        // Past the pebbles' size, their mean cover of the ground in their mean shade, and no cells to work out. (A rim
        // widened to a pixel shrank their cover before the fade began, so it stays sharp: on the ground, where pebbles
        // lie, they fade early.)
        float edge = PebbleCover * accent * up;
        float shade = 1.0;
        float hollow = PebbleHollow * accent * up;
        if (fade > 0.0)
        {
            // Pebbles in some cells (the accent share of them), domed and in the accent colour, over a speckled ground.
            Cells cells = cells2(whole.xz, part.xz, ivec2(shift), 10u);
            float radius = 0.25 + 0.2 * fract(cells.id * 7.31);
            float dome = 1.0 - cells.f1 * cells.f1 / (radius * radius);
            float pebble = step(cells.id, accent) * step(0.0, dome) * up;
            edge = mix(edge, smoothRamp(dome, 0.25) * pebble, fade);
            shade = mix(1.0, 0.7 + 0.6 * fract(cells.id * 13.7), fade);
            vec2 slope = 2.0 * cells.toNearest / (radius * radius) * pebble * fade;
            gradient = vec3(slope.x, 0.0, slope.y);
            hollow = mix(hollow, pebble * (1.0 - smoothRamp(dome, 0.35)), fade);
        }

        vec3 base = baseColour(index, tint);
        colour = base * (1.0 + 0.45 * amount.x * fine * speckle(fineNoise(whole, part, ivec3(1), 11u, fine)));
        colour = mix(colour, materials[index].accentColour * shade, edge);
        cavity = 1.0 - 0.25 * hollow;
    }
    else if (pattern == VOXEL_PATTERN_CLUMPS)
    {
        float size = float(1 << shift);
        // The clumps fade out while each is still a few pixels wide, so far grass shows no repeating pattern of them.
        float fade = fadeFor(footprint, 0.25 * size);
        float fine = fadeFor(footprint, 1.0);
        // Laid out over x and z: holds on ground that faces up, and fades out on steep banks.
        float up = smoothstep(0.45, 0.8, normal.y);
        // Hues drift between the colour and the accent over about 20 m, 2.5 m, and (fading with the clumps) 0.3 m, so
        // no one size of patch repeats across a field. The 2.5 m noise also decides how much the clumps stand out, from
        // hardly at all to fully.
        ivec3 turnedWhole;
        vec3 turnedPart;
        turnPoint(whole, part, turnedWhole, turnedPart);
        float thick = valueNoise2(whole.xz, part.xz, 8, 19u);
        float drift = 0.45 * valueNoise2(turnedWhole.xz, turnedPart.xz, 11, 13u) + 0.3 * thick;
        float shape = up * (0.2 + 0.8 * smoothRamp(thick + 0.5, 1.0));
        // Soft clumps that bulge a little and darken towards their edges, and fine bright and dark blade tips up close.
        float dome = 1.0;
        vec2 slope = vec2(0.0);
        if (fade > 0.0)
        {
            Cells cells = cells2(whole.xz, part.xz, ivec2(shift), 12u);
            dome = 1.0 - clamp(1.5 * cells.f1 * cells.f1, 0.0, 1.0);
            slope = 3.0 * cells.toNearest * step(0.0, dome) * shape * fade;
            drift += 0.5 * fade * valueNoise2(whole.xz, part.xz, shift + 1, 20u);
        }

        float hue = clamp(0.5 + 0.8 * drift, 0.0, 1.0);
        vec3 clump = mix(baseColour(index, tint), materials[index].accentColour, hue * accent);
        float blades = speckle(fineNoise(whole, part, ivec3(0), 14u, fine));
        // The clumps' darker edges up close; past their size, their mean darkening.
        float hollow = mix(ClumpHollow, 1.0 - dome, fade);
        colour = clump * (1.0 - 0.12 * amount.x * shape * hollow) * (1.0 + 0.5 * amount.x * fine * blades);
        gradient = vec3(slope.x, 0.0, slope.y);
        cavity = 1.0 - 0.15 * shape * hollow;
    }
    else if (pattern == VOXEL_PATTERN_CLUSTERS)
    {
        float fade = fadeFor(footprint, float(1 << shift));
        ivec3 lattice = ivec3(shift, shift + aspect, shift);
        // Soft clusters of leaves with shaded hollows between them, lighter in the accent's hue here and there. Gradient
        // noise, so no square cells show; no relief, which made leaves look hard.
        float hue = 0.5 + 0.45 * gradientNoise(whole, part, lattice + 2, 17u);
        vec3 lit = mix(baseColour(index, tint), materials[index].accentColour, hue * accent);
        // Past the clusters' size, no shape to work out: the leaves keep their hue alone.
        colour = lit;
        if (fade > 0.0)
        {
            float shape = gradientNoise(whole, part, lattice, 15u) + 0.4 * fade * gradientNoise(whole, part, lattice - 1, 16u);
            float cluster = smoothRamp(shape + 0.5, 1.0);
            colour *= 1.0 - 0.4 * amount.x * fade * (1.0 - cluster);
            cavity = mix(1.0, 0.7 + 0.3 * cluster, fade);
        }
    }
    else if (pattern == VOXEL_PATTERN_PLATES)
    {
        // Bark: plates split by thin dark cracks, each its own shade, with the accent (inner bark) in the cracks. The
        // cells are projected onto the vertical planes the surface faces (and the horizontal one, for logs and stumps),
        // blended by how much it faces each, so the pattern wraps a trunk without stretching. Past the plates' size,
        // their mean shade and the cracks' mean share, and no cells to work out.
        const float crackWidth = 0.2;
        float fade = fadeFor(footprint, float(1 << shift));
        float cracksFootprint = footprint / float(1 << shift);
        vec3 facing = fade > 0.0 ? pow(abs(normal), vec3(4.0)) : vec3(0.0, 1.0, 0.0);
        facing /= facing.x + facing.y + facing.z;
        float plate = 0.0;
        float id = 0.0;
        vec3 slope = vec3(0.0);
        float cellPlate;
        vec2 cellSlope;
        float cellId;
        if (fade > 0.0 && facing.x > 0.01)
        {
            barkCells(whole.zy, part.zy, shift, aspect, crackWidth, 21u, cracksFootprint, cellPlate, cellSlope, cellId);
            plate += facing.x * cellPlate;
            slope += facing.x * vec3(0.0, cellSlope.y, cellSlope.x);
            id = cellId;
        }

        if (fade > 0.0 && facing.z > 0.01)
        {
            barkCells(whole.xy, part.xy, shift, aspect, crackWidth, 22u, cracksFootprint, cellPlate, cellSlope, cellId);
            plate += facing.z * cellPlate;
            slope += facing.z * vec3(cellSlope.x, cellSlope.y, 0.0);
            id = facing.z > facing.x ? cellId : id;
        }

        if (fade > 0.0 && facing.y > 0.01)
        {
            barkCells(whole.xz, part.xz, shift, 0, crackWidth, 23u, cracksFootprint, cellPlate, cellSlope, cellId);
            plate += facing.y * cellPlate;
            slope += facing.y * vec3(cellSlope.x, 0.0, cellSlope.y);
            id = facing.y > max(facing.x, facing.z) ? cellId : id;
        }

        // The grain fades at its 1-voxel width.
        float fine = fadeFor(footprint, 1.0);
        float grain = fineNoise(whole, part, ivec3(0, 2, 0), 24u, fine);
        vec3 base = baseColour(index, tint);
        float shade = mix(MeanPlateShade, 0.8 + 0.45 * fract(id * 5.3), fade);
        vec3 surface = base * shade * (1.0 + 0.3 * amount.x * fine * grain);
        vec3 crack = mix(base * 0.22, materials[index].accentColour * 0.6, accent);
        float share = mix(MeanPlateShare, plate, fade);
        colour = mix(crack, surface, share);
        gradient = slope * fade;
        cavity = 0.45 + 0.55 * share;
    }
    else if (pattern == VOXEL_PATTERN_BLOCKS)
    {
        // Dressed blocks in courses on the plane the surface faces most (the horizontal one on tops), each its own
        // shade, with rounded, chipped edges and thin sunken joints between them. One plane only: the joints of two
        // planes blended would show twice.
        vec3 facing = abs(normal);
        bool top = facing.y > max(facing.x, facing.z);
        bool side = facing.x > facing.z;
        ivec2 planeWhole = top ? whole.xz : side ? whole.zy : whole.xy;
        vec2 planePart = top ? part.xz : side ? part.zy : part.xy;
        float edge;
        vec2 away;
        float id;
        blockJoints(planeWhole, planePart, shift, aspect, 28u, edge, away, id);
        // Chipped edges: the joint wanders in by up to 2 cm.
        edge -= 1.0 + valueNoise2(planeWhole, planePart, 2, 29u);
        const float jointHalf = 1.0;
        const float bevel = 3.0;
        float fade = fadeFor(footprint, 8.0);
        float face = mix(1.0, smoothRamp(edge - jointHalf, bevel), fade);
        vec2 slope = 2.0 * bevel * smoothDerivative(edge - jointHalf, bevel) * fade * away;
        gradient = top ? vec3(slope.x, 0.0, slope.y) : side ? vec3(0.0, slope.y, slope.x) : vec3(slope.x, slope.y, 0.0);
        cavity = 0.5 + 0.5 * face;
        float fine = fadeFor(footprint, 2.0);
        float grain = speckle(fineNoise(whole, part, ivec3(1), 30u, fine));
        vec3 base = baseColour(index, tint);
        vec3 block = base * (1.0 + amount.x * (0.4 * id - 0.2 + 0.2 * fine * grain));
        colour = mix(base * 0.45, block, face);
    }
    else if (pattern == VOXEL_PATTERN_SOIL || pattern == VOXEL_PATTERN_STREAKS || pattern == VOXEL_PATTERN_MOTTLE)
    {
        // Soil and mottle lie over x and z (mottle's patches are round, so its tile serves every side); streaks run
        // up the plane of x and y (the bake's side; a trunk's sides are read from it) and over x and z on a top. The
        // relief comes from the height half a centimetre to each side.
        bool top = pattern != VOXEL_PATTERN_STREAKS || abs(normal.y) > 0.5;
        ivec2 planeWhole = top ? whole.xz : whole.xy;
        vec2 planePart = top ? part.xz : part.xy;
        float height;
        planeRecipeAt(pattern, index, planeWhole, planePart, shift, aspect, accent, amount.x, colour, height);
        const vec2 steps[4] = vec2[](vec2(0.5, 0.0), vec2(-0.5, 0.0), vec2(0.0, 0.5), vec2(0.0, -0.5));
        float around[4];
        vec3 unused;
        for (int i = 0; i < 4; i++)
        {
            ivec2 movedWhole;
            vec2 movedPart;
            movePoint(planeWhole, planePart, steps[i], movedWhole, movedPart);
            planeRecipeAt(pattern, index, movedWhole, movedPart, shift, aspect, accent, amount.x, unused, around[i]);
        }

        vec2 slope = PlaneDepth * vec2(around[0] - around[1], around[2] - around[3]);
        gradient = top ? vec3(slope.x, 0.0, slope.y) : vec3(slope.x, slope.y, 0.0);
        cavity = height;
    }
    else if (pattern == VOXEL_PATTERN_CRACKS)
    {
        float size = float(1 << shift);
        float fade = fadeFor(footprint, size);
        float fine = fadeFor(footprint, 2.0);
        // Laid out over x and z: holds on ground that faces up, and fades out on steep banks.
        float up = smoothstep(0.45, 0.8, normal.y);
        // Dry flakes, each its own shade and slightly raised, split by a network of thin dark cracks.
        Cells cells = cells2(whole.xz, part.xz, ivec2(shift), 26u);
        float border = cells.f2 - cells.f1;
        const float crackWidth = 0.08;
        float flake = smoothRamp(border - 0.015, crackWidth);
        float cracks = (1.0 - flake) * up * fade;
        float grain = speckle(fineNoise(whole, part, ivec3(1), 27u, fine));
        vec3 base = baseColour(index, tint);
        colour = base * (0.85 + 0.3 * fract(cells.id * 7.7)) * (1.0 + 0.4 * amount.x * fine * grain);
        colour *= 1.0 - 0.7 * amount.x * cracks;
        vec2 slope = smoothDerivative(border - 0.015, crackWidth) * borderGradient(cells) * up * fade;
        gradient = vec3(slope.x, 0.0, slope.y);
        cavity = 1.0 - 0.4 * cracks;
    }
    else
    {
        colour = baseColour(index, tint);
    }

    // The relief and hollows by the material's relief.
    gradient *= amount.y * ReliefScale;
    cavity = mix(1.0, cavity, amount.y);
}

#ifndef MESH_SURFACE_BAKE
// The texture sets (docs/rendering-redesign.md, the materials plan): a material that names one is drawn from it instead
// of its recipe, read from the sides of x, y, and z the surface faces (triplanar mapping), each filtered by the GPU
// through mip levels and anisotropically, so no fade is worked out per pixel.

// How much each of the three projections counts, by how squarely the surface faces its axis; a side the surface hardly
// faces (under 5 % of the blend) counts not at all and is not read, so ground and walls read one side and only edges
// and slopes two or three (all three took 0.7 to 1.4 ms more over the benchmark's views).
vec3 triplanarWeights(vec3 normal)
{
    vec3 weights = pow(abs(normal), vec3(4.0));
    weights /= weights.x + weights.y + weights.z;
    weights = max(weights - 0.05, 0.0);
    return weights / (weights.x + weights.y + weights.z);
}

// The point offset (metres from the camera) in tiles tileMetres wide, kept small: the camera's place within a tile plus
// the offset. The camera's place in the world is its local position plus the match's origin; the origin's place within
// material index's tile comes worked out on the CPU in doubles (MeshMaterial.TileOrigin), so the tiles stay where they
// are in the world far from the map's middle, at any tile width.
vec3 tilePoint(uint index, vec3 offset, float tileMetres)
{
    return (offset + mod(view.position + materials[index].tileOrigin, tileMetres)) / tileMetres;
}

// A set's colour with occlusion and its normal map's x and y (each -1 to 1) at uv in layer, from the large arrays or the
// small, with the derivatives dx and dy taken where every pixel of a 2x2 group runs.
void readSet(bool large, vec3 uvLayer, vec2 dx, vec2 dy, out vec4 colour, out vec2 bend)
{
    if (large)
    {
        colour = textureGrad(largeColours, uvLayer, dx, dy);
        bend = textureGrad(largeNormals, uvLayer, dx, dy).xy;
    }
    else
    {
        colour = textureGrad(smallColours, uvLayer, dx, dy);
        bend = textureGrad(smallNormals, uvLayer, dx, dy).xy;
    }

    // The normal map's 0 to 1 back to -1 to 1, a stored 128 at exactly 0 (Castaño's 2v - 254/255).
    bend = bend * 2.0 - 254.0 / 255.0;
}

// The height a material without a texture set has at a border between materials (materialHeight): the middle, so its
// share of the border's weights alone decides it.
const float RecipeHeight = 0.5;

// How many mip levels softer material index's texture set is read (MeshMaterial.textureStyle's blur, 0 to 4), as the
// factor its derivatives are scaled by.
float textureBlur(uint index)
{
    return exp2(4.0 * unpackUnorm4x8(materials[index].textureStyle).z);
}

// The height of the texture set texture (MeshMaterial.texture's word, a tile metres wide) at offset (metres from the
// camera) on a surface facing normal, 0 to 1, read from the sides it faces with the offset's derivatives dx and dy (taken
// where every pixel of a 2x2 group runs), as soft as material index's blur.
float textureHeight(uint index, uint texture, float metres, vec3 offset, vec3 normal, vec3 dx, vec3 dy)
{
    bool large = (texture & MESH_TEXTURE_LARGE) != 0u;
    float layer = float(int(texture & 0xFFFFu) - 1);
    vec3 weights = triplanarWeights(normal);
    vec3 p = tilePoint(index, offset, metres);
    float blur = textureBlur(index);
    dx *= blur / metres;
    dy *= blur / metres;
    float height = 0.0;
    if (weights.x > 0.0)
    {
        height += weights.x * (large ? textureGrad(largeHeights, vec3(p.zy, layer), dx.zy, dy.zy).r
            : textureGrad(smallHeights, vec3(p.zy, layer), dx.zy, dy.zy).r);
    }

    if (weights.y > 0.0)
    {
        height += weights.y * (large ? textureGrad(largeHeights, vec3(p.xz, layer), dx.xz, dy.xz).r
            : textureGrad(smallHeights, vec3(p.xz, layer), dx.xz, dy.xz).r);
    }

    if (weights.z > 0.0)
    {
        height += weights.z * (large ? textureGrad(largeHeights, vec3(p.xy, layer), dx.xy, dy.xy).r
            : textureGrad(smallHeights, vec3(p.xy, layer), dx.xy, dy.xy).r);
    }

    return height;
}

// Material index drawn from the texture set texture (MeshMaterial.texture's word) whose tile is metres wide and whose
// colour's mean is mean: the photograph's colour, matched to the material's own colour by the colour match (the mean
// scaled to it) and moved by the tint as the material's colour moves; its normal map bending the lit normal (OpenGL's
// convention: green along the image's upward direction, the texture's -v); its occlusion as the cavity; the material's
// style (MeshMaterial.textureStyle) keeping part of the colour's variation and of the relief, and softening it by mip
// levels. dOffsetX and dOffsetY are the offset's derivatives, taken where every pixel of a 2x2 group runs.
Surface surfaceTextured(uint index, uint texture, float metres, vec3 mean, float tint, ivec3 whole, vec3 part, vec3 offset,
    vec3 normal, vec3 dOffsetX, vec3 dOffsetY)
{
    bool large = (texture & MESH_TEXTURE_LARGE) != 0u;
    float layer = float(int(texture & 0xFFFFu) - 1);
    float match = float(texture >> 24u) / 255.0;
    vec4 amount = unpackUnorm4x8(materials[index].amounts);
    vec3 weights = triplanarWeights(normal);
    vec3 p = tilePoint(index, offset, metres);
    float blur = textureBlur(index);
    vec3 dx = dOffsetX * blur / metres;
    vec3 dy = dOffsetY * blur / metres;
    vec4 colour = vec4(0.0);
    vec3 bend = vec3(0.0);
    vec4 read;
    vec2 tangent;
    if (weights.x > 0.0)
    {
        readSet(large, vec3(p.zy, layer), dx.zy, dy.zy, read, tangent);
        colour += weights.x * read;
        bend += weights.x * vec3(0.0, -tangent.y, tangent.x);
    }

    if (weights.y > 0.0)
    {
        readSet(large, vec3(p.xz, layer), dx.xz, dy.xz, read, tangent);
        colour += weights.y * read;
        bend += weights.y * vec3(tangent.x, 0.0, -tangent.y);
    }

    if (weights.z > 0.0)
    {
        readSet(large, vec3(p.xy, layer), dx.xy, dy.xy, read, tangent);
        colour += weights.z * read;
        bend += weights.z * vec3(tangent.x, -tangent.y, 0.0);
    }

    // The material's style: the detail kept round the set's mean colour, and how strongly its relief shades it.
    vec4 style = unpackUnorm4x8(materials[index].textureStyle);
    colour.rgb = mean + (colour.rgb - mean) * style.x;
    colour.a = mix(1.0, colour.a, style.y);
    bend *= style.y;
    vec3 own = materials[index].colour;
    vec3 matched = colour.rgb * mix(vec3(1.0), own / max(mean, vec3(1e-3)), match);
    if ((texture & MESH_TEXTURE_BANDS) != 0u)
    {
        matched *= strataBands(whole, part);
    }

    vec3 tinted = matched * baseColour(index, tint) / max(own, vec3(1e-3));
    return surfaceFinish(tinted, -bend, colour.a, whole, part, normal, amount);
}

#endif

// Material index without a texture set (a plain one: a flat stone, glow caps): its colour, tint of the way to
// its tint colour, with the patches and its glow.
Surface surfacePlain(uint index, float tint, ivec3 whole, vec3 part, vec3 normal)
{
    return surfaceFinish(baseColour(index, tint), vec3(0.0), 1.0, whole, part, normal,
        unpackUnorm4x8(materials[index].amounts));
}

// Material index in its flat colour, tint of the way to its tint colour, with its glow: no pattern, no patches.
Surface surfaceFlat(uint index, float tint)
{
    Surface surface;
    surface.colour = baseColour(index, tint);
    surface.slope = vec3(0.0);
    surface.cavity = 1.0;
    surface.glow = unpackUnorm4x8(materials[index].amounts).w;
    return surface;
}
