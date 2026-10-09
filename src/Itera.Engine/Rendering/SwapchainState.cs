namespace Itera.Engine.Rendering;

/// <summary>
/// When the swapchain must be made again, and at what size, with no
/// Vulkan call, so tests can drive every order of events:
/// <list type="bullet">
/// <item>A resize (main thread) stores the framebuffer size; it asks for a new swapchain only when that size differs
/// from the one the swapchain was last made for (a resize event repeating the size, or
/// one that came during a recreate, would make it again at the same size).</item>
/// <item>A change of the present mode (VSync), or of the world's samples a pixel (whose pipelines and images are made
/// again with it), asks for one (main thread).</item>
/// <item>An out-of-date acquire or present (render thread) asks for one.</item>
/// <item>A suboptimal acquire or present asks for one after the present, once for each framebuffer size: a swapchain
/// made for that size and still suboptimal is kept (otherwise a suboptimal acquire is never acted
/// on, and a suboptimal present remakes the swapchain after every frame).</item>
/// <item>A zero-sized framebuffer (a minimised window) makes none; the request stays until a size comes.</item>
/// </list>
/// Sizes are passed as width and height in pixels. Thread-safe as stated for each member.
/// </summary>
public sealed class SwapchainState
{
    /// <summary>Newest framebuffer size, width in the high 32 bits. Written by the main thread, read by the render thread.</summary>
    private long _framebufferSize;

    // Asked for by the main thread.
    private volatile bool _resized;
    private volatile bool _settingChanged;

    // Render thread only.
    private bool _stale;
    private bool _suboptimal;
    private long _madeFor = -1;
    private long _suboptimalMadeFor = -1;

    /// <summary>
    /// True when the next frame must make the swapchain again before it begins. Render thread.
    /// </summary>
    public bool NeedsRecreate =>
        _stale || _settingChanged || _suboptimal || (_resized && Interlocked.Read(ref _framebufferSize) != _madeFor);

    /// <summary>The newest framebuffer size. Any thread.</summary>
    public (int Width, int Height) FramebufferSize
    {
        get
        {
            var packed = Interlocked.Read(ref _framebufferSize);
            return ((int)(packed >> 32), (int)(packed & 0xFFFF_FFFF));
        }
    }

    /// <summary>
    /// The framebuffer size the first swapchain was made for, before the render thread starts: a resize to the same size
    /// makes no other. Main thread.
    /// </summary>
    public void Start(int width, int height)
    {
        var size = Pack(width, height);
        Interlocked.Exchange(ref _framebufferSize, size);
        _madeFor = size;
    }

    /// <summary>The window's framebuffer changed size. Main thread; the size is stored before the request.</summary>
    public void Resized(int width, int height)
    {
        Interlocked.Exchange(ref _framebufferSize, Pack(width, height));
        _resized = true;
    }

    /// <summary>The present mode changed (VSync). Main thread.</summary>
    public void PresentModeChanged() => _settingChanged = true;

    /// <summary>The world's samples a pixel changed: the passes make what depends on them again. Main thread.</summary>
    public void SamplesChanged() => _settingChanged = true;

    /// <summary>An acquire or present said the swapchain is out of date. Render thread.</summary>
    public void OutOfDate() => _stale = true;

    /// <summary>
    /// An acquire or present said the swapchain is suboptimal: one is made again after the present, unless one was made
    /// for this framebuffer size after the last suboptimal. Render thread.
    /// </summary>
    public void Suboptimal()
    {
        if (Interlocked.Read(ref _framebufferSize) != _suboptimalMadeFor)
        {
            _suboptimal = true;
        }
    }

    /// <summary>
    /// Takes the requests and returns the size to make the swapchain for; false, with the request kept, when the
    /// framebuffer is empty. The requests are cleared before the size is read, so a resize during the recreate asks
    /// again. Render thread.
    /// </summary>
    public bool TryBeginRecreate(out int width, out int height)
    {
        _resized = false;
        _settingChanged = false;
        var suboptimal = _suboptimal;
        _stale = false;
        _suboptimal = false;
        (width, height) = FramebufferSize;
        if (width <= 0 || height <= 0)
        {
            // Kept for when a size comes: the old swapchain may be out of date.
            _stale = true;
            return false;
        }

        _madeFor = Pack(width, height);
        if (suboptimal)
        {
            _suboptimalMadeFor = _madeFor;
        }

        return true;
    }

    private static long Pack(int width, int height) => ((long)width << 32) | (uint)height;
}
