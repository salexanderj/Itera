// The noise the surface recipes are built from (mesh-surface.glsl, and the texture prototype's bake,
// material-bake.comp): lattice hashes, value and gradient noise with their gradients, cellular noise on a plane, and a
// few ramps. Keyed on a point in centimetres as an integer and a fraction. Defining MESH_SURFACE_TILE (centimetres, a
// power of two) before including it wraps every lattice at that size, so the noise repeats seamlessly over a tile of it
// (lattices coarser than the tile do not repeat); without it the noise never repeats.

#ifdef MESH_SURFACE_TILE
// A lattice cell wrapped into the tile: the tile holds MESH_SURFACE_TILE >> shift cells along each axis.
ivec3 wrapCell(ivec3 cell, ivec3 shift)
{
    return cell & max((ivec3(MESH_SURFACE_TILE) >> shift) - 1, -1);
}

ivec2 wrapCell(ivec2 cell, ivec2 shift)
{
    return cell & max((ivec2(MESH_SURFACE_TILE) >> shift) - 1, -1);
}

int wrapCell(int cell, int shift)
{
    return cell & max((MESH_SURFACE_TILE >> shift) - 1, -1);
}
#else
ivec3 wrapCell(ivec3 cell, ivec3 shift)
{
    return cell;
}

ivec2 wrapCell(ivec2 cell, ivec2 shift)
{
    return cell;
}

int wrapCell(int cell, int shift)
{
    return cell;
}
#endif

// A cell of a lattice over x and z as the lattice point the hashes take.
ivec3 planeCell(ivec2 cell, int shift)
{
    ivec2 wrapped = wrapCell(cell, ivec2(shift));
    return ivec3(wrapped.x, 0, wrapped.y);
}

// Cellular noise on a plane: the distances from a point to the nearest and second-nearest random points, one per
// lattice cell (in cells), the offsets from the point to both, and a hash of the nearest point's cell, 0 to 1.
struct Cells
{
    float f1;
    float f2;
    vec2 toNearest;
    vec2 toSecond;
    float id;
};

// A hash of a lattice point and a salt, 0 to 1: one xor-shift and multiply after mixing the axes, the top 24 bits.
float latticeHash(ivec3 p, uint salt)
{
    uint h = uint(p.x) * 0x8DA6B343u ^ uint(p.y) * 0xD8163841u ^ uint(p.z) * 0xCB1AB31Fu ^ salt;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    return float(h >> 8) * (1.0 / 16777216.0);
}

// Three hashes of a lattice point, 0 to 1, 10 bits each.
vec3 latticeHash3(ivec3 p, uint salt)
{
    uint h = uint(p.x) * 0x8DA6B343u ^ uint(p.y) * 0xD8163841u ^ uint(p.z) * 0xCB1AB31Fu ^ salt;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 13;
    return vec3(uvec3(h, h >> 10u, h >> 20u) & 1023u) * (1.0 / 1023.0);
}

// Value noise from -1 to 1 at the point whole + part (whole an integer voxel, part the fraction within it), on a
// lattice of 2^shift centimetres along each axis, and its gradient per lattice cell of each axis.
vec4 valueNoise(ivec3 whole, vec3 part, ivec3 shift, uint salt)
{
    ivec3 cell = whole >> shift;
    vec3 f = (vec3(whole - (cell << shift)) + part) / vec3(ivec3(1) << shift);
    vec3 u = f * f * (3.0 - 2.0 * f);
    vec3 du = 6.0 * f * (1.0 - f);
    float a = latticeHash(wrapCell(cell, shift), salt);
    float b = latticeHash(wrapCell(cell + ivec3(1, 0, 0), shift), salt);
    float c = latticeHash(wrapCell(cell + ivec3(0, 1, 0), shift), salt);
    float d = latticeHash(wrapCell(cell + ivec3(1, 1, 0), shift), salt);
    float e = latticeHash(wrapCell(cell + ivec3(0, 0, 1), shift), salt);
    float g = latticeHash(wrapCell(cell + ivec3(1, 0, 1), shift), salt);
    float h = latticeHash(wrapCell(cell + ivec3(0, 1, 1), shift), salt);
    float k = latticeHash(wrapCell(cell + ivec3(1, 1, 1), shift), salt);
    float kx = b - a;
    float ky = c - a;
    float kz = e - a;
    float kxy = a - b - c + d;
    float kyz = a - c - e + h;
    float kzx = a - b - e + g;
    float kxyz = -a + b + c - d + e - g - h + k;
    float value = a + kx * u.x + ky * u.y + kz * u.z + kxy * u.x * u.y + kyz * u.y * u.z + kzx * u.z * u.x
        + kxyz * u.x * u.y * u.z;
    vec3 gradient = du * vec3(
        kx + kxy * u.y + kzx * u.z + kxyz * u.y * u.z,
        ky + kyz * u.z + kxy * u.x + kxyz * u.z * u.x,
        kz + kzx * u.x + kyz * u.y + kxyz * u.x * u.y);
    return vec4(value * 2.0 - 1.0, gradient * 2.0);
}

