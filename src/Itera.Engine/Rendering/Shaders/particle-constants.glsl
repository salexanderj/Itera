// The push constants of the particle compute passes. Mirrors Itera.Engine.Rendering.GpuData.ParticleConstants member
// for member; ParticlePassTests checks the layout. They are not the view's constants (view.glsl), which take 116 of
// the 128 bytes every device must allow.
#extension GL_EXT_scalar_block_layout : require

layout(push_constant, scalar) uniform ParticlePush
{
    float delta;
    uint slot;
    uint bursts;
    uint spawns;
    uint capacity;
    vec3 sunDirection;
    float maxDistance;
    vec3 camera;
    uint draw;
    vec3 forward;
    float viewCosine;
    uint burstStart;
    vec3 right;
    vec3 up;
    uint width;
    uint height;
    float near;
    float far;
    uint orthographic;
} push;
