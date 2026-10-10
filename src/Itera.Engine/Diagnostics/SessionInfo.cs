using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Diagnostics;

/// <summary>Facts about the build, machine, and process, written at the top of every log so a log file describes itself.</summary>
/// <param name="ProductVersion">Informational version of the entry assembly, including the git commit when SourceLink added it.</param>
/// <param name="BuildConfiguration">Debug or Release, from the entry assembly.</param>
/// <param name="OperatingSystem">OS name and version.</param>
/// <param name="Architecture">Process architecture.</param>
/// <param name="Runtime">.NET runtime description.</param>
/// <param name="ProcessId">Process id.</param>
/// <param name="ProcessorCount">Logical processors available to the process.</param>
/// <param name="TotalMemoryBytes">Physical memory the garbage collector considers available.</param>
/// <param name="ServerGc">True when the server garbage collector is in use.</param>
/// <param name="DebuggerAttached">True when a debugger was attached at startup.</param>
/// <param name="EnvironmentName">Value of DOTNET_ENVIRONMENT, or "Production".</param>
/// <param name="BaseDirectory">Folder that holds the executable.</param>
/// <param name="CommandLine">Arguments after the executable path.</param>
public sealed record SessionInfo(
    string ProductVersion,
    string BuildConfiguration,
    string OperatingSystem,
    string Architecture,
    string Runtime,
    int ProcessId,
    int ProcessorCount,
    long TotalMemoryBytes,
    bool ServerGc,
    bool DebuggerAttached,
    string EnvironmentName,
    string BaseDirectory,
    IReadOnlyList<string> CommandLine
)
{
    /// <summary>Reads the facts from the running process.</summary>
    public static SessionInfo Capture()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(SessionInfo).Assembly;
        var version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var args = Environment.GetCommandLineArgs();

        return new SessionInfo(
            version,
            configuration,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessId,
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            GCSettings.IsServerGC,
            Debugger.IsAttached,
            string.IsNullOrEmpty(environment) ? "Production" : environment,
            AppContext.BaseDirectory,
            args.Length > 1 ? args[1..] : []
        );
    }

    /// <summary>Writes the header: one line per fact, then the log file facts, then the effective engine options at Debug.</summary>
    public void LogTo(ILogger logger, LogSession session, EngineOptions options)
    {
        var startedAt = session.StartedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var memoryGb = TotalMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        var commandLine = CommandLine.Count == 0 ? "(none)" : string.Join(" ", CommandLine);

        SessionLog.Header(logger, options.Application.Name, ProductVersion, BuildConfiguration, startedAt);
        SessionLog.Machine(logger, OperatingSystem, Architecture, Runtime, ProcessorCount, memoryGb);
        SessionLog.Process(logger, ProcessId, ServerGc, DebuggerAttached, EnvironmentName, BaseDirectory);
        SessionLog.CommandLine(logger, commandLine);

        if (session.FilePath is { } filePath)
        {
            SessionLog.LogFile(logger, filePath, session.Pruned.Deleted);
        }
        else if (session.SetupError is { } error)
        {
            SessionLog.LogFileUnavailable(logger, session.Directory, error);
        }
        else
        {
            SessionLog.LogFileDisabled(logger);
        }

        if (session.Pruned.Failed > 0)
        {
            SessionLog.PruneFailed(logger, session.Pruned.Failed, session.Pruned.LastError ?? "unknown");
        }

        var window = options.Window;
        var renderer = options.Renderer;
        var device = renderer.PreferredDevice ?? "(auto)";
        var validation = renderer.Validation?.ToString() ?? "(build default)";
        var modsDirectory = options.Mods.Directory ?? "(default)";
        SessionLog.Options(
            logger,
            window.Width,
            window.Height,
            window.VSync,
            device,
            validation,
            modsDirectory,
            options.FixedStepHz,
            options.MaxFixedStepsPerFrame,
            options.Logging.ConsoleLevel,
            options.Logging.StatsIntervalSeconds
        );

        var voxels = options.Voxels;
        var workers = voxels.WorkerThreads == 0 ? "auto" : voxels.WorkerThreads.ToString(CultureInfo.InvariantCulture);
        SessionLog.VoxelOptions(
            logger,
            voxels.StreamingRadiusMetres,
            voxels.MemoryBudgetMegabytes,
            workers,
            voxels.Seed,
            voxels.DebugWalkSpeed
        );
    }
}

/// <summary>Log messages for the session header.</summary>
internal static partial class SessionLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{Application} {Version} ({Configuration}) on the Itera engine, session started {StartedAtUtc} UTC"
    )]
    public static partial void Header(
        ILogger logger,
        string application,
        string version,
        string configuration,
        string startedAtUtc
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Machine: {OperatingSystem}, {Architecture}, {Runtime}, {Processors} processors, {MemoryGb:F1} GB memory"
    )]
    public static partial void Machine(
        ILogger logger,
        string operatingSystem,
        string architecture,
        string runtime,
        int processors,
        double memoryGb
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Process: pid {ProcessId}, server GC {ServerGc}, debugger {DebuggerAttached}, environment {Environment}, base directory {BaseDirectory}"
    )]
    public static partial void Process(
        ILogger logger,
        int processId,
        bool serverGc,
        bool debuggerAttached,
        string environment,
        string baseDirectory
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "Command line: {CommandLine}")]
    public static partial void CommandLine(ILogger logger, string commandLine);

    [LoggerMessage(Level = LogLevel.Information, Message = "Log file: {Path} ({Pruned} old file(s) removed)")]
    public static partial void LogFile(ILogger logger, string path, int pruned);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log file disabled: could not use {Directory}: {Reason}")]
    public static partial void LogFileUnavailable(ILogger logger, string directory, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Log file disabled by configuration")]
    public static partial void LogFileDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log retention could not remove {Failed} file(s): {Reason}")]
    public static partial void PruneFailed(ILogger logger, int failed, string reason);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Options: window {Width}x{Height} vsync {VSync}, preferred device {Device}, validation {Validation}, mods {Mods}, fixed step {FixedStepHz} Hz max {MaxSteps}/frame, console level {ConsoleLevel}, stats every {StatsSeconds} s"
    )]
    public static partial void Options(
        ILogger logger,
        int width,
        int height,
        bool vSync,
        string device,
        string validation,
        string mods,
        int fixedStepHz,
        int maxSteps,
        LogLevel consoleLevel,
        double statsSeconds
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Voxel options: streaming radius {RadiusMetres} m (0: from the grid), memory budget {BudgetMegabytes} MB, worker threads {Workers}, configured seed {Seed}, debug walk {WalkSpeed} m/s"
    )]
    public static partial void VoxelOptions(
        ILogger logger,
        double radiusMetres,
        int budgetMegabytes,
        string workers,
        ulong seed,
        double walkSpeed
    );
}
