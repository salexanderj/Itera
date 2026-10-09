namespace Itera.Engine.Diagnostics;

/// <summary>Deletes old log files. Never throws: every file system error is counted and the rest continues.</summary>
public static class LogRetention
{
    /// <summary>
    /// Deletes files matching <paramref name="filePattern"/> (<see cref="LogPaths.FilePattern"/>) in
    /// <paramref name="directory"/> that are older than
    /// <paramref name="retainedDays"/> (when positive) or beyond the newest <paramref name="retainedFiles"/> by last write time.
    /// A negative <paramref name="retainedFiles"/> keeps every file; zero deletes every file.
    /// </summary>
    public static LogRetentionResult Prune(
        string directory,
        string filePattern,
        int retainedFiles,
        int retainedDays,
        DateTime nowUtc
    )
    {
        if (!Directory.Exists(directory))
        {
            return default;
        }

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(directory)
                .EnumerateFiles(filePattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LogRetentionResult(0, 1, ex.Message);
        }

        var cutoff = retainedDays > 0 ? nowUtc.AddDays(-retainedDays) : DateTime.MinValue;
        var deleted = 0;
        var failed = 0;
        string? lastError = null;

        for (var i = 0; i < files.Length; i++)
        {
            var beyondCount = retainedFiles >= 0 && i >= retainedFiles;
            var tooOld = files[i].LastWriteTimeUtc < cutoff;
            if (!beyondCount && !tooOld)
            {
                continue;
            }

            try
            {
                files[i].Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                lastError = ex.Message;
            }
        }

        return new LogRetentionResult(deleted, failed, lastError);
    }
}

/// <summary>Outcome of one <see cref="LogRetention.Prune"/> call.</summary>
/// <param name="Deleted">Files removed.</param>
/// <param name="Failed">Files that could not be removed or listed.</param>
/// <param name="LastError">Message of the last failure, or null.</param>
public readonly record struct LogRetentionResult(int Deleted, int Failed, string? LastError);
