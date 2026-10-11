using Itera.Engine;
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
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.MeshSample;

/// <summary>
/// The sample's own menu in place of the engine's (an <see cref="IMenu"/> registered after <c>AddIteraEngine</c>), a
/// toolkit layer in a look of its own (<see cref="SampleMenuStyle"/>): a bar down the left with the title and its
/// entries (Play, Join, Settings, Quit; over a match Resume, Settings, Leave, Quit), and beside it the page an entry
/// opens: the host's address to join (Enter joins), or the engine's Settings page (<see cref="SettingsPage"/>). With no
/// match it always shows; over a match Escape opens and closes it; while a join connects it shows how far, with Cancel.
/// It reaches the run only through the engine's public services: <see cref="MatchControl"/> and
/// <see cref="MatchCommands"/> (start, leave, the phase, why the last match ended), <see cref="WindowService"/> (quit),
/// <see cref="PlayerSettings"/>, and <see cref="MusicPlayer"/> (its music, <see cref="SampleSounds.MenuMusic"/>, while
/// there is no match or one is being joined, faded out as a match starts unless the game played another). Steam is off
/// in the sample, so it lists no friends and asks about no friend's join (a game with Steam answers
/// <see cref="JoinRequests"/>: a friend's join asked for during a match waits for it, or for the match's end). Main
/// thread.
/// </summary>
public sealed class SampleMenu : UiLayer, IMenu
{
    /// <summary>The longest address that can be typed.</summary>
    public const int MaxAddressLength = 120;

    private const float BarWidth = 560;
    private const float EntryHeight = 64;

    /// <summary>Over a match: lighter than the panels, so the yard shows behind the bar.</summary>
    private static readonly SKColor s_matchShade = new(12, 18, 27, 170);

    private static readonly int s_shade = Ui.Id("sample.shade");
    private static readonly int s_play = Ui.Id("sample.play");
    private static readonly int s_join = Ui.Id("sample.join");
    private static readonly int s_settings = Ui.Id("sample.settings");
    private static readonly int s_quit = Ui.Id("sample.quit");
    private static readonly int s_resume = Ui.Id("sample.resume");
    private static readonly int s_leave = Ui.Id("sample.leave");
    private static readonly int s_cancel = Ui.Id("sample.cancel");
    private static readonly int s_address = Ui.Id("sample.address");
    private static readonly int s_joinAddress = Ui.Id("sample.address.join");

    private readonly MatchControl _matches;
    private readonly MatchCommands _starts;
    private readonly WindowService _window;
    private readonly PlayerSettings _settings;
    private readonly MusicPlayer? _music;
    private readonly SettingsPage _settingsPage;
    private readonly TextEntry _address;
    private readonly string _title;
    private SamplePage _page = SamplePage.Main;
    private bool _open;
    private string? _notice;
    private bool _seenMatch;
    private int _seenStarted;
    private bool _playingMusic;

    public SampleMenu(
        UiToolkit toolkit,
        MatchControl matches,
        MatchCommands starts,
        IFrameInput input,
        WindowService window,
        PlayerSettings settings,
        SettingsRows rows,
        IOptions<EngineOptions> options,
        MusicPlayer? music = null
    )
        : base(toolkit, input)
    {
        ArgumentNullException.ThrowIfNull(options);
        _matches = matches;
        _starts = starts;
        _window = window;
        _settings = settings;
        _music = music;
        _settingsPage = new SettingsPage(settings, rows);
        _address = new TextEntry(MaxAddressLength, PressJoin);
        _title = options.Value.Application.Name;
        Ui.Style = new SampleMenuStyle();
    }

    /// <summary>The page beside the bar.</summary>
    public SamplePage Page => _page;

    /// <summary>Shown with no match, while a join connects or a match starts, and over a match once Escape opened it.</summary>
    public bool IsOpen => _matches.Phase is MatchPhase.Menu or MatchPhase.Joining or MatchPhase.Starting || _open;

    public override bool Visible => IsOpen;

    /// <summary>Over the match's layers (an <see cref="IMenu"/> says so too, but a class's own member wins).</summary>
    public override bool AboveMatch => true;

    /// <summary>The address box while it has the typing: a menu's typing goes through <see cref="IMenuInput"/>.</summary>
    public ITextInput? TextFocus => IsOpen && _page == SamplePage.Join ? Ui.Typing : null;

    public bool CapturingBinding => IsOpen && _page == SamplePage.Settings && _settingsPage.CapturingBinding;

    public void BindingPressed(InputBinding binding) => _settingsPage.BindingPressed(binding);

