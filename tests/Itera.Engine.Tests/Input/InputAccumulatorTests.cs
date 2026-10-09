using System.Numerics;
using Itera.Engine.Input;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Input;

public sealed class InputAccumulatorTests
{
    [Fact]
    public void A_held_key_is_down_in_every_frame_but_pressed_only_in_the_first()
    {
        var input = new InputAccumulator();
        input.KeyDown(Key.W);

        var first = input.TakeFrame(false);
        var second = input.TakeFrame(false);
        input.KeyUp(Key.W);
        var third = input.TakeFrame(false);

        Assert.True(first.IsDown(Key.W) && first.WasPressed(Key.W));
        Assert.True(second.IsDown(Key.W) && !second.WasPressed(Key.W));
        Assert.False(third.IsDown(Key.W) || third.WasPressed(Key.W));
    }

    [Fact]
    public void A_tap_between_two_frames_still_counts_as_pressed()
    {
        var input = new InputAccumulator();
        input.KeyDown(Key.Space);
        input.KeyUp(Key.Space);
        input.ButtonDown(MouseButton.Left);
        input.ButtonUp(MouseButton.Left);

        var frame = input.TakeFrame(false);

        Assert.True(frame.WasPressed(Key.Space) && !frame.IsDown(Key.Space));
        Assert.True(frame.WasPressed(MouseButton.Left) && !frame.IsDown(MouseButton.Left));
    }

    [Fact]
    public void Movement_and_scroll_add_up_until_the_frame_takes_them()
    {
        var input = new InputAccumulator();
        input.AddMouseDelta(new Vector2(3, -1));
        input.AddMouseDelta(new Vector2(2, 4));
        input.AddScroll(1);
        input.AddScroll(0.5f);

        var first = input.TakeFrame(true);
        var second = input.TakeFrame(true);

        Assert.Equal(new Vector2(5, 3), first.MouseDelta);
        Assert.Equal(1.5f, first.Scroll);
        Assert.True(first.PointerCaptured);
        Assert.Equal(Vector2.Zero, second.MouseDelta);
        Assert.Equal(0, second.Scroll);
    }

    [Fact]
    public void Losing_focus_releases_held_keys_and_buttons()
    {
        var input = new InputAccumulator();
        input.KeyDown(Key.ShiftLeft);
        input.ButtonDown(MouseButton.Right);

        input.ReleaseAll();
        var frame = input.TakeFrame(false);

        Assert.False(frame.IsDown(Key.ShiftLeft));
        Assert.False(frame.IsDown(MouseButton.Right));
        Assert.False(input.IsDown(MouseButton.Right));
        // The press happened, so this frame still sees it.
        Assert.True(frame.WasPressed(Key.ShiftLeft));
    }

    [Fact]
    public void Frames_and_fixed_steps_each_see_a_press_once()
    {
        var input = new InputAccumulator();
        input.KeyDown(Key.Space);
        input.ButtonDown(MouseButton.Left);

        var frame = input.TakeFrame(false);
        var laterFrame = input.TakeFrame(false);
        var tick = input.TakeTick(false);
        var laterTick = input.TakeTick(false);

        Assert.True(frame.WasPressed(Key.Space) && frame.WasPressed(MouseButton.Left));
        Assert.True(tick.WasPressed(Key.Space) && tick.WasPressed(MouseButton.Left));
        Assert.False(laterFrame.WasPressed(Key.Space) || laterFrame.WasPressed(MouseButton.Left));
        Assert.False(laterTick.WasPressed(Key.Space) || laterTick.WasPressed(MouseButton.Left));
        // Held state is shared: both readers see the key still down.
        Assert.True(laterFrame.IsDown(Key.Space) && laterTick.IsDown(Key.Space));
    }

    [Fact]
    public void Mouse_movement_since_the_last_step_waits_for_the_next_step()
    {
        var input = new InputAccumulator();
        input.AddMouseDelta(new Vector2(4, 1));
        input.AddScroll(2);

        var frame = input.TakeFrame(true);
        Assert.Equal(new Vector2(4, 1), frame.MouseDelta);
        // Taking a frame leaves the step's share alone.
        Assert.Equal(new Vector2(4, 1), input.MouseSinceTick);

        input.AddMouseDelta(new Vector2(1, 1));
        Assert.Equal(new Vector2(5, 2), input.MouseSinceTick);

        var tick = input.TakeTick(true);
        Assert.Equal(new Vector2(5, 2), tick.MouseDelta);
        Assert.Equal(2, tick.Scroll);
        Assert.True(tick.PointerCaptured);
        Assert.Equal(Vector2.Zero, input.MouseSinceTick);
        Assert.Equal(new Vector2(1, 1), input.TakeFrame(true).MouseDelta);
    }

    [Fact]
    public void A_second_step_in_one_iteration_sees_no_presses_or_movement()
    {
        var input = new InputAccumulator();
        input.KeyDown(Key.W);
        input.AddMouseDelta(new Vector2(3, 3));

        var first = input.TakeTick(false);
        var second = input.TakeTick(false);

        Assert.True(first.WasPressed(Key.W));
        Assert.Equal(new Vector2(3, 3), first.MouseDelta);
        Assert.True(second.IsDown(Key.W) && !second.WasPressed(Key.W));
        Assert.Equal(Vector2.Zero, second.MouseDelta);
    }

    [Fact]
    public void Clearing_mouse_movement_clears_it_for_both_readers()
    {
        var input = new InputAccumulator();
        input.AddMouseDelta(new Vector2(40, -20));

        input.ClearMouseDelta();

        Assert.Equal(Vector2.Zero, input.MouseSinceTick);
        Assert.Equal(Vector2.Zero, input.TakeFrame(true).MouseDelta);
        Assert.Equal(Vector2.Zero, input.TakeTick(true).MouseDelta);
    }

    [Fact]
    public void Losing_focus_releases_held_keys_for_fixed_steps_too()
    {
        var input = new InputAccumulator();
        input.KeyDown(Key.D);

        input.ReleaseAll();
        var tick = input.TakeTick(false);

        Assert.False(tick.IsDown(Key.D));
        Assert.True(tick.WasPressed(Key.D));
    }

    [Fact]
    public void Every_key_code_has_its_own_bit_and_unknown_keys_are_ignored()
    {
        var input = new InputAccumulator();
        foreach (var key in Enum.GetValues<Key>())
        {
            input.KeyDown(key);
            var frame = input.TakeFrame(false);
            input.KeyUp(key);

            Assert.Equal(key != Key.Unknown, frame.IsDown(key));
            Assert.False(frame.IsDown(key == Key.A ? Key.B : Key.A));
        }
    }

    [Fact]
    public void Collecting_and_taking_frames_and_steps_allocates_nothing()
    {
        var input = new InputAccumulator();
        Run(input, 100);

        var before = SystemAllocation.StartMeasurement();
        Run(input, 1000);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Run(InputAccumulator input, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            input.KeyDown(Key.W);
            input.ButtonDown(MouseButton.Right);
            input.AddMouseDelta(new Vector2(1, 2));
            input.AddScroll(1);
            var frame = input.TakeFrame(true);
            var tick = input.TakeTick(true);
            input.KeyUp(Key.W);
            input.ButtonUp(MouseButton.Right);
            if (!frame.IsDown(Key.W) || !frame.WasPressed(MouseButton.Right) || !tick.WasPressed(Key.W))
            {
                throw new InvalidOperationException("The snapshot lost an event.");
            }
        }
    }
}
