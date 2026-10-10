using Itera.Engine.Rendering.GpuData;
using Itera.Modding;

namespace Itera.Engine.Rendering;

/// <summary>
/// What the render thread asks the main thread for. <paramref name="Slot"/> is the frame slot whose GPU work has
/// finished: until the frame is published, the main thread may write that slot's memory.
/// </summary>
/// <param name="Slot">Frame slot the frame is drawn in, from 0 to <see cref="FrameSync.FramesInFlight"/> - 1.</param>
/// <param name="Width">Width of the image the frame is drawn to, in pixels.</param>
/// <param name="Height">Height of the image the frame is drawn to, in pixels.</param>
public readonly record struct RenderFrameRequest(int Slot, uint Width, uint Height);

/// <summary>One frame the main thread built for the render thread. The extract step adds its data here later.</summary>
/// <param name="Time">The frame's timing, as the frame systems saw it.</param>
/// <param name="Slot">The slot from the request this frame answers.</param>
/// <param name="View">Where the frame is seen from: the camera of <see cref="RenderView"/> for the request's image size.</param>
/// <param name="MeshCopies">Copy regions the world's meshes staged in the slot (<c>WorldMeshes.Regions</c>).</param>
/// <param name="DebugLineVertices">Line ends <see cref="DebugLinePass.Write"/> put in the slot's region; 0 draws none.</param>
/// <param name="UiRowStart">First row of the UI that <see cref="UiPass.Write"/> put in the slot's region.</param>
/// <param name="UiRows">Rows of the UI <see cref="UiPass.Write"/> put in the slot's region; 0 copies none.</param>
/// <param name="UiVisible">True when the UI shows anything; false draws no UI.</param>
/// <param name="Particles">What <see cref="ParticlePass.Write"/> put in the slot's regions; not active draws none.</param>
/// <param name="RenderScale">
/// The render scale the view was built with (<see cref="Rendering.RenderScale"/>). The render thread sizes the drawn
/// image from it and the swapchain's size, so a frame presented again after a resize still fits the swapchain.
/// </param>
/// <param name="Loading">The loading screen: none, covering the picture (the world is not drawn), or fading out over it.</param>
/// <param name="Screenshot">The screenshot this frame takes (<see cref="Screenshots"/>), or null; taken only when fresh.</param>
/// <param name="ShaderReload">
/// The reload of the shaders made before this frame is drawn (<see cref="Shaders.ShaderReloads"/>), or null; only when fresh.
/// </param>
/// <param name="ScreenDim">
/// How dark the picture is drawn under the UI, 0 to 1 (<see cref="RenderView.ScreenDim"/>); above 0 the UI pass draws
/// even when the UI shows nothing.
/// </param>
/// <param name="AntiAlias">
/// Whether the picture is anti-aliased with SMAA 1x (<see cref="RenderView.AntiAliasDrawn"/>); never on a world drawn with
/// more than one sample a pixel.
/// </param>
/// <param name="AntiAliasPicture">What the anti-aliasing shows (<see cref="RenderView.AntiAliasPicture"/>).</param>
/// <param name="Meshes">The draws <c>WorldMeshes</c> wrote in the slot's regions.</param>
/// <param name="MeshAssets">The mesh assets' draws <c>MeshAssetDraws</c> wrote in the slot's regions.</param>
public readonly record struct RenderFrame(
    FrameTime Time,
    int Slot,
    ViewConstants View,
    int MeshCopies,
    int DebugLineVertices = 0,
    int UiRowStart = 0,
    int UiRows = 0,
    bool UiVisible = false,
    float RenderScale = 1,
    ParticleWrite Particles = default,
    LoadingDraw Loading = default,
    ScreenshotRequest? Screenshot = null,
    Shaders.ShaderReloadRequest? ShaderReload = null,
    float ScreenDim = 0,
    bool AntiAlias = false,
    SmaaPicture AntiAliasPicture = SmaaPicture.Blended,
    Meshes.MeshFrame Meshes = default,
    Meshes.MeshAssetFrame MeshAssets = default
);

/// <summary>The loading screen in a frame (<see cref="LoadingPass"/>).</summary>
/// <param name="Alpha">How opaque it is: 0 draws none, 1 covers the picture instead of the world.</param>
/// <param name="Seconds">Seconds since loading began, for the shader's motion.</param>
public readonly record struct LoadingDraw(float Alpha, float Seconds)
{
    /// <summary>True when the loading screen is drawn at all.</summary>
    public bool Drawn => Alpha > 0;

    /// <summary>True when it covers the whole picture, so the world's passes are not recorded.</summary>
    public bool Covers => Alpha >= 1;
}

/// <summary>
/// Hands frames from the main thread to the render thread, one at a time. The render thread asks with
/// <see cref="Request"/> once a frame slot is free, which wakes the main thread; the main thread takes the request,
/// builds the frame, and <see cref="Publish"/>es it; the render thread receives it from <see cref="WaitForFrame"/>.
/// The main thread never waits here, so presentation that blocks never stops the simulation. Nothing allocates per
/// frame.
/// </summary>
public sealed class FrameExchange
{
    private readonly Action _wakeMainThread;
    private readonly object _lock = new();
    private RenderFrameRequest _request;
    private int _requested;
    private RenderFrame _frame;
    private bool _frameReady;
    private bool _stopping;
    private bool _requestTaken;

    /// <param name="wakeMainThread">Wakes the main loop from its wait for window events. Called on the render thread.</param>
    public FrameExchange(Action wakeMainThread)
    {
        _wakeMainThread = wakeMainThread;
    }

    /// <summary>True while a request waits for the main thread. Main thread.</summary>
    public bool HasRequest => Volatile.Read(ref _requested) != 0;

    /// <summary>
    /// True from <see cref="TryTakeRequest"/> until <see cref="Publish"/>: a frame is owed to the render thread, which
    /// waits for it. Main thread.
    /// </summary>
    public bool RequestTaken => _requestTaken;

    /// <summary>Asks the main thread for a frame and wakes it. Render thread.</summary>
    public void Request(in RenderFrameRequest request)
    {
        // The fields are written before the flag, and the flag is read before the fields.
        _request = request;
        Volatile.Write(ref _requested, 1);
        _wakeMainThread();
    }

    /// <summary>Takes the pending request, if there is one. Main thread; follow a true result with <see cref="Publish"/>.</summary>
    public bool TryTakeRequest(out RenderFrameRequest request)
    {
        if (Interlocked.Exchange(ref _requested, 0) == 0)
        {
            request = default;
            return false;
        }

        request = _request;
        _requestTaken = true;
        return true;
    }

    /// <summary>Hands the frame to the render thread. Main thread, once per taken request.</summary>
    public void Publish(in RenderFrame frame)
    {
        if (!_requestTaken)
        {
            throw new InvalidOperationException("A frame can only be published in answer to a taken request.");
        }

        _requestTaken = false;
        lock (_lock)
        {
            _frame = frame;
            _frameReady = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Blocks until the main thread publishes a frame, then returns it. Returns false without a frame once
    /// <see cref="Stop"/> has been called and no frame is waiting. Render thread.
    /// </summary>
    public bool WaitForFrame(out RenderFrame frame)
    {
        lock (_lock)
        {
            while (!_frameReady)
            {
                if (_stopping)
                {
                    frame = default;
                    return false;
                }

                Monitor.Wait(_lock);
            }

            _frameReady = false;
            frame = _frame;
            return true;
        }
    }

    /// <summary>Makes <see cref="WaitForFrame"/> return at once, now and from now on. Any thread.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopping = true;
            Monitor.PulseAll(_lock);
        }
    }
}
