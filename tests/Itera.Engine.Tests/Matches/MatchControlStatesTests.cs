using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// The hooks a game's tests use for its menu or HUD: <see cref="MatchControlStates"/> moves a test's
/// <see cref="MatchControl"/> as the loop does (a join counted as it begins and not again as its world starts, a host's
/// or an offline match counted as it starts, an end leaving the menu with the reason), the same states a real offline
/// match gives (<see cref="HeadlessRun"/>); <see cref="MatchControl.PendingStart"/> and
/// <see cref="MatchControl.PendingLeave"/> show a request until the loop takes it; and
/// <see cref="PlayerSettings.WithoutFile"/> reads and writes no file.
/// </summary>
public sealed class MatchControlStatesTests
{
    [Fact]
    public void A_join_is_counted_as_it_begins_shows_how_far_it_is_and_its_end_leaves_the_menu_with_the_reason()
    {
        var matches = new MatchControl();
        MatchControlStates.Ended(matches, "The host quit.");

        MatchControlStates.Joining(matches, "127.0.0.1:47600");
        Assert.Equal((MatchPhase.Joining, 1, null, "127.0.0.1:47600"), State(matches));

        var progress = new JoinProgress(JoinStage.Downloading, 1 << 20, 3 << 20, null);
        MatchControlStates.JoinProgress(matches, progress);
        Assert.Equal(progress, matches.JoinProgress);

        MatchControlStates.Starting(matches);
        MatchControlStates.Loading(matches);
        MatchControlStates.Playing(matches);
        Assert.Equal((MatchPhase.Playing, 1, null, "127.0.0.1:47600"), State(matches));

        MatchControlStates.Ended(matches, "You left the match.");
        Assert.Equal((MatchPhase.Menu, 1, "You left the match.", null), State(matches));
        Assert.Equal(default, matches.JoinProgress);
    }

    [Fact]
    public void A_match_started_and_ended_gives_the_states_a_real_offline_match_gives()
    {
        // Without terrain: no world to stream, so it loads at once.
        using var provider = EngineComposition.Build(terrain: false);
        var real = provider.GetRequiredService<MatchControl>();
        var run = new HeadlessRun(provider);
        var made = new MatchControl();

        var match = run.Play(seed: 3);
        MatchControlStates.Starting(made);
        MatchControlStates.Loading(made);
        Assert.Equal(State(real), State(made));

        Assert.True(match.WaitUntilLoaded());
        MatchControlStates.Playing(made);
        Assert.Equal(State(real), State(made));

        match.Dispose();
        MatchControlStates.Ended(made, real.LastEnd);
        Assert.Equal(State(real), State(made));
        Assert.Null(real.Current);
    }

    [Fact]
    public void A_request_shows_until_the_loop_takes_it()
    {
        var matches = new MatchControl();
        var start = new MatchStart(SessionMode.Offline, SessionTransportKind.Direct, null, 4, null, false);

        matches.RequestStart(start);
        matches.RequestLeave("You left the match.");

        Assert.Same(start, matches.PendingStart);
        Assert.Equal("You left the match.", matches.PendingLeave);
        Assert.Same(start, matches.TakeStart());
        Assert.Equal("You left the match.", matches.TakeLeave());
        Assert.Null(matches.PendingStart);
        Assert.Null(matches.PendingLeave);
    }

    [Fact]
    public void Settings_without_a_file_take_the_options_apply_the_ui_scale_and_save_nothing()
    {
        var options = new EngineOptions();
        options.Ui.Scale = 1.25f;
        using var fonts = new UiFonts(Options.Create(options), NullLogger<UiFonts>.Instance);
        using var toolkit = new UiToolkit(
            fonts,
            MenuStyle.Plain,
            Options.Create(options),
            NullLogger<UiToolkit>.Instance
        );

        var settings = PlayerSettings.WithoutFile(options, toolkit: toolkit);
        Assert.Equal(1.25f, settings.UiScale);

        settings.SetUiScale(1.5f);
        settings.SetVolume(0.3f);
        settings.Save();

        Assert.Equal(1.5f, toolkit.Scale);
        Assert.Equal(0.3f, settings.Volume, 3);
    }

    private static (MatchPhase Phase, int Started, string? LastEnd, string? JoinAddress) State(MatchControl matches) =>
        (matches.Phase, matches.Started, matches.LastEnd, matches.JoinAddress);
}
