namespace Itera.Engine.Voxels;

/// <summary>
/// When the edit warm-up (<see cref="VoxelWorld.StartEditWarmUp"/>) stops, from each round's time and the number of
/// methods .NET has compiled in the process (<see cref="System.Runtime.JitInfo.GetCompiledMethodCount"/>). It stops:
/// <list type="bullet">
/// <item>After the first round in a build whose code .NET never optimises (Debug): that round compiles the code, and
/// more would only run out the time limit.</item>
/// <item>When two rounds in a row take under half the first-pass time (the least of the rounds after the first):
/// .NET has compiled the edit code again, optimised.</item>
/// <item>When nothing in the process has been compiled for <see cref="QuietSeconds"/>: .NET compiles the methods
/// called often again, optimised, a moment after the start-up stops compiling new code, so the edit code is as fast as
/// it gets. This stops the warm-up when its code is already optimised by the second round, which no later round can
/// halve (51.8 ms against about 155; one Release start in eight ran out the time limit), and in composite ReadyToRun
/// builds (Steam's), whose first pass is precompiled optimised code only 1.6 times slower than the best (43 to 50 ms
/// against 27 to 28; 2026-10-09).</item>
/// <item>At <see cref="LimitSeconds"/>.</item>
/// </list>
/// </summary>
/// <param name="optimisable">False in a build whose code .NET never optimises (Debug).</param>
internal sealed class EditWarmUpStop(bool optimisable)
{
    /// <summary>The rounds after the first, which compiles, whose least time is the first-pass code's.</summary>
    public const int FirstPassRounds = 4;

    /// <summary>
    /// How long nothing may be compiled before the edit code counts as optimised. In twenty Release starts (2026-10-09)
    /// the process went quiet for up to 312 ms (ReadyToRun; 174 ms with the JIT alone) while the code was not optimised
    /// yet, and the rounds after 0.75 s of quiet took within 11 % of the best.
    /// </summary>
    public const double QuietSeconds = 0.75;

    /// <summary>The longest the warm-up runs.</summary>
    public const double LimitSeconds = 10;

    private long _compiled = -1;
    private double _quietSince;
    private int _fastRounds;

    /// <summary>The rounds taken.</summary>
    public int Rounds { get; private set; }

    /// <summary>
    /// The first-pass code's time for a round (the least of the rounds after the first); infinity until it is measured.
    /// </summary>
    public double FirstPassMilliseconds { get; private set; } = double.PositiveInfinity;

    /// <summary>The last round's time.</summary>
    public double LastMilliseconds { get; private set; }

    /// <summary>Why the warm-up stopped; null until it should.</summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// Takes a round: its time, the methods compiled in the process after it, and the seconds since the warm-up began.
    /// True when the warm-up should stop (<see cref="Reason"/> says why).
    /// </summary>
    public bool Stop(double roundMilliseconds, long compiledMethods, double seconds)
    {
        Rounds++;
        LastMilliseconds = roundMilliseconds;
        if (compiledMethods != _compiled)
        {
            _compiled = compiledMethods;
            _quietSince = seconds;
        }

        if (!optimisable)
        {
            FirstPassMilliseconds = roundMilliseconds;
            Reason = "code never optimised (Debug), one round";
        }
        else if (Rounds > 1 && Rounds <= 1 + FirstPassRounds)
        {
            FirstPassMilliseconds = Math.Min(FirstPassMilliseconds, roundMilliseconds);
        }
        else if (Rounds > 1 + FirstPassRounds)
        {
            _fastRounds = roundMilliseconds < 0.5 * FirstPassMilliseconds ? _fastRounds + 1 : 0;
            if (_fastRounds >= 2)
            {
                Reason = "two rounds under half the first-pass time";
            }
        }

        if (Reason is null && seconds - _quietSince >= QuietSeconds)
        {
            Reason = "nothing compiled for 0.75 s";
        }

        if (Reason is null && seconds >= LimitSeconds)
        {
            Reason = "time limit";
        }

        return Reason is not null;
    }
}
