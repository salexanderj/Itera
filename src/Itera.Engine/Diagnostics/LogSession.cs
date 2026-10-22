namespace Itera.Engine.Diagnostics;

/// <summary>Facts about this run's log file, decided once before the logger exists so the session header can report them.</summary>
public sealed class LogSession
{
    private LogSession(
        string directory,
        string? filePath,
        DateTime startedAtUtc,
        LogRetentionResult pruned,
        string? setupError
    )
    {
        Directory = directory;
        FilePath = filePath;
        StartedAtUtc = startedAtUtc;
        Pruned = pruned;
        SetupError = setupError;
    }

    /// <summary>Folder that holds the log files.</summary>
    public string Directory { get; }

    /// <summary>Full path of this run's log file, or null when file logging is off or the folder could not be created.</summary>
    public string? FilePath { get; }

    /// <summary>When the session started, in UTC.</summary>
    public DateTime StartedAtUtc { get; }

    /// <summary>What retention deleted at startup.</summary>
    public LogRetentionResult Pruned { get; }

    /// <summary>Why <see cref="FilePath"/> is null although file logging was requested, or null.</summary>
    public string? SetupError { get; }

    /// <summary>
    /// Creates the log folder, prunes old files, and picks this run's file name. Never throws. A data folder the options'
    /// validator refuses makes no folder and no file, and <see cref="SetupError"/> says why.
    /// </summary>
    public static LogSession Create(LoggingSettings settings, ApplicationSettings application, DateTime? nowUtc = null)
    {
        var startedAtUtc = nowUtc ?? DateTime.UtcNow;

        // This runs before the options' validator, which then stops the run with the same message. A relative path such
        // as "../x" would make a folder outside the local application data folder, and separators or wildcards in the
        // name an invalid file pattern or one that matches other files.
        if (EngineOptionsValidator.DataFolderProblem(application.DataFolder) is { } problem)
        {
            return new LogSession(application.DataFolder, null, startedAtUtc, default, problem);
        }

        var directory = Path.GetFullPath(settings.Directory ?? LogPaths.DefaultDirectory(application));

        if (!settings.FileEnabled)
        {
            return new LogSession(directory, null, startedAtUtc, default, null);
        }

        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new LogSession(directory, null, startedAtUtc, default, ex.Message);
        }

        var pruned = LogRetention.Prune(
            directory,
            LogPaths.FilePattern(application),
            Math.Max(0, settings.RetainedFiles - 1),
            settings.RetainedDays,
            startedAtUtc
        );
        var fileName = LogPaths.SessionFileName(application, startedAtUtc.ToLocalTime(), Environment.ProcessId);
        return new LogSession(directory, Path.Combine(directory, fileName), startedAtUtc, pruned, null);
    }
}
