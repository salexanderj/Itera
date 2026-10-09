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
    }
}
