using Itera.Engine.Diagnostics;

namespace Itera.Engine.Tests.Diagnostics;

public sealed class LogPathsTests
{
    private static readonly ApplicationSettings Application = new() { Name = "Sample Game", DataFolder = "SampleGame" };

    [Fact]
    public void Session_file_name_holds_the_data_folder_timestamp_and_process_id()
    {
        var name = LogPaths.SessionFileName(Application, new DateTime(2026, 9, 22, 14, 5, 9), 4242);

        Assert.Equal("samplegame-20260922-140509-4242.log", name);
    }

    [Fact]
    public void Session_file_name_matches_the_retention_pattern()
    {
        var directory = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var name = LogPaths.SessionFileName(Application, DateTime.Now, Environment.ProcessId);
            File.WriteAllText(Path.Combine(directory, name), "log");
            File.WriteAllText(Path.Combine(directory, "itera-20260922-140509-4242.log"), "log");

            var matched = Directory.GetFiles(directory, LogPaths.FilePattern(Application));

            Assert.Equal([name], matched.Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Default_directory_is_the_data_folder_under_local_application_data()
    {
        var root = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify
        );

        Assert.Equal(Path.Combine(root, "SampleGame", "Logs"), LogPaths.DefaultDirectory(Application));
    }

    [Fact]
    public void A_full_path_holds_the_logs_and_its_last_folder_names_the_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Sample Games", "SampleGame");
        var application = new ApplicationSettings { DataFolder = folder + Path.DirectorySeparatorChar };

        var name = LogPaths.SessionFileName(application, new DateTime(2026, 9, 22, 14, 5, 9), 4242);

        Assert.Equal(Path.Combine(folder, "Logs"), LogPaths.DefaultDirectory(application));
        Assert.Equal("samplegame-20260922-140509-4242.log", name);
        Assert.Equal("samplegame-*.log", LogPaths.FilePattern(application));
    }
}
