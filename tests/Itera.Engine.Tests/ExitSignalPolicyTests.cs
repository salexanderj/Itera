using System.Diagnostics;

namespace Itera.Engine.Tests;

public sealed class ExitSignalPolicyTests
{
    private static readonly long s_start = Stopwatch.GetTimestamp();

    [Fact]
    public void The_first_signal_requests_a_clean_exit()
    {
        var policy = new ExitSignalPolicy();

        Assert.Equal(ExitSignalAction.RequestExit, policy.OnSignal(s_start));
    }

    [Fact]
    public void A_copy_within_the_window_is_ignored()
    {
        var policy = new ExitSignalPolicy();
        policy.OnSignal(s_start);

        Assert.Equal(ExitSignalAction.Ignore, policy.OnSignal(s_start));
        Assert.Equal(ExitSignalAction.Ignore, policy.OnSignal(After(TimeSpan.FromMilliseconds(100))));
    }

    [Fact]
    public void A_later_signal_terminates()
    {
        var policy = new ExitSignalPolicy();
        policy.OnSignal(s_start);

        Assert.Equal(ExitSignalAction.Terminate, policy.OnSignal(After(ExitSignalPolicy.DuplicateWindow)));
        Assert.Equal(ExitSignalAction.Terminate, policy.OnSignal(After(TimeSpan.FromSeconds(5))));
    }

    private static long After(TimeSpan elapsed)
    {
        return s_start + (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
    }
}
