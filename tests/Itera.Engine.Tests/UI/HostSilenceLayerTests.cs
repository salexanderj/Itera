using Itera.Engine.Replication;
using Itera.Engine.UI;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="HostSilenceLayer"/>: hidden while snapshots arrive, then the whole seconds without one, up to its most; hidden
/// while the loading screen is drawn; and drawn inside its bounds.
/// </summary>
public sealed class HostSilenceLayerTests : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;

    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private double _seconds;
    private bool _loading;

    public void Dispose() => _fonts.Dispose();

    [Fact]
    public void It_shows_nothing_while_snapshots_arrive()
    {
        using var layer = Layer();
        _seconds = ReplicationService.SilenceSeconds - 0.1;
        Update(layer);

        Assert.False(layer.Visible);
    }

    [Fact]
    public void It_counts_the_whole_seconds_without_a_snapshot_up_to_its_most()
    {
        using var layer = Layer();
        _seconds = 5.7;
        Update(layer);
        Assert.Equal("No data from the host for 5 s", layer.Text);

        _seconds = HostSilenceLayer.MostSeconds;
        Update(layer);
        Assert.Equal($"No data from the host for {HostSilenceLayer.MostSeconds} s", layer.Text);

        _seconds = 1_000;
        Update(layer);
        Assert.Equal($"No data from the host for over {HostSilenceLayer.MostSeconds} s", layer.Text);
    }

    [Fact]
    public void It_hides_while_the_loading_screen_is_drawn()
    {
        using var layer = Layer();
        _seconds = 10;
        _loading = true;
        Update(layer);

        Assert.False(layer.Visible);
    }

    [Fact]
    public void It_draws_inside_its_bounds()
    {
        using var layer = Layer();
        _seconds = 12;
        Update(layer);

        var (outside, drawn) = LayerInk.Outside(layer, Width, Height);
        Assert.Equal(0, outside);
        Assert.True(drawn > 0);
    }

    private HostSilenceLayer Layer() => new(_fonts, () => _seconds, () => _loading);

    private static void Update(HostSilenceLayer layer) => layer.Update(new FrameTime(1 / 60f, 0, 0, 0));
}
