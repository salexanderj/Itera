using Itera.Engine.Input;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Input;

public sealed class PointerCaptureTests
{
    [Fact]
    public void A_left_click_captures_and_does_not_reach_the_game()
    {
        var pointer = new PointerCapture();

        var reachesGame = pointer.ButtonDown(MouseButton.Left, focused: true);
        var captured = pointer.Update(available: true);

        Assert.False(reachesGame);
        Assert.True(captured);
        Assert.True(pointer.Captured);
    }

    [Fact]
    public void Clicks_while_captured_reach_the_game()
    {
        var pointer = Captured();

        Assert.True(pointer.ButtonDown(MouseButton.Left, focused: true));
        Assert.True(pointer.ButtonDown(MouseButton.Middle, focused: true));
        Assert.True(pointer.Update(available: true));
    }

    [Fact]
    public void Other_buttons_do_not_capture()
    {
        var pointer = new PointerCapture();

        Assert.True(pointer.ButtonDown(MouseButton.Right, focused: true));
        Assert.True(pointer.ButtonDown(MouseButton.Middle, focused: true));
        Assert.False(pointer.Update(available: true));
    }

    [Fact]
    public void Escape_frees_a_captured_pointer_and_quits_only_when_it_is_free()
    {
        var pointer = Captured();

        Assert.False(pointer.Escape());
        Assert.False(pointer.Update(available: true));
        Assert.True(pointer.Escape());
    }

    [Fact]
    public void Escape_right_after_the_capturing_click_cancels_it()
    {
        var pointer = new PointerCapture();
        pointer.ButtonDown(MouseButton.Left, focused: true);

        Assert.False(pointer.Escape());
        Assert.False(pointer.Update(available: true));
    }

    [Fact]
    public void A_click_after_Escape_in_the_same_iteration_captures_again_without_reaching_the_game()
    {
        var pointer = Captured();
        pointer.Escape();

        Assert.False(pointer.ButtonDown(MouseButton.Left, focused: true));
        Assert.True(pointer.Update(available: true));
    }

    [Fact]
    public void Losing_focus_frees_the_pointer_and_a_click_without_focus_does_not_capture()
    {
        var pointer = Captured();

        Assert.False(pointer.Update(available: false));
        Assert.True(pointer.ButtonDown(MouseButton.Left, focused: false));
        Assert.False(pointer.Update(available: true));
    }

    [Fact]
    public void A_capture_wish_is_dropped_when_the_window_is_not_available()
    {
        var pointer = new PointerCapture();
        pointer.ButtonDown(MouseButton.Left, focused: true);

        Assert.False(pointer.Update(available: false));
        // The wish does not linger into the next iteration.
        Assert.False(pointer.Update(available: true));
    }

    private static PointerCapture Captured()
    {
        var pointer = new PointerCapture();
        pointer.ButtonDown(MouseButton.Left, focused: true);
        pointer.Update(available: true);
        return pointer;
    }
}
