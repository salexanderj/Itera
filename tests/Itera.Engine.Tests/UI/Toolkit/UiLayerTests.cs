using System.Numerics;
using System.Text;
using Itera.Engine.Audio;
using Itera.Engine.Input;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.Tests.UI.Toolkit;

/// <summary>
/// A toolkit layer (<see cref="UiLayer"/>) drawn through the UI surface: drawn again only when its UI changes, nothing
/// outside its bounds, the engine's plain style and a game's own drawing of an area, a declaration that throws logged
/// once and shown as nothing, a text box's typing given and taken back through the input, the clicks, fallback
/// typefaces, a picture of the page, and idle frames and redraws that allocate nothing.
/// </summary>
public sealed class UiLayerTests : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;

    private static readonly int Play = Ui.Id("play");
    private static readonly int Name = Ui.Id("name");
    private static readonly int Map = Ui.Id("map");

    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly AudioService _audio = MenuLayerTests.QueuingAudio();
    private readonly RecordingLogger _log = new();
    private readonly UiToolkit _toolkit;
    private readonly Input _input = new();
    private readonly Page _page;
    private readonly UISurface _surface;
    private ulong _frame;

    public UiLayerTests()
    {
        _toolkit = new UiToolkit(_fonts, MenuStyle.Plain, Options.Create(new EngineOptions()), _log, _audio);
        _page = new Page(_toolkit, _input);
        _surface = new UISurface([_page], NullLogger<UISurface>.Instance);
        _surface.Initialize(Width, Height);
        Frame();
    }

    public void Dispose()
    {
        _surface.Dispose();
        _page.Dispose();
        _toolkit.Dispose();
        _fonts.Dispose();
        _audio.Dispose();
    }

    [Fact]
    public void It_draws_again_only_when_its_ui_changes()
    {
        // The first frame, in the constructor, drew it; nothing has changed since.
        Assert.Equal(0, Frame().Rows);

        Point(PlayCentre());
        Assert.True(Frame().Rows > 0);
        Assert.Contains("hover", _page.Ui.Dump(), StringComparison.Ordinal);
        Assert.Equal(0, Frame().Rows);
    }

    [Fact]
    public void It_draws_nothing_outside_its_bounds()
    {
        Point(PlayCentre());
        Frame();
        var (outside, drawn) = LayerInk.Outside(_page, Width, Height);

        Assert.Equal(0, outside);
        Assert.True(drawn > 0);
    }

    [Fact]
    public void Its_page_draws_the_plain_style_and_the_games_own_area()
    {
        var drawn = _page.AreaDraws;
        using var picture = LayerPicture.Render(_page, Width, Height);
        var play = _page.Ui.Rect(Play);
        var map = _page.Ui.Rect(Map);

        Assert.Equal(SKColors.Red, picture.GetPixel((int)map.MidX, (int)map.MidY));
        // The button's face, away from its label.
        var face = picture.GetPixel((int)play.Left + 6, (int)play.MidY);
        Assert.NotEqual(LayerPicture.Background, face);
        Assert.Equal(drawn + 1, _page.AreaDraws);

        // ITERA_UI_PICTURES names a folder to keep the page's picture in, to look at.
        if (Environment.GetEnvironmentVariable("ITERA_UI_PICTURES") is { Length: > 0 } folder)
        {
            Point(PlayCentre());
            Frame();
            LayerPicture.Save(_page, Width, Height, Path.Combine(folder, "toolkit-page.png"));
        }

        var path = Path.Combine(Path.GetTempPath(), $"itera-ui-page-{Guid.NewGuid():N}.png");
        try
        {
            LayerPicture.Save(_page, Width, Height, path);
            using var saved = SKBitmap.Decode(path);
            Assert.Equal(Width, saved.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_declaration_that_throws_is_logged_once_and_shows_nothing_until_one_does_not()
    {
        _page.Throw = true;
        Frame();
        Frame();
        Assert.Single(_log.Errors);
        Assert.Equal(SKRectI.Empty, _page.Bounds(Width, Height));

        _page.Throw = false;
        Frame();
        Assert.False(_page.Bounds(Width, Height).IsEmpty);
    }

    [Fact]
    public void A_text_box_pressed_gets_the_typing_through_the_input_and_loses_it_when_the_input_takes_it_back()
    {
        var name = Centre(Name);
        Press(name);
        Assert.Same(_page.Entry, _input.TextFocus);

        // Its Escape took the typing back where it was given.
        _input.ClearTextFocus(_page.Entry);
        Frame();
        Frame();
        Assert.Null(_page.Ui.Typing);
        Assert.Null(_input.TextFocus);
    }

    [Fact]
    public void The_pointer_coming_onto_an_element_and_a_click_play_the_toolkits_clicks()
    {
        _audio.ClearQueued();
        Point(PlayCentre());
        Frame();
        Assert.Equal(1, _audio.QueuedEvents.Length);

        Press(PlayCentre());
        Assert.Equal(2, _audio.QueuedEvents.Length);
        Assert.Equal(1, _page.Clicks);
    }

    [Fact]
    public void Under_the_match_it_takes_no_pointer_while_the_games_input_is_blocked()
    {
        _input.Blocked = true;
        Point(PlayCentre());
        Frame();
        Assert.Equal(0, _page.Ui.Hovered);

        _input.Blocked = false;
        Frame();
        Assert.Equal(Play, _page.Ui.Hovered);
    }

    [Fact]
    public void A_new_version_of_an_area_or_an_invalidation_draws_it_again()
    {
        Assert.Equal(0, Frame().Rows);
        _page.PictureVersion++;
        Assert.True(Frame().Rows > 0);
        Assert.Equal(0, Frame().Rows);
        _page.Invalidate();
        Assert.True(Frame().Rows > 0);
        Assert.Equal(0, Frame().Rows);
    }

    [Fact]
    public void Hidden_it_gives_the_typing_back_and_shown_again_it_does_not_take_it()
    {
        Press(Centre(Name));
        Assert.Same(_page.Entry, _input.TextFocus);

        _page.Hidden = true;
        Frame();
        Assert.Null(_input.TextFocus);
        _page.Hidden = false;
        Frame();
        Frame();
        Assert.Null(_input.TextFocus);
        Assert.Null(_page.Ui.Typing);
    }

    [Fact]
    public void A_failure_after_a_recovery_is_logged_again_and_a_drawing_that_throws_once()
    {
        _page.Throw = true;
        Frame();
        _page.Throw = false;
        Frame();
        _page.Throw = true;
        Frame();
        Assert.Equal(2, _log.Errors.Count);

        _page.Throw = false;
        _page.ThrowInDraw = true;
        _page.Invalidate();
        Frame();
        _page.Invalidate();
        Frame();
        Assert.Equal(3, _log.Errors.Count);
    }

    [Fact]
    public void Characters_the_ui_font_lacks_come_from_a_system_typeface_where_one_has_them()
    {
        var faces = _toolkit.Typefaces;
        Assert.Same(faces.Primary, faces.For(new Rune('A')));
        Assert.True(faces.Width("漢字", 20, bold: false) > 0);
        Assert.True(faces.Width("😀", 20, bold: false) > 0);
    }

    [Fact]
    public void An_idle_frame_and_a_redraw_of_the_same_text_allocate_nothing()
    {
        using var bitmap = new SKBitmap(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        for (var i = 0; i < 4; i++)
        {
            Frame();
            _page.Draw(canvas, Width, Height);
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                _page.Update(new FrameTime(1 / 60f, i / 60.0, (ulong)i, 0), Width, Height);
                _page.Draw(canvas, Width, Height);
            }
        });

        Assert.Equal(0, bytes);
    }

    private SKPoint PlayCentre() => Centre(Play);

    private SKPoint Centre(int id)
    {
        var rect = _page.Ui.Rect(id);
        return new SKPoint(rect.MidX, rect.MidY);
    }

    private void Point(SKPoint at) => _input.Pointer = new UiPointer(new Vector2(at.X, at.Y), false, false, false, 0);

    /// <summary>A click at <paramref name="at"/>: down in one frame, up in the next.</summary>
    private void Press(SKPoint at)
    {
        _input.Pointer = new UiPointer(new Vector2(at.X, at.Y), true, true, false, 0);
        Frame();
        _input.Pointer = new UiPointer(new Vector2(at.X, at.Y), false, false, true, 0);
        Frame();
        Point(at);
    }

    private UiDraw Frame() => _surface.Draw(new FrameTime(1 / 60f, _frame / 60.0, _frame++, 0), Width, Height);

    /// <summary>A page with a panel: a title, Play, a switch, a slider, a text box, an area the game draws, wrapped text.</summary>
    private sealed class Page : UiLayer
    {
        private bool _on;
        private float _volume = 0.5f;

        public Page(UiToolkit toolkit, IFrameInput input)
            : base(toolkit, input) { }

        public bool Throw { get; set; }

        public bool ThrowInDraw { get; set; }

        public bool Hidden { get; set; }

        public int PictureVersion { get; set; }

        public override bool Visible => !Hidden;

        public int Clicks { get; private set; }

        public int AreaDraws { get; private set; }

        public TextEntry Entry { get; } = new(40);

        protected override void Build(Ui ui)
        {
            if (Throw)
            {
                throw new InvalidOperationException("A broken page.");
            }

            using (ui.Column(0, UiBox.Fill with { AlignX = UiAlign.Centre, AlignY = UiAlign.Centre }))
            using (
                ui.Column(
                    1,
                    new UiBox
                    {
                        Panel = true,
                        Padding = new UiEdges(30),
                        Gap = 15,
                    }
                )
            )
            {
                ui.Text("Settings", UiText.Title);
                if (ui.Button(Play, "Play").Clicked)
                {
                    Clicks++;
                }

                if (ui.Switch(Ui.Id("shadows"), _on).Clicked)
                {
                    _on = !_on;
                }

                if (ui.Slider(Ui.Id("volume"), _volume, 0, 1) is { Changed: true } volume)
                {
                    _volume = volume.Value;
                }

                ui.TextBox(Name, Entry, "your name");
                ui.Area(Map, UiBox.Fixed(120, 60), version: PictureVersion);
                ui.Text(
                    "Wrapped words, 漢字 and an emoji 😀, long enough to break.",
                    UiText.Wrapped,
                    UiBox.Sized(UiSize.Fixed(300), UiSize.Fit)
                );
            }
        }

        protected override void DrawArea(UiPainter painter, in UiElement element)
        {
            if (ThrowInDraw)
            {
                throw new InvalidOperationException("A broken picture.");
            }

            AreaDraws++;
            painter.Fill(element.Rect, SKColors.Red);
        }
    }

    /// <summary>The frame's input: a pointer the test sets, and the text focus it is given.</summary>
    private sealed class Input : IFrameInput
    {
        private readonly InputSnapshot _frame;

        public UiPointer Pointer { get; set; }

        public ref readonly InputSnapshot Frame => ref _frame;

        public Vector2 MouseSinceTick => Vector2.Zero;

        public UiPointer MenuPointer => Pointer;

        public bool Blocked { get; set; }

        public bool GameInputBlocked => Blocked;

        public ITextInput? TextFocus { get; private set; }

        public void SetTextFocus(ITextInput? text) => TextFocus = text;

        public void ClearTextFocus(ITextInput text)
        {
            if (ReferenceEquals(text, TextFocus))
            {
                TextFocus = null;
            }
        }
    }

    /// <summary>Keeps the errors logged.</summary>
    private sealed class RecordingLogger : ILogger<UiToolkit>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel >= LogLevel.Error)
            {
                Errors.Add(formatter(state, exception));
            }
        }
    }
}