    /// <summary>Stops a key's binding, cancels a join, goes back to the bar, or opens and closes the menu over a match.</summary>
    public void Escape()
    {
        if (CapturingBinding)
        {
            _settingsPage.Escape();
        }
        else if (_matches.Phase == MatchPhase.Joining)
        {
            _matches.RequestLeave("The join was cancelled.");
        }
        else if (_page != SamplePage.Main && IsOpen)
        {
            Show(SamplePage.Main);
        }
        else if (_matches.Phase is MatchPhase.Loading or MatchPhase.Playing)
        {
            _open = !_open;
        }
    }

    /// <summary>
    /// The menu's music follows the matches; a match that started (also one that failed within a frame) or ended: the bar
    /// alone, the menu closed.
    /// </summary>
    protected override void BeforeUpdate(in FrameTime time)
    {
        FollowMusic();
        var hasMatch = _matches.Phase != MatchPhase.Menu;
        if (hasMatch == _seenMatch && _matches.Started == _seenStarted)
        {
            return;
        }

        _seenMatch = hasMatch;
        _seenStarted = _matches.Started;
        Show(SamplePage.Main);
        _open = false;
    }

    /// <summary>
    /// The menu's music plays while there is no match or one is being joined, fading in over 2 s, and fades out as a match
    /// starts, unless the game has played another track meanwhile.
    /// </summary>
    private void FollowMusic()
    {
        var wanted = _matches.Phase is MatchPhase.Menu or MatchPhase.Joining;
        if (_music is null || wanted == _playingMusic || !_music.TryFind(SampleSounds.MenuMusic, out var track))
        {
            return;
        }

        _playingMusic = wanted;
        if (wanted)
        {
            _music.Play(track);
        }
        else if (_music.Current == track)
        {
            _music.Stop();
        }
    }

    /// <summary>The address box's typing goes through <see cref="TextFocus"/>, not the game's text focus.</summary>
    protected override void GiveTyping(TextEntry? entry, TextEntry? before) { }

    protected override bool StillTyping(TextEntry entry) => true;

    protected override void Build(Ui ui)
    {
        var overMatch = _matches.Phase is MatchPhase.Loading or MatchPhase.Playing;
        if (overMatch)
        {
            // The match shaded: first in the flow, so it draws under the bar and the page.
            ui.Area(s_shade, UiBox.Fill, UiMarks.Passive);
        }

        using (ui.Row(0, UiBox.Fill))
        {
            Bar(ui, overMatch);
            using (ui.Column(0, UiBox.Fill with { AlignX = UiAlign.Centre, Padding = new UiEdges(48, 96, 48, 48) }))
            {
                if (_matches.Phase == MatchPhase.Joining)
                {
                    Joining(ui);
                }
                else if (_matches.Phase == MatchPhase.Starting)
                {
                    ui.Text("Starting…", UiText.Heading);
                }
                else if (_page == SamplePage.Join)
                {
                    JoinPage(ui);
                }
                else if (_page == SamplePage.Settings)
                {
                    SettingsPanel(ui);
                }
            }
        }
    }

    protected override void DrawArea(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        if (element.Id == s_shade)
        {
            painter.Fill(element.Rect, s_matchShade);
        }
    }

    /// <summary>The bar: the title, the entries, and at its foot why the last match ended.</summary>
    private void Bar(Ui ui, bool overMatch)
    {
        var bar = new UiBox
        {
            Width = UiSize.Fixed(BarWidth),
            Height = UiSize.Grow(),
            Padding = new UiEdges(40, 96, 40, 64),
            Gap = 8,
            Panel = true,
        };
        using (ui.Column(0, bar))
        {
            ui.Text(_title, UiText.Title with { Wrap = true }, new UiBox { Width = UiSize.Grow() });
            ui.Space(UiBox.Fixed(0, 40));
            if (_matches.Phase == MatchPhase.Joining)
            {
                if (Entry(ui, s_cancel, "Cancel"))
                {
                    _matches.RequestLeave("The join was cancelled.");
                }
            }
            else if (overMatch)
            {
                if (Entry(ui, s_resume, "Resume"))
                {
                    // The bar shows beside Settings too: leaving it writes them.
                    Show(SamplePage.Main);
                    _open = false;
                }

                SettingsEntry(ui);
                if (Entry(ui, s_leave, "Leave"))
                {
                    _matches.RequestLeave("You left the match.");
                }

                QuitEntry(ui);
            }
            else if (_matches.Phase == MatchPhase.Menu)
            {
                if (Entry(ui, s_play, "Play"))
                {
                    _matches.RequestStart(_starts.OfflineStart());
                }

                if (Entry(ui, s_join, "Join", _page == SamplePage.Join))
                {
                    Show(SamplePage.Join);
                }

                SettingsEntry(ui);
                QuitEntry(ui);
                if (_matches.LastEnd is { } end)
                {
                    ui.Space(new UiBox { Height = UiSize.Grow() });
                    ui.Text(
                        end,
                        UiText.Small with
                        {
                            Tone = UiTone.Warning,
                            Wrap = true,
                        },
                        new UiBox { Width = UiSize.Grow() }
                    );
                }
            }
        }
    }

