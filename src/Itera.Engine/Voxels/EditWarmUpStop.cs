namespace Itera.Engine.Voxels;

/// <summary>
/// When the edit warm-up (<see cref="VoxelWorld.StartEditWarmUp"/>) stops, from each round's time and the number of
/// methods .NET has compiled in the process (<see cref="System.Runtime.JitInfo.GetCompiledMethodCount"/>). It stops:
/// <list type="bullet">
/// <item>After the first round in a build whose code .NET never optimises (Debug): that round compiles the code, and
/// more would only run out the time limit.</item>
/// <item>When two rounds in a row take under half the first-pass time (the least of the rounds after the first):
/// .NET has compiled the edit code again, optimised.</item>
/// <item>When <see cref="SteadyRounds"/> rounds in a row have taken within a tenth of the best round, and that is under
/// <see cref="SteadyShare"/> of the best round of the first second: the code has passed its last compilation. Composite
/// ReadyToRun builds (Steam's) run their precompiled code at about 42 ms a round, then about 90 while .NET measures it,
/// then 28 for good; their process went on compiling a method or a few every second for 4 to 6 s after (the game's own
/// start-up), so the quiet below came only at about 8 s, the loading screen waiting all that time (2026-10-15).</item>
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

    /// <summary>The first stretch of the warm-up, whose best round is the code as first compiled (or precompiled).</summary>
    public const double EarlySeconds = 1;

    /// <summary>The rounds in a row within <see cref="SteadyBand"/> of the best that count as steady.</summary>
    public const int SteadyRounds = 8;

    /// <summary>How much slower than the best round a steady round may be.</summary>
    public const double SteadyBand = 1.1;

    /// <summary>
    /// The most a steady best may take of the first second's best: under it the code was compiled again, faster. The
    /// Steam build's last compilation took 0.65 to 0.68 of its precompiled code's time (2026-10-15).
    /// </summary>
    public const double SteadyShare = 0.75;

    private long _compiled = -1;
    private double _quietSince;
    private int _fastRounds;
    private double _best = double.PositiveInfinity;
    private int _steadyRounds;

    /// <summary>The rounds taken.</summary>
    public int Rounds { get; private set; }

    /// <summary>
    /// The first-pass code's time for a round (the least of the rounds after the first); infinity until it is measured.
    /// </summary>
    public double FirstPassMilliseconds { get; private set; } = double.PositiveInfinity;

    /// <summary>The last round's time.</summary>
    public double LastMilliseconds { get; private set; }

    /// <summary>The best round of the first <see cref="EarlySeconds"/> after the first round; infinity until one ends.</summary>
    public double EarlyMilliseconds { get; private set; } = double.PositiveInfinity;

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

        if (optimisable && Rounds > 1)
        {
            CountSteady(roundMilliseconds, seconds);
        }

        if (
            Reason is null
            && optimisable
            && Rounds > 1 + FirstPassRounds
            && seconds > EarlySeconds
            && _steadyRounds >= SteadyRounds
            && _best < SteadyShare * EarlyMilliseconds
        )
        {
            Reason = "rounds steady under three quarters of the first second's best";
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

    /// <summary>
    /// Counts the rounds in a row within <see cref="SteadyBand"/> of the best: a round faster than that starts the count
    /// again from itself, and one slower ends it.
    /// </summary>
    private void CountSteady(double roundMilliseconds, double seconds)
    {
        if (seconds <= EarlySeconds)
        {
            EarlyMilliseconds = Math.Min(EarlyMilliseconds, roundMilliseconds);
        }

        if (roundMilliseconds < _best / SteadyBand)
        {
            _best = roundMilliseconds;
            _steadyRounds = 1;
        }
        else if (roundMilliseconds <= SteadyBand * _best)
        {
            _best = Math.Min(_best, roundMilliseconds);
            _steadyRounds++;
        }
        else
        {
            _steadyRounds = 0;
        }
    }
}
