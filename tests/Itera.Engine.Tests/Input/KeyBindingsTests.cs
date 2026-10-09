using System.Text;
using System.Text.Json;
using Itera.Engine.Input;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Input;

/// <summary>
/// <see cref="InputBinding"/> and <see cref="KeyBindings"/>: names read and written, labels, Shift, Ctrl, and Alt on
/// either side; actions registered with their defaults or the file's bindings (read before or after they register);
/// an id twice refused; a clash unbinding the other action only where both can work at once; Escape and ` refused from
/// the menu and the file; a reset; only changed bindings written, with unknown ids kept; and reading an action
/// allocating nothing.
/// </summary>
public sealed class KeyBindingsTests
{
    [Theory]
    [InlineData("W", "W")]
    [InlineData("space", "Space")]
    [InlineData("ShiftRight", "Shift")]
    [InlineData("ControlLeft", "Ctrl")]
    [InlineData("Number1", "1")]
    [InlineData("LeftBracket", "[")]
    [InlineData("F8", "F8")]
    [InlineData("MouseLeft", "Left mouse")]
    [InlineData("mousemiddle", "Middle mouse")]
    [InlineData("Mouse4", "Mouse 4")]
    [InlineData("None", "None")]
    public void Names_read_back_and_show_their_labels(string name, string label)
    {
        Assert.True(InputBinding.TryParse(name, out var binding));
        Assert.Equal(label, binding.Label);
        Assert.True(InputBinding.TryParse(binding.Name, out var again));
        Assert.Equal(binding, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("65")]
    [InlineData("Unknown")]
    [InlineData("NotAKey")]
    [InlineData("Mouse0")]
    [InlineData("Mouse13")]
    public void Anything_else_is_not_a_binding(string name) => Assert.False(InputBinding.TryParse(name, out _));

    [Fact]
    public void A_modifier_on_either_side_matches()
    {
        var shift = InputBinding.Of(Key.ShiftRight);
        var events = new InputAccumulator();
        events.KeyDown(Key.ShiftRight);
        var right = events.TakeTick(false);
        events.KeyUp(Key.ShiftRight);
        events.KeyDown(Key.ShiftLeft);
        var left = events.TakeTick(false);

        Assert.Equal(InputBinding.Of(Key.ShiftLeft), shift);
        Assert.True(shift.IsDown(in right) && shift.WasPressed(in right));
        Assert.True(shift.IsDown(in left));
        Assert.False(InputBinding.Of(Key.W).IsDown(in left));
    }

    [Fact]
    public void Actions_take_their_defaults_or_the_files_bindings_read_before_or_after_they_register()
    {
        var bindings = new KeyBindings();
        var jump = bindings.Register("jump", "Jump", "", InputBinding.Of(Key.Space));
        bindings.Load(Json("""{ "jump": "J", "fire": "MouseRight", "walk": "W" }"""));
        var fire = bindings.Register("fire", "Fire", "", InputBinding.Of(MouseButton.Left));
        var crouch = bindings.Register("crouch", "Crouch", "", InputBinding.Of(Key.C));

        Assert.Equal(InputBinding.Of(Key.J), bindings[jump]);
        Assert.Equal(InputBinding.Of(MouseButton.Right), bindings[fire]);
        Assert.Equal(InputBinding.Of(Key.C), bindings[crouch]);
        // Reading the file is no change.
        Assert.Equal(0, bindings.Version);
        Assert.Throws<InvalidOperationException>(() => bindings.Register("JUMP", "Jump", "", InputBinding.None));
    }

    [Fact]
    public void A_key_another_action_has_unbinds_it_where_both_can_work_at_once()
    {
        var bindings = new KeyBindings();
        var jump = bindings.Register("jump", "Jump", "", InputBinding.Of(Key.Space));
        var crouch = bindings.Register("crouch", "Crouch", "", InputBinding.Of(Key.C));
        var attack = bindings.Register("attack", "Attack", "", InputBinding.Of(MouseButton.Left), context: 1);
        var tool = bindings.Register("tool", "Tool", "", InputBinding.Of(MouseButton.Left), context: 2);
        var dig = bindings.Register("dig", "Dig", "", InputBinding.Of(Key.Number1), context: 2);

        Assert.True(bindings.Set(crouch, InputBinding.Of(Key.Space)));
        Assert.Equal(InputBinding.None, bindings[jump]);
        Assert.Equal(InputBinding.Of(Key.Space), bindings[crouch]);

        // The attacks and the tools' keys never work at once: they keep sharing the left button.
        Assert.Equal(InputBinding.Of(MouseButton.Left), bindings[attack]);
        Assert.Equal(InputBinding.Of(MouseButton.Left), bindings[tool]);
        Assert.True(bindings.Set(dig, InputBinding.Of(MouseButton.Left)));
        Assert.Equal(InputBinding.None, bindings[tool]);
        Assert.Equal(InputBinding.Of(MouseButton.Left), bindings[attack]);

        // An action that works always clashes with both.
        Assert.True(bindings.Set(jump, InputBinding.Of(MouseButton.Left)));
        Assert.Equal(InputBinding.None, bindings[attack]);
        Assert.Equal(InputBinding.None, bindings[dig]);
    }

    [Fact]
    public void Escape_and_the_console_key_are_refused_from_the_menu_and_the_file()
    {
        var bindings = new KeyBindings();
        bindings.Load(Json("""{ "jump": "Escape", "crouch": "GraveAccent" }"""));
        var jump = bindings.Register("jump", "Jump", "", InputBinding.Of(Key.Space));
        var crouch = bindings.Register("crouch", "Crouch", "", InputBinding.Of(Key.C));

        Assert.Equal(InputBinding.Of(Key.Space), bindings[jump]);
        Assert.Equal(InputBinding.Of(Key.C), bindings[crouch]);
        Assert.False(bindings.Set(jump, InputBinding.Of(Key.Escape)));
        Assert.False(bindings.Set(jump, InputBinding.Of(Key.GraveAccent)));
        Assert.Equal(InputBinding.Of(Key.Space), bindings[jump]);
        Assert.Throws<ArgumentException>(() => bindings.Register("menu", "Menu", "", InputBinding.Of(Key.Escape)));
    }

    [Fact]
    public void The_engines_rows_come_first_and_fixed_rows_cannot_be_bound()
    {
        var bindings = new KeyBindings();

        Assert.Equal(["Menu", "Console", "Capture the pointer", "Stats line"], bindings.Rows.Select(r => r.Label));
        Assert.Equal("Escape", bindings.Rows[0].FixedKeys);
        Assert.Equal(InputBinding.Of(Key.F1), bindings[bindings.Stats]);
        Assert.False(bindings.Set(new KeyAction(0), InputBinding.Of(Key.M)));
    }

    [Fact]
    public void Reset_puts_every_binding_back_and_only_changed_ones_are_written_with_unknown_ids_kept()
    {
        var bindings = new KeyBindings();
        bindings.Load(Json("""{ "mod-action": "K" }"""));
        var jump = bindings.Register("jump", "Jump", "", InputBinding.Of(Key.Space));
        var crouch = bindings.Register("crouch", "Crouch", "", InputBinding.Of(Key.C));
        bindings.Set(crouch, InputBinding.Of(Key.Space));

        var written = Write(bindings);
        Assert.Equal("""{"Keys":{"jump":"None","crouch":"Space","mod-action":"K"}}""", written);
        Assert.Equal(1, bindings.Version);

        bindings.ResetAll();
        Assert.Equal(InputBinding.Of(Key.Space), bindings[jump]);
        Assert.Equal(InputBinding.Of(Key.C), bindings[crouch]);
        Assert.Equal(2, bindings.Version);
        Assert.Equal("{}", Write(bindings));
    }

    [Fact]
    public void Reading_an_action_allocates_nothing()
    {
        var bindings = new KeyBindings();
        var sprint = bindings.Register("sprint", "Sprint", "", InputBinding.Of(Key.ShiftLeft));
        var fire = bindings.Register("fire", "Fire", "", InputBinding.Of(MouseButton.Left));
        var events = new InputAccumulator();
        events.KeyDown(Key.ShiftRight);
        events.ButtonDown(MouseButton.Left);
        var input = events.TakeTick(true);
        var held = 0;
        _ = bindings[sprint].Label;

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            held += bindings.IsDown(in input, sprint) && bindings.WasPressed(in input, fire) ? 1 : 0;
            held += bindings[sprint].Label.Length > 0 ? 0 : 1;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(1000, held);
    }

    /// <summary>
    /// Bindings from the file that clash with a default, read before or after the action registers, unbind the action
    /// with the default; two from the file leave the first registered bound; actions that never work at once keep both.
    /// </summary>
    [Fact]
    public void Clashes_from_the_file_or_new_defaults_unbind_the_default_where_both_can_work_at_once()
    {
        var before = new KeyBindings();
        before.Load(Json("""{ "jump": "Q" }"""));
        var jump = before.Register("jump", "Jump", "", InputBinding.Of(Key.Space));
        var radial = before.Register("radial", "Radial", "", InputBinding.Of(Key.Q));
        Assert.Equal(InputBinding.Of(Key.Q), before[jump]);
        Assert.True(before[radial].IsNone);

        var after = new KeyBindings();
        var first = after.Register("radial", "Radial", "", InputBinding.Of(Key.Q));
        var second = after.Register("jump", "Jump", "", InputBinding.Of(Key.Space));
        after.Load(Json("""{ "jump": "Q" }"""));
        Assert.True(after[first].IsNone);
        Assert.Equal(InputBinding.Of(Key.Q), after[second]);

        var both = new KeyBindings();
        var a = both.Register("a", "A", "", InputBinding.Of(Key.A));
        var b = both.Register("b", "B", "", InputBinding.Of(Key.B));
        both.Load(Json("""{ "a": "X", "b": "X" }"""));
        Assert.Equal(InputBinding.Of(Key.X), both[a]);
        Assert.True(both[b].IsNone);

        var apart = new KeyBindings();
        var attack = apart.Register("attack", "Attack", "", InputBinding.Of(MouseButton.Left), context: 1);
        var tool = apart.Register("tool", "Tool", "", InputBinding.Of(MouseButton.Right), context: 2);
        apart.Load(Json("""{ "tool": "MouseLeft" }"""));
        Assert.Equal(InputBinding.Of(MouseButton.Left), apart[attack]);
        Assert.Equal(InputBinding.Of(MouseButton.Left), apart[tool]);
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string Write(KeyBindings bindings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            bindings.Write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
