using Itera.Engine.Diagnostics;

namespace Itera.Engine.Tests.Diagnostics;

public sealed class LogSessionTests : IDisposable
{
    private static readonly ApplicationSettings Application = new();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Creates_the_directory_and_picks_a_file_inside_it()
    {
        var settings = new LoggingSettings { Directory = _directory };

        var session = LogSession.Create(settings, Application);

        Assert.True(Directory.Exists(_directory));
        Assert.NotNull(session.FilePath);
        Assert.Equal(_directory, Path.GetDirectoryName(session.FilePath));
        Assert.Null(session.SetupError);
    }

    [Fact]
    public void Disabled_file_logging_yields_no_path()
    {
        var settings = new LoggingSettings { Directory = _directory, FileEnabled = false };

        var session = LogSession.Create(settings, Application);

        Assert.Null(session.FilePath);
        Assert.Null(session.SetupError);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void Prunes_before_choosing_the_new_file()
    {
        Directory.CreateDirectory(_directory);
        for (var i = 0; i < 4; i++)
        {
            File.WriteAllText(Path.Combine(_directory, $"itera-old{i}.log"), "log");
        }

        var session = LogSession.Create(
            new LoggingSettings
            {
                Directory = _directory,
                RetainedFiles = 2,
                RetainedDays = 0,
            },
            Application
        );

        // Two old files remain so that, with this run's file, the folder holds RetainedFiles files.
        Assert.Equal(3, session.Pruned.Deleted);
        Assert.Single(Directory.EnumerateFiles(_directory));
        Assert.NotNull(session.FilePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
