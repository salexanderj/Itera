using System.Numerics;

namespace Itera.Engine.Maths;

/// <summary>
/// The octahedral encoding of unit directions, as the mesh shaders read it (<c>meshNormalAt</c>), and the packed words
/// of a normal and a tangent. It repeats the encoding of <c>MeshVertex.Pack</c>, so both give the same bits.
/// </summary>
public static class Octahedral
{
    /// <summary>The value that stands for 1 in a stored 16-bit component.</summary>
    private const float Snorm16Scale = 32767f;

    /// <summary>The value that stands for 1 in the second component of a stored tangent (15 bits).</summary>
    private const float Snorm15Scale = 16383f;

    /// <summary>
    /// The coordinates of a direction on the octahedron, each in [-1, 1]. The direction need not have length 1. A zero
    /// direction, or one with a NaN, gives (0, 0), which decodes to +z.
    /// </summary>
    public static Vector2 Encode(Vector3 direction)
    {
        var l1 = MathF.Abs(direction.X) + MathF.Abs(direction.Y) + MathF.Abs(direction.Z);
        if (!(l1 > 0f))
        {
            return Vector2.Zero;
        }

        var x = direction.X / l1;
        var y = direction.Y / l1;
        if (direction.Z < 0f)
        {
            var foldedX = (1f - MathF.Abs(y)) * SignNotZero(x);
            var foldedY = (1f - MathF.Abs(x)) * SignNotZero(y);
            x = foldedX;
            y = foldedY;
        }

        return new Vector2(x, y);
    }

    /// <summary>The unit direction at the coordinates <paramref name="encoded"/> on the octahedron.</summary>
    public static Vector3 Decode(Vector2 encoded)
    {
        var x = encoded.X;
        var y = encoded.Y;
        var z = 1f - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0f)
        {
            var foldedX = (1f - MathF.Abs(y)) * SignNotZero(x);
            var foldedY = (1f - MathF.Abs(x)) * SignNotZero(y);
            x = foldedX;
            y = foldedY;
        }

        var length = MathF.Sqrt(x * x + y * y + z * z);
        return length > 0f ? new Vector3(x / length, y / length, z / length) : new Vector3(0f, 0f, 1f);
    }

    /// <summary>
    /// A direction as one word: its encoded u in bits 0-15 and v in bits 16-31, each a signed 16-bit value of
    /// <c>round(value * 32767)</c>. These are the bits <c>MeshVertex.Pack</c> stores as a vertex's normal.
    /// </summary>
    public static uint PackSnorm16(Vector3 direction)
    {
        var encoded = Encode(direction);
        var u = (ushort)Quantize(encoded.X, Snorm16Scale);
        var v = (ushort)Quantize(encoded.Y, Snorm16Scale);
        return u | ((uint)v << 16);
    }

    /// <summary>The unit direction packed in <paramref name="packed"/> by <see cref="PackSnorm16"/>.</summary>
    public static Vector3 UnpackSnorm16(uint packed) =>
        Decode(new Vector2((short)(packed & 0xFFFFu) / Snorm16Scale, (short)(packed >> 16) / Snorm16Scale));

    /// <summary>
    /// A tangent as one word: its encoded u as a signed 16-bit value of <c>round(value * 32767)</c> in bits 0-15, its
    /// encoded v as a signed 15-bit value of <c>round(value * 16383)</c> in bits 16-30, and bit 31 set when
    /// <paramref name="handedness"/> is below 0 (glTF's tangent.w of -1; any other value is +1). A GLSL shader reads it as
    /// <c>u = float(int(packed &lt;&lt; 16) &gt;&gt; 16) / 32767.0; v = float(int(packed &lt;&lt; 1) &gt;&gt; 17) / 16383.0;
    /// w = (packed &gt;&gt; 31) != 0u ? -1.0 : 1.0;</c> and decodes (u, v) as it decodes a normal.
    /// </summary>
    public static uint PackTangent(Vector3 tangent, float handedness)
    {
        var encoded = Encode(tangent);
        var u = (ushort)Quantize(encoded.X, Snorm16Scale);
        var v = (uint)Quantize(encoded.Y, Snorm15Scale) & 0x7FFFu;
        return u | (v << 16) | (handedness < 0f ? 0x8000_0000u : 0u);
    }

    /// <summary>
    /// The unit tangent packed in <paramref name="packed"/> by <see cref="PackTangent"/>, and its handedness,
    /// +1 or -1.
    /// </summary>
    public static Vector3 UnpackTangent(uint packed, out float handedness)
    {
        var u = (short)(packed & 0xFFFFu) / Snorm16Scale;
        var v = ((int)(packed << 1) >> 17) / Snorm15Scale;
        handedness = (packed >> 31) != 0u ? -1f : 1f;
        return Decode(new Vector2(u, v));
    }

    private static int Quantize(float value, float scale) =>
        (int)MathF.Round(Math.Clamp(value, -1f, 1f) * scale, MidpointRounding.AwayFromZero);

    private static float SignNotZero(float value) => value >= 0f ? 1f : -1f;
}
