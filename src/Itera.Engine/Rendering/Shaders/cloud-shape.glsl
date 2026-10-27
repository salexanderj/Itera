// The look's clouds' shape on their plane, shared by the sky's clouds (clouds.glsl) and the shadows they cast on what
// lies under them (cloudGroundShadow: mesh-shade.glsl's shadeSurface and grass.frag). The shape is the clouds' noise
// (mesh-data.glsl's cloudNoise): its gradient noise, carved into puffs by its cellular noise as the look's billow says.
// Reads here take a mip level, not derivatives, so any stage may use them. Include after view.glsl, sky.glsl (for
// look.glsl), and mesh-data.glsl.

// Lattice cells a tile of the clouds' noise spans: its coarsest octaves have about one feature a cell.
const float CloudCellsPerTile = 4.0;

// The ground shadow's read of the noise: this mip level, about 90 m a texel at the clouds' usual scale, as soft as a
// cloud's shadow on the ground (the sun's disc and the cloud's depth blur it); and how much wider its edge is than the
// clouds' own.
const float CloudShadowLevel = 3.0;
const float CloudShadowSoftening = 2.0;

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

// How thick the clouds are where their shape is shape, 0 to 1: covering the look's share of the sky, with edges
// softness wide.
float cloudCovering(float shape, float softness)
{
    float edge = 1.0 - LOOK.cloudCover;
    return smoothstep(edge, edge + softness, shape);
}

// How deep the clouds are where their shape is shape, 0 to 1: none at the cover's edge, rising evenly to their deepest
// at the shape's top.
float cloudDepth(float shape)
{
    float edge = 1.0 - LOOK.cloudCover;
    return clamp((shape - edge) / max(1.0 - edge, 0.01), 0.0, 1.0);
}

// How high a cloud's column rises where its shape is shape, as a share of the slab: its depth's square root, so it
// rises fast from the cover's edge and is domed on top.
float cloudColumn(float shape)
{
    return sqrt(cloudDepth(shape));
}

// A point of the plane, local, in the clouds' frame in metres: along their heading over their stretch, and across it.
vec2 cloudFrame(vec2 place)
{
    vec2 along = vec2(LOOK.cloudAlongX, LOOK.cloudAlongZ);
    return vec2(dot(place, along) / LOOK.cloudStretch, dot(place, vec2(-along.y, along.x)));
}

// The clouds' noise at lattice point p, scale times finer, at mip level level.
vec4 cloudReadLod(vec2 p, float scale, float level)
{
    return textureLod(cloudNoise, p * (scale / CloudCellsPerTile), level);
}

// The lattice point p bent across the clouds' heading by the look's warp, from mip level level of its slow read.
vec2 cloudWarpLod(vec2 p, float level)
{
    if (LOOK.cloudWarp > 0.0)
    {
        p.y += LOOK.cloudWarp * (cloudReadLod(p + vec2(5.2, 1.3), 0.25, level).r * 2.0 - 1.0);
    }

    return p;
}

// How much of the sunlight the look's clouds let reach world (local), 0 to 1: the look's ground shadow of the clouds'
// thickness where the ray from world towards the sun meets their plane, read blurred. 1 above the clouds, with the sun
// low, or without the shadow or the clouds.
float cloudGroundShadow(vec3 world)
{
    float rise = LOOK.cloudLocalHeight - world.y;
    if (LOOK.cloudGroundShadow <= 0.0 || LOOK.cloudCover <= 0.0 || view.sunDirection.y < 0.05 || rise <= 0.0)
    {
        return 1.0;
    }

    vec2 place = world.xz + view.sunDirection.xz * (rise / view.sunDirection.y);
    vec2 p = cloudWarpLod(LOOK.cloudNear + cloudFrame(place) * LOOK.cloudNearScale, CloudShadowLevel);
    float shape = cloudShape(cloudReadLod(p, 1.0, CloudShadowLevel));
    float thick = cloudCovering(shape, LOOK.cloudSoftness * CloudShadowSoftening);
    return 1.0 - LOOK.cloudGroundShadow * LOOK.cloudOpacity * thick;
}
