using System.Numerics;
using System.Text;
using Itera.Engine;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Input;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The sample's own menu (<see cref="SampleMenu"/>) drawn on a 1280 x 720 surface with a pointer the test moves, its
/// elements found by what they read: the bar's entries and the starts and quit they ask for, the address typed and
/// joined (a bad one refused with the reason), the engine's Settings page inside it with a key's binding waited for,
/// Escape over a match (Resume, Leave), a join's progress with Cancel, a match's start closing it, the keyboard, the
/// sample's composition using it in place of the engine's, a real match followed, and nothing allocated while idle.
/// </summary>
public sealed class SampleMenuTests : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;

    private readonly SampleMenuHarness _harness = new(Width, Height);

    private SampleMenu Menu => _harness.Menu;

    private MatchControl Matches => _harness.Matches;

    private SampleMenuHarness.PointerInput Input => _harness.Input;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void With_no_match_the_bar_shows_and_play_starts_an_offline_match_and_quit_closes_the_window()
    {
        Assert.True(Menu.IsOpen);
        Assert.Equal(["Play", "Join", "Settings", "Quit"], Buttons());

        Click("Play");
        var start = Matches.PendingStart;
        Assert.NotNull(start);
        Assert.Equal(SessionMode.Offline, start.Mode);

        Click("Quit");
        Assert.True(_harness.Window.ExitRequested);
    }

    [Fact]
    public void Join_takes_the_typing_refuses_a_bad_address_with_the_reason_and_joins_a_good_one()
    {
        Click("Join");
        Assert.Equal(SamplePage.Join, Menu.Page);
        var box = Assert.IsAssignableFrom<ITextInput>(Menu.TextFocus);

        Type(box, "nowhere");
        box.KeyDown(Key.Enter, false);
        Frame();
        Assert.Null(Matches.PendingStart);
        Assert.True(Menu.Ui.TryFind("'nowhere' is not host:port.", out _));

        // Ctrl+U clears the line.
        box.KeyDown(Key.U, true);
        Type(box, "127.0.0.1:47600");
        Click("Join", nth: 1);
        var join = Matches.PendingStart;
        Assert.NotNull(join);
        Assert.Equal(SessionMode.Join, join.Mode);
        Assert.Equal("127.0.0.1:47600", join.Address);

        // Escape goes back to the bar alone.
        Menu.Escape();
        Frame();
        Assert.Equal(SamplePage.Main, Menu.Page);
        Assert.Null(Menu.TextFocus);
    }

    [Fact]
    public void Settings_shows_the_engines_page_whose_key_wait_escape_stops_and_back_leaves()
    {
        Click("Settings");
        Assert.Equal(SamplePage.Settings, Menu.Page);
        Assert.True(Menu.Ui.TryFind("Volume", out _));

        Click("Keybindings");
        Click("F1");
        Assert.True(Menu.CapturingBinding);
        Menu.Escape();
        Frame();
        Assert.False(Menu.CapturingBinding);
        Assert.Equal(SamplePage.Settings, Menu.Page);

        Click("Back");
        Assert.Equal(SamplePage.Main, Menu.Page);
    }

    [Fact]
    public void Over_a_match_escape_opens_it_resume_closes_it_and_leave_ends_the_match()
    {
        MatchControlStates.Starting(Matches);
        MatchControlStates.Playing(Matches);
        Frame();
        Assert.False(Menu.IsOpen);

        Menu.Escape();
        Frame();
        Assert.True(Menu.IsOpen);
        Assert.Equal(["Resume", "Settings", "Leave", "Quit"], Buttons());
        // The picture shaded under the bar: the shade is drawn first.
        Assert.Equal(Ui.Id("sample.shade"), Menu.Ui.Drawn(0).Id);

        Click("Resume");
        Assert.False(Menu.IsOpen);

        // Resume beside the Settings page leaves it: the menu opens again on the bar alone.
        Menu.Escape();
        Frame();
        Click("Settings");
        Assert.Equal(SamplePage.Settings, Menu.Page);
        Click("Resume");
        Assert.False(Menu.IsOpen);
        Menu.Escape();
        Frame();
        Assert.Equal(SamplePage.Main, Menu.Page);

        Click("Leave");
        Assert.Equal("You left the match.", Matches.PendingLeave);

        // The match ended: the bar again, with the reason at its foot.
        MatchControlStates.Ended(Matches, "You left the match.");
        Frame();
        Assert.Equal(["Play", "Join", "Settings", "Quit"], Buttons());
        Assert.True(Menu.Ui.TryFind("You left the match.", out _));
    }

    [Fact]
    public void While_a_join_connects_it_shows_how_far_and_cancel_leaves()
    {
        MatchControlStates.Joining(Matches, "127.0.0.1:47600");
        Frame();

        Assert.True(Menu.IsOpen);
        Assert.True(Menu.Ui.TryFind("Connecting…", out _));
        Assert.True(Menu.Ui.TryFind("127.0.0.1:47600", out _));
        MatchControlStates.JoinProgress(Matches, new JoinProgress(JoinStage.Downloading, 1 << 20, 3 << 20, null));
        Frame();
        Assert.True(Menu.Ui.TryFind("Downloading the host's world…", out _));

        Click("Cancel");
        Assert.Equal("The join was cancelled.", Matches.PendingLeave);
    }

    [Fact]
    public void Escape_while_a_join_connects_cancels_it()
    {
        MatchControlStates.Joining(Matches, "127.0.0.1:47600");
        Frame();

        Menu.Escape();

        Assert.Equal("The join was cancelled.", Matches.PendingLeave);
    }

    [Fact]
    public void A_match_that_started_shows_the_bar_alone_again()
    {
        Click("Settings");
        Assert.Equal(SamplePage.Settings, Menu.Page);

        // A join that started and failed between two frames: a match counted, back at the menu with the reason.
        MatchControlStates.Joining(Matches, "127.0.0.1:47600");
        MatchControlStates.Ended(Matches, "The host refused the join.");
        Frame();

        Assert.Equal(SamplePage.Main, Menu.Page);
    }

    [Fact]
    public void The_keyboard_moves_the_focus_along_the_bar_and_presses_an_entry()
    {
        Input.Keys = UiKeys.Next;
        Frame();
        Input.Keys = UiKeys.Next;
        Frame();
        Input.Keys = UiKeys.Press;
        Frame();
        Input.Keys = UiKeys.None;
        Frame();

        Assert.Equal(SamplePage.Join, Menu.Page);
        Assert.NotNull(Menu.TextFocus);
    }

    [Fact]
    public void Idle_it_draws_nothing_again_and_allocates_nothing()
    {
        Point(new Vector2(Width - 10, Height - 10));
        Frame();
        Assert.Equal(0, Frame().Rows);

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            Menu.Update(new FrameTime(1 / 60f, i / 60.0, (ulong)i, 0), Width, Height);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Fact]
    public void The_sample_runs_its_menu_in_place_of_the_engines()
    {
        using var provider = SampleComposition.Build();

        var menu = provider.GetRequiredService<IMenu>();
        var layers = provider.GetServices<IUiLayer>().ToList();

        Assert.IsType<SampleMenu>(menu);
        Assert.Single(layers, layer => ReferenceEquals(layer, menu));
        Assert.DoesNotContain(layers, layer => layer is MenuLayer);
    }

    [Fact]
    public void It_follows_a_real_match_from_its_start_to_its_end()
    {
        using var provider = SampleComposition.Build(
            new Dictionary<string, string?> { ["Engine:Physics:Threads"] = "1" }
        );
        var menu = (SampleMenu)provider.GetRequiredService<IMenu>();
        var time = new FrameTime(1 / 60f, 0, 0, 0);
        menu.Update(in time, Width, Height);
        Assert.True(menu.IsOpen);

        var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        menu.Update(in time, Width, Height);
        Assert.False(menu.IsOpen);
        menu.Escape();
        menu.Update(in time, Width, Height);
        Assert.True(menu.Ui.TryFind("Resume", out _));

        match.Dispose();
        menu.Update(in time, Width, Height);
        Assert.True(menu.IsOpen);
        Assert.True(menu.Ui.TryFind("Play", out _));
    }

    private List<string> Buttons() => _harness.Buttons();

    private void Click(string label, int nth = 0) => _harness.Click(label, nth);

    private void Point(Vector2 at) => _harness.Point(at);

    private UiDraw Frame() => _harness.Frame();

    private static void Type(ITextInput box, string text)
    {
        foreach (var character in text)
        {
            box.Character(new Rune(character));
        }
    }
}
