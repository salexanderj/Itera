using System.Numerics;
using Itera.Engine.UI;
using Itera.Engine.UI.Toolkit;
using SkiaSharp;

namespace Itera.Engine.Tests.UI.Toolkit;

/// <summary>
/// <see cref="Ui"/>'s input matched against the last layout: hover, a click when released on the element pressed, a
/// slider dragged also off itself, the wheel to the innermost scroll area, the keyboard's focus (Tab, the arrows, a
/// slider's steps, Enter), a text box's typing, disabled elements, anchored elements hit first, tweens, and a frame of
/// input that allocates nothing.
/// </summary>
public sealed class UiInputTests
{
    private static readonly int Play = Ui.Id("play");
    private static readonly int Quit = Ui.Id("quit");
    private static readonly int Volume = Ui.Id("volume");
    private static readonly int Name = Ui.Id("name");
    private static readonly int List = Ui.Id("list");

    private readonly Ui _ui = new(new FixedTextMeasurer(), new UiStyle(bodyUnits: 20));
    private readonly TextEntry _entry = new(40);
    private double _seconds;

    // The page: a column at the top left, 300 wide: Play (0..45), Quit (45..90), the volume slider (90..120),
    // and the name box (120..).
    private UiState _play;
    private UiState _quit;
    private UiState _volume;
    private UiState _name;
    private bool _quitDisabled;

    /// <summary>The page laid out once: input is matched against the last layout.</summary>
    public UiInputTests() => Frame(default);

    [Fact]
    public void The_pointer_hovers_and_a_release_on_the_element_pressed_clicks_it()
    {
        // A pointer first seen over Play hovers it, but did not move onto it: no soft click.
        Frame(new UiInput(new Vector2(10, 10), PointerShown: true));
        Assert.True(_play.Hovered);
        Assert.Equal(0, _ui.Entered);
        Frame(new UiInput(new Vector2(500, 500), true));
        Frame(new UiInput(new Vector2(10, 10), true));
        Assert.Equal(Play, _ui.Entered);

        Frame(new UiInput(new Vector2(10, 10), true, Down: true, Pressed: true));
        Assert.True(_play.Pressed);
        Assert.False(_play.Clicked);

        Frame(new UiInput(new Vector2(10, 12), true, Released: true));
        Assert.True(_play.Clicked);
        Assert.Equal(Play, _ui.Clicked);
        Assert.Equal(0, _ui.Entered);

        // Down on Play, up on Quit: neither clicks.
        Frame(new UiInput(new Vector2(10, 10), true, Down: true, Pressed: true));
        Frame(new UiInput(new Vector2(10, 60), true, Released: true));
        Assert.False(_play.Clicked);
        Assert.False(_quit.Clicked);
    }

    [Fact]
    public void A_press_and_release_in_one_frame_clicks()
    {
        Frame(new UiInput(new Vector2(10, 60), true));
        Frame(new UiInput(new Vector2(10, 60), true, Pressed: true, Released: true));

        Assert.True(_quit.Clicked);
    }

