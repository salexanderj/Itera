using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;

namespace Itera.Engine.Diagnostics;

/// <summary>
/// Configures Serilog behind <see cref="ILogger{TCategoryName}"/>: a console sink at a configurable level and one
/// log file per run that receives every level the global minimum allows. Levels and per-category overrides come from
/// the "Serilog" configuration section; sink settings come from <see cref="LoggingSettings"/>.
/// </summary>
public static class LoggingServiceCollectionExtensions
{
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [T{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
    private const int AsyncQueueLimit = 10_000;

    /// <summary>Adds Serilog logging. <paramref name="configuration"/> is the root configuration.</summary>
    public static IServiceCollection AddIteraLogging(this IServiceCollection services, IConfiguration configuration)
    {
        var engine = configuration.GetSection(EngineOptions.SectionName);
        var settings = engine.GetSection(LoggingSettings.SectionName).Get<LoggingSettings>() ?? new LoggingSettings();
        var application =
            engine.GetSection(ApplicationSettings.SectionName).Get<ApplicationSettings>() ?? new ApplicationSettings();

        var session = LogSession.Create(settings, application);
        services.TryAddSingleton(session);
        var consoleLines = new ConsoleLogBuffer();
        services.TryAddSingleton(consoleLines);
        // Filled only while the control port listens: its sink formats every Debug line.
        var controlLines = new ControlLog();
        services.TryAddSingleton(controlLines);
        var control = engine.GetSection(ControlSettings.SectionName).Get<ControlSettings>() ?? new ControlSettings();
        services.TryAddSingleton<FrameStats>();

        // Serilog reports its own failures (for example an unwritable log file) here instead of throwing.
        SelfLog.Enable(message => Console.Error.WriteLine(message));

        services.AddSerilog(
            (_, logger) =>
                Configure(logger, configuration, settings, session, consoleLines, control.Enabled ? controlLines : null)
        );
        return services;
    }

    /// <summary>Maps a Microsoft logging level to the Serilog level with the same meaning.</summary>
    public static LogEventLevel ToSerilogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            _ => LevelAlias.Off,
        };
    }

    private static void Configure(
        LoggerConfiguration logger,
        IConfiguration configuration,
        LoggingSettings settings,
        LogSession session,
        ConsoleLogBuffer consoleLines,
        ControlLog? controlLines
    )
    {
        // Both sinks write on a background thread, so a burst of lines never stalls the game loop on terminal or
        // disk writes. The console drops lines when its queue is full; the file blocks instead and keeps them all.
        logger
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .WriteTo.Async(
                sink =>
                    sink.Console(
                        restrictedToMinimumLevel: ToSerilogLevel(settings.ConsoleLevel),
                        outputTemplate: ConsoleTemplate,
                        formatProvider: CultureInfo.InvariantCulture
                    ),
                bufferSize: AsyncQueueLimit,
                blockWhenFull: false
            )
            // The in-game console's scrollback: synchronous, so a command's echo is there before its answer.
            .WriteTo.Sink(new ConsoleLogSink(consoleLines), LogEventLevel.Information);
        if (controlLines is not null)
        {
            // The control port's lines: synchronous too, so a reply holds the lines its request logged.
            logger.WriteTo.Sink(new ControlLogSink(controlLines), LogEventLevel.Debug);
        }

        if (session.FilePath is not { } filePath)
        {
            return;
        }

        var sizeLimit = Math.Max(1, settings.FileSizeLimitMegabytes) * 1024L * 1024L;
        logger.WriteTo.Async(
            sink =>
                sink.File(
                    filePath,
                    outputTemplate: FileTemplate,
                    formatProvider: CultureInfo.InvariantCulture,
                    fileSizeLimitBytes: sizeLimit,
                    rollOnFileSizeLimit: true,
                    shared: false,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                ),
            bufferSize: AsyncQueueLimit,
            blockWhenFull: true
        );
    }
}

/// <summary>Writes log events of Information and above into the in-game console's <see cref="ConsoleLogBuffer"/>.</summary>
internal sealed class ConsoleLogSink(ConsoleLogBuffer lines) : Serilog.Core.ILogEventSink
{
    private readonly LogLineFormatter _formatter = new();

    public void Emit(LogEvent logEvent) => lines.Add(LogLineFormatter.Level(logEvent), _formatter.Format(logEvent));
}

/// <summary>Writes log events of Debug and above into the control port's <see cref="ControlLog"/>.</summary>
internal sealed class ControlLogSink(ControlLog lines) : Serilog.Core.ILogEventSink
{
    private readonly LogLineFormatter _formatter = new();

    public void Emit(LogEvent logEvent) => lines.Add(LogLineFormatter.Level(logEvent), _formatter.Format(logEvent));
}

/// <summary>A log event's message as the file shows it, with its exception's type and message after it.</summary>
internal sealed class LogLineFormatter
{
    // As the file shows messages: strings without quotes, other values as JSON.
    private readonly Serilog.Formatting.Display.MessageTemplateTextFormatter _formatter = new(
        "{Message:lj}",
        CultureInfo.InvariantCulture
    );

    public string Format(LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        _formatter.Format(logEvent, writer);
        var text = writer.ToString();
        if (logEvent.Exception is { } exception)
        {
            text += " (" + exception.GetType().Name + ": " + exception.Message + ")";
        }

        return text;
    }

    public static LogLevel Level(LogEvent logEvent) =>
        logEvent.Level switch
        {
            LogEventLevel.Fatal => LogLevel.Critical,
            LogEventLevel.Error => LogLevel.Error,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Information => LogLevel.Information,
            LogEventLevel.Debug => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
}
