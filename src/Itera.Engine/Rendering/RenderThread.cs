using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;
using Silk.NET.Maths;

namespace Itera.Engine.Rendering;

/// <summary>
/// The thread that draws. After <see cref="Start"/>, it alone uses the Vulkan queues, the swapchain, and
/// <see cref="FrameSync"/>; the main thread makes no Vulkan call until <see cref="Stop"/> has ended the thread but two
/// on the memory of the frame slot it builds (the staging flush and the probe's invalidate, which have no externally
/// synchronized parameter). Each
/// frame it waits for its frame slot, acquires an image, asks the main thread for a frame through
/// <see cref="FrameExchange"/>, and records, submits, and presents it. When presentation blocks (for example while the
/// window is hidden), only this thread waits; the main thread keeps simulating.
/// </summary>
public sealed partial class RenderThread
{
    /// <summary>A gap between two presented frames longer than this is logged.</summary>
    private static readonly TimeSpan BlockedThreshold = TimeSpan.FromSeconds(0.5);

    private const int ResizeWaitMilliseconds = 100;

    private readonly IFrameRenderer _renderer;
    private readonly FrameExchange _exchange;
    private readonly Action _wake;
    private readonly ILogger<RenderThread> _logger;
    private readonly long _frameCapTicks;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile ExceptionDispatchInfo? _fault;
    private long _allocatedBytes;

    public RenderThread(
        FrameRenderer renderer,
        FrameExchange exchange,
        WindowService window,
        ILogger<RenderThread> logger
    )
        : this(renderer, exchange, window.Wake, logger, window.FrameCapSeconds) { }

    /// <param name="renderer">What draws the frames.</param>
    /// <param name="exchange">Where frames are asked for and handed over.</param>
    /// <param name="wake">Wakes the main loop, which then finds the thread faulted.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="frameCapSeconds">The least time between the starts of two frames; 0 for none (presenting paces).</param>
    internal RenderThread(
        IFrameRenderer renderer,
        FrameExchange exchange,
        Action wake,
        ILogger<RenderThread> logger,
        double frameCapSeconds = 0
    )
    {
        _frameCapTicks = (long)(Math.Max(0, frameCapSeconds) * Stopwatch.Frequency);
        _renderer = renderer;
        _exchange = exchange;
        _wake = wake;
        _logger = logger;
    }

    /// <summary>True after the thread ended with an exception. Main thread: call <see cref="ThrowIfFaulted"/>.</summary>
    public bool IsFaulted => _fault is not null;

    /// <summary>Managed bytes the render thread has allocated since it started, as of its last presented frame.</summary>
    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

    /// <summary>Starts the thread. Main thread, after the swapchain and frame sync exist.</summary>
    /// <param name="framebufferSize">The window's framebuffer size now; the render thread cannot ask GLFW.</param>
    public void Start(Vector2D<int> framebufferSize)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("The render thread has already been started.");
        }

        _renderer.SetFramebufferSize(framebufferSize);
        _thread = new Thread(Run) { Name = "Itera render", IsBackground = true };
        _thread.Start();
        LogStarted(_thread.ManagedThreadId);
    }

    /// <summary>
    /// Asks the thread to stop and waits up to <paramref name="timeout"/>. Returns true when it has ended (or never
    /// started). False means it is still inside a Vulkan call, usually a present that blocks while the window is
    /// hidden; the caller must then leave every Vulkan object and the window alone. Main thread.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        if (_thread is null)
        {
            return true;
        }

        _stopRequested = true;
        _exchange.Stop();
        if (!_thread.Join(timeout))
        {
            return false;
        }

        _thread = null;
        LogStopped();
        return true;
    }

    /// <summary>Rethrows the exception that ended the render thread, if any. Main thread.</summary>
    public void ThrowIfFaulted()
    {
        _fault?.Throw();
    }

    private void Run()
    {
        try
        {
            var lastPresent = Stopwatch.GetTimestamp();
            var lastFrame = default(RenderFrame);
            var frameStart = 0L;
            while (!_stopRequested)
            {
                // Without a window nothing paces the frames: each starts a frame's time after the last.
                if (_frameCapTicks > 0)
                {
                    var wait = frameStart + _frameCapTicks - Stopwatch.GetTimestamp();
                    if (wait > 0)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
                    }

                    frameStart = Stopwatch.GetTimestamp();
                }

                if (!_renderer.BeginFrame())
                {
                    _renderer.WaitForResize(ResizeWaitMilliseconds);
                    continue;
                }

                if (!_renderer.TryAcquire(out var imageIndex))
                {
                    continue;
                }

                var gap = Stopwatch.GetElapsedTime(lastPresent);
                if (gap > BlockedThreshold)
                {
                    var seconds = gap.TotalSeconds;
                    LogPresentationGap(seconds);
                }

                _exchange.Request(_renderer.CreateRequest());

                // Stop can arrive while this thread holds an acquired image. It is then presented with the last frame,
                // so no acquire is left without a present and the slot's fence is signalled again; that frame's
                // uploads are not recorded a second time.
                var fresh = _exchange.WaitForFrame(out var frame);
                if (fresh)
                {
                    lastFrame = frame;
                }

                lastPresent = Stopwatch.GetTimestamp();
                _renderer.Present(imageIndex, in lastFrame, fresh);
                Interlocked.Exchange(ref _allocatedBytes, GC.GetAllocatedBytesForCurrentThread());
            }
        }
        catch (Exception ex)
        {
            _fault = ExceptionDispatchInfo.Capture(ex);
            if (_stopRequested)
            {
                // The main thread has stopped waiting for this thread; nothing is left to tell.
                return;
            }

            try
            {
                _wake();
            }
            catch (Exception)
            {
                // The main loop checks IsFaulted on its next wake-up at the latest, one fixed step later.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Render thread started (managed thread {RenderThreadId})")]
    private partial void LogStarted(int renderThreadId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Render thread stopped")]
    private partial void LogStopped();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No frame was presented for {Seconds:F1} s (window hidden or minimised, or the compositor paused); the simulation kept running"
    )]
    private partial void LogPresentationGap(double seconds);
}
