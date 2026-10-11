using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The push constants of the particle compute passes (<c>particle-spawn.comp</c> and <c>particle-simulate.comp</c>,
/// <c>layout(push_constant, scalar)</c>). They do not push <see cref="ViewConstants"/>, which takes all 128 bytes
/// every device must allow; these take all of them too. Built by the main thread (<c>ParticlePass.Write</c>) and pushed by the render
/// thread. Field order and types match the GLSL block; <c>ParticlePassTests</c> checks them.
/// </summary>
public struct ParticleConstants
{
    /// <summary>Seconds since the last frame; 0 for a frame presented again.</summary>
    public float Delta;

    /// <summary>The frame slot, which picks the region of the recipes, bursts, and attractors.</summary>
    public uint Slot;

    /// <summary>Bursts in the slot's region.</summary>
    public uint Bursts;

    /// <summary>Particles the bursts make.</summary>
    public uint Spawns;

    /// <summary>Particles the pool holds.</summary>
    public uint Capacity;

    /// <summary>Towards the sun, unit length.</summary>
    public Vector3 SunDirection;

    /// <summary>The farthest a particle is drawn, metres.</summary>
    public float MaxDistance;

    /// <summary>The camera, metres from the match's origin.</summary>
    public Vector3 Camera;

    /// <summary>1 when the sprites' and cubes' lists are filled; 0 simulates without drawing.</summary>
    public uint Draw;

    /// <summary>The way the camera looks, unit length.</summary>
    public Vector3 Forward;

    /// <summary>The cosine of half the widest angle the view covers, from its corner: particles outside are not drawn.</summary>
    public float ViewCosine;

    /// <summary>The frame slot's first burst in the bursts' buffer.</summary>
    public uint BurstStart;

    /// <summary>The view's right, scaled so 1 along it at unit forward is the drawn image's right edge (<see cref="ViewConstants.Right"/>).</summary>
    public Vector3 Right;

    /// <summary>The view's up, scaled so 1 along it at unit forward is the drawn image's top edge (<see cref="ViewConstants.Up"/>).</summary>
    public Vector3 Up;

    /// <summary>Width of the drawn image, pixels: the part of the depth image the world's pass wrote.</summary>
    public uint Width;

    /// <summary>Height of the drawn image, pixels.</summary>
    public uint Height;

    /// <summary>
    /// Where the view's drawn depth starts along it (<see cref="ViewConstants.Near"/>): the depth image's reverse depth is
    /// it over the distance along the view, or orthographically falls evenly from it to <see cref="Far"/>.
    /// </summary>
    public float Near;

    /// <summary>Where an orthographic view's box ends along the view (<see cref="ViewConstants.Far"/>); 0 for perspective.</summary>
    public float Far;

    /// <summary>
    /// 1 for an orthographic view (<see cref="ViewSwitches.Orthographic"/>): <see cref="Right"/> and <see cref="Up"/> are
    /// the image's half width and half height in metres, and its rays run along <see cref="Forward"/>.
    /// </summary>
    public uint Orthographic;

    /// <summary>
    /// How far the match's origin moved, in whole metres, since the pool's particles were last moved
    /// (<see cref="ParticlePoolOrigin"/>): the simulate pass takes it off every particle made before this frame, so
    /// nothing moves in the world; zero while the origin stays. Set by the render thread.
    /// </summary>
    public Vector3 Shift;
}
