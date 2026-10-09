namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The particle counters (<c>particle-data.glsl</c>, set 1 binding 2, <c>layout(scalar)</c>): the two indirect draws
/// (cubes, then sprites; each a <c>VkDrawIndirectCommand</c>) whose instance counts the simulate pass adds up, and the
/// frame's counts, copied to a readback region per frame slot for <see cref="Itera.Engine.Particles.ParticleStats"/>. Reset by the
/// render thread each simulated frame (<c>vkCmdUpdateBuffer</c>). Field order and types match the GLSL struct;
/// <c>ParticlePassTests</c> checks them.
/// </summary>
public struct ParticleCounters
{
    public uint CubeVertexCount;
    public uint CubeInstanceCount;
    public uint CubeFirstVertex;
    public uint CubeFirstInstance;
    public uint SpriteVertexCount;
    public uint SpriteInstanceCount;
    public uint SpriteFirstVertex;
    public uint SpriteFirstInstance;

    /// <summary>Particles alive after the frame's simulation.</summary>
    public uint Living;

    /// <summary>Living particles the frame's spawns overwrote (the pool was full).</summary>
    public uint Overwritten;

    /// <summary>Particles that rested this frame; no shader counts them now, so it stays 0.</summary>
    public uint Resting;

    /// <summary>Unused; keeps the struct 48 bytes.</summary>
    public uint Spare;

    /// <summary>The counters at the start of a frame: 36 vertices a cube, 6 a sprite, nothing counted.</summary>
    public static ParticleCounters Start => new() { CubeVertexCount = 36, SpriteVertexCount = 6 };
}
