namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The UI draw's push constants (<c>ui.frag</c>, <c>layout(scalar)</c>): the size of the UI pixels in the storage
/// buffer, which is the swapchain's size; how dark the picture is drawn under the UI, 0 to 1; and whether the pixels are
/// read (0: the dim alone). Field order and types match the GLSL block; <c>UiPassTests</c> checks them.
/// </summary>
public struct UiConstants
{
    public uint Width;

    public uint Height;

    public float Dim;

    public uint Pixels;
}
