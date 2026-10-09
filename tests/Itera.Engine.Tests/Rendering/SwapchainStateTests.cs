using Itera.Engine.Rendering;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// <see cref="SwapchainState"/>: a resize to a new size
/// asks for a swapchain and one repeating its size does not; a resize during a recreate asks again only for another
/// size; an empty framebuffer waits with the request kept; out-of-date and a VSync change always ask; a suboptimal asks
/// once for each size.
/// </summary>
public sealed class SwapchainStateTests
{
    [Fact]
    public void A_resize_asks_only_for_a_size_the_swapchain_was_not_made_for()
    {
        var state = new SwapchainState();
        state.Start(800, 600);
        Assert.False(state.NeedsRecreate);

        state.Resized(800, 600);
        Assert.False(state.NeedsRecreate);

        state.Resized(1024, 768);
        Assert.True(state.NeedsRecreate);
        Assert.True(state.TryBeginRecreate(out var width, out var height));
        Assert.Equal((1024, 768), (width, height));
        Assert.False(state.NeedsRecreate);

        // During the recreate: the same size again asks for nothing, another size asks.
        state.Resized(1024, 768);
        Assert.False(state.NeedsRecreate);
        state.Resized(1280, 720);
        Assert.True(state.NeedsRecreate);
        Assert.True(state.TryBeginRecreate(out width, out height));
        Assert.Equal((1280, 720), (width, height));
        Assert.False(state.NeedsRecreate);
    }

    [Fact]
    public void An_empty_framebuffer_waits_with_the_request_kept()
    {
        var state = new SwapchainState();
        state.Start(800, 600);
        state.Resized(0, 0);
        Assert.True(state.NeedsRecreate);

        Assert.False(state.TryBeginRecreate(out _, out _));
        Assert.True(state.NeedsRecreate);
        Assert.False(state.TryBeginRecreate(out _, out _));

        // Restored at the old size: the old swapchain may be out of date, so one is made.
        state.Resized(800, 600);
        Assert.True(state.NeedsRecreate);
        Assert.True(state.TryBeginRecreate(out var width, out var height));
        Assert.Equal((800, 600), (width, height));
        Assert.False(state.NeedsRecreate);
    }

    [Fact]
    public void Out_of_date_a_vsync_change_and_a_samples_change_always_ask()
    {
        var state = new SwapchainState();
        state.Start(800, 600);

        state.OutOfDate();
        Assert.True(state.NeedsRecreate);
        Assert.True(state.TryBeginRecreate(out _, out _));
        Assert.False(state.NeedsRecreate);

        state.PresentModeChanged();
        Assert.True(state.NeedsRecreate);
        Assert.True(state.TryBeginRecreate(out _, out _));
        Assert.False(state.NeedsRecreate);

        state.SamplesChanged();
        Assert.True(state.NeedsRecreate);
        Assert.True(state.TryBeginRecreate(out _, out _));
        Assert.False(state.NeedsRecreate);
    }

    [Fact]
    public void A_suboptimal_asks_once_for_each_size()
    {
        var state = new SwapchainState();
        state.Start(800, 600);

        state.Suboptimal();
        Assert.True(state.NeedsRecreate);
        Assert.True(state.TryBeginRecreate(out _, out _));

        // Still suboptimal at the size it was made for: kept.
        state.Suboptimal();
        Assert.False(state.NeedsRecreate);

        // At another size it asks again.
        state.Resized(1024, 768);
        Assert.True(state.TryBeginRecreate(out _, out _));
        state.Suboptimal();
        Assert.True(state.NeedsRecreate);
    }
}
