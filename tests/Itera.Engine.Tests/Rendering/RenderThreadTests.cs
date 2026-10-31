using Itera.Engine.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// <see cref="RenderThread"/> over a renderer that draws nothing: published frames are presented fresh;
/// a stop that arrives while the thread waits for a frame presents the last one again, so no acquired image is left
/// without a present; a renderer that throws faults the thread, wakes the main loop, and the fault is rethrown there;
/// a thread stuck inside a present makes <see cref="RenderThread.Stop"/> say so; and a frame that cannot begin (a
/// minimised window) waits for a resize and tries again, an acquire that fails tries again without asking for a frame,
/// and a stop ends a thread that is waiting.
/// </summary>
public sealed class RenderThreadTests
{
    private static readonly TimeSpan s_timeout = TestTime.Limit(30);

    [Fact]
    public void Published_frames_are_presented_fresh_and_a_stop_presents_the_last_one_again()
    {
        var renderer = new FakeRenderer();
        var exchange = new FrameExchange(() => { });
        var thread = new RenderThread(renderer, exchange, () => { }, NullLogger<RenderThread>.Instance);
        thread.Start(new Vector2D<int>(640, 480));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(SpinUntil(() => exchange.HasRequest));
            Assert.True(exchange.TryTakeRequest(out _));
            exchange.Publish(default);
        }

        // The fourth request is taken but never answered: the thread holds an acquired image when the stop comes.
        Assert.True(SpinUntil(() => exchange.HasRequest));
        Assert.True(thread.Stop(s_timeout));

