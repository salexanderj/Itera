using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The push constants of FSR 1's upscale (<c>fsr-easu.frag</c>, <c>layout(scalar)</c>): the size of the picture drawn
/// at the render scale and of the screen, in pixels. The shader works out FSR's constants from them
/// (<c>FsrEasuCon</c>). Field order and types match the GLSL block; <c>UpscalePassTests</c> checks them.
/// </summary>
public struct EasuConstants
{
    public Vector2 InputSize;

    public Vector2 OutputSize;
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
