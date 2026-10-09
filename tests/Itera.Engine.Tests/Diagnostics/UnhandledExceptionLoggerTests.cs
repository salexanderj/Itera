using System.Reflection;
using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Tests.Diagnostics;

/// <summary>
/// <see cref="UnhandledExceptionLogger"/> (the crash logger had no test): an unhandled exception is logged
/// at Critical and the log flushed; a thrown object that is not an exception is described; an unobserved task's fault
/// is logged at Error and marked observed; disposing twice is safe.
/// </summary>
public sealed class UnhandledExceptionLoggerTests
{
    [Fact]
    public void An_unhandled_exception_is_logged_at_critical_and_the_log_flushed()
    {
        var logger = new RecordingLogger();
        var flushes = 0;
        using var crashes = UnhandledExceptionLogger.Install(logger, () => flushes++);

        Raise(crashes, new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), isTerminating: true));
        Raise(crashes, new UnhandledExceptionEventArgs("a string", isTerminating: false));

        Assert.Equal(2, flushes);
        Assert.Equal(LogLevel.Critical, logger.Entries[0].Level);
        Assert.Equal("Unhandled exception (terminating: True)", logger.Entries[0].Message);
        Assert.Equal("boom", logger.Entries[0].Exception?.Message);
        Assert.Equal("Unhandled non-exception object thrown: a string (terminating: False)", logger.Entries[1].Message);
    }

    [Fact]
    public void A_flush_that_throws_is_swallowed()
    {
        var logger = new RecordingLogger();
        using var crashes = UnhandledExceptionLogger.Install(logger, () => throw new IOException("disk gone"));

        Raise(crashes, new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), isTerminating: true));

        Assert.Single(logger.Entries);
    }

    [Fact]
    public void An_unobserved_task_fault_is_logged_at_error_and_observed()
    {
        var logger = new RecordingLogger();
        using var crashes = UnhandledExceptionLogger.Install(logger, () => { });
        var fault = new AggregateException(new InvalidOperationException("lost"));
        var args = new UnobservedTaskExceptionEventArgs(fault);

        typeof(UnhandledExceptionLogger)
            .GetMethod("OnUnobservedTaskException", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(crashes, [null, args]);

        Assert.True(args.Observed);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(fault, entry.Exception);
    }

    [Fact]
    public void Disposing_it_twice_is_safe()
    {
        var crashes = UnhandledExceptionLogger.Install(new RecordingLogger(), () => { });

        crashes.Dispose();
        crashes.Dispose();
    }

    private static void Raise(UnhandledExceptionLogger crashes, UnhandledExceptionEventArgs args) =>
        typeof(UnhandledExceptionLogger)
            .GetMethod("OnUnhandledException", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(crashes, [AppDomain.CurrentDomain, args]);

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
