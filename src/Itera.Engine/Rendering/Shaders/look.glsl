// The frame's look (RenderLook: the art direction's values a game sets), one record per frame slot, read as LOOK (the
// frame's record, by view.slot): its values, and the light and colours it gives with the frame's atmosphere, worked out
// once a frame (LookConstants.From says how each is made). Mirrors Itera.Engine.Rendering.GpuData.LookConstants member
// for member; GpuLayout tests check it. In MeshPass's set (0, binding 17) by default; a pass with its own set gives LOOK_SET and LOOK_BINDING
// (ParticlePass's draws). Include after view.glsl.
#extension GL_EXT_scalar_block_layout : require

#ifndef LOOK_SET
#define LOOK_SET 0
#endif

#ifndef LOOK_BINDING
#define LOOK_BINDING 17
#endif

struct Look
{
    vec3 sunLight;
    float worldSaturation;
    vec3 shadeCool;
    float worldValue;
    vec3 skyLight;
    float worldSideShade;
    vec3 groundLight;
    float worldTopShade;
    vec3 zenith;
    float sideShadeBelow;
    vec3 below;
    float topShadeAbove;
    vec3 gradeShadows;
    float shadowsBelow;
    vec3 gradeLights;
    float lightsAbove;
    vec3 sunDisc;
    float sunDiscSharpness;
    vec3 gradeHue;
    float gradeMix;
    float gradeScale;
    float occlusionFloor;
    float skyFloor;
    float glowStrength;
    float fogStart;
    float hazePerMetre;
    float exposure;
    float daylight;
    vec3 sunGlow;
    float sunGlowSharpness;
    vec3 cloudColour;
    float cloudCover;
    float cloudOpacity;
    float cloudScale;
    float cloudStretch;
    float cloudAlongX;
    float cloudAlongZ;
    float groundHazePerMetre;
    float groundHazeFloor;
    float groundHazeFalloff;
};

layout(scalar, set = LOOK_SET, binding = LOOK_BINDING) restrict readonly buffer Looks
{
    Look looks[];
};

#define LOOK looks[view.slot]
