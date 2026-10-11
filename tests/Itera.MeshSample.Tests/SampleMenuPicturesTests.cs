using Itera.Engine.Matches;

namespace Itera.MeshSample.Tests;

/// <summary>
/// Pictures of the sample's menu, page by page, at 1280x720 and 2560x1440, drawn alone over a plain background: written
/// to the folder <c>ITERA_UI_PICTURES</c> names (for a look at the menu without running the sample), and otherwise only
/// drawn. Each page draws something.
/// </summary>
public sealed class SampleMenuPicturesTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    public void Every_page_of_the_sample_menu_draws(int width, int height)
    {
        using var menu = new SampleMenuHarness(width, height);
        menu.Picture("main");
        menu.Click("Join");
        menu.Picture("join");
        menu.Menu.Escape();
        menu.Click("Settings");
        menu.Picture("settings-game");
        menu.Click("Keybindings");
        menu.Picture("settings-keys");
        menu.Menu.Escape();
        menu.Frame();
        MatchControlStates.Joining(menu.Matches, "127.0.0.1:47600");
        menu.Picture("joining");
        MatchControlStates.Starting(menu.Matches);
        MatchControlStates.Playing(menu.Matches);
        menu.Frame();
        menu.Menu.Escape();
        menu.Picture("in-game");
        MatchControlStates.Ended(menu.Matches, "The host left: the host quit.");
        menu.Picture("main-after-match");
    }
}