    private void SettingsEntry(Ui ui)
    {
        if (Entry(ui, s_settings, "Settings", _page == SamplePage.Settings))
        {
            Show(SamplePage.Settings);
        }
    }

    private void QuitEntry(Ui ui)
    {
        if (Entry(ui, s_quit, "Quit"))
        {
            _window.RequestExit("Quit in the menu");
        }
    }

    /// <summary>A bar entry: a button whose label is a heading, which the style draws flat; marked while its page shows.</summary>
    private static bool Entry(Ui ui, int id, string label, bool shown = false) =>
        ui.Button(
            id,
            label,
            UiText.Heading,
            new UiBox { Width = UiSize.Grow(), Height = UiSize.Fixed(EntryHeight) },
            shown ? UiMarks.Selected : UiMarks.None
        ).Clicked;

    private void JoinPage(Ui ui)
    {
        using (
            ui.Column(
                0,
                new UiBox
                {
                    Panel = true,
                    Padding = new UiEdges(48),
                    Gap = 24,
                }
            )
        )
        {
            ui.Text("Join a game", UiText.Heading);
            ui.Text("The host's address, host:port:", UiText.Small);
            ui.TextBox(s_address, _address, "127.0.0.1:47600", UiBox.Fixed(640, 64));
            if (ui.Button(s_joinAddress, "Join", box: UiBox.Fixed(200, 64)).Clicked)
            {
                Join();
            }

            if (_notice is not null)
            {
                ui.Text(
                    _notice,
                    UiText.Small with
                    {
                        Tone = UiTone.Warning,
                        Wrap = true,
                    },
                    UiBox.Sized(UiSize.Fixed(640), UiSize.Fit)
                );
            }
        }
    }

    /// <summary>The engine's Settings page inside a panel of the sample's; Back (its footer) writes the settings.</summary>
    private void SettingsPanel(Ui ui)
    {
        using (
            ui.Column(
                0,
                new UiBox
                {
                    Panel = true,
                    Padding = new UiEdges(48, 32, 48, 40),
                    Gap = 16,
                    AlignX = UiAlign.Centre,
                }
            )
        )
        {
            ui.Text("Settings", UiText.Heading);
            _settingsPage.Build(ui);
            if (_settingsPage.Footer(ui))
            {
                Show(SamplePage.Main);
            }
        }
    }

    /// <summary>A join under way: where to, and how far (Cancel is on the bar; Escape cancels too).</summary>
    private void Joining(Ui ui)
    {
        using (
            ui.Column(
                0,
                new UiBox
                {
                    Panel = true,
                    Padding = new UiEdges(48),
                    Gap = 16,
                }
            )
        )
        {
            ui.Text(
                _matches.JoinProgress.Stage switch
                {
                    JoinStage.Welcoming => "Connected; waiting for the host…",
                    JoinStage.Downloading => "Downloading the host's world…",
                    _ => "Connecting…",
                },
                UiText.Heading
            );
            ui.Text(_matches.JoinAddress ?? "", UiText.Small);
        }
    }

    /// <summary>Opens <paramref name="page"/> beside the bar; leaving Settings writes them.</summary>
    private void Show(SamplePage page)
    {
        if (_page == SamplePage.Settings && page != SamplePage.Settings)
        {
            _settings.Save();
        }

        if (page == SamplePage.Settings && _page != SamplePage.Settings)
        {
            _settingsPage.Opened();
        }

        if (page == SamplePage.Join)
        {
            // Typing goes to the address box at once (from the next layout, which declares it).
            Ui.StartTyping(s_address);
            _notice = null;
        }

        _page = page;
    }

    /// <summary>Enter in the address box: it clicks as the Join button does.</summary>
    private void PressJoin()
    {
        Toolkit.PlayPress();
        Join();
    }

    /// <summary>Joins the address typed over UDP, or says why it cannot.</summary>
    private void Join()
    {
        var address = _address.Line.Text.Trim();
        if (!SessionAddress.TryParse(address, out _, out _))
        {
            _notice =
                address.Length == 0 ? "Type the host's address, as 127.0.0.1:47600." : $"'{address}' is not host:port.";
            return;
        }

        _notice = null;
        _matches.RequestStart(_starts.JoinStart(address));
    }
}

/// <summary>The page beside the sample menu's bar.</summary>
public enum SamplePage
{
    /// <summary>None: the bar alone.</summary>
    Main,

    /// <summary>The host's address to join.</summary>
    Join,

    /// <summary>The engine's Settings page.</summary>
    Settings,
}
