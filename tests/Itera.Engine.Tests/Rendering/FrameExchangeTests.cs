using System.Diagnostics;
using Itera.Engine.Rendering;
using Itera.Modding;

namespace Itera.Engine.Tests.Rendering;

public sealed class FrameExchangeTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void No_request_means_nothing_to_take()
    {
        var exchange = new FrameExchange(() => { });

        Assert.False(exchange.HasRequest);
        Assert.False(exchange.TryTakeRequest(out _));
    }

    [Fact]
    public void Each_request_wakes_the_main_thread_once_and_is_taken_once()
    {
        var wakes = 0;
        var exchange = new FrameExchange(() => wakes++);

        exchange.Request(new RenderFrameRequest(1, 640, 480));

        Assert.Equal(1, wakes);
        Assert.True(exchange.HasRequest);
        Assert.True(exchange.TryTakeRequest(out var request));
        Assert.Equal(new RenderFrameRequest(1, 640, 480), request);
        Assert.False(exchange.HasRequest);
        Assert.False(exchange.TryTakeRequest(out _));
    }

    [Fact]
    public void Publishing_without_a_taken_request_throws()
    {
        var exchange = new FrameExchange(() => { });

        Assert.Throws<InvalidOperationException>(() => exchange.Publish(default));
    }

    [Fact]
    public void Stop_ends_a_wait_that_has_no_frame()
    {
        var exchange = new FrameExchange(() => { });
        bool? result = null;
        var waiter = StartThread(() => result = exchange.WaitForFrame(out _));

        Thread.Sleep(50);
        var stopwatch = Stopwatch.StartNew();
        exchange.Stop();

        Assert.True(waiter.Join(s_timeout));
        Assert.False(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(exchange.WaitForFrame(out _));
    }

    /// <summary>A frame published before the stop is still handed over; only then does the wait report the stop.</summary>
    [Fact]
    public void A_frame_waiting_when_the_stop_comes_is_still_handed_over()
    {
        var exchange = new FrameExchange(() => { });
        exchange.Request(new RenderFrameRequest(1, 1, 1));
        Assert.True(exchange.TryTakeRequest(out var request));
        exchange.Publish(new RenderFrame(default, request.Slot, default, 0));

        exchange.Stop();

        Assert.True(exchange.WaitForFrame(out var frame));
        Assert.Equal(1, frame.Slot);
        Assert.False(exchange.WaitForFrame(out _));
    }

    /// <summary>A request the main thread took but never answered leaves the render thread nothing to wait for after a stop.</summary>
    [Fact]
    public void A_taken_request_never_answered_does_not_hold_up_a_stop()
    {
        var exchange = new FrameExchange(() => { });
        exchange.Request(new RenderFrameRequest(0, 1, 1));
        Assert.True(exchange.TryTakeRequest(out _));

        exchange.Stop();

        Assert.False(exchange.WaitForFrame(out _));
        Assert.False(exchange.HasRequest);
    }

    [Fact]
    public void Two_threads_exchange_frames_in_order()
    {
        const int Frames = 10_000;
        using var wake = new SemaphoreSlim(0);
        var exchange = new FrameExchange(() => wake.Release());
        var received = new List<(ulong Index, int Slot)>(Frames);

        // The render thread: ask, wait, check, like RenderThread does.
        var render = StartThread(() =>
        {
            for (var i = 0; i < Frames; i++)
            {
                exchange.Request(new RenderFrameRequest(i % FrameSync.FramesInFlight, 1, 1));
                if (!exchange.WaitForFrame(out var frame))
                {
                    return;
                }

                received.Add((frame.Time.FrameIndex, frame.Slot));
            }
        });

        // The main thread: wait for a wake-up, then answer every pending request, like GameLoop does.
        var built = 0UL;
        var deadline = Stopwatch.StartNew();
        while (built < Frames && deadline.Elapsed < s_timeout)
        {
            wake.Wait(TimeSpan.FromMilliseconds(100));
            while (exchange.TryTakeRequest(out var request))
            {
                exchange.Publish(new RenderFrame(new FrameTime(0f, 0d, built, 0f), request.Slot, default, 0));
                built++;
            }
        }

        exchange.Stop();
        Assert.True(render.Join(s_timeout));
        Assert.Equal(Frames, received.Count);
        for (var i = 0; i < Frames; i++)
        {
            Assert.Equal(((ulong)i, i % FrameSync.FramesInFlight), received[i]);
        }
    }

    private static Thread StartThread(Action body)
    {
        var thread = new Thread(() => body()) { IsBackground = true };
        thread.Start();
        return thread;
    }
}
