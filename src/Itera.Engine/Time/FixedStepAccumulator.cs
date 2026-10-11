using Itera.Modding;

namespace Itera.Engine.Time;

/// <summary>
/// Converts variable loop time into fixed simulation steps. The main loop calls <see cref="Add"/> once per iteration; it
/// wakes at least once per fixed step, and more often when frames or window events arrive. The console's time commands
/// (offline only) pause the steps, run a number of them while paused, and scale the time they take
/// (<see cref="Pause"/>, <see cref="StepWhilePaused"/>, <see cref="SetTimeScale"/>); the menu over an offline match holds
/// them apart from that (<see cref="Hold"/>).
/// </summary>
public sealed class FixedStepAccumulator
{
    /// <summary>Slowest time scale: a twentieth of real time.</summary>
    public const float LeastTimeScale = 0.05f;

    /// <summary>Fastest time scale: sixteen times real time, as far as the machine keeps up.</summary>
    public const float MostTimeScale = 16;

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

    /// <summary>
    /// True while the steps are paused: none run but those <see cref="StepWhilePaused"/> asks for. A new match starts
    /// unpaused (<see cref="Restart"/>).
    /// </summary>
    public bool Paused { get; private set; }

    /// <summary>Steps still to run while paused.</summary>
    public int StepsOwed { get; private set; }

    /// <summary>
    /// True while the steps are held (the menu open over an offline match, <c>Engine:Ui:MenuPausesOffline</c>): none run,
    /// not even those owed while paused, and no time waits. Apart from <see cref="Paused"/>, so letting go never resumes a
    /// match the console paused. A new match starts not held (<see cref="Restart"/>).
    /// </summary>
    public bool Held { get; private set; }

    /// <summary>
    /// Game time per second of loop time: 1, or what <see cref="SetTimeScale"/> set for the match (a new match starts at
    /// 1, so a scale set offline never reaches a session).
    /// </summary>
    public float TimeScale { get; private set; } = 1;

    /// <summary>
    /// Seconds of loop time still needed before the next step can run. Zero when a step is due; a step's length while
    /// paused with none owed, so the loop still wakes.
    /// </summary>
    public float SecondsUntilNextStep =>
        Held ? StepSeconds
        : Paused ? (StepsOwed > 0 ? 0f : StepSeconds)
        : Math.Max(0f, StepSeconds - _accumulated) / TimeScale;

    /// <summary>
    /// Fraction of a step accumulated but not yet consumed, in [0, 1); nearly 1 while paused or held, so frames show the
    /// last step run.
    /// </summary>
    public float Alpha => Paused || Held ? 0.999999f : Math.Clamp(_accumulated / StepSeconds, 0f, 0.999999f);

    /// <summary>
    /// A match starts: its first step is tick 0, and no time is waiting, so the time its start took is not caught up in a
    /// burst of steps. It runs unpaused at real time.
    /// </summary>
    public void Restart()
    {
        _accumulated = 0;
        _stepsThisIteration = 0;
        LastDropped = 0;
        Tick = 0;
        Paused = false;
        Held = false;
        StepsOwed = 0;
        TimeScale = 1;
    }

    /// <summary>Holds the steps (<see cref="Held"/>) or lets them go again, from now with no time waiting.</summary>
    public void Hold(bool held)
    {
        if (held == Held)
        {
            return;
        }

        Held = held;
        _accumulated = 0;
    }

    /// <summary>Stops the steps until <see cref="Resume"/>; steps owed are dropped.</summary>
    public void Pause()
    {
        Paused = true;
        StepsOwed = 0;
        _accumulated = 0;
    }

    /// <summary>Runs the steps again from now, with no time waiting.</summary>
    public void Resume()
    {
        Paused = false;
        StepsOwed = 0;
        _accumulated = 0;
    }

    /// <summary>Pauses, and runs <paramref name="steps"/> more steps over the next iterations, at most the cap each.</summary>
    public void StepWhilePaused(int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        if (!Paused)
        {
            Pause();
        }

        StepsOwed += steps;
    }

    /// <summary>Sets the time scale for the match, <see cref="LeastTimeScale"/> to <see cref="MostTimeScale"/>.</summary>
    public void SetTimeScale(float scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, LeastTimeScale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, MostTimeScale);
        TimeScale = scale;
    }

    /// <summary>
    /// Adds the time since the previous iteration, scaled by <see cref="TimeScale"/> (none while paused), and resets the
    /// per-iteration step counter.
    /// </summary>
    public void Add(float deltaSeconds)
    {
        if (!Paused && !Held)
        {
            _accumulated += Math.Max(0f, deltaSeconds) * TimeScale;
        }

        _stepsThisIteration = 0;
        LastDropped = 0;
    }

    /// <summary>The steps an iteration may run: the cap, times the time scale when it is above 1.</summary>
    private int StepsPerIteration => MaxStepsPerIteration * (int)Math.Ceiling(Math.Max(1f, TimeScale));

    /// <summary>
    /// Consumes one step if enough time has accumulated and the per-iteration cap has not been reached.
    /// When the cap is reached, leftover whole steps are discarded so the next iteration starts fresh.
    /// </summary>
    public bool TryConsumeStep(out FixedStepTime time)
    {
        if (Held)
        {
            time = default;
            return false;
        }

        if (Paused)
        {
            if (StepsOwed == 0 || _stepsThisIteration >= MaxStepsPerIteration)
            {
                time = default;
                return false;
            }

            StepsOwed--;
            _stepsThisIteration++;
            time = new FixedStepTime(StepSeconds, Tick);
            Tick++;
            return true;
        }

        if (_accumulated < StepSeconds)
        {
            time = default;
            return false;
        }

        if (_stepsThisIteration >= StepsPerIteration)
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
