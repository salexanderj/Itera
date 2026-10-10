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
    // Where the drawn depth starts along the view (a perspective view's near plane; where an orthographic view's box
    // starts, negative behind the position), and where an orthographic view's box ends (0 for perspective).
    float near;
    float far;
} view;

// The bits of view.switches, as ViewSwitches has them (ViewConstantsTests checks): the sun casts shadows through the
// shadow maps; the view is orthographic; the debug views: the meshes' edges drawn over the world, surfaces tinted by their voxels' size, and
// the blocks drawn from their plain meshes, the seams open (the CPU's choice; no shader reads it).
const uint ViewShadows = 1u;
const uint ViewOrthographic = 2u;
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

// True for an orthographic view: parallel rays along view.forward, from view.position plus x times view.right and y
// times view.up (the image's half width and half height in metres); its position is the middle of what it shows.
bool viewOrthographic()
{
    return viewSwitch(ViewOrthographic);
}

// Depth is stored reversed, so the sky is 0, nearer is greater, and passes compare GREATER. Perspective: view.near over
// the distance along the view direction; a rasterised point gets it as clip z = view.near and w = its distance in metres,
// which varies evenly across a face on screen, so the early depth test can drop hidden faces. Orthographic: falling
// evenly from 1 at view.near to 0 at view.far, with w = 1.
//
// The clip position of a point at offset from the camera, as every pass that draws into the view's depth puts it: x and
// y over the view's axes (right and up reach the image's edges at unit forward, or at their length in metres
// orthographically), z and w as above.
vec4 viewClip(vec3 offset)
{
    float depth = dot(offset, view.forward);
    float x = dot(offset, view.right) / dot(view.right, view.right);
    float y = dot(offset, view.up) / dot(view.up, view.up);
    if (viewOrthographic())
    {
        return vec4(x, y, (view.far - depth) / (view.far - view.near), 1.0);
    }

    return vec4(x, y, view.near, depth);
}

// The depth along the view, in metres from view.position, of a depth the view stored above 0 (0 is the sky).
float viewDepthOf(float stored)
{
    return viewOrthographic() ? view.far - stored * (view.far - view.near) : view.near / stored;
}

// The world-space direction of the ray through a point of the image; ndc is in [-1, 1] with +y up. Orthographic rays
// all run along the view.
vec3 viewRay(vec2 ndc)
{
    return viewOrthographic() ? view.forward : normalize(view.forward + ndc.x * view.right + ndc.y * view.up);
}

// The world-space direction of the ray that meets the point at offset from the camera (towards the camera is its
// negative): for fog's sky colour behind it, and for what faces the viewer.
vec3 viewRayTo(vec3 offset)
{
    return viewOrthographic() ? view.forward : normalize(offset);
}

// The view depth by which the shadow cascades are chosen for a point at view depth depth: the depth itself, or in an
// orthographic view how far before or behind the view's middle it lies (the cascades lie round the middle).
float viewCascadeDepth(float depth)
{
    return viewOrthographic() ? abs(depth) : depth;
}