// Gradient noise, about -1 to 1, at the point whole + part on a lattice of 2^shift centimetres: a random direction at
// each lattice point and a smooth blend of the distances along them. Its blobs are round where value noise's are square,
// which shows in soft shading; it costs about the same, but has no gradient here.
float gradientNoise(ivec3 whole, vec3 part, ivec3 shift, uint salt)
{
    ivec3 cell = whole >> shift;
    vec3 f = (vec3(whole - (cell << shift)) + part) / vec3(ivec3(1) << shift);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float a = dot(latticeHash3(wrapCell(cell, shift), salt) - 0.5, f);
    float b = dot(latticeHash3(wrapCell(cell + ivec3(1, 0, 0), shift), salt) - 0.5, f - vec3(1.0, 0.0, 0.0));
    float c = dot(latticeHash3(wrapCell(cell + ivec3(0, 1, 0), shift), salt) - 0.5, f - vec3(0.0, 1.0, 0.0));
    float d = dot(latticeHash3(wrapCell(cell + ivec3(1, 1, 0), shift), salt) - 0.5, f - vec3(1.0, 1.0, 0.0));
    float e = dot(latticeHash3(wrapCell(cell + ivec3(0, 0, 1), shift), salt) - 0.5, f - vec3(0.0, 0.0, 1.0));
    float g = dot(latticeHash3(wrapCell(cell + ivec3(1, 0, 1), shift), salt) - 0.5, f - vec3(1.0, 0.0, 1.0));
    float h = dot(latticeHash3(wrapCell(cell + ivec3(0, 1, 1), shift), salt) - 0.5, f - vec3(0.0, 1.0, 1.0));
    float k = dot(latticeHash3(wrapCell(cell + ivec3(1, 1, 1), shift), salt) - 0.5, f - vec3(1.0));
    float near = mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
    float far = mix(mix(e, g, u.x), mix(h, k, u.x), u.y);
    return 3.0 * mix(near, far, u.z);
}

// Value noise from -1 to 1 over x and z only, on a lattice of 2^shift centimetres.
float valueNoise2(ivec2 whole, vec2 part, int shift, uint salt)
{
    ivec2 cell = whole >> shift;
    vec2 f = (vec2(whole - (cell << shift)) + part) / float(1 << shift);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float near = mix(latticeHash(planeCell(cell, shift), salt), latticeHash(planeCell(cell + ivec2(1, 0), shift), salt), u.x);
    float far = mix(latticeHash(planeCell(cell + ivec2(0, 1), shift), salt), latticeHash(planeCell(cell + ivec2(1, 1), shift), salt), u.x);
    return mix(near, far, u.y) * 2.0 - 1.0;
}

