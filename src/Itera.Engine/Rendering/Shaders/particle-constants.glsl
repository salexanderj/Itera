// The push constants of the particle compute passes. Mirrors Itera.Engine.Rendering.GpuData.ParticleConstants member
// for member; ParticlePassTests checks the layout. They are not the view's constants (view.glsl); each takes all 128
// bytes every device must allow.
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
    // The first ring slot the simulate pass reads; invocation i reads (liveFirst + i) % capacity.
    uint liveFirst;
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
    // How far the match's origin moved since the pool's particles were last moved: taken off every particle made before
    // this frame (zero while it stays).
    vec3 shift;
} push;
