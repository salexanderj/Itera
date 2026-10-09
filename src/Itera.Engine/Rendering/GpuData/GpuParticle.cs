using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One particle in the pool (<c>particle-data.glsl</c>, <c>layout(scalar)</c>, set 1 binding 0), 64 bytes. Written and
/// read by the GPU only: the spawn pass makes it, the simulate pass moves it, and the draws read it. The CPU never
/// writes it; the type exists for the pool's size and the layout test. Field order and types match the GLSL struct;
/// <c>ParticlePassTests</c> checks them.
/// </summary>
public struct GpuParticle
{
    /// <summary>Where it is, world metres.</summary>
    public Vector3 Position;

    /// <summary>Seconds since it was made.</summary>
    public float Age;

    /// <summary>Metres per second.</summary>
    public Vector3 Velocity;

    /// <summary>Seconds it lives; 0 or less is a free slot.</summary>
    public float Lifetime;

    /// <summary>
    /// The recipe's id in bits 0 to 7, the attractor in bits 8 to 13, resting in bit 14, the sky light 0 to 255 in bits 16 to
    /// 23, the sunlight 0 to 255 in bits 24 to 31.
    /// </summary>
    public uint Packed;

    /// <summary>Its burst's tint, RGBA8 with red lowest; white for none.</summary>
    public uint Colour;

    /// <summary>Its own size and colour, a hash; its turn axis and random values come from it.</summary>
    public uint Seed;

    /// <summary>A multiple of the recipe's size, from its jitter.</summary>
    public float Size;

    /// <summary>The second end of a segment sprite, world metres.</summary>
    public Vector3 End;

    /// <summary>A cube's first turn rate, radians per second.</summary>
    public float Spin;
}
