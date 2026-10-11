using Itera.Engine.UI;
using Itera.Engine.UI.Toolkit;
using SkiaSharp;

namespace Itera.Engine.Tests.UI.Toolkit;

/// <summary>
/// <see cref="Ui"/>'s layout, pinned with a fixed-advance text measurer (a character half its size wide, a line 1.25 of
/// it tall) at one pixel a unit: columns, rows, stacks, grids, scroll areas, anchored elements, sizes that fit, are
/// fixed, grow, or take a share, padding, gaps, alignment, text wrapped, cut, and broken at line breaks, the dump, ids,
/// the hash, and a rebuild that allocates nothing.
/// </summary>
public sealed class UiLayoutTests
{
    /// <summary>Body text 20 units: a character 10 pixels wide, a line 25 tall.</summary>
    private readonly Ui _ui = new(new FixedTextMeasurer(), new UiStyle(bodyUnits: 20));

    [Fact]
    public void A_column_fits_its_widest_child_and_stacks_them_with_padding_and_gaps()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, new UiBox { Padding = new UiEdges(5), Gap = 4 }))
        {
            _ui.Text(2, "abcd");
            _ui.Text(3, "abcdefgh");
        }

        _ui.End();

        Assert.Equal(new SKRect(0, 0, 90, 64), _ui.Rect(1));
        Assert.Equal(new SKRect(5, 5, 45, 30), _ui.Rect(2));
        Assert.Equal(new SKRect(5, 34, 85, 59), _ui.Rect(3));
    }

    [Fact]
    public void A_row_gives_growing_children_what_the_others_leave_by_weight()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Row(1, new UiBox { Width = UiSize.Fixed(400), Gap = 10 }))
        {
            _ui.Area(2, UiBox.Fixed(50, 20));
            _ui.Area(3, new UiBox { Width = UiSize.Grow(1), Height = UiSize.Fixed(20) });
            _ui.Area(4, new UiBox { Width = UiSize.Grow(3), Height = UiSize.Fixed(20) });
            _ui.Area(5, new UiBox { Width = UiSize.Share(0.25f), Height = UiSize.Fixed(20) });
        }

        _ui.End();

        // 400 less 50, 100 (a quarter), and three gaps of 10 leaves 220: 55 and 165.
        Assert.Equal(new SKRect(0, 0, 50, 20), _ui.Rect(2));
        Assert.Equal(new SKRect(60, 0, 115, 20), _ui.Rect(3));
        Assert.Equal(new SKRect(125, 0, 290, 20), _ui.Rect(4));
        Assert.Equal(new SKRect(300, 0, 400, 20), _ui.Rect(5));
    }

    [Theory]
    [InlineData(UiAlign.Start, UiAlign.Start, 0, 0)]
    [InlineData(UiAlign.Centre, UiAlign.Centre, 910, 520)]
    [InlineData(UiAlign.End, UiAlign.End, 1820, 1040)]
    public void A_screen_filling_root_places_its_children_by_its_alignment(
        UiAlign alignX,
        UiAlign alignY,
        float left,
        float top
    )
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, UiBox.Fill with { AlignX = alignX, AlignY = alignY }))
        {
            _ui.Area(2, UiBox.Fixed(100, 40));
        }

        _ui.End();

        Assert.Equal(new SKRect(0, 0, 1920, 1080), _ui.Rect(1));
        Assert.Equal(new SKRect(left, top, left + 100, top + 40), _ui.Rect(2));
    }

    [Fact]
    public void Across_a_column_children_grow_take_a_share_or_fit_at_most_the_room()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, new UiBox { Width = UiSize.Fixed(200) }))
        {
            _ui.Area(2, new UiBox { Width = UiSize.Grow(), Height = UiSize.Fixed(10) });
            _ui.Area(3, new UiBox { Width = UiSize.Share(0.5f), Height = UiSize.Fixed(10) });
            _ui.Text(4, "a text far too long for the column");
        }

        _ui.End();

        Assert.Equal(200, _ui.Rect(2).Width);
        Assert.Equal(100, _ui.Rect(3).Width);
        Assert.Equal(200, _ui.Rect(4).Width);
    }

    [Fact]
    public void Sizes_keep_within_their_least_and_most()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Row(1, new UiBox { Width = UiSize.Fixed(1000) }))
        {
            _ui.Area(2, new UiBox { Width = UiSize.Grow().Within(0, 300), Height = UiSize.Fixed(10) });
            _ui.Area(3, new UiBox { Width = UiSize.Fit.Within(80), Height = UiSize.Fixed(10) });
        }

        _ui.End();

        Assert.Equal(300, _ui.Rect(2).Width);
        Assert.Equal(80, _ui.Rect(3).Width);
    }

    [Fact]
    public void Wrapped_text_breaks_after_the_last_word_that_fits_and_grows_down()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, new UiBox { Width = UiSize.Fixed(100) }))
        {
            _ui.Text(2, "aaaa bbbb cccc", UiText.Wrapped);
        }

        _ui.End();

        var text = Element(2);
        Assert.Equal(2, text.LineCount);
        Assert.Equal(new UiLine(0, 9, 90, false), _ui.Line(text.FirstLine));
        Assert.Equal(new UiLine(10, 4, 40, false), _ui.Line(text.FirstLine + 1));
        Assert.Equal(50, _ui.Rect(2).Height);
    }

    [Fact]
    public void A_word_longer_than_the_line_breaks_where_it_must_and_line_breaks_start_new_lines()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, new UiBox { Width = UiSize.Fixed(60) }))
        {
            _ui.Text(2, "abcdefghij", UiText.Wrapped);
            _ui.Text(3, "ab\ncd", UiText.Wrapped);
        }

        _ui.End();

        Assert.Equal([(0, 6), (6, 4)], Lines(2));
        Assert.Equal([(0, 2), (3, 2)], Lines(3));
    }

    [Fact]
    public void Text_that_does_not_fit_is_cut_with_an_ellipsis()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, new UiBox { Width = UiSize.Fixed(60) }))
        {
            _ui.Text(2, "abcdefghij");
            _ui.Text(3, "aaaa bbbb cccc dddd", UiText.Wrapped with { MostLines = 2 });
        }

        _ui.End();

        // Five characters and the ellipsis fill the 60 pixels.
        Assert.Equal(new UiLine(0, 5, 60, true), _ui.Line(Element(2).FirstLine));
        var wrapped = Element(3);
        Assert.Equal(2, wrapped.LineCount);
        Assert.Equal(new UiLine(5, 4, 50, true), _ui.Line(wrapped.FirstLine + 1));
        Assert.Contains("cut", _ui.Dump().Split('\n')[1], StringComparison.Ordinal);
    }

    [Fact]
    public void In_a_row_wrapping_text_takes_what_the_rest_leave()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Row(1, new UiBox { Width = UiSize.Fixed(200), Gap = 10 }))
        {
            _ui.Area(2, UiBox.Fixed(90, 10));
            _ui.Text(3, "aaaa bbbb cccc", UiText.Wrapped);
        }

        _ui.End();

        Assert.Equal(new SKRect(100, 0, 200, 50), _ui.Rect(3));
        Assert.Equal(50, _ui.Rect(1).Height);
    }

    [Fact]
    public void A_grid_places_its_children_in_rows_of_equal_cells()
    {
        _ui.Begin(1920, 1080, 1);
        using (
            _ui.Grid(
                1,
                columns: 3,
                new UiBox
                {
                    Width = UiSize.Fixed(320),
                    Gap = 10,
                    Padding = new UiEdges(5),
                }
            )
        )
        {
            for (var i = 0; i < 7; i++)
            {
                _ui.Area(10 + i, new UiBox { Width = UiSize.Grow(), Height = UiSize.Fixed(i == 4 ? 60 : 30) });
            }
        }

        _ui.End();

        // Cells (310 - 20) / 3 wide; the second row is as tall as its tallest, 60.
        Assert.Equal(new SKRect(5, 5, 102, 35), _ui.Rect(10));
        Assert.Equal(new SKRect(218, 5, 315, 35), _ui.Rect(12));
        Assert.Equal(new SKRect(5, 45, 102, 75), _ui.Rect(13));
        Assert.Equal(60, _ui.Rect(14).Height);
        Assert.Equal(115, _ui.Rect(16).Top);
        Assert.Equal(5 + 30 + 10 + 60 + 10 + 30 + 5, _ui.Rect(1).Height);
    }

    [Fact]
    public void A_scroll_area_shows_what_fits_and_scrolls_within_its_content()
    {
        Scrolled(60);
        Assert.Equal(new SKRect(0, 0, 100, 100), _ui.Rect(1));
        Assert.Equal(-60, _ui.Rect(10).Top);
        Assert.Equal(new SKRect(0, 0, 100, 100), Element(10).Clip);

        // Past its end it stops with its last child at its bottom.
        Scrolled(1000);
        Assert.Equal(100, _ui.Rect(14).Bottom);
    }

    [Fact]
    public void Anchored_elements_sit_over_the_rest_against_the_screen_or_an_element()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1))
        {
            _ui.Area(2, UiBox.Fixed(100, 40));
            _ui.Area(
                3,
                UiBox.Fixed(80, 30) with
                {
                    Anchor = UiAnchor.Beside(2, UiPoint.BottomLeft, UiPoint.TopLeft, 0, 5),
                }
            );
            _ui.Area(4, UiBox.Fixed(100, 40));
        }

        _ui.Area(5, UiBox.Fixed(50, 20) with { Anchor = UiAnchor.Screen(UiPoint.TopRight, -10, 10) });
        _ui.End();

        Assert.Equal(new SKRect(0, 40, 100, 80), _ui.Rect(4));
        Assert.Equal(new SKRect(0, 45, 80, 75), _ui.Rect(3));
        Assert.Equal(new SKRect(1860, 10, 1910, 30), _ui.Rect(5));
        Assert.Equal(new SKRect(0, 0, 100, 80), _ui.Rect(1));
    }

    [Fact]
    public void Units_scale_every_size_and_text_keeps_a_least_size()
    {
        _ui.Begin(3840, 2160, 2);
        _ui.Area(1, UiBox.Fixed(100, 40));
        _ui.Text(2, "ab");
        _ui.End();
        Assert.Equal(new SKRect(0, 0, 200, 80), _ui.Rect(1));
        Assert.Equal(40, _ui.Rect(2).Width);

        _ui.Begin(640, 360, 0.1f);
        _ui.Text(2, "ab");
        _ui.End();
        Assert.Equal(Ui.LeastTextPixels, Element(2).TextPixels);
    }

    [Fact]
    public void The_dump_names_each_element_by_kind_id_text_and_rectangle()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, new UiBox { Panel = true }))
        {
            _ui.Button(2, "Play");
            _ui.Switch(3, on: true);
            _ui.Slider(4, 0.25f, 0, 1);
        }

        _ui.End();

        var lines = _ui.Dump().Split('\n');
        Assert.Equal("Column #1 0,0 240x105 panel", lines[0]);
        Assert.Equal("  Button #2 \"Play\" 0,0 80x45", lines[1]);
        Assert.Equal("  Switch #3 0,45 50x30 on", lines[2]);
        Assert.Equal("  Slider #4 0,75 240x30 value=0.25", lines[3]);
        Assert.True(_ui.TryFind("Play", out var play));
        Assert.Equal(2, play.Id);
    }

    [Fact]
    public void An_id_declared_twice_or_a_container_left_open_throws()
    {
        _ui.Begin(100, 100, 1);
        _ui.Area(7);
        _ui.Area(7);
        Assert.Throws<InvalidOperationException>(_ui.End);

        _ui.Begin(100, 100, 1);
        _ui.Column(1);
        Assert.Throws<InvalidOperationException>(_ui.End);

        // The next layout starts afresh.
        _ui.Begin(100, 100, 1);
        _ui.Area(2);
        _ui.End();
        Assert.Equal(1, _ui.Count);
    }

    [Fact]
    public void While_the_next_layout_is_declared_rectangles_are_the_last_layouts()
    {
        _ui.Begin(1920, 1080, 1);
        _ui.Area(1, UiBox.Fixed(100, 40));
        _ui.End();

        _ui.Begin(1920, 1080, 1);
        var state = _ui.Area(1, UiBox.Fixed(300, 40));
        Assert.Equal(new SKRect(0, 0, 100, 40), state.Rect);
        _ui.End();
        Assert.Equal(300, _ui.Rect(1).Width);
    }

    [Fact]
    public void The_hash_changes_only_when_what_is_drawn_changes()
    {
        var first = Page("Play", on: false);
        Assert.Equal(first, Page(string.Concat("Pl", "ay"), on: false));
        Assert.NotEqual(first, Page("Quit", on: false));
        Assert.NotEqual(first, Page("Play", on: true));
    }

    [Fact]
    public void A_rebuild_allocates_nothing()
    {
        var entry = new TextEntry(40);
        entry.Line.Set("typed");
        for (var i = 0; i < 3; i++)
        {
            Settings(entry);
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                Settings(entry);
            }
        });

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void A_pair_at_a_break_or_a_cut_stays_whole_and_never_stops_the_layout()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, new UiBox { Width = UiSize.Fixed(15) }))
        {
            _ui.Text(2, "a\U0001F600");
            _ui.Text(3, "a\U0001F600b\U0001F600", UiText.Wrapped);
        }

        _ui.End();

        Assert.Equal(new UiLine(0, 0, 10, true), _ui.Line(Element(2).FirstLine));
        Assert.Equal([(0, 1), (1, 2), (3, 1), (4, 2)], Lines(3));
    }

    [Fact]
    public void A_fit_grid_is_as_wide_as_all_its_columns_with_fewer_children()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Grid(1, columns: 4, new UiBox { Gap = 10 }))
        {
            _ui.Area(2, UiBox.Fixed(100, 20));
            _ui.Area(3, UiBox.Fixed(100, 20));
        }

        _ui.End();

        Assert.Equal(430, _ui.Rect(1).Width);
        Assert.Equal(new SKRect(110, 0, 210, 20), _ui.Rect(3));
    }

    [Fact]
    public void Growing_children_start_from_their_content_so_a_fit_row_cuts_no_label()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Row(1))
        {
            _ui.Button(2, "OK", box: new UiBox { Width = UiSize.Grow() });
            _ui.Button(3, "Cancel", box: new UiBox { Width = UiSize.Grow() });
        }

        _ui.End();

        Assert.Equal(60, _ui.Rect(2).Width);
        Assert.Equal(100, _ui.Rect(3).Width);
        Assert.DoesNotContain("cut", _ui.Dump(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_growing_child_held_at_its_most_leaves_the_rest_to_the_others()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Row(1, new UiBox { Width = UiSize.Fixed(1000) }))
        {
            _ui.Area(2, new UiBox { Width = UiSize.Grow().Within(0, 100), Height = UiSize.Fixed(10) });
            _ui.Area(3, new UiBox { Width = UiSize.Grow(), Height = UiSize.Fixed(10) });
        }

        _ui.End();

        Assert.Equal(100, _ui.Rect(2).Width);
        Assert.Equal(900, _ui.Rect(3).Width);
    }

    [Fact]
    public void Texts_in_a_row_share_its_room_each_at_most_its_own_width()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Row(1, new UiBox { Width = UiSize.Fixed(300) }))
        {
            _ui.Text(2, "aaaaa", UiText.Wrapped);
            _ui.Text(3, new string('b', 40), UiText.Wrapped);
        }

        _ui.End();

        Assert.Equal(50, _ui.Rect(2).Width);
        Assert.Equal(250, _ui.Rect(3).Width);
    }

    [Fact]
    public void A_fit_column_holding_wrapped_text_in_a_narrow_row_wraps_it()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Row(1, new UiBox { Width = UiSize.Fixed(100) }))
        {
            using (_ui.Column(2))
            {
                _ui.Text(3, "aaaa bbbb cccc", UiText.Wrapped);
            }
        }

        _ui.End();

        Assert.Equal(100, _ui.Rect(2).Width);
        Assert.Equal(2, Element(3).LineCount);
    }

    [Fact]
    public void Line_breaks_start_lines_also_without_wrapping_and_the_widest_line_sets_the_width()
    {
        _ui.Begin(1920, 1080, 1);
        _ui.Text(1, "ab\ncdef");
        _ui.End();

        Assert.Equal(new SKRect(0, 0, 40, 50), _ui.Rect(1));
        Assert.Equal([(0, 2), (3, 4)], Lines(1));
    }

    [Fact]
    public void Text_at_the_top_wraps_within_the_screen()
    {
        _ui.Begin(100, 1080, 1);
        _ui.Text(1, "aaaa bbbb cccc", UiText.Wrapped);
        _ui.End();

        Assert.Equal(100, _ui.Rect(1).Width);
        Assert.Equal(2, Element(1).LineCount);
    }

    [Fact]
    public void A_declaration_that_threw_is_dropped_by_the_next_begin()
    {
        _ui.Begin(100, 100, 1);
        _ui.Column(1);
        // The code threw here, before closing the column and ending.
        _ui.Begin(100, 100, 1);
        _ui.Area(2, UiBox.Fixed(10, 10));
        _ui.End();

        Assert.Equal(1, _ui.Count);
    }

    [Fact]
    public void A_scroll_offset_past_the_content_is_kept_where_it_was_held()
    {
        Scrolled(1000);
        _ui.Begin(1920, 1080, 1);
        using (_ui.Scroll(1, UiBox.Fixed(100, 100)))
        {
            for (var i = 0; i < 10; i++)
            {
                _ui.Area(10 + i, UiBox.Fixed(100, 50));
            }
        }

        _ui.End();

        Assert.Equal(-150, _ui.Rect(10).Top);
    }

    [Fact]
    public void A_growing_space_pushes_its_neighbours_apart()
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Row(1, new UiBox { Width = UiSize.Fixed(300) }))
        {
            _ui.Area(2, UiBox.Fixed(50, 20));
            _ui.Space(new UiBox { Width = UiSize.Grow() });
            _ui.Area(3, UiBox.Fixed(50, 20));
        }

        _ui.End();

        Assert.Equal(250, _ui.Rect(3).Left);
    }

    [Fact]
    public void Ids_from_names_and_list_items_are_stable_distinct_and_never_zero()
    {
        Assert.Equal(Ui.Id("menu.play"), Ui.Id("menu.play"));
        Assert.NotEqual(Ui.Id("menu.play"), Ui.Id("menu.quit"));
        var items = Enumerable.Range(0, 1000).Select(i => Ui.Id(Ui.Id("slots"), i)).ToHashSet();
        Assert.Equal(1000, items.Count);
        Assert.DoesNotContain(0, items);
    }

    [Fact]
    public void Closing_a_container_twice_closes_it_once()
    {
        _ui.Begin(100, 100, 1);
        using (_ui.Column(1))
        {
            var inner = _ui.Row(2);
            inner.Dispose();
            inner.Dispose();
            _ui.Area(3, UiBox.Fixed(10, 10));
        }

        _ui.End();

        Assert.Equal(1, Element(3).Depth);
    }

    /// <summary>
    /// A page like the settings': a title, rows of a name and a control, a scroll list, a text box, a grid of slots (more
    /// elements than the pools first hold), and a tooltip anchored to the text box.
    /// </summary>
    private void Settings(TextEntry entry)
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1, UiBox.Fill with { AlignX = UiAlign.Centre, Gap = 10, Padding = new UiEdges(20) }))
        {
            _ui.Text("Settings", UiText.Title);
            for (var i = 0; i < 6; i++)
            {
                using (
                    _ui.Row(
                        10 + i,
                        new UiBox
                        {
                            Width = UiSize.Fixed(600),
                            Gap = 8,
                            AlignY = UiAlign.Centre,
                        }
                    )
                )
                {
                    _ui.Text("A setting's name", UiText.Body, new UiBox { Width = UiSize.Grow() });
                    _ui.Slider(20 + i, 0.5f, 0, 1);
                    _ui.Switch(30 + i, (i & 1) == 0);
                }
            }

            using (_ui.Scroll(40, new UiBox { Height = UiSize.Fixed(200), Width = UiSize.Fixed(600) }))
            {
                for (var i = 0; i < 12; i++)
                {
                    _ui.Button(50 + i, "A friend's name, rather long for its row");
                }
            }

            _ui.TextBox(70, entry, "host:port");
            _ui.Text("Wrapped words under it all, long enough to break.", UiText.Wrapped);
            using (_ui.Grid(80, columns: 5, new UiBox { Gap = 4 }))
            {
                for (var i = 0; i < 20; i++)
                {
                    _ui.Area(Ui.Id(80, i), UiBox.Fixed(40, 40));
                }
            }
        }

        _ui.Text(
            90,
            "A tooltip",
            UiText.Small,
            UiBox.Sized(UiSize.Fit, UiSize.Fit) with
            {
                Anchor = UiAnchor.Beside(70, UiPoint.BottomLeft, UiPoint.TopLeft),
            }
        );
        _ui.End();
    }

    private int Page(string label, bool on)
    {
        _ui.Begin(1920, 1080, 1);
        using (_ui.Column(1))
        {
            _ui.Button(2, label);
            _ui.Switch(3, on);
        }

        _ui.End();
        return _ui.Hash;
    }

    private void Scrolled(float pixels)
    {
        _ui.ScrollTo(1, pixels);
        _ui.Begin(1920, 1080, 1);
        using (_ui.Scroll(1, UiBox.Fixed(100, 100)))
        {
            for (var i = 0; i < 5; i++)
            {
                _ui.Area(10 + i, UiBox.Fixed(100, 50));
            }
        }

        _ui.End();
    }

    private UiElement Element(int id)
    {
        for (var i = 0; i < _ui.Count; i++)
        {
            if (_ui.ElementAt(i).Id == id)
            {
                return _ui.ElementAt(i);
            }
        }

        throw new InvalidOperationException($"No element {id}");
    }

    private List<(int Start, int Length)> Lines(int id)
    {
        var element = Element(id);
        var lines = new List<(int, int)>();
        for (var i = 0; i < element.LineCount; i++)
        {
            var line = _ui.Line(element.FirstLine + i);
            lines.Add((line.Start, line.Length));
        }

        return lines;
    }
}