        Assert.Equal(renderer.Acquired, renderer.Presented.Count);
        Assert.Equal([true, true, true, false], renderer.Presented.Take(4));
        Assert.False(thread.IsFaulted);
    }

    /// <summary>Without a window nothing paces presents: a frame cap spaces the frames' starts.</summary>
    [Fact]
    public void A_frame_cap_spaces_the_frames_it_asks_for()
    {
        var renderer = new FakeRenderer();
        var exchange = new FrameExchange(() => { });
        var thread = new RenderThread(renderer, exchange, () => { }, NullLogger<RenderThread>.Instance, 0.05);
        // Started before the thread, which may ask for its first frame at once: a clock started after it missed up to
        // 12 ms of the first gap on a busy CI runner.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        thread.Start(new Vector2D<int>(640, 480));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(SpinUntil(() => exchange.HasRequest));
            Assert.True(exchange.TryTakeRequest(out _));
            exchange.Publish(default);
        }

        // Five frames' starts, four gaps of at least 50 ms between them.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(190), $"{clock.Elapsed.TotalMilliseconds} ms");
        Assert.True(thread.Stop(s_timeout));
    }

    [Fact]
    public void A_renderer_that_throws_faults_the_thread_and_wakes_the_main_loop()
    {
        var woken = new ManualResetEventSlim(false);
        var renderer = new FakeRenderer { Failure = new InvalidOperationException("device lost") };
        var thread = new RenderThread(
            renderer,
            new FrameExchange(() => { }),
            woken.Set,
            NullLogger<RenderThread>.Instance
        );

        thread.Start(new Vector2D<int>(640, 480));

        Assert.True(woken.Wait(s_timeout));
        Assert.True(thread.IsFaulted);
        var failure = Assert.Throws<InvalidOperationException>(thread.ThrowIfFaulted);
        Assert.Equal("device lost", failure.Message);
        Assert.True(thread.Stop(s_timeout));
    }

    [Fact]
    public void A_thread_stuck_in_a_present_makes_stop_say_so()
    {
        using var release = new ManualResetEventSlim(false);
        var renderer = new FakeRenderer { Hold = release };
        var exchange = new FrameExchange(() => { });
        var thread = new RenderThread(renderer, exchange, () => { }, NullLogger<RenderThread>.Instance);
        thread.Start(new Vector2D<int>(640, 480));
        Assert.True(SpinUntil(() => exchange.HasRequest));
        Assert.True(exchange.TryTakeRequest(out _));
        exchange.Publish(default);
        Assert.True(renderer.InPresent.Wait(s_timeout));

        Assert.False(thread.Stop(TimeSpan.FromMilliseconds(100)));

        release.Set();
        Assert.True(thread.Stop(s_timeout));
    }

    [Fact]
    public void A_frame_that_cannot_begin_waits_for_a_resize_and_an_acquire_that_fails_tries_again()
    {
        var renderer = new FakeRenderer();
        renderer.Script(begins: [false, false, false], acquires: [false, false]);
        var exchange = new FrameExchange(() => { });
        var thread = new RenderThread(renderer, exchange, () => { }, NullLogger<RenderThread>.Instance);
        thread.Start(new Vector2D<int>(640, 480));

        for (var i = 0; i < 2; i++)
        {
            Assert.True(SpinUntil(() => exchange.HasRequest));
            Assert.True(exchange.TryTakeRequest(out _));
            exchange.Publish(default);
        }

        Assert.True(SpinUntil(() => exchange.HasRequest));
        Assert.True(thread.Stop(s_timeout));

        Assert.Equal(3, renderer.ResizeWaits);
        // Two acquires failed: each tried again from the next frame's start, and no frame was asked for them.
        Assert.Equal(renderer.Acquired - 2, renderer.Presented.Count);
        Assert.Equal([true, true, false], renderer.Presented);
        Assert.False(thread.IsFaulted);
    }

    [Fact]
    public void A_stop_ends_a_thread_waiting_for_a_window_to_draw_to()
    {
        var renderer = new FakeRenderer();
        renderer.Script(begins: [], acquires: [], thenBegin: false);
        var thread = new RenderThread(
            renderer,
            new FrameExchange(() => { }),
            () => { },
            NullLogger<RenderThread>.Instance
        );
        thread.Start(new Vector2D<int>(0, 0));
        Assert.True(SpinUntil(() => renderer.ResizeWaits > 2));

        Assert.True(thread.Stop(s_timeout));
        Assert.Equal(0, renderer.Acquired);
    }

    private static bool SpinUntil(Func<bool> condition) => SpinWait.SpinUntil(condition, s_timeout);

    /// <summary>
    /// A renderer with a swapchain that acquires (but for the failures scripted), and records what it presents.
    /// </summary>
    private sealed class FakeRenderer : IFrameRenderer
    {
        private readonly Lock _lock = new();
        private readonly Queue<bool> _begins = new();
        private readonly Queue<bool> _acquires = new();
        private int _resizeWaits;
        private bool _thenBegin = true;

        public Exception? Failure { get; init; }

        /// <summary>While set, each present waits for it.</summary>
        public ManualResetEventSlim? Hold { get; init; }

        public ManualResetEventSlim InPresent { get; } = new(false);

        public int Acquired { get; private set; }

        /// <summary>Whether each presented frame was fresh.</summary>
        public List<bool> Presented { get; } = [];

        /// <summary>Times the thread waited for a resize after a frame could not begin.</summary>
        public int ResizeWaits => Volatile.Read(ref _resizeWaits);

        /// <summary>
        /// Results for the next calls of <see cref="BeginFrame"/> and <see cref="TryAcquire"/>; then true, but for
        /// <see cref="BeginFrame"/> when not <paramref name="thenBegin"/> (a window that never comes back).
        /// </summary>
        public void Script(bool[] begins, bool[] acquires, bool thenBegin = true)
        {
            lock (_lock)
            {
                _thenBegin = thenBegin;
                foreach (var begin in begins)
                {
                    _begins.Enqueue(begin);
                }

                foreach (var acquire in acquires)
                {
                    _acquires.Enqueue(acquire);
                }
            }
        }

        public void SetFramebufferSize(Vector2D<int> size) { }

        public bool BeginFrame()
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            lock (_lock)
            {
                return _begins.TryDequeue(out var begin) ? begin : _thenBegin;
            }
        }

        public void WaitForResize(int millisecondsTimeout) => Interlocked.Increment(ref _resizeWaits);

        public bool TryAcquire(out uint imageIndex)
        {
            imageIndex = 0;
            lock (_lock)
            {
                Acquired++;
                return !_acquires.TryDequeue(out var acquire) || acquire;
            }
        }

        public RenderFrameRequest CreateRequest() => new(0, 640, 480);

        public void Present(uint imageIndex, in RenderFrame frame, bool fresh)
        {
            InPresent.Set();
            Hold?.Wait();
            lock (_lock)
            {
                Presented.Add(fresh);
            }
        }
    }
}
