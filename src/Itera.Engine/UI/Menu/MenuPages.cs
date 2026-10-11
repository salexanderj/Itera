using Itera.Engine.UI.Toolkit;

namespace Itera.Engine.UI.Menu;

/// <summary>Where a game's page shows in the engine's menu.</summary>
public enum MenuPlace
{
    /// <summary>On the main menu, while no match is played.</summary>
    Main,

    /// <summary>On the menu over a match.</summary>
    InGame,

    /// <summary>On both.</summary>
    Both,
}

/// <summary>
/// A game's page in the engine's menu (<see cref="MenuPages"/>): its content, declared with the toolkit inside the
/// menu's panel under its <see cref="Title"/>, with the menu's Back button after it. Main thread.
/// </summary>
public interface IMenuPage
{
    /// <summary>The page's title.</summary>
    string Title { get; }

    /// <summary>Declares the page's content (the menu's panel is open; the menu adds the title and Back).</summary>
    void Build(Ui ui);

    /// <summary>Escape went down on the page: true when the page took it (closing something of its own); false goes back.</summary>
    bool Escape() => false;
}

/// <summary>A game's page with the button that opens it.</summary>
/// <param name="Label">The button's label.</param>
/// <param name="Hint">What the button does, shown while the pointer is on it.</param>
/// <param name="Page">The page.</param>
/// <param name="Place">Where the button shows.</param>
public sealed record MenuPage(string Label, string Hint, IMenuPage Page, MenuPlace Place);

/// <summary>
/// The pages a game adds to the engine's menu, each with a button above Settings (<see cref="MenuLayer"/>), in the order
/// added. A game adds them once a run, in its <c>IRunStartup</c>. Run-wide; main thread.
/// </summary>
public sealed class MenuPages
{
    private readonly List<MenuPage> _pages = [];

    /// <summary>The pages added, in order.</summary>
    public IReadOnlyList<MenuPage> Pages => _pages;

    /// <summary>Adds <paramref name="page"/>, opened by a button labelled <paramref name="label"/> at <paramref name="place"/>.</summary>
    public void Add(string label, string hint, IMenuPage page, MenuPlace place = MenuPlace.Main)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(page);
        _pages.Add(new MenuPage(label, hint, page, place));
    }
}
