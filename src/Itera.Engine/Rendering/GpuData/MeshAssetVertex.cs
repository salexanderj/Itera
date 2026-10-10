using System.Numerics;
using Itera.Engine.Maths;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// A vertex of a mesh asset (a model from a glTF file, or a mesh a game builds; <c>Assets/MeshAsset</c>), as the mesh
/// pass's asset shaders read it, 36 bytes: the position in the asset's space in metres, the normal and the tangent in
/// <see cref="Octahedral"/> encodings (<see cref="Octahedral.PackSnorm16"/> and <see cref="Octahedral.PackTangent"/>,
/// whose top bit is the bitangent's sign, glTF's tangent w), the first texture coordinates as floats, the second as two
/// halves (<c>unpackHalf2x16</c>), and the vertex colour as linear RGBA bytes, red in the low byte (white for none).
/// </summary>
public struct MeshAssetVertex
{
    public Vector3 Position;

    public uint Normal;

    public uint Tangent;

    public Vector2 Uv;

    public uint Uv1;

    public uint Colour;

    /// <summary>White, opaque: the colour of a vertex its file gives none.</summary>
    public const uint White = 0xFFFFFFFFu;

    /// <summary>
    /// Packs a vertex. The normal and the tangent need not have length 1; the tangent's w is the bitangent's sign (glTF's
    /// tangent w, -1 or 1). The colour's channels are clamped to 0..1.
    /// </summary>
    public static MeshAssetVertex Pack(
        Vector3 position,
        Vector3 normal,
        Vector4 tangent,
        Vector2 uv,
        Vector2 uv1,
        Vector4 colour
    ) =>
        new()
        {
            Position = position,
            Normal = Octahedral.PackSnorm16(normal),
            Tangent = Octahedral.PackTangent(new Vector3(tangent.X, tangent.Y, tangent.Z), tangent.W),
            Uv = uv,
            Uv1 = PackHalves(uv1),
            Colour = PackColour(colour),
        };

    /// <summary>Two floats as halves, x in the low 16 bits, as GLSL's <c>unpackHalf2x16</c> reads them.</summary>
    public static uint PackHalves(Vector2 value) =>
        BitConverter.HalfToUInt16Bits((Half)value.X) | ((uint)BitConverter.HalfToUInt16Bits((Half)value.Y) << 16);

    /// <summary>Linear RGBA in 0..1 as bytes, red in the low byte, as GLSL's <c>unpackUnorm4x8</c> reads them.</summary>
    public static uint PackColour(Vector4 colour) =>
        Unorm8(colour.X) | (Unorm8(colour.Y) << 8) | (Unorm8(colour.Z) << 16) | (Unorm8(colour.W) << 24);

    private static uint Unorm8(float value) => (uint)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
