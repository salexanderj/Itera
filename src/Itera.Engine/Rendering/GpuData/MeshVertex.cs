using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One vertex of a block's surface mesh as the shaders read it, 16 bytes (four <c>uint</c>s, a <c>layout(scalar)</c>
/// storage buffer). <see cref="PositionXY"/> holds x in bits 0-15 and y in bits 16-31, <see cref="PositionZMaterial"/>
/// holds z in bits 0-15, the material in bits 16-23, and the openness in bits 24-31. <see cref="Weights"/> holds, in four
/// bits for each of the eight materials of its mesh's palette (<c>MeshBuffers.Palette</c>), how many of its cell's surface
/// corners are that material, 0 to 8: blended across a triangle, they say where the borders between materials lie. A position component is
/// <c>round((p + 2) * 256)</c> as an unsigned 16-bit value, p being the block-local position in voxels (a block's
/// vertices lie in [-0.5, 32.5]; the encoding reaches [-2, 253.99]). <see cref="Normal"/> is the octahedral encoding of
/// the unit normal: u in bits 0-15 and v in bits 16-31, each a signed 16-bit value of <c>round(value * 32767)</c>.
/// Field order and types match the GLSL struct.
/// </summary>
public struct MeshVertex
{
    public uint PositionXY;

    public uint PositionZMaterial;

    public uint Normal;

    public uint Weights;

    /// <summary>Words of a vertex in the mesh heap.</summary>
    public const int Words = 4;

    /// <summary>What a position component is shifted by before it is scaled and stored, in voxels.</summary>
    public const float PositionBias = 2f;

    /// <summary>Steps per voxel of a stored position component.</summary>
    public const float PositionScale = 256f;

    /// <summary>The value that stands for 1 in a stored normal component.</summary>
    public const float NormalScale = 32767f;

    /// <summary>
    /// Packs a vertex. The normal need not have length 1: only its direction is stored (a zero normal is stored as
    /// (0, 0, 1)). The openness is 255 for fully open space.
    /// </summary>
    public static MeshVertex Pack(Vector3 position, Vector3 normal, byte material, byte openness, uint weights = 0)
    {
        EncodeOctahedral(normal, out var u, out var v);
        return new MeshVertex
        {
            PositionXY = PackPosition(position.X) | (PackPosition(position.Y) << 16),
            PositionZMaterial = PackPosition(position.Z) | ((uint)material << 16) | ((uint)openness << 24),
            Normal = (ushort)u | ((uint)(ushort)v << 16),
            Weights = weights,
        };
    }

    /// <summary>The block-local position, in voxels.</summary>
    public static Vector3 DecodePosition(in MeshVertex vertex) =>
        new(
            UnpackPosition(vertex.PositionXY & 0xFFFFu),
            UnpackPosition(vertex.PositionXY >> 16),
            UnpackPosition(vertex.PositionZMaterial & 0xFFFFu)
        );

    /// <summary>The unit normal.</summary>
    public static Vector3 DecodeNormal(in MeshVertex vertex)
    {
        var u = (short)(vertex.Normal & 0xFFFFu) / NormalScale;
        var v = (short)(vertex.Normal >> 16) / NormalScale;
        var x = u;
        var y = v;
        var z = 1f - MathF.Abs(u) - MathF.Abs(v);
        if (z < 0f)
        {
            x = (1f - MathF.Abs(v)) * SignNotZero(u);
            y = (1f - MathF.Abs(u)) * SignNotZero(v);
        }

        var length = MathF.Sqrt(x * x + y * y + z * z);
        return length > 0f ? new Vector3(x / length, y / length, z / length) : new Vector3(0f, 0f, 1f);
    }

    public static byte DecodeMaterial(in MeshVertex vertex) => (byte)(vertex.PositionZMaterial >> 16);

    /// <summary>How many of the vertex's cell's surface corners are its palette's material <paramref name="slot"/>, 0 to 8.</summary>
    public static int DecodeWeight(in MeshVertex vertex, int slot) => (int)(vertex.Weights >> (slot * 4)) & 15;

    /// <summary>
    /// How open the space round the vertex is: 255 less the mean density round it (<c>SurfaceNets</c>), 255 for only air,
    /// 0 for only solid.
    /// </summary>
    public static byte DecodeOpenness(in MeshVertex vertex) => (byte)(vertex.PositionZMaterial >> 24);

    private static uint PackPosition(float value)
    {
        var scaled = MathF.Floor((value + PositionBias) * PositionScale + 0.5f);
        return (uint)Math.Clamp(scaled, 0f, 65535f);
    }

    private static float UnpackPosition(uint stored) => stored / PositionScale - PositionBias;

    private static void EncodeOctahedral(Vector3 normal, out short u, out short v)
    {
        var l1 = MathF.Abs(normal.X) + MathF.Abs(normal.Y) + MathF.Abs(normal.Z);
        if (!(l1 > 0f))
        {
            u = 0;
            v = 0;
            return;
        }

        var px = normal.X / l1;
        var py = normal.Y / l1;
        if (normal.Z < 0f)
        {
            var qx = (1f - MathF.Abs(py)) * SignNotZero(px);
            var qy = (1f - MathF.Abs(px)) * SignNotZero(py);
            px = qx;
            py = qy;
        }

        u = QuantizeNormal(px);
        v = QuantizeNormal(py);
    }

    private static short QuantizeNormal(float value) =>
        (short)MathF.Round(Math.Clamp(value, -1f, 1f) * NormalScale, MidpointRounding.AwayFromZero);

    private static float SignNotZero(float value) => value >= 0f ? 1f : -1f;
}
