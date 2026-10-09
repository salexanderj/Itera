using Silk.NET.Maths;

namespace Itera.Engine.Rendering;

/// <summary>
/// What <see cref="RenderThread"/> asks of <see cref="FrameRenderer"/> each frame, so its stop and fault paths can be
/// tested with a renderer that draws nothing. Render thread, except <see cref="SetFramebufferSize"/>.
/// </summary>
internal interface IFrameRenderer
{
    /// <summary>The window's framebuffer size, from the main thread.</summary>
    void SetFramebufferSize(Vector2D<int> size);

    /// <summary>Recreates the swapchain if asked, waits for the slot's fence; false while there is no swapchain.</summary>
    bool BeginFrame();

    /// <summary>Waits up to <paramref name="millisecondsTimeout"/> for a resize while there is no swapchain.</summary>
    void WaitForResize(int millisecondsTimeout);

    /// <summary>Acquires a swapchain image; false when it must be recreated first.</summary>
    bool TryAcquire(out uint imageIndex);

    /// <summary>The request for the frame of the current slot.</summary>
    RenderFrameRequest CreateRequest();

    /// <summary>Records, submits, and presents <paramref name="frame"/>; <paramref name="fresh"/> false for one shown again.</summary>
    void Present(uint imageIndex, in RenderFrame frame, bool fresh);
}
