using System.Globalization;

namespace Itera.TestSupport;

/// <summary>
/// The wall-time limits of tests that wait on other threads: a world's workers, navigation, sockets, the audio thread.
/// Each limit stops a wait that never ends; none checks how fast anything is. A slower machine (a CI runner of 2 or 3
/// cores running the whole suite) stretches every one by the number <see cref="ScaleVariable"/> gives, which the
/// <see cref="Watchdog"/>'s limits take too.
/// </summary>
internal static class TestTime
{
    /// <summary>The environment variable whose number above 0 multiplies every limit, for a slower machine.</summary>
    public const string ScaleVariable = "ITERA_TEST_TIME_SCALE";

    /// <summary>
    /// The number <see cref="ScaleVariable"/> gives, or 1 when it is unset or not a number above 0. Read once, so a wait's
    /// loop that asks for its limit each round allocates nothing.
    /// </summary>
    public static double Scale { get; } =
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

    /// <summary><paramref name="seconds"/> of wall time, times <see cref="Scale"/>.</summary>
    public static TimeSpan Limit(double seconds) => TimeSpan.FromSeconds(seconds * Scale);

    /// <summary>The moment, in UTC, <see cref="Limit"/> of <paramref name="seconds"/> from now.</summary>
    public static DateTime Deadline(double seconds) => DateTime.UtcNow + Limit(seconds);
}