    [Fact]
    public void A_disabled_element_takes_no_hover_press_or_focus()
    {
        _quitDisabled = true;
        Frame(new UiInput(new Vector2(10, 60), true));
        Frame(new UiInput(new Vector2(10, 60), true, Pressed: true, Released: true));
        Assert.False(_quit.Hovered);
        Assert.False(_quit.Clicked);

        Frame(new UiInput(Keys: UiKeys.Next));
        Frame(new UiInput(Keys: UiKeys.Next));
        Assert.Equal(Volume, _ui.Focused);
        Assert.Contains("disabled", _ui.Dump(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_slider_follows_the_held_pointer_also_off_itself()
    {
        Frame(new UiInput(new Vector2(50, 100), true, Down: true, Pressed: true));
        Assert.True(_volume.Changed);
        Assert.Equal(0.25f, _volume.Value, 3);

        Frame(new UiInput(new Vector2(500, 300), true, Down: true));
        Assert.True(_volume.Dragging);
        Assert.Equal(1, _volume.Value);

        Frame(new UiInput(new Vector2(500, 300), true, Released: true));
        Frame(new UiInput(new Vector2(0, 100), true));
        Assert.False(_volume.Changed);
        Assert.False(_volume.Clicked);
    }

    [Fact]
    public void Tab_moves_the_focus_in_order_round_to_the_first_and_shift_tab_back()
    {
        Frame(new UiInput(Keys: UiKeys.Next));
        Assert.True(_play.Focused);
        Assert.True(_ui.FocusShown);
        Frame(new UiInput(Keys: UiKeys.Next));
        Frame(new UiInput(Keys: UiKeys.Next));
        Frame(new UiInput(Keys: UiKeys.Next));
        Assert.True(_name.Focused);
        Frame(new UiInput(Keys: UiKeys.Next));
        Assert.True(_play.Focused);
        Frame(new UiInput(Keys: UiKeys.Previous));
        Assert.True(_name.Focused);

        // A press with the pointer hides the focus's outline, and focuses what it presses.
        Frame(new UiInput(new Vector2(10, 60), true, Down: true, Pressed: true));
        Assert.False(_ui.FocusShown);
        Assert.Equal(Quit, _ui.Focused);
    }

    [Fact]
    public void Enter_presses_the_focused_element_and_a_focused_slider_steps_with_left_and_right()
    {
        Frame(new UiInput(Keys: UiKeys.Next));
        Frame(new UiInput(Keys: UiKeys.Press));
        Assert.True(_play.Clicked);

        _ui.Focus(Volume);
        Frame(new UiInput(Keys: UiKeys.Right));
        Assert.True(_volume.Changed);
        Assert.Equal(0.55f, _volume.Value, 3);
        Assert.True(_volume.Focused);
    }

    [Fact]
    public void The_arrows_move_the_focus_to_the_nearest_element_that_way()
    {
        var cells = new int[9];
        for (var i = 0; i < 9; i++)
        {
            cells[i] = Ui.Id(List, i);
        }

        void Grid(UiKeys keys)
        {
            _ui.Begin(1920, 1080, 1, new UiInput(Keys: keys, Seconds: _seconds));
            using (_ui.Grid(1, columns: 3, new UiBox { Gap = 10 }))
            {
                foreach (var cell in cells)
                {
                    _ui.Area(cell, UiBox.Fixed(50, 50));
                }
            }

            _ui.End();
        }

        Grid(UiKeys.None);
        _ui.Focus(cells[4]);
        Grid(UiKeys.Up);
        Assert.Equal(cells[1], _ui.Focused);
        Grid(UiKeys.Right);
        Assert.Equal(cells[2], _ui.Focused);
        Grid(UiKeys.Down);
        Assert.Equal(cells[5], _ui.Focused);
        // Nothing further right: it stays.
        Grid(UiKeys.Right);
        Assert.Equal(cells[5], _ui.Focused);
    }

    /// <summary>A page whose button opens the next page with its box typing: the box is declared only from then.</summary>
    [Fact]
    public void Typing_started_for_a_box_the_next_layout_declares_reaches_it()
    {
        var other = new TextEntry(40);
        var page = 0;
        void Page(UiInput input)
        {
            _ui.Begin(1920, 1080, 1, input with { Seconds = _seconds });
            if (page == 0 && _ui.Button(Play, "Play", box: UiBox.Fixed(200, 40)).Clicked)
            {
                page = 1;
                _ui.StartTyping(Name);
            }
            else if (page == 1)
            {
                _ui.TextBox(Name, other, "");
            }

            _ui.End();
        }

        Page(default);
        Page(new UiInput(new Vector2(10, 10), true, Pressed: true, Released: true));
        Assert.Equal(1, page);
        Assert.Null(_ui.Typing);
        Page(new UiInput(new Vector2(10, 10), true));
        Assert.Same(other, _ui.Typing);

        // A start for a box no layout declares is dropped after the next one.
        _ui.StopTyping();
        page = 2;
        _ui.StartTyping(Name);
        Page(default);
        page = 1;
        Page(default);
        Assert.Null(_ui.Typing);
    }

    [Fact]
    public void A_text_box_takes_the_typing_when_pressed_and_loses_it_on_a_press_elsewhere()
    {
        Frame(new UiInput(new Vector2(10, 130), true, Pressed: true, Released: true));
        Assert.Same(_entry, _ui.Typing);
        Assert.True(_name.Typing);
        Assert.Contains("typing", _ui.Dump(), StringComparison.Ordinal);

        Frame(new UiInput(new Vector2(1000, 1000), true, Pressed: true, Released: true));
        Assert.Null(_ui.Typing);

        _ui.Focus(Name);
        Frame(new UiInput(Keys: UiKeys.Press));
        Assert.Same(_entry, _ui.Typing);
    }

    [Fact]
    public void The_wheel_scrolls_the_innermost_scroll_area_under_the_pointer()
    {
        void Lists(UiInput input)
        {
            _ui.Begin(1920, 1080, 1, input);
            using (_ui.Scroll(List, UiBox.Fixed(200, 200)))
            {
                _ui.Area(1, UiBox.Fixed(200, 100));
                using (_ui.Scroll(2, UiBox.Fixed(200, 100)))
                {
                    for (var i = 0; i < 5; i++)
                    {
                        _ui.Area(10 + i, UiBox.Fixed(200, 50));
                    }
                }

                _ui.Area(3, UiBox.Fixed(200, 300));
            }

            _ui.End();
        }

        Lists(default);
        // Over the inner list: it scrolls one step (60 units) down.
        Lists(new UiInput(new Vector2(50, 150), true, Wheel: -1));
        Lists(default);
        Assert.Equal(100 - 60, _ui.Rect(10).Top);
        Assert.Equal(0, _ui.Rect(1).Top);

        // Over the outer list, past the inner: the outer scrolls, held within its content.
        Lists(new UiInput(new Vector2(50, 20), true, Wheel: -10));
        Lists(default);
        Assert.Equal(-300, _ui.Rect(1).Top);
    }

    [Fact]
    public void A_focused_element_moved_to_by_a_key_is_scrolled_into_view()
    {
        void Long(UiKeys keys)
        {
            _ui.Begin(1920, 1080, 1, new UiInput(Keys: keys, Seconds: _seconds));
            using (_ui.Scroll(List, UiBox.Fixed(200, 100)))
            {
                for (var i = 0; i < 6; i++)
                {
                    _ui.Button(Ui.Id(List, i), "Row", box: UiBox.Fixed(200, 50));
                }
            }

            _ui.End();
        }

        Long(UiKeys.None);
        _ui.Focus(Ui.Id(List, 4));
        Long(UiKeys.None);
        Long(UiKeys.None);

        Assert.Equal(new SKRect(0, 50, 200, 100), _ui.Rect(Ui.Id(List, 4)));
    }

    [Fact]
    public void An_anchored_element_is_hit_before_what_it_covers()
    {
        void Covered(UiInput input)
        {
            _ui.Begin(1920, 1080, 1, input);
            _ui.Area(1, UiBox.Fixed(200, 200));
            _ui.Area(2, UiBox.Fixed(50, 50) with { Anchor = UiAnchor.Screen(UiPoint.TopLeft, 10, 10) });
            _ui.End();
        }

        Covered(default);
        Covered(new UiInput(new Vector2(20, 20), true));

        Assert.Equal(2, _ui.Hovered);
    }

    [Fact]
    public void A_tween_starts_at_its_first_target_eases_to_a_new_one_and_settles()
    {
        float Glow(float target)
        {
            _ui.Begin(100, 100, 1, new UiInput(Seconds: _seconds));
            var value = _ui.Tween(7, target, 0.5f);
            _ui.End();
            return value;
        }

        Assert.Equal(0, Glow(0));
        Assert.False(_ui.Animating);
        var still = _ui.Hash;

        Assert.Equal(0, Glow(1));
        Assert.True(_ui.Animating);
        _seconds = 0.25;
        Assert.Equal(0.5f, Glow(1), 3);
        Assert.NotEqual(still, _ui.Hash);
        _seconds = 0.6;
        Assert.Equal(1, Glow(1));
        Assert.False(_ui.Animating);
    }

    [Fact]
    public void A_pressable_row_takes_the_pointer_as_a_whole_and_its_passive_picture_none()
    {
        var row = Ui.Id("row");
        UiState state = default;
        void Rows(UiInput input)
        {
            _ui.Begin(1920, 1080, 1, input);
            using (var scope = _ui.Pressable(row, new UiBox { Gap = 10 }))
            {
                state = scope.State;
                _ui.Area(Ui.Id("avatar"), UiBox.Fixed(40, 40), UiMarks.Passive);
                _ui.Text("A friend");
            }

            _ui.End();
        }

        Rows(default);
        Rows(new UiInput(new Vector2(20, 20), true));
        Assert.True(state.Hovered);
        Rows(new UiInput(new Vector2(20, 20), true, Pressed: true, Released: true));
        Assert.True(state.Clicked);
        Assert.Equal(row, _ui.Clicked);
    }

    [Fact]
    public void A_box_that_blocks_and_a_disabled_element_stop_the_pointer_reaching_what_lies_under_them()
    {
        void Covered(UiInput input, bool disabled)
        {
            _ui.Begin(1920, 1080, 1, input);
            _ui.Button(Play, "Play");
            using (_ui.Stack(1, UiBox.Fill with { Blocks = true, Anchor = UiAnchor.Screen(UiPoint.TopLeft) }))
            {
                _ui.Button(
                    Quit,
                    "Quit",
                    box: new UiBox { Width = UiSize.Fixed(400) },
                    marks: disabled ? UiMarks.Disabled : UiMarks.None
                );
            }

            _ui.End();
        }

        Covered(default, disabled: false);
        // Over the shade, away from Quit: Play under it takes nothing.
        Covered(new UiInput(new Vector2(10, 200), true), disabled: false);
        Assert.Equal(0, _ui.Hovered);
        Covered(new UiInput(new Vector2(10, 10), true), disabled: false);
        Assert.Equal(Quit, _ui.Hovered);
        Covered(new UiInput(new Vector2(10, 10), true), disabled: true);
        Covered(new UiInput(new Vector2(10, 10), true), disabled: true);
        Assert.Equal(0, _ui.Hovered);
    }

    [Fact]
    public void The_wheel_goes_past_a_list_with_nothing_to_scroll_to_the_page_and_a_focus_does_not_undo_it()
    {
        void Page(UiInput input)
        {
            _ui.Begin(1920, 1080, 1, input);
            using (_ui.Scroll(List, UiBox.Fixed(200, 200)))
            {
                _ui.Button(Play, "Play", box: UiBox.Fixed(200, 50));
                using (_ui.Scroll(2, UiBox.Fixed(200, 100)))
                {
                    _ui.Area(10, UiBox.Fixed(200, 50));
                }

                _ui.Area(3, UiBox.Fixed(200, 400));
            }

            _ui.End();
        }

        Page(default);
        Page(new UiInput(Keys: UiKeys.Next));
        Assert.Equal(Play, _ui.Focused);
        // Over the short inner list: the page scrolls, and stays scrolled with Play focused above the view.
        Page(new UiInput(new Vector2(50, 75), true, Wheel: -1));
        Page(default);
        Page(default);
        Assert.Equal(-60, _ui.Rect(Play).Top);
    }

    [Fact]
    public void A_focus_moved_by_a_key_shows_through_nested_scroll_areas()
    {
        var target = Ui.Id("deep");
        void Nested(UiKeys keys)
        {
            _ui.Begin(1920, 1080, 1, new UiInput(Keys: keys));
            using (_ui.Scroll(List, UiBox.Fixed(200, 200)))
            {
                _ui.Area(1, UiBox.Fixed(200, 300));
                using (_ui.Scroll(2, UiBox.Fixed(200, 100)))
                {
                    _ui.Area(3, UiBox.Fixed(200, 150));
                    _ui.Button(target, "Deep", box: UiBox.Fixed(200, 50));
                }
            }

            _ui.End();
        }

        Nested(UiKeys.None);
        _ui.Focus(target);
        for (var i = 0; i < 4; i++)
        {
            Nested(UiKeys.None);
        }

        var rect = _ui.Rect(target);
        Assert.InRange(rect.Top, 0, 150);
        Assert.InRange(rect.Bottom, 50, 200);
        Assert.False(_ui.Animating);
    }

    [Fact]
    public void A_right_click_is_a_release_of_the_right_button_over_the_element_it_went_down_on()
    {
        Frame(new UiInput(new Vector2(10, 10), true, RightPressed: true));
        Assert.False(_play.RightClicked);
        Frame(new UiInput(new Vector2(10, 12), true, RightReleased: true));
        Assert.True(_play.RightClicked);

        Frame(new UiInput(new Vector2(10, 10), true, RightPressed: true));
        Frame(new UiInput(new Vector2(10, 60), true, RightReleased: true));
        Assert.False(_play.RightClicked);
        Assert.False(_quit.RightClicked);
    }

    [Fact]
    public void Tab_out_of_a_text_box_takes_the_typing_with_the_focus()
    {
        Frame(new UiInput(new Vector2(10, 130), true, Pressed: true, Released: true));
        Assert.Same(_entry, _ui.Typing);

        Frame(new UiInput(Keys: UiKeys.Next));
        Assert.True(_play.Focused);
        Assert.Null(_ui.Typing);
    }

    [Fact]
    public void A_frame_of_input_allocates_nothing()
    {
        var inputs = new[]
        {
            new UiInput(new Vector2(10, 10), true),
            new UiInput(new Vector2(10, 12), true, Down: true, Pressed: true),
            new UiInput(new Vector2(60, 100), true, Released: true, Wheel: 1),
            new UiInput(Keys: UiKeys.Next | UiKeys.Down),
        };
        for (var i = 0; i < 8; i++)
        {
            Frame(inputs[i % inputs.Length]);
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                Frame(inputs[i % inputs.Length]);
            }
        });

        Assert.Equal(0, bytes);
    }

    /// <summary>One frame of the page with <paramref name="input"/>.</summary>
    private void Frame(UiInput input)
    {
        _ui.Begin(1920, 1080, 1, input with { Seconds = _seconds });
        using (_ui.Column(1, new UiBox { Width = UiSize.Fixed(300) }))
        {
            _play = _ui.Button(Play, "Play", box: new UiBox { Width = UiSize.Grow() });
            _quit = _ui.Button(
                Quit,
                "Quit",
                box: new UiBox { Width = UiSize.Grow() },
                marks: _quitDisabled ? UiMarks.Disabled : UiMarks.None
            );
            _volume = _ui.Slider(Volume, 0.5f, 0, 1, UiBox.Fixed(200, 30));
            _name = _ui.TextBox(Name, _entry, "your name");
        }

        _ui.End();
    }
}
