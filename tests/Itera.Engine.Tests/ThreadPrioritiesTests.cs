using System.Globalization;

namespace Itera.Engine.Tests;

/// <summary>
/// <see cref="ThreadPriorities.Lower"/>: on Linux a thread that calls it runs at nice <see cref="ThreadPriorities.LinuxNice"/>
/// and the thread that made it runs as it did; on another platform it does nothing and says so.
/// </summary>
public sealed class ThreadPrioritiesTests
{
    [Fact]
    public void A_thread_lowered_on_linux_runs_at_its_nice_value_and_no_other_thread_does()
    {
        var before = OperatingSystem.IsLinux() ? Nice() : 0;
        var (lowered, nice) = (false, int.MinValue);
        var thread = new Thread(() =>
        {
            lowered = ThreadPriorities.Lower();
            nice = OperatingSystem.IsLinux() ? Nice() : 0;
        });
        thread.Start();
        thread.Join();

        if (!OperatingSystem.IsLinux())
        {
            Assert.False(lowered);
            return;
        }

        Assert.True(lowered);
        Assert.Equal(ThreadPriorities.LinuxNice, nice);
        Assert.Equal(before, Nice());
    }

    /// <summary>The calling thread's nice value, the 19th field of its <c>/proc</c> stat line.</summary>
    private static int Nice()
    {
        var stat = File.ReadAllText("/proc/thread-self/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(fields[16], CultureInfo.InvariantCulture);
    }
}
