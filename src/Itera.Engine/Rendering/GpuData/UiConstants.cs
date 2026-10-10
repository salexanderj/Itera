using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// The UI draw's push constants (<c>ui.frag</c>, <c>layout(scalar)</c>): the size of the UI pixels in the storage
/// buffer, which is the swapchain's size; how dark the picture is drawn under the UI, 0 to 1; whether the pixels are
/// read (0: the dim alone); and the image views shown under the UI's pixels (<see cref="RenderViews.Show"/>): how many,
/// each one's view (a byte each of <see cref="PanelViews"/>, lowest first), and each one's rectangle in screen pixels
/// (x and y from the top-left, width, height). Field order and types match the GLSL block; <c>UiPassTests</c> checks
/// them.
/// </summary>
public struct UiConstants
{
    public uint Width;

    public uint Height;

    public float Dim;

    public uint Pixels;

    public uint Panels;

    public uint PanelViews;

    public Vector4 Panel0;

    public Vector4 Panel1;

    public Vector4 Panel2;

    public Vector4 Panel3;
}
