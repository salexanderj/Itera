using Itera.Engine.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Tests.Diagnostics;

/// <summary>
/// <c>AddIteraLogging</c> (<see cref="Itera.Engine.Diagnostics.LoggingServiceCollectionExtensions"/>) on configuration
/// in memory (the Serilog set-up had no test): the <c>Serilog</c> section's minimum level and a category's override
/// decide what is logged; the in-game console gets Information and above, the control port's log Debug and above while
/// it listens, and the run's file every line the levels let through, written out when the services are disposed.
/// </summary>
public sealed class LoggingSetupTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("itera-logging-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_levels_and_overrides_decide_what_each_sink_gets()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Serilog:MinimumLevel:Default"] = "Debug",
                    ["Serilog:MinimumLevel:Override:Quiet"] = "Warning",
                    ["Engine:Logging:Directory"] = _folder,
                    ["Engine:Logging:ConsoleLevel"] = "None",
                    ["Engine:Control:Enabled"] = "true",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddIteraLogging(configuration);
        ConsoleLogBuffer console;
        ControlLog control;
        using (var provider = services.BuildServiceProvider())
        {
            console = provider.GetRequiredService<ConsoleLogBuffer>();
            control = provider.GetRequiredService<ControlLog>();
            var factory = provider.GetRequiredService<ILoggerFactory>();
            var loud = factory.CreateLogger("Loud");
            var quiet = factory.CreateLogger("Quiet");
            loud.LogTrace("loud trace");
            loud.LogDebug("loud debug");
            loud.LogInformation("loud information");
            quiet.LogInformation("quiet information");
            quiet.LogWarning("quiet warning");
            loud.LogError(new InvalidOperationException("broken"), "loud error");
        }

        var shown = new List<string>();
        console.Drain(line => shown.Add(line.Text));
        Assert.Equal(["loud information", "quiet warning", "loud error (InvalidOperationException: broken)"], shown);

        var lines = new List<ControlLogLine>();
        control.Read(0, LogLevel.Trace, lines);
        Assert.Equal(
            ["loud debug", "loud information", "quiet warning", "loud error (InvalidOperationException: broken)"],
            lines.Select(line => line.Text)
        );

        var file = Assert.Single(Directory.GetFiles(_folder, "*.log", SearchOption.AllDirectories));
        var text = File.ReadAllText(file);
        Assert.Contains("[DBG] [T", text, StringComparison.Ordinal);
        Assert.Contains("Loud: loud debug", text, StringComparison.Ordinal);
        Assert.Contains("Quiet: quiet warning", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: broken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("loud trace", text, StringComparison.Ordinal);
        Assert.DoesNotContain("quiet information", text, StringComparison.Ordinal);
    }
}
