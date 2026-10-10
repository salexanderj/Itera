// Per-frame camera data, pushed by the render thread. Mirrors Itera.Engine.Rendering.GpuData.ViewConstants
// member for member; GpuLayoutTests checks the layout.
#extension GL_EXT_scalar_block_layout : require

layout(push_constant, scalar) uniform View
{
    vec3 position;
    vec3 forward;
    vec3 right;
    vec3 up;
    vec3 sunDirection;
    vec3 skyHorizon;
    uint slot;
    float maxDistance;
    // The view's switches, a bit each (ViewSwitches): test them with viewSwitch.
    uint switches;
    uint width;
    uint height;
    vec3 sunColour;
    // Haze, overcast, and grade, a byte each: unpackUnorm4x8(view.air) gives them as x, y, and z.
    uint air;
} view;

// The bits of view.switches, as ViewSwitches has them (ViewConstantsTests checks): the sun casts shadows through the
// shadow maps; the debug views: the meshes' edges drawn over the world, surfaces tinted by their voxels' size, and
// the blocks drawn from their plain meshes, the seams open (the CPU's choice; no shader reads it).
const uint ViewShadows = 1u;
const uint ViewWireframe = 4u;
const uint ViewLevels = 8u;
const uint ViewPlain = 16u;
const uint ViewMaterialsFlat = 32u;
const uint ViewMaterialsAll = 64u;

// True when the switch which (a ViewSwitches bit) is on.
bool viewSwitch(uint which)
{
    return (view.switches & which) != 0u;
}

// Depth is stored reversed: the near distance (1 cm) over the distance along the view direction, so the sky is 0 and
// nearer is greater. A rasterised point gets it as clip z = NearMetres and w = its distance in metres, which varies
// evenly across a face on screen, so the early depth test can drop hidden faces. Passes compare GREATER.
const float NearMetres = 0.01;

// The clip position of a point at offset from the camera, as every pass that draws into the view's depth puts it: x and
// y over the view's axes (right and up reach the image's edges at unit forward), z NearMetres, w the depth along the
// view direction.
vec4 viewClip(vec3 offset)
{
    float depth = dot(offset, view.forward);
    float x = dot(offset, view.right) / dot(view.right, view.right);
    float y = dot(offset, view.up) / dot(view.up, view.up);
    return vec4(x, y, NearMetres, depth);
}

// The world-space direction of the ray through a point of the image; ndc is in [-1, 1] with +y up.
vec3 viewRay(vec2 ndc)
{
    return normalize(view.forward + ndc.x * view.right + ndc.y * view.up);
}
