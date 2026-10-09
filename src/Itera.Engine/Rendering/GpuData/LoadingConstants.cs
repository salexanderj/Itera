namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The loading screen's push constants (<c>loading.frag</c>, <c>layout(scalar)</c>): the screen's size in pixels, the
/// seconds since loading began, how opaque it is (1, falling to 0 as it fades out), and 1 when the swapchain image is
/// sRGB, so the shader's display colours are made linear first. Field order and types match the GLSL block;
/// <c>LoadingPassTests</c> checks them.
/// </summary>
public struct LoadingConstants
{
    public uint Width;

    public uint Height;

    public float Time;

    public float Alpha;

    public uint LinearOutput;
}
