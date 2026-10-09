using Itera.Engine.Rendering;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Windowing;

/// <summary>
/// <see cref="WindowService"/> without a window (<c>Engine:Window:Mode=Offscreen</c>): no GLFW window is made, the size
/// is the options', the surface is headless, events and titles do nothing, the frames are capped, and exits still come
/// with their codes.
/// </summary>
public sealed class WindowServiceTests
{
    [Fact]
    public void Offscreen_it_makes_no_window_and_stands_in_for_one()
    {
        using var window = Create(WindowMode.Offscreen);

        window.Initialize();
        window.PollEvents();
        window.SetTitle("ignored");

        Assert.True(window.Offscreen);
        Assert.False(window.HasWindow);
        Assert.False(window.IsClosing);
        Assert.Equal(new Silk.NET.Maths.Vector2D<int>(800, 600), window.FramebufferSize);
        Assert.IsType<HeadlessSurfaceSource>(window.SurfaceSource);
        Assert.Equal(["VK_KHR_surface", "VK_EXT_headless_surface"], window.SurfaceSource.InstanceExtensions);
        Assert.Equal(1.0 / 120, window.FrameCapSeconds, 9);
        Assert.Throws<InvalidOperationException>(() => window.Window);
    }

    [Fact]
    public void With_a_window_the_frames_are_not_capped()
    {
        using var window = Create(WindowMode.Window);

        Assert.False(window.Offscreen);
        Assert.Equal(0, window.FrameCapSeconds);
    }

    [Fact]
    public void The_first_exit_request_sets_the_exit_code()
    {
        using var window = Create(WindowMode.Offscreen);

        window.RequestExit("first", 3);
        window.RequestExit("second", 1);

        Assert.True(window.ExitRequested);
        Assert.Equal(3, window.ExitCode);
        Assert.Equal("first", window.ExitReason);
    }

    private static WindowService Create(WindowMode mode)
    {
        var options = new EngineOptions
        {
            Window = new WindowSettings
            {
                Mode = mode,
                Width = 800,
                Height = 600,
                OffscreenFps = 120,
            },
        };
        return new WindowService(Options.Create(options), NullLogger<WindowService>.Instance);
    }
}
