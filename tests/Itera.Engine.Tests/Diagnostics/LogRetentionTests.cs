using Itera.Engine.Diagnostics;

namespace Itera.Engine.Tests.Diagnostics;

public sealed class LogRetentionTests : IDisposable
{
    private const string Pattern = "itera-*.log";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    public LogRetentionTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Keeps_the_newest_files_by_count()
    {
        CreateFiles(5);

        var result = LogRetention.Prune(_directory, Pattern, retainedFiles: 2, retainedDays: 0, _now);

        Assert.Equal(3, result.Deleted);
        Assert.Equal(0, result.Failed);
        Assert.Equal(["itera-3.log", "itera-4.log"], Remaining());
    }

    [Fact]
    public void Deletes_files_older_than_the_age_limit()
    {
        CreateFiles(4);

        var result = LogRetention.Prune(_directory, Pattern, retainedFiles: -1, retainedDays: 2, _now);

        // Files 0 and 1 are 4 and 3 days old; 2 and 3 are 2 and 1 days old.
        Assert.Equal(2, result.Deleted);
        Assert.Equal(["itera-2.log", "itera-3.log"], Remaining());
    }

    [Fact]
    public void Ignores_files_that_do_not_match_the_pattern()
    {
        CreateFiles(2);
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "keep");
        File.WriteAllText(Path.Combine(_directory, "other-0.log"), "keep");

        var result = LogRetention.Prune(_directory, Pattern, retainedFiles: 0, retainedDays: 0, _now);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(["notes.txt", "other-0.log"], Remaining());
    }

    [Fact]
    public void Missing_directory_returns_zero_without_throwing()
    {
        var result = LogRetention.Prune(Path.Combine(_directory, "missing"), Pattern, 1, 1, _now);

        Assert.Equal(default, result);
    }

    [Fact]
    public void Negative_count_and_zero_days_keep_everything()
    {
        CreateFiles(3);

        var result = LogRetention.Prune(_directory, Pattern, retainedFiles: -1, retainedDays: 0, _now);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(3, Remaining().Count);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Creates itera-0.log (oldest) to itera-N.log (newest), one day apart, the newest one day old.</summary>
    private void CreateFiles(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_directory, $"itera-{i}.log");
            File.WriteAllText(path, "log");
            File.SetLastWriteTimeUtc(path, _now.AddDays(-(count - i)));
        }
    }

    private List<string> Remaining()
    {
        return Directory
            .EnumerateFiles(_directory)
            .Select(p => Path.GetFileName(p) ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
