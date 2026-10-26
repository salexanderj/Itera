// The grass's data (GrassPlacePass and GrassDrawPass, the grass a game opts into), in set 1 (set 0 is the mesh pass's:
// the heap, the draws, the materials, the light, and the look). Each frame slot has a GrassFrame (the numbers the CPU
// writes) and up to GRASS_MOST_BLOCKS GrassBlocks (the camera's blocks near enough to hold grass, nearest first).
// GrassFrame and GrassBlock mirror GpuData.GrassFrame and GrassBlock member for member; GrassPassTests checks them. The
// blades and their two draws are the GPU's own: grass-place.comp writes them, grass-count.comp the draws, and grass.vert
// reads them.
#extension GL_EXT_scalar_block_layout : require

// The blocks a frame slot holds (GrassFrame.MostBlocks).
#define GRASS_MOST_BLOCKS 1024u

// Blades a draw's instance holds: its index pattern repeats this many blades (GrassPlacePass.BladesAnInstance).
#define GRASS_BLADES_AN_INSTANCE 64u

// A near blade's corners (three pairs up its length and the tip: five triangles), and a far one's (one triangle).
#define GRASS_NEAR_CORNERS 7u
#define GRASS_FAR_CORNERS 3u

struct GrassFrame
{
    // The blocks listed, their triangles in all, and the grass's material id.
    uint blocks;
    uint triangles;
    uint material;
    // The near and far blades' most: the near ones fill the blades from the first, the far ones from nearMost.
    uint nearMost;
    uint farMost;
    // Where the grass ends, where it starts to thin towards that, and where the near blades end, in metres.
    float radius;
    float fullMetres;
    float nearMetres;
    // Blades a square metre at full density, their height in metres and its spread (a share either way), their width at
    // the root in metres, and their least width in pixels.
    float density;
    float height;
    float heightSpread;
    float width;
    float leastPixels;
    // The least up of a surface that holds grass (the cosine of the steepest slope), the blades' mean lean in radians,
    // how far the wind sways their tips (a share of their height), the gusts' speed in metres a second, and the wind's
    // way on the ground (a unit vector of x and z).
    float leastUp;
    float lean;
    float wind;
    float windSpeed;
    vec2 windWay;
    // The frame's seconds, wrapped (GrassPlacePass.RepeatSeconds).
    float seconds;
    // How light a blade's tip and its root are, as shares of the ground's colour, and how far blades' colours spread
    // round the ground's (a share either way).
    float tipLight;
    float rootShade;
    float colourSpread;
    // How far the ground's texture may stray from its set's mean hue and still hold blades, as the distance between unit
    // colour directions; none grow past twice it (a bare patch of another hue). 0 lets every texel hold them.
    float hueTolerance;
};

struct GrassBlock
{
    // The block's first index in the heap (16-bit indices), its triangles, its draw (draws[draw], the command's first
    // instance), and the triangles of the blocks before it in the list.
    uint firstIndex;
    uint triangles;
    uint draw;
    uint firstTriangle;
};

// A blade: its root (local to the match's origin), its colour (the ground's under it, the square root of each linear
// channel in a byte, and its shade in the fourth), the way it faces (two halves: x and z of a unit vector), its height
// and width (two halves, metres), the ground's normal (three signed bytes) and its lean (the fourth byte, 0 to 1 of
// GrassMostLean), and its sway's phase, stiffness, and curl (three bytes, 0 to 1).
struct GrassBlade
{
    vec3 root;
    uint colour;
    uint facing;
    uint size;
    uint normal;
    uint motion;
};

// The most a blade leans at its byte's 1, in radians.
const float GrassMostLean = 1.2;

// The two draws (each an indexed indirect command: index count, instances, first index, vertex offset, first instance)
// and the blades placed this frame, near and far.
struct GrassDraws
{
    uint nearIndexCount;
    uint nearInstances;
    uint nearFirstIndex;
    int nearVertexOffset;
    uint nearFirstInstance;
    uint farIndexCount;
    uint farInstances;
    uint farFirstIndex;
    int farVertexOffset;
    uint farFirstInstance;
    uint nearPlaced;
    uint farPlaced;
};

layout(scalar, set = 1, binding = 0) restrict readonly buffer GrassFrames
{
    GrassFrame grassFrames[];
};

layout(scalar, set = 1, binding = 1) restrict readonly buffer GrassBlocks
{
    GrassBlock grassBlocks[];
};

#ifdef GRASS_PLACE
layout(scalar, set = 1, binding = 2) restrict writeonly buffer GrassBlades
#else
layout(scalar, set = 1, binding = 2) restrict readonly buffer GrassBlades
#endif
{
    GrassBlade blades[];
};

#ifdef GRASS_DRAWS_WRITE
layout(scalar, set = 1, binding = 3) restrict coherent buffer GrassDrawList
#else
layout(scalar, set = 1, binding = 3) restrict readonly buffer GrassDrawList
#endif
{
    GrassDraws grassDraws;
};

// A hash of three whole numbers (PCG3D, Jarzynski and Olano 2020), each word well mixed.
uvec3 grassHash3(uvec3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    return v;
}

// A word's top 24 bits as a number from 0 up to 1.
float grassUnit(uint word)
{
    return float(word >> 8u) * (1.0 / 16777216.0);
}
