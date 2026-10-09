using Itera.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Tests;

/// <summary>The game's real service collection, as <c>Program.cs</c> composes it, for tests of the composition.</summary>
internal static class GameComposition
{
    /// <summary>The shipped settings, with no log file: composing the engine with one prunes the real log folder.</summary>
    public static IConfiguration Configuration(IReadOnlyDictionary<string, string?>? options = null) =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:Logging:FileEnabled"] = "false" })
            .AddInMemoryCollection(options ?? new Dictionary<string, string?>())
            .Build();

    /// <summary>
    /// The game's services, from the shipped settings and <paramref name="options"/> over them, with every registration
    /// checked as <c>Program.cs</c> checks it.
    /// </summary>
    public static ServiceProvider Build(IReadOnlyDictionary<string, string?>? options = null)
    {
        var configuration = Configuration(options);
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddIteraEngine(configuration);
        services.AddGame(configuration);
        // The host adds ILogger<T>; a null factory keeps Serilog from writing a log file.
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
