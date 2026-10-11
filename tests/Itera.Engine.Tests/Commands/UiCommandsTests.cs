using Itera.Engine.Commands;
using Itera.Engine.Input;
using Itera.Engine.Networking;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Commands;

/// <summary>
/// The console's <c>ui</c>: every toolkit layer shown, an element a line; <c>ui press LABEL</c> clicks the element that
/// reads LABEL, through the input as a click there would; a label no element reads is said so.
/// </summary>
public sealed class UiCommandsTests : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;

    private readonly ConsoleCommands _commands = new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
    private readonly RecordingOutput _output = new();
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly UiToolkit _toolkit;
    private readonly InputService _input;
    private readonly Page _page;
    private readonly UISurface _surface;
    private ulong _frame;

    public UiCommandsTests()
    {
        _toolkit = new UiToolkit(
            _fonts,
            MenuStyle.Plain,
            Options.Create(new EngineOptions()),
            NullLogger<UiToolkit>.Instance
        );
        _input = new InputService(_window, NullLogger<InputService>.Instance);
        // A panel lets the pointer's clicks reach the layer, as a game's clickable HUD does.
        _input.SetPanel(new Panel());
        _page = new Page(_toolkit, _input);
        _surface = new UISurface([_page], NullLogger<UISurface>.Instance);
        _surface.Initialize(Width, Height);
        new UiCommands(_commands, _surface, _input).Register();
        Frame();
    }

    public void Dispose()
    {
        _surface.Dispose();
        _page.Dispose();
        _toolkit.Dispose();
        _input.Dispose();
        _fonts.Dispose();
        _window.Dispose();
    }

    [Fact]
    public void Ui_lists_each_shown_toolkit_layers_elements()
    {
        _commands.Execute("ui", _output);

        Assert.Equal("Page:", _output.Lines[0]);
        Assert.Contains(
            _output.Lines,
            line =>
                line.Contains("Button", StringComparison.Ordinal)
                && line.Contains("\"Play Offline\"", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void Ui_press_clicks_the_element_reading_the_label()
    {
        _commands.Execute("ui press Play Offline", _output);
        Frame();

        Assert.Empty(_output.Failures);
        Assert.Equal(1, _page.Clicks);
    }

    [Fact]
    public void A_label_no_element_reads_is_said_so()
    {
        _commands.Execute("ui press Nothing Here", _output);

        Assert.Single(_output.Failures);
        Assert.Equal(0, _page.Clicks);
    }

    private void Frame()
    {
        _input.BeginFrame();
        _surface.Draw(new FrameTime(1 / 60f, _frame / 60.0, _frame++, 0), Width, Height);
    }

    private sealed class Page(UiToolkit toolkit, IFrameInput input) : UiLayer(toolkit, input)
    {
        public int Clicks { get; private set; }

        protected override void Build(Ui ui)
        {
            using (ui.Column(1, new UiBox { Padding = new UiEdges(20) }))
            {
                if (ui.Button(Ui.Id("play"), "Play Offline").Clicked)
                {
                    Clicks++;
                }
            }
        }
    }

    private sealed class Panel : IPanelInput
    {
        public void Escape() { }
    }

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Failures { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Failures.Add(text);
    }
}
