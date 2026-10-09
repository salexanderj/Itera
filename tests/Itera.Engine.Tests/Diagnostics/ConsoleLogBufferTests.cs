using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Tests.Diagnostics;

public sealed class ConsoleLogBufferTests
{
    [Fact]
    public void Lines_come_out_oldest_first_once()
    {
        var buffer = new ConsoleLogBuffer();
        buffer.Add(LogLevel.Information, "a");
        buffer.Add(LogLevel.Error, "b");
        var taken = new List<ConsoleLogLine>();

        Assert.Equal(2, buffer.Drain(taken.Add));
        Assert.Equal(0, buffer.Drain(taken.Add));

        Assert.Equal([new(LogLevel.Information, "a"), new(LogLevel.Error, "b")], taken);
    }

    [Fact]
    public void Past_the_capacity_the_oldest_lines_go()
    {
        var buffer = new ConsoleLogBuffer();
        for (var i = 0; i < ConsoleLogBuffer.Capacity + 10; i++)
        {
            buffer.Add(LogLevel.Information, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var taken = new List<ConsoleLogLine>();
        buffer.Drain(taken.Add);

        Assert.Equal(ConsoleLogBuffer.Capacity, taken.Count);
        Assert.Equal("10", taken[0].Text);
    }

    [Fact]
    public void Lines_added_from_two_threads_while_another_drains_come_out_whole()
    {
        var buffer = new ConsoleLogBuffer();
        var taken = new List<ConsoleLogLine>();
        const int PerThread = 20_000;
        var writers = Enumerable
            .Range(0, 2)
            .Select(t =>
                Task.Run(() =>
                {
                    for (var i = 0; i < PerThread; i++)
                    {
                        buffer.Add(LogLevel.Information, "x");
                    }
                })
            )
            .ToArray();
        while (!Task.WhenAll(writers).IsCompleted)
        {
            buffer.Drain(taken.Add);
        }

        buffer.Drain(taken.Add);

        // A full ring drops its oldest lines by design; every line that comes out is one that went in.
        Assert.InRange(taken.Count, ConsoleLogBuffer.Capacity, 2 * PerThread);
        Assert.All(taken, line => Assert.Equal(new ConsoleLogLine(LogLevel.Information, "x"), line));
    }
}
