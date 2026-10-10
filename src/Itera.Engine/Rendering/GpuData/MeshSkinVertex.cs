using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// A skinned mesh asset's vertex's joints and weights (<c>mesh-skin.comp</c>, binding 5 of set 1): four joints of its
/// rig, 16 bits each, two to a word (the first in the low bits), and their weights as 16-bit fractions in the same way.
/// All four weights zero leave the vertex where it is (a mesh of a rigged asset that no joint moves). 16 bytes.
/// </summary>
public struct MeshSkinVertex
{
    public uint Joints01;
    public uint Joints23;
    public uint Weights01;
    public uint Weights23;

    /// <summary>The vertex moved by joints <paramref name="j0"/> to <paramref name="j3"/> (0 to 65,535) by <paramref name="weights"/> (0 to 1).</summary>
    public static MeshSkinVertex Pack(int j0, int j1, int j2, int j3, Vector4 weights) =>
        new()
        {
            Joints01 = (uint)(ushort)j0 | ((uint)(ushort)j1 << 16),
            Joints23 = (uint)(ushort)j2 | ((uint)(ushort)j3 << 16),
            Weights01 = Unorm16(weights.X) | (Unorm16(weights.Y) << 16),
            Weights23 = Unorm16(weights.Z) | (Unorm16(weights.W) << 16),
        };

    /// <summary>Joint <paramref name="slot"/> (0 to 3).</summary>
    public readonly int Joint(int slot) => (int)((slot < 2 ? Joints01 : Joints23) >> (slot % 2 * 16)) & 0xFFFF;

    /// <summary>Weight <paramref name="slot"/> (0 to 3), 0 to 1.</summary>
    public readonly float Weight(int slot) =>
        (((slot < 2 ? Weights01 : Weights23) >> (slot % 2 * 16)) & 0xFFFF) / 65535f;

    private static uint Unorm16(float value) => (uint)MathF.Round(Math.Clamp(value, 0, 1) * 65535);
}