// Value noise from -1 to 1 along one axis, on a lattice of 2^shift, at whole + part.
float valueNoise1(int whole, float part, int shift, uint salt)
{
    int cell = whole >> shift;
    float f = (float(whole - (cell << shift)) + part) / float(1 << shift);
    float u = f * f * (3.0 - 2.0 * f);
    return mix(latticeHash(ivec3(0, wrapCell(cell, shift), 0), salt), latticeHash(ivec3(0, wrapCell(cell + 1, shift), 0), salt), u) * 2.0 - 1.0;
}

// Cellular noise on a plane, on cells of 2^shift centimetres along each of its two axes (taller than wide when
// shift.y is larger): nine cells, one random point in each. whole and part are the point's two coordinates. The loop
// keeps the two nearest cells by index and works out their offsets afterwards, which keeps few values live.
Cells cells2(ivec2 whole, vec2 part, ivec2 shift, uint salt)
{
    ivec2 cell = whole >> shift;
    vec2 f = (vec2(whole - (cell << shift)) + part) / vec2(ivec2(1) << shift);
    float f1 = 64.0;
    float f2 = 64.0;
    int nearest = 4;
    int second = 4;
    for (int k = 0; k < 9; k++)
    {
        ivec2 offset = ivec2(k % 3, k / 3) - 1;
        vec3 r = latticeHash3(ivec3(wrapCell(cell + offset, shift), int(salt)), salt);
        vec2 d = vec2(offset) + r.xy - f;
        float distance = dot(d, d);
        if (distance < f1)
        {
            f2 = f1;
            second = nearest;
            f1 = distance;
            nearest = k;
        }
        else if (distance < f2)
        {
            f2 = distance;
            second = k;
        }
    }

    ivec2 nearestOffset = ivec2(nearest % 3, nearest / 3) - 1;
    ivec2 secondOffset = ivec2(second % 3, second / 3) - 1;
    vec3 near = latticeHash3(ivec3(wrapCell(cell + nearestOffset, shift), int(salt)), salt);
    vec3 next = latticeHash3(ivec3(wrapCell(cell + secondOffset, shift), int(salt)), salt);
    Cells result;
    result.f1 = sqrt(f1);
    result.f2 = sqrt(f2);
    result.toNearest = vec2(nearestOffset) + near.xy - f;
    result.toSecond = vec2(secondOffset) + next.xy - f;
    result.id = near.z;
    return result;
}

// The gradient, per cell, of the distance from the nearest cell border (f2 - f1).
vec2 borderGradient(Cells cells)
{
    return cells.toNearest / max(cells.f1, 1e-4) - cells.toSecond / max(cells.f2, 1e-4);
}

// The point whole + part turned 45 degrees about the y axis and scaled by the square root of 2, (x + z, y, z - x), as
// a whole voxel and a fraction again. Value noise has square cells along its lattice's axes; an octave taken at the
// turned point has its cells along the diagonals, which hides the squares of an octave taken on the axes.
void turnPoint(ivec3 whole, vec3 part, out ivec3 turnedWhole, out vec3 turnedPart)
{
    vec3 p = vec3(part.x + part.z, part.y, part.z - part.x);
    vec3 carry = floor(p);
    turnedWhole = ivec3(whole.x + whole.z, whole.y, whole.z - whole.x) + ivec3(carry);
    turnedPart = p - carry;
}

// 0 at x <= 0 and 1 at x >= width, a cubic step between.
float smoothRamp(float x, float width)
{
    float t = clamp(x / width, 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

// The derivative of smoothRamp by x.
float smoothDerivative(float x, float width)
{
    float t = clamp(x / width, 0.0, 1.0);
    return 6.0 * t * (1.0 - t) / width;
}

// Value noise for fine grain, which fades by fine (fadeFor at the grain's size): 0 without working it out where it has
// faded away, as it has on most of the picture.
float fineNoise(ivec3 whole, vec3 part, ivec3 shift, uint salt, float fine)
{
    return fine > 0.0 ? valueNoise(whole, part, shift, salt).x : 0.0;
}

// Sharp light and dark grains out of a quieter ground, from value noise.
float speckle(float n)
{
    return 0.3 * n + smoothstep(0.35, 0.55, n) - 0.8 * smoothstep(0.4, 0.6, -n);
}
