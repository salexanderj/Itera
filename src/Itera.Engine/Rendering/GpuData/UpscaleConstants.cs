using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The push constants of FSR 1's upscale (<c>fsr-easu.frag</c>, <c>layout(scalar)</c>): FSR's four constants, worked out
/// once a frame on the CPU (<see cref="Create"/>), and what the shader needs to stay inside the picture. The picture
/// fills the top-left part of the source image. Field order and types match the GLSL block; <c>UpscalePassTests</c>
/// checks them.
/// </summary>
public struct EasuConstants
{
    /// <summary>
    /// FSR 1's <c>con0</c> to <c>con3</c> (<c>FsrEasuCon</c> in <c>ffx_fsr1.glsl</c>). FSR packs each value as a float's
    /// bits in a <c>uvec4</c> (<c>AU1_AF1</c>); these hold the same floats, and the shader takes their bits.
    /// </summary>
    public Vector4 Con0;

    /// <inheritdoc cref="Con0"/>
    public Vector4 Con1;

    /// <inheritdoc cref="Con0"/>
    public Vector4 Con2;

    /// <inheritdoc cref="Con0"/>
    public Vector4 Con3;

    /// <summary>The size of the source image in texels.</summary>
    public Vector2 SourceSize;

    /// <summary>The size of the picture in the source image's top-left part, in texels.</summary>
    public Vector2 PictureSize;

    /// <summary>
    /// The constants for a picture of <paramref name="picture"/> in the top-left part of a source image of
    /// <paramref name="source"/>, upscaled to <paramref name="output"/>, all in pixels: FSR 1's <c>FsrEasuCon</c> with
    /// the picture as the input viewport and the source as the input size, worked out as the GLSL does in 32-bit floats.
    /// </summary>
    public static EasuConstants Create(Vector2 picture, Vector2 source, Vector2 output)
    {
        // FsrEasuCon, line for line; ARcpF1 is 1 / x.
        var constants = new EasuConstants
        {
            Con0 = new Vector4(
                picture.X * (1f / output.X),
                picture.Y * (1f / output.Y),
                0.5f * picture.X * (1f / output.X) - 0.5f,
                0.5f * picture.Y * (1f / output.Y) - 0.5f
            ),
            Con1 = new Vector4(1f / source.X, 1f / source.Y, 1f * (1f / source.X), -1f * (1f / source.Y)),
            Con2 = new Vector4(-1f * (1f / source.X), 2f * (1f / source.Y), 1f * (1f / source.X), 2f * (1f / source.Y)),
            Con3 = new Vector4(0f * (1f / source.X), 4f * (1f / source.Y), 0f, 0f),
            SourceSize = source,
            PictureSize = picture,
        };
        return constants;
    }
}

/// <summary>
/// The push constants of FSR 1's sharpening (<c>fsr-rcas.frag</c>, <c>layout(scalar)</c>). Field order and types match
/// the GLSL block; <c>UpscalePassTests</c> checks them.
/// </summary>
public struct RcasConstants
{
    /// <summary>Width of the screen in pixels.</summary>
    public uint Width;

    /// <summary>Height of the screen in pixels.</summary>
    public uint Height;

    /// <summary>Sharpening in stops below the most: 0 the sharpest, each 1 half as much.</summary>
    public float Sharpness;

    /// <summary>1 when the target is an sRGB image, which encodes what it is given: the shader then writes linear colour.</summary>
    public uint LinearOutput;
}
