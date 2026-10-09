using Itera.Modding;

namespace Itera.Engine.Time;

/// <summary>
/// Converts variable loop time into fixed simulation steps. The main loop calls <see cref="Add"/> once per iteration; it
/// wakes at least once per fixed step, and more often when frames or window events arrive.
/// </summary>
public sealed class FixedStepAccumulator
{
    private float _accumulated;
    private int _stepsThisIteration;

    /// <param name="stepSeconds">Length of one fixed step.</param>
    /// <param name="maxStepsPerIteration">Steps allowed per loop iteration before excess time is dropped.</param>
    public FixedStepAccumulator(float stepSeconds, int maxStepsPerIteration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStepsPerIteration);
        StepSeconds = stepSeconds;
        MaxStepsPerIteration = maxStepsPerIteration;
    }

    /// <summary>Length of one fixed step in seconds.</summary>
    public float StepSeconds { get; }

    /// <summary>Steps allowed per loop iteration before excess time is dropped.</summary>
    public int MaxStepsPerIteration { get; }

    /// <summary>Number of fixed steps consumed so far.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Whole steps discarded so far because the per-iteration cap was reached.</summary>
    public ulong DroppedSteps { get; private set; }

    /// <summary>Whole steps discarded during the current loop iteration.</summary>
    public int LastDropped { get; private set; }

    /// <summary>Seconds of loop time still needed before the next step can run. Zero when a step is due.</summary>
    public float SecondsUntilNextStep => Math.Max(0f, StepSeconds - _accumulated);

    /// <summary>Fraction of a step accumulated but not yet consumed, in [0, 1).</summary>
    public float Alpha => Math.Clamp(_accumulated / StepSeconds, 0f, 0.999999f);

    /// <summary>
    /// A match starts: its first step is tick 0, and no time is waiting, so the time its start took is not caught up in a
    /// burst of steps.
    /// </summary>
    public void Restart()
    {
        _accumulated = 0;
        _stepsThisIteration = 0;
        LastDropped = 0;
        Tick = 0;
    }

    /// <summary>Adds the time since the previous iteration and resets the per-iteration step counter.</summary>
    public void Add(float deltaSeconds)
    {
        _accumulated += Math.Max(0f, deltaSeconds);
        _stepsThisIteration = 0;
        LastDropped = 0;
    }

    /// <summary>
    /// Consumes one step if enough time has accumulated and the per-iteration cap has not been reached.
    /// When the cap is reached, leftover whole steps are discarded so the next iteration starts fresh.
    /// </summary>
    public bool TryConsumeStep(out FixedStepTime time)
    {
        if (_accumulated < StepSeconds)
        {
            time = default;
            return false;
        }

        if (_stepsThisIteration >= MaxStepsPerIteration)
        {
            var dropped = (int)(_accumulated / StepSeconds);
            _accumulated %= StepSeconds;
            LastDropped += dropped;
            DroppedSteps += (ulong)dropped;
            time = default;
            return false;
        }

        _accumulated -= StepSeconds;
        _stepsThisIteration++;
        time = new FixedStepTime(StepSeconds, Tick);
        Tick++;
        return true;
    }
}
