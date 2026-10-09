using System.Diagnostics;
using System.Globalization;

namespace Itera.TestSupport;

/// <summary>
/// Runs rows of hostile input, each through the systems that use it, on one worker thread, and fails the test when a row
/// takes longer than a limit or throws (a state's tick of <c>uint.MaxValue</c> once made
/// the host's checks count for ever, and feet 21,000 km out made its column walks loop for hours). The test thread waits
/// for every row, however slow, before it fails, so the test never disposes a world a row still uses: a first version
/// failed at once, and the slow row then read freed voxel memory and ended the test process. Only a row that makes no
/// progress for <see cref="HangLimit"/> is left running when the test fails. Both limits are wall time, which a loaded
/// machine stretches: a slower one (a CI runner) multiplies them by <see cref="ScaleVariable"/>.
/// </summary>
internal static class Watchdog
{
    /// <summary>The environment variable whose number above 0 multiplies both limits, for a slower machine.</summary>
    public const string ScaleVariable = "ITERA_TEST_TIME_SCALE";

    /// <summary>
    /// How long one row may take: 1 s, times <see cref="Scale"/>. It also stands for the host: a client's request that
    /// holds it longer is a stall. Measured 2026-10-04 in Debug on the development machine:
    /// of 14,614 rows the slowest took 171 ms alone and 165 ms while the whole suite ran (rocket bursts' craters), a
    /// sixth of the limit.
    /// </summary>
    public static TimeSpan Limit => TimeSpan.FromSeconds(1 * Scale);

    /// <summary>How long a row may run before it counts as never returning and the test stops waiting for it.</summary>
    public static TimeSpan HangLimit => TimeSpan.FromSeconds(60 * Scale);

    /// <summary>The number <see cref="ScaleVariable"/> gives, or 1 when it is unset or not a number above 0.</summary>
    public static double Scale =>
        double.TryParse(
            Environment.GetEnvironmentVariable(ScaleVariable),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var scale
        )
        && double.IsFinite(scale)
        && scale > 0
            ? scale
            : 1;

    /// <summary>
    /// Runs <paramref name="row"/> for each index below <paramref name="rows"/> in order on a worker thread, failing when
    /// one takes longer than <paramref name="limit"/> (<see cref="Limit"/> by default; a given limit is scaled too) or
    /// throws.
    /// <paramref name="label"/> names a row in the failure.
    /// </summary>
    public static void Run(int rows, Func<int, string> label, Action<int> row, TimeSpan? limit = null)
    {
        var most = limit is { } given ? given * Scale : Limit;
        var finished = -1;
        Exception? failure = null;
        var failedRow = -1;
        var slowRow = -1;
        var slowTime = TimeSpan.Zero;
        var worker = new Thread(() =>
        {
            var watch = new Stopwatch();
            for (var i = 0; i < rows; i++)
            {
                watch.Restart();
                try
                {
                    row(i);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    failedRow = i;
                    return;
                }

                if (slowRow < 0 && watch.Elapsed > most)
                {
                    slowRow = i;
                    slowTime = watch.Elapsed;
                }

                Volatile.Write(ref finished, i);
            }
        })
        {
            IsBackground = true,
            Name = "Hostile rows",
        };
        worker.Start();

        var lastSeen = -1;
        var since = Stopwatch.StartNew();
        while (!worker.Join(10))
        {
            var now = Volatile.Read(ref finished);
            if (now != lastSeen)
            {
                lastSeen = now;
                since.Restart();
            }
            else if (since.Elapsed > HangLimit)
            {
                Assert.Fail(
                    $"Row {now + 1} ({label(now + 1)}) has not returned after {HangLimit.TotalSeconds:0} s; its thread "
                        + "still runs, and may use the world the test now disposes."
                );
            }
        }

        // Join has ordered the worker's writes before these reads.
        if (failure is not null)
        {
            Assert.Fail($"Row {failedRow} ({label(failedRow)}) threw {failure}");
        }

        if (slowRow >= 0)
        {
            Assert.Fail(
                $"Row {slowRow} ({label(slowRow)}) took {slowTime.TotalSeconds:0.00} s, past {most.TotalSeconds:0.##} s."
            );
        }
    }
}
