using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// Pictures of the engine's menu, page by page, at 1280x720 and 2560x1440, drawn alone over a plain background: written
/// to the folder <c>ITERA_UI_PICTURES</c> names (for a look at the menu without running the game), and otherwise only
/// drawn. Each page draws something.
/// </summary>
public sealed class MenuPicturesTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    public void Every_page_of_the_menu_draws(int width, int height)
    {
        using var menu = new MenuHarness(width, height);
        menu.Picture("main");
        menu.Click("Join Game");
        menu.Picture("join");
        menu.Menu.Escape();
        menu.Frame();
        menu.Click("Settings");
        menu.Picture("settings-game");
        menu.Click("Graphics");
        menu.Picture("settings-graphics");
        menu.Click("Keybindings");
        menu.Picture("settings-keys");
        menu.Menu.Escape();
        menu.Frame();
        menu.Matches.Phase = MatchPhase.Playing;
        menu.Matches.Started++;
        menu.Frame();
        menu.Menu.Escape();
        menu.Frame();
        menu.Picture("in-game");
        menu.Request();
        menu.Frame();
        menu.Picture("confirm");
    }

    /// <summary>The engine's menu drawn alone, with a pointer the test moves and clicks.</summary>
    private sealed class MenuHarness : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly WindowService _window = new(
            Options.Create(new EngineOptions()),
            NullLogger<WindowService>.Instance
        );
        private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
        private readonly AudioService _audio = MenuLayerTests.QueuingAudio();
        private readonly PointerInput _pointer = new();
        private readonly UiToolkit _toolkit;
        private readonly SteamFriendList _friends;
        private readonly UISurface _surface;
        private readonly string? _folder = Environment.GetEnvironmentVariable("ITERA_UI_PICTURES");
        private ulong _frame;

        public MenuHarness(int width, int height)
        {
            _width = width;
            _height = height;
            var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
            var steam = new SteamService(
                new SteamSettings { Enabled = false },
                NullLogger<SteamService>.Instance,
                () => (null, "off")
            );
            steam.Initialize();
            var options = new EngineOptions();
            options.Application.Name = "Itera Game";
            var starts = new MatchCommands(commands, Matches, steam, Options.Create(options));
            _friends = new SteamFriendList(steam, "test", NullLogger<SteamFriendList>.Instance);
            _toolkit = new UiToolkit(
                _fonts,
                MenuStyle.Plain,
                Options.Create(options),
                NullLogger<UiToolkit>.Instance,
                _audio
            );
            Menu = new MenuLayer(
                _toolkit,
                MenuStyle.Plain,
                Matches,
                starts,
                _pointer,
                _window,
                steam,
                _friends,
                new JoinRequests(_friends, Matches, starts),
                new PlayerSettings(
                    new EngineOptions(),
                    CommandLineKeys.None,
                    null,
                    NullLogger<PlayerSettings>.Instance
                ),
                Options.Create(options)
            );
            _surface = new UISurface([Menu], NullLogger<UISurface>.Instance);
            _surface.Initialize(width, height);
            Frame();
        }

        public MatchControl Matches { get; } = new();

        public MenuLayer Menu { get; }

        /// <summary>A click on the element reading <paramref name="label"/>.</summary>
        public void Click(string label)
        {
            Assert.True(Menu.Ui.TryFind(label, out var element), $"Nothing reads '{label}':\n{Menu.Ui.Dump()}");
            var at = new Vector2(element.Rect.MidX, element.Rect.MidY);
            _pointer.Pointer = new UiPointer(at, true, true, false, 0);
            Frame();
            _pointer.Pointer = new UiPointer(at, false, false, true, 0);
            Frame();
            // The pointer away, so no button shows hovered.
            _pointer.Pointer = new UiPointer(new Vector2(1, 1), false, false, false, 0);
            Frame();
        }

        /// <summary>A friend's "Join game" from Steam, as Steam reports it to the list.</summary>
        public void Request() => ((ISteamFriendsListener)_friends).OnJoinRequested(5, "lobby:109775240000000005");

        public void Frame() => _surface.Draw(new FrameTime(1 / 60f, _frame / 60.0, _frame++, 0), _width, _height);

        public void Picture(string name)
        {
            Frame();
            using var picture = LayerPicture.Render(Menu, _width, _height);
            var drawn = 0;
            for (var y = 0; y < _height; y += 3)
            {
                for (var x = 0; x < _width; x += 3)
                {
                    drawn += picture.GetPixel(x, y) != LayerPicture.Background ? 1 : 0;
                }
            }

            Assert.True(drawn > 100, $"The {name} page drew {drawn} sampled pixels.");
            if (_folder is { Length: > 0 })
            {
                LayerPicture.Save(Menu, _width, _height, Path.Combine(_folder, $"menu-{name}-{_width}x{_height}.png"));
            }
        }

        public void Dispose()
        {
            _surface.Dispose();
            Menu.Dispose();
            _toolkit.Dispose();
            _fonts.Dispose();
            _window.Dispose();
            _audio.Dispose();
        }
    }

    private sealed class PointerInput : IFrameInput
    {
        private readonly InputSnapshot _frame;

        public UiPointer Pointer { get; set; }

        public ref readonly InputSnapshot Frame => ref _frame;

        public Vector2 MouseSinceTick => Vector2.Zero;

        public UiPointer MenuPointer => Pointer;
    }
}
