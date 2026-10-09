using System.Globalization;

namespace Itera.Engine.Diagnostics;

/// <summary>Where log files live and how they are named. Both follow the application's data folder.</summary>
public static class LogPaths
{
    /// <summary>Glob that matches every log file of the application, including size-rolled continuations.</summary>
    public static string FilePattern(ApplicationSettings application)
    {
        return Prefix(application) + "-*.log";
    }

    /// <summary>"Logs" in the application's data folder.</summary>
    public static string DefaultDirectory(ApplicationSettings application)
    {
        return Path.Combine(application.DataDirectory(), "Logs");
    }

    /// <summary>
    /// File name for one run: <c>&lt;data folder in lower case&gt;-yyyyMMdd-HHmmss-&lt;pid&gt;.log</c>, using local
    /// time.
    /// </summary>
    public static string SessionFileName(ApplicationSettings application, DateTime startedAt, int processId)
    {
        var stamp = startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix(application)}-{stamp}-{processId}.log");
    }

    private static string Prefix(ApplicationSettings application)
    {
        return application.DataFolder.ToLowerInvariant();
    }
}
