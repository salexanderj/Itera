using Microsoft.Extensions.Logging;

namespace Itera.Engine.Diagnostics;

/// <summary>
/// Writes exceptions that escape every other handler to the log, then flushes it. Covers unhandled exceptions on any
/// thread and unobserved task faults. Native crashes cannot be caught here.
/// </summary>
public sealed partial class UnhandledExceptionLogger : IDisposable
{
    private readonly ILogger _logger;
    private readonly Action _flush;
    private bool _disposed;

    private UnhandledExceptionLogger(ILogger logger, Action flush)
    {
        _logger = logger;
        _flush = flush;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>Subscribes to the process-wide events. Dispose to unsubscribe.</summary>
    /// <param name="logger">Logger that receives the entries.</param>
    /// <param name="flush">Called after a terminating exception is logged so the entry reaches disk.</param>
    public static UnhandledExceptionLogger Install(ILogger logger, Action flush)
    {
        return new UnhandledExceptionLogger(logger, flush);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogUnhandled(exception, e.IsTerminating);
        }
        else
        {
            var description = e.ExceptionObject?.ToString() ?? "null";
            LogUnhandledObject(description, e.IsTerminating);
        }

        try
        {
            _flush();
        }
        catch (Exception)
        {
            // The process is about to end; nothing more can be done.
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnobserved(e.Exception);
        e.SetObserved();
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception (terminating: {Terminating})")]
    private partial void LogUnhandled(Exception exception, bool terminating);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Unhandled non-exception object thrown: {Description} (terminating: {Terminating})"
    )]
    private partial void LogUnhandledObject(string description, bool terminating);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unobserved task exception")]
    private partial void LogUnobserved(Exception exception);
}
