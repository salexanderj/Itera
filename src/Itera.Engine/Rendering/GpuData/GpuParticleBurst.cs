using System.Numerics;
using Itera.Engine.Particles;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// A <see cref="ParticleBurst"/> as the spawn pass reads it (<c>particle-data.glsl</c>, <c>layout(scalar)</c>, set 1
/// binding 4), with the ring slots the CPU gave it. Written by the main thread (<c>ParticlePass.Write</c>) into the
/// frame slot's region, in order of <see cref="Prefix"/>. Field order and types match the GLSL struct;
/// <c>ParticlePassTests</c> checks them.
/// </summary>
public struct GpuParticleBurst
{
    public Vector3 Origin;

    /// <summary>The frame's spawn index of its first particle: the bursts before it hold this many.</summary>
    public uint Prefix;
    public Vector3 Direction;
    public uint Count;
    public Vector3 End;
    public float Radius;
    public Vector3 Velocity;

    /// <summary>The pool slot of its first particle; the others follow it without wrapping.</summary>
    public uint First;

    /// <summary>The recipe's id in bits 0 to 7, the shape in bits 8 to 11, the attractor in bits 12 to 17.</summary>
    public uint Packed;

    /// <summary>The tint, RGBA8 with red lowest; white for none.</summary>
    public uint Colour;
    public uint Seed;
}
