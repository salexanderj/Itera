using Itera.Engine.Diagnostics;
using Itera.Engine.Tests.Matches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    [Fact]
    public void A_data_folder_that_is_a_full_path_holds_the_log_file_named_after_its_last_folder()
    {
        var application = new ApplicationSettings { DataFolder = _directory };

        var session = LogSession.Create(new LoggingSettings(), application);

        Assert.Null(session.SetupError);
        Assert.Equal(Path.Combine(_directory, "Logs"), Path.GetDirectoryName(session.FilePath));
        var prefix = Path.GetFileName(_directory).ToLowerInvariant() + "-";
        Assert.StartsWith(prefix, Path.GetFileName(session.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void A_data_folder_the_validator_refuses_makes_no_folder_and_no_file()
    {
        var application = new ApplicationSettings { DataFolder = Path.Combine(_directory, "Sample Game") };

        var session = LogSession.Create(new LoggingSettings(), application);

        Assert.Null(session.FilePath);
        Assert.StartsWith("Engine:Application:DataFolder must be", session.SetupError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void The_engine_given_a_data_folder_the_validator_refuses_stops_at_its_options_with_the_key_named()
    {
        using var services = EngineComposition.Build(
            new Dictionary<string, string?>
            {
                ["Engine:Logging:FileEnabled"] = "true",
                ["Engine:Application:DataFolder"] = Path.Combine(_directory, "Sample Game"),
            }
        );

        var failure = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<EngineOptions>>().Value
        );

        Assert.Contains("Engine:Application:DataFolder must be", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
