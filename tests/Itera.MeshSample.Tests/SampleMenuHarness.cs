using System.Numerics;
using Itera.Engine;
using Itera.Engine.Commands;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The sample's menu drawn alone on a surface of a given size, with a pointer and the menu's keys the test sets, its
/// elements found by what they read; its pictures written to the folder <c>ITERA_UI_PICTURES</c> names.
/// </summary>
internal sealed class SampleMenuHarness : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly SteamService _steam = new(Options.Create(new EngineOptions()), NullLogger<SteamService>.Instance);
    private readonly UiToolkit _toolkit;
    private readonly UISurface _surface;
    private readonly string? _folder = Environment.GetEnvironmentVariable("ITERA_UI_PICTURES");
    private ulong _frame;

    public SampleMenuHarness(int width, int height)
    {
        _width = width;
        _height = height;
        var options = new EngineOptions();
        options.Application.Name = "Itera Mesh Sample";
        _steam.Initialize();
        _toolkit = new UiToolkit(_fonts, MenuStyle.Plain, Options.Create(options), NullLogger<UiToolkit>.Instance);
        var starts = new MatchCommands(
            new ConsoleCommands(Matches, NullLogger<ConsoleCommands>.Instance),
            Matches,
            _steam,
            Options.Create(options)
        );
        Menu = new SampleMenu(
            _toolkit,
            Matches,
            starts,
            Input,
            Window,
            EngineState.SettingsWithoutFile(),
            new SettingsRows(),
            Options.Create(options)
        );
        _surface = new UISurface([Menu], NullLogger<UISurface>.Instance);
        _surface.Initialize(width, height);
        Frame();
    }

    public SampleMenu Menu { get; }

    public MatchControl Matches { get; } = new();

    public WindowService Window { get; } = new(Options.Create(new EngineOptions()), NullLogger<WindowService>.Instance);

    public PointerInput Input { get; } = new();

    /// <summary>The buttons' labels, in the order declared.</summary>
    public List<string> Buttons()
    {
        var labels = new List<string>();
        for (var i = 0; i < Menu.Ui.Count; i++)
        {
            if (Menu.Ui.ElementAt(i) is { Kind: UiKind.Button, Text: { } label })
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    /// <summary>A click on the <paramref name="nth"/> element reading <paramref name="label"/> that is not plain text.</summary>
    public void Click(string label, int nth = 0)
    {
        for (var i = 0; i < Menu.Ui.Count; i++)
        {
            var element = Menu.Ui.ElementAt(i);
            if (element.Text == label && element.Kind != UiKind.Text && nth-- == 0)
            {
                ClickAt(new Vector2(element.Rect.MidX, element.Rect.MidY));
                return;
            }
        }

        Assert.Fail($"Nothing pressable reads '{label}':\n{Menu.Ui.Dump()}");
    }

    /// <summary>Over, down, up, and over again, a frame each.</summary>
    public void ClickAt(Vector2 at)
    {
        Point(at);
        Frame();
        Input.Pointer = new UiPointer(at, true, true, false, 0);
        Frame();
        Input.Pointer = new UiPointer(at, false, false, true, 0);
        Frame();
        Point(at);
        Frame();
    }

    public void Point(Vector2 at) => Input.Pointer = new UiPointer(at, false, false, false, 0);

    public UiDraw Frame() => _surface.Draw(new FrameTime(1 / 60f, _frame / 60.0, _frame++, 0), _width, _height);

    /// <summary>The menu drawn alone over a plain background, which must show something; kept when a folder is named.</summary>
    public void Picture(string name)
    {
        // The pointer away from every element, so none shows hovered.
        Point(new Vector2(1, 1));
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
            LayerPicture.Save(
                Menu,
                _width,
                _height,
                Path.Combine(_folder, $"sample-menu-{name}-{_width}x{_height}.png")
            );
        }
    }

    public void Dispose()
    {
        _surface.Dispose();
        Menu.Dispose();
        _toolkit.Dispose();
        _fonts.Dispose();
        Window.Dispose();
        _steam.Dispose();
    }

    /// <summary>The frame input a menu reads: the pointer and the menu's keys.</summary>
    public sealed class PointerInput : IFrameInput
    {
        private readonly InputSnapshot _frame;

        public UiPointer Pointer { get; set; }

        public UiKeys Keys { get; set; }

        public ref readonly InputSnapshot Frame => ref _frame;

        public Vector2 MouseSinceTick => Vector2.Zero;

        public UiPointer MenuPointer => Pointer;

        public UiKeys MenuKeys => Keys;
    }
}
