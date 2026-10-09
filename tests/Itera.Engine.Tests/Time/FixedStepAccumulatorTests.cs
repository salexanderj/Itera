using Itera.Engine.Time;

namespace Itera.Engine.Tests.Time;

public sealed class FixedStepAccumulatorTests
{
    private const float Step = 1f / 60f;

    [Fact]
    public void No_step_before_enough_time_has_accumulated()
    {
        var accumulator = new FixedStepAccumulator(Step, 8);

        accumulator.Add(Step * 0.5f);

        Assert.False(accumulator.TryConsumeStep(out _));
        Assert.Equal(0UL, accumulator.Tick);
    }

    [Fact]
    public void Consumes_whole_steps_and_keeps_the_remainder()
    {
        var accumulator = new FixedStepAccumulator(Step, 8);

        accumulator.Add(Step * 2.5f);

        Assert.True(accumulator.TryConsumeStep(out var first));
        Assert.True(accumulator.TryConsumeStep(out var second));
        Assert.False(accumulator.TryConsumeStep(out _));
        Assert.Equal(0UL, first.Tick);
        Assert.Equal(1UL, second.Tick);
        Assert.Equal(Step, first.StepSeconds);
        Assert.InRange(accumulator.Alpha, 0.49f, 0.51f);
    }

    [Fact]
    public void Caps_steps_per_frame_and_drops_the_excess()
    {
        var accumulator = new FixedStepAccumulator(Step, 3);

        accumulator.Add(Step * 10f);
        var consumed = 0;
        while (accumulator.TryConsumeStep(out _))
        {
            consumed++;
        }

        Assert.Equal(3, consumed);
        Assert.Equal(3UL, accumulator.Tick);

        // Next frame starts with less than one step left over, so the loop does not spiral.
        accumulator.Add(0f);
        Assert.False(accumulator.TryConsumeStep(out _));
    }

    [Fact]
    public void Counts_dropped_steps_per_frame_and_in_total()
    {
        var accumulator = new FixedStepAccumulator(Step, 2);

        accumulator.Add(Step * 5.5f);
        while (accumulator.TryConsumeStep(out _)) { }

        Assert.Equal(3, accumulator.LastDropped);
        Assert.Equal(3UL, accumulator.DroppedSteps);

        // Less than one step left over plus one more step: exactly one step runs and nothing is dropped.
        accumulator.Add(Step);
        Assert.Equal(0, accumulator.LastDropped);
        Assert.True(accumulator.TryConsumeStep(out _));
        Assert.False(accumulator.TryConsumeStep(out _));
        Assert.Equal(0, accumulator.LastDropped);
        Assert.Equal(3UL, accumulator.DroppedSteps);
    }

    [Fact]
    public void Tick_counts_across_frames()
    {
        var accumulator = new FixedStepAccumulator(Step, 8);

        for (var frame = 0; frame < 5; frame++)
        {
            accumulator.Add(Step);
            Assert.True(accumulator.TryConsumeStep(out _));
        }

        Assert.Equal(5UL, accumulator.Tick);
    }

    [Fact]
    public void Rejects_invalid_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepAccumulator(0f, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepAccumulator(Step, 0));
        var accumulator = new FixedStepAccumulator(Step, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.SetTimeScale(0.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.SetTimeScale(17));
        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.StepWhilePaused(-1));
    }

    [Fact]
    public void Paused_no_time_makes_a_step_and_frames_show_the_last_step()
    {
        var accumulator = new FixedStepAccumulator(Step, 8);
        accumulator.Add(Step * 0.5f);

        accumulator.Pause();
        accumulator.Add(Step * 10);

        Assert.False(accumulator.TryConsumeStep(out _));
        Assert.Equal(0UL, accumulator.Tick);
        Assert.True(accumulator.Paused);
        Assert.Equal(Step, accumulator.SecondsUntilNextStep);
        Assert.True(accumulator.Alpha > 0.99f);
    }

    [Fact]
    public void Stepping_while_paused_runs_exactly_the_steps_asked_for_at_most_the_cap_an_iteration()
    {
        var accumulator = new FixedStepAccumulator(Step, 3);

        accumulator.StepWhilePaused(5);
        var first = Consume(accumulator, 0);
        var second = Consume(accumulator, 0);
        var third = Consume(accumulator, 0);

        Assert.Equal((3, 2, 0), (first, second, third));
        Assert.Equal(5UL, accumulator.Tick);
        Assert.True(accumulator.Paused);
        Assert.Equal(0, accumulator.StepsOwed);
    }

    [Fact]
    public void Resuming_runs_the_steps_again_from_no_time_waiting()
    {
        var accumulator = new FixedStepAccumulator(Step, 8);
        accumulator.StepWhilePaused(2);

        accumulator.Resume();

        Assert.False(accumulator.Paused);
        Assert.Equal(0, Consume(accumulator, Step * 0.5f));
        Assert.Equal(1, Consume(accumulator, Step * 0.5f));
    }

    [Fact]
    public void A_time_scale_runs_that_many_steps_a_second_with_a_cap_that_grows_with_it()
    {
        var accumulator = new FixedStepAccumulator(Step, 2);

        accumulator.SetTimeScale(4);

        // Two steps of real time are eight of game time; the cap of two an iteration grows to eight.
        Assert.Equal(8, Consume(accumulator, Step * 2.001f));
        Assert.Equal(0, accumulator.LastDropped);
        Assert.Equal(Step * 0.25f, accumulator.SecondsUntilNextStep, 3);

        accumulator.SetTimeScale(0.5f);
        Assert.Equal(0, Consume(accumulator, Step));
        Assert.Equal(1, Consume(accumulator, Step));
    }

    [Fact]
    public void A_new_match_starts_unpaused_at_real_time()
    {
        var accumulator = new FixedStepAccumulator(Step, 8);
        accumulator.SetTimeScale(2);
        accumulator.StepWhilePaused(3);

        accumulator.Restart();

        Assert.False(accumulator.Paused);
        Assert.Equal(0, accumulator.StepsOwed);
        Assert.Equal(1, accumulator.TimeScale);
    }

    /// <summary>One loop iteration: adds the time and returns how many steps it ran.</summary>
    private static int Consume(FixedStepAccumulator accumulator, float seconds)
    {
        accumulator.Add(seconds);
        var steps = 0;
        while (accumulator.TryConsumeStep(out _))
        {
            steps++;
        }

        return steps;
    }
}
